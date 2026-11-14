from __future__ import annotations

import ctypes
import logging
import os
import sys

from PySide6.QtCore import QObject, QTimer
from PySide6.QtGui import QAction, QActionGroup
from PySide6.QtWidgets import (
    QApplication,
    QMenu,
    QMessageBox,
    QStyle,
    QSystemTrayIcon,
)

from .config import RATES, VOICES, AppConfig, load_config, save_config
from .foreground import get_foreground_info, get_process_name
from .hotkeys import HotkeyManager
from .logging_setup import setup_logging
from .paths import LOG_DIR, ensure_dirs
from .popup import SelectionPopup
from .reader import ReaderService
from .selection_monitor import SelectionMonitor

log = logging.getLogger(__name__)

_MUTEX_HANDLE: int | None = None
_MUTEX_NAME = "SelectionTTS.Phase0.SingleInstance"
_ERROR_ALREADY_EXISTS = 183


def ensure_single_instance() -> bool:
    global _MUTEX_HANDLE
    kernel32 = ctypes.windll.kernel32
    kernel32.CreateMutexW.restype = ctypes.c_void_p
    kernel32.CreateMutexW.argtypes = [ctypes.c_void_p, ctypes.c_bool, ctypes.c_wchar_p]
    _MUTEX_HANDLE = kernel32.CreateMutexW(None, False, _MUTEX_NAME)
    if not _MUTEX_HANDLE:
        return True
    return kernel32.GetLastError() != _ERROR_ALREADY_EXISTS


