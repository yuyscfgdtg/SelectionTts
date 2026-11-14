from __future__ import annotations

import ctypes
import logging
from ctypes import wintypes
from typing import Callable

from PySide6.QtCore import QObject, Qt, Signal
from PySide6.QtWidgets import QWidget

log = logging.getLogger(__name__)

_user32 = ctypes.windll.user32

_MOD_ALT = 0x0001
_MOD_CONTROL = 0x0002
_MOD_SHIFT = 0x0004
_MOD_WIN = 0x0008
_MOD_NOREPEAT = 0x4000
_WM_HOTKEY = 0x0312


def parse_hotkey(spec: str) -> tuple[int, int]:
    normalized = spec.replace("<", "").replace(">", "").replace(" ", "").lower()
    parts = [part for part in normalized.split("+") if part]
    modifiers = 0
    virtual_key: int | None = None

    for part in parts:
        if part in {"ctrl", "control"}:
            modifiers |= _MOD_CONTROL
        elif part == "alt":
            modifiers |= _MOD_ALT
        elif part == "shift":
            modifiers |= _MOD_SHIFT
        elif part in {"win", "windows"}:
            modifiers |= _MOD_WIN
        elif len(part) == 1 and part.isalnum():
            virtual_key = ord(part.upper())
        else:
            raise ValueError(f"无法解析快捷键: {spec}")

    if virtual_key is None:
        raise ValueError(f"快捷键缺少主键: {spec}")
    return modifiers, virtual_key


class _HotkeyWindow(QWidget):
    def __init__(self, callbacks: dict[int, Callable[[], None]]) -> None:
        super().__init__()
        self._callbacks = callbacks
        self.setWindowFlag(Qt.WindowType.Tool, True)
        self.setAttribute(Qt.WidgetAttribute.WA_DontShowOnScreen, True)
        self.setAttribute(Qt.WidgetAttribute.WA_ShowWithoutActivating, True)
        self.resize(1, 1)
        self.show()
        self.hwnd = int(self.winId())

    def nativeEvent(self, eventType, message):  # type: ignore[override]
        if bytes(eventType) == b"windows_generic_MSG":
            msg = wintypes.MSG.from_address(int(message))
            if msg.message == _WM_HOTKEY:
                callback = self._callbacks.get(int(msg.wParam))
                if callback is not None:
                    callback()
                return True, 0
        return super().nativeEvent(eventType, message)


class HotkeyManager(QObject):
    read_requested = Signal()
    toggle_requested = Signal()
    registration_failed = Signal(str)

    def __init__(self, parent: QObject | None = None) -> None:
        super().__init__(parent)
        self._window: _HotkeyWindow | None = None
        self._ids: list[int] = []

    def start(self, read_hotkey: str, toggle_hotkey: str) -> None:
        try:
            read_mods, read_vk = parse_hotkey(read_hotkey)
            toggle_mods, toggle_vk = parse_hotkey(toggle_hotkey)
        except ValueError as exc:
            log.exception("快捷键格式无效")
            self.registration_failed.emit(str(exc))
            return

        callbacks: dict[int, Callable[[], None]] = {
            1: self.read_requested.emit,
            2: self.toggle_requested.emit,
        }
        self._window = _HotkeyWindow(callbacks)

        registrations = (
            (1, read_mods, read_vk, read_hotkey),
            (2, toggle_mods, toggle_vk, toggle_hotkey),
        )
        for hotkey_id, modifiers, virtual_key, name in registrations:
            ok = _user32.RegisterHotKey(
                self._window.hwnd,
                hotkey_id,
                modifiers | _MOD_NOREPEAT,
                virtual_key,
            )
            if ok:
                self._ids.append(hotkey_id)
                log.info("注册快捷键 %s (id=%s)", name, hotkey_id)
            else:
                error = ctypes.GetLastError()
                log.error("注册快捷键 %s 失败: %s", name, error)
                self.registration_failed.emit(
                    f"快捷键 {name} 注册失败（可能被其他程序占用）"
                )

        if not self._ids:
            self._window.close()
            self._window = None

    def stop(self) -> None:
        if self._window is None:
            return
        for hotkey_id in self._ids:
            try:
                _user32.UnregisterHotKey(self._window.hwnd, hotkey_id)
            except Exception:
                pass
        self._ids.clear()
        self._window.close()
        self._window.deleteLater()
        self._window = None