class TrayApp(QObject):
    def __init__(self, app: QApplication) -> None:
        super().__init__()
        self.app = app
        self.config: AppConfig = load_config()
        self.reader = ReaderService(self.config)
        self.reader.status.connect(self._on_status)
        self.reader.error.connect(self._on_error)

        self._build_tray()

        self.hotkeys = HotkeyManager()
        self.hotkeys.read_requested.connect(self._on_hotkey_read)
        self.hotkeys.toggle_requested.connect(self.toggle_auto_popup)
        self.hotkeys.registration_failed.connect(self._on_hotkey_failure)
        self.hotkeys.start(self.config.hotkey_read, self.config.hotkey_toggle)

        self.popup = SelectionPopup(self.config)
        self.popup.read_requested.connect(self.reader.read_text)
        self.popup.shown.connect(self._on_popup_shown)
        self.popup.hidden.connect(self._on_popup_hidden)

        self.monitor = SelectionMonitor(self.config)
        self.monitor.selection_ready.connect(self._on_selection_ready)
        self.monitor.selection_cleared.connect(self.popup.hide)
        self.monitor.set_enabled(self.config.auto_popup)

        self.tray.show()
        QTimer.singleShot(1200, self._show_ready_message)

    def _build_tray(self) -> None:
        icon = self.app.style().standardIcon(QStyle.StandardPixmap.SP_MediaVolume)
        self.tray = QSystemTrayIcon(icon, self)
        self.tray.setToolTip("划词朗读 (Phase 0)")
        self.tray.activated.connect(self._on_tray_activated)

        self.menu = QMenu()

        self.read_action = QAction("朗读当前选区    Ctrl+Alt+R", self.menu)
        self.read_action.triggered.connect(
            lambda: self.reader.read_selection(wait_release_ms=0)
        )
        self.menu.addAction(self.read_action)

        self.auto_action = QAction("自动弹窗    Ctrl+Alt+Shift+P", self.menu)
        self.auto_action.setCheckable(True)
        self.auto_action.setChecked(self.config.auto_popup)
        self.auto_action.triggered.connect(self.toggle_auto_popup)
        self.menu.addAction(self.auto_action)

        self.fullscreen_action = QAction("全屏/游戏时暂停自动弹窗", self.menu)
        self.fullscreen_action.setCheckable(True)
        self.fullscreen_action.setChecked(self.config.pause_in_fullscreen)
        self.fullscreen_action.triggered.connect(self.toggle_fullscreen_pause)
        self.menu.addAction(self.fullscreen_action)

        self.stop_action = QAction("停止朗读", self.menu)
        self.stop_action.triggered.connect(self.reader.stop)
        self.menu.addAction(self.stop_action)

        self.menu.addSeparator()

        self.voice_menu = self.menu.addMenu("音色")
        self.voice_group = QActionGroup(self.voice_menu)
        self.voice_group.setExclusive(True)
        self._voice_actions: dict[str, QAction] = {}
        for label, voice_id in VOICES.items():
            action = QAction(label, self.voice_menu)
            action.setCheckable(True)
            action.setChecked(voice_id == self.config.voice)
            action.setData(voice_id)
            action.triggered.connect(
                lambda _checked=False, vid=voice_id, text=label: self.set_voice(vid, text)
            )
            self.voice_group.addAction(action)
            self.voice_menu.addAction(action)
            self._voice_actions[voice_id] = action

        self.rate_menu = self.menu.addMenu("语速")
        self.rate_group = QActionGroup(self.rate_menu)
        self.rate_group.setExclusive(True)
        self._rate_actions: dict[str, QAction] = {}
        for label, rate in RATES.items():
            action = QAction(label, self.rate_menu)
            action.setCheckable(True)
            action.setChecked(rate == self.config.rate)
            action.setData(rate)
            action.triggered.connect(
                lambda _checked=False, value=rate, text=label: self.set_rate(value, text)
            )
            self.rate_group.addAction(action)
            self.rate_menu.addAction(action)
            self._rate_actions[rate] = action

        self.menu.addSeparator()

        self.log_action = QAction("打开日志目录", self.menu)
        self.log_action.triggered.connect(lambda: os.startfile(str(LOG_DIR)))
        self.menu.addAction(self.log_action)

        self.quit_action = QAction("退出", self.menu)
        self.quit_action.triggered.connect(self.quit)
        self.menu.addAction(self.quit_action)

        self.tray.setContextMenu(self.menu)

    def _on_tray_activated(self, reason: QSystemTrayIcon.ActivationReason) -> None:
        if reason == QSystemTrayIcon.ActivationReason.DoubleClick:
            self.reader.read_selection(wait_release_ms=0)

    def _on_hotkey_read(self) -> None:
        name = self._excluded_foreground()
        if name:
            log.info("前台程序 %s 在排除列表中，跳过热键朗读", name)
            self._on_status(f"已跳过：{name} 在排除列表中")
            return
        self.reader.read_selection(wait_release_ms=250)

    def _excluded_foreground(self) -> str:
        excluded = {item.lower() for item in self.config.excluded_processes}
        if not excluded:
            return ""
        pid = get_foreground_info()[0]
        name = get_process_name(pid)
        return name if name and name.lower() in excluded else ""

    def _on_selection_ready(self, text: str, x: int, y: int) -> None:
        self.popup.show_for(text, x, y)

    def _on_popup_shown(self, rect: object) -> None:
        self.monitor.set_popup_rect(rect)  # type: ignore[arg-type]

    def _on_popup_hidden(self) -> None:
        self.monitor.set_popup_rect(None)

    def _on_status(self, message: str) -> None:
        log.info("status: %s", message)
        self.tray.setToolTip(f"划词朗读\n{message}" if message else "划词朗读")
        important = {
            "没有检测到选中文本",
            "开始朗读",
            "朗读完成",
            "已停止",
            "朗读失败",
        }
        if message in important or message.startswith("文本较长"):
            self.tray.showMessage(
                "划词朗读",
                message,
                QSystemTrayIcon.MessageIcon.Information,
                1800,
            )

    def _on_error(self, message: str) -> None:
        log.error(message)
        self.tray.showMessage(
            "划词朗读",
            message,
            QSystemTrayIcon.MessageIcon.Warning,
            4000,
        )

    def _on_hotkey_failure(self, message: str) -> None:
        log.error(message)
        self.tray.showMessage(
            "划词朗读",
            message,
            QSystemTrayIcon.MessageIcon.Warning,
            5000,
        )

    def toggle_auto_popup(self) -> None:
        self.config.auto_popup = not self.config.auto_popup
        self.auto_action.setChecked(self.config.auto_popup)
        self.monitor.set_enabled(self.config.auto_popup)
        if not self.config.auto_popup:
            self.popup.hide()
        save_config(self.config)
        state = "开启" if self.config.auto_popup else "关闭"
        self.tray.showMessage(
            "划词朗读",
            f"自动弹窗已{state}（快捷键朗读始终可用）",
            QSystemTrayIcon.MessageIcon.Information,
            2200,
        )

    def toggle_fullscreen_pause(self) -> None:
        self.config.pause_in_fullscreen = not self.config.pause_in_fullscreen
        self.fullscreen_action.setChecked(self.config.pause_in_fullscreen)
        save_config(self.config)
        state = "开启" if self.config.pause_in_fullscreen else "关闭"
        self.tray.showMessage(
            "划词朗读",
            f"全屏/游戏时暂停自动弹窗已{state}",
            QSystemTrayIcon.MessageIcon.Information,
            2200,
        )

    def set_voice(self, voice_id: str, label: str) -> None:
        self.config.voice = voice_id
        self._voice_actions[voice_id].setChecked(True)
        save_config(self.config)
        self.tray.showMessage(
            "划词朗读",
            f"音色：{label}",
            QSystemTrayIcon.MessageIcon.Information,
            1600,
        )

    def set_rate(self, rate: str, label: str) -> None:
        self.config.rate = rate
        self._rate_actions[rate].setChecked(True)
        save_config(self.config)
        self.tray.showMessage(
            "划词朗读",
            f"语速：{label}",
            QSystemTrayIcon.MessageIcon.Information,
            1600,
        )

    def _show_ready_message(self) -> None:
        self.tray.showMessage(
            "划词朗读已启动",
            "划选文本会自动弹出朗读按钮\nCtrl+Alt+R 朗读当前选区\nCtrl+Alt+Shift+P 切换自动弹窗",
            QSystemTrayIcon.MessageIcon.Information,
            3000,
        )

    def quit(self) -> None:
        self.monitor.stop()
        self.popup.hide()
        self.hotkeys.stop()
        self.reader.stop()
        self.tray.hide()
        self.app.quit()


def main() -> None:
    # A tray app should survive a stray Ctrl+C in its console; quitting is done
    # from the tray menu instead.
    try:
        ctypes.windll.kernel32.SetConsoleCtrlHandler(None, True)
    except Exception:
        pass

    setup_logging()
    if not ensure_single_instance():
        app = QApplication(sys.argv)
        QMessageBox.information(None, "划词朗读", "程序已经在运行。")
        return

    app = QApplication(sys.argv)
    app.setQuitOnLastWindowClosed(False)

    if not QSystemTrayIcon.isSystemTrayAvailable():
        QMessageBox.critical(None, "划词朗读", "系统托盘不可用，无法启动。")
        return

    ensure_dirs()
    _tray = TrayApp(app)
    sys.exit(app.exec())
