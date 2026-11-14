from __future__ import annotations

import ctypes
import logging
import os
import threading
import time

from PySide6.QtCore import QObject, Signal
from pynput import mouse

from .config import AppConfig
from .foreground import get_foreground_info
from .fullscreen import is_fullscreen_or_busy
from .metrics import log_event
from .selection import read_selection_via_clipboard
from .text_utils import normalize_text

log = logging.getLogger(__name__)

_user32 = ctypes.windll.user32

_SHELL_CLASSES = {
    "Progman",
    "WorkerW",
    "Shell_TrayWnd",
    "MSTaskListWClass",
    "TrayNotifyWnd",
}
_CONSOLE_CLASSES = {
    "ConsoleWindowClass",
    "CASCADIA_HOSTING_WINDOW_CLASS",
}

_MIN_DRAG_PX = 6
_MIN_DRAG_SECONDS = 0.08
_DOUBLE_CLICK_SECONDS = 0.5
_COOLDOWN_SECONDS = 0.35


class SelectionMonitor(QObject):
    selection_ready = Signal(str, int, int)
    selection_cleared = Signal()

    def __init__(self, config: AppConfig, parent: QObject | None = None) -> None:
        super().__init__(parent)
        self.config = config
        self._listener: mouse.Listener | None = None
        self._enabled = False
        self._press_pos: tuple[int, int] | None = None
        self._press_time = 0.0
        self._press_generation = 0
        self._last_release_time = 0.0
        self._click_count = 0
        self._generation = 0
        self._ignore_until = 0.0
        self._popup_rect: tuple[int, int, int, int] | None = None
        self._rect_lock = threading.Lock()

    def set_popup_rect(self, rect: tuple[int, int, int, int] | None) -> None:
        with self._rect_lock:
            self._popup_rect = rect

    def _point_in_popup(self, x: int, y: int) -> bool:
        with self._rect_lock:
            rect = self._popup_rect
        if rect is None:
            return False
        left, top, width, height = rect
        return left <= x <= left + width and top <= y <= top + height

    def set_enabled(self, enabled: bool) -> None:
        if enabled == self._enabled:
            return
        self._enabled = enabled
        if enabled:
            try:
                self._listener = mouse.Listener(on_click=self._on_click)
                self._listener.start()
                log.info("鼠标划选监听已启用")
            except Exception:
                log.exception("启动鼠标监听失败")
                self._listener = None
                self._enabled = False
        else:
            self._generation += 1
            self._stop_listener()
            self.selection_cleared.emit()
            log.info("鼠标划选监听已停用")

    def _stop_listener(self) -> None:
        if self._listener is None:
            return
        try:
            self._listener.stop()
        except Exception:
            log.exception("停止鼠标监听失败")
        self._listener = None

    def stop(self) -> None:
        self._enabled = False
        self._generation += 1
        self._stop_listener()

    def _on_click(self, x: float, y: float, button: mouse.Button, pressed: bool) -> None:
        if button != mouse.Button.left:
            return

        ix, iy = int(x), int(y)
        now = time.monotonic()

        if pressed:
            if self._point_in_popup(ix, iy):
                self._press_pos = None
                return
            self.selection_cleared.emit()
            self._press_pos = (ix, iy)
            self._press_time = now
            self._generation += 1
            self._press_generation = self._generation
            return

        if not self._enabled or self._press_pos is None:
            return
        if self._point_in_popup(ix, iy):
            return
        if now < self._ignore_until:
            return

        press_x, press_y = self._press_pos
        self._press_pos = None
        distance = ((ix - press_x) ** 2 + (iy - press_y) ** 2) ** 0.5
        duration = now - self._press_time

        if now - self._last_release_time <= _DOUBLE_CLICK_SECONDS and distance < _MIN_DRAG_PX:
            self._click_count += 1
        else:
            self._click_count = 1
        self._last_release_time = now

        shift = bool(_user32.GetAsyncKeyState(0x10) & 0x8000)
        is_drag = distance >= _MIN_DRAG_PX and duration >= _MIN_DRAG_SECONDS
        is_multi_click = self._click_count >= 2
        if not (is_drag or is_multi_click or shift):
            return

        foreground_pid, foreground_class, foreground_title = get_foreground_info()
        if foreground_pid == os.getpid():
            return
        if foreground_class in _SHELL_CLASSES or foreground_class in _CONSOLE_CLASSES:
            return
        if is_fullscreen_or_busy():
            return

        log.info(
            "检测到划选手势 class=%s title=%r distance=%.1f duration=%.2f clicks=%s",
            foreground_class,
            foreground_title,
            distance,
            duration,
            self._click_count,
        )

        self._ignore_until = now + _COOLDOWN_SECONDS
        generation = self._press_generation
        threading.Thread(
            target=self._collect,
            args=(ix, iy, generation),
            daemon=True,
            name="selection-monitor",
        ).start()

    def _collect(self, x: int, y: int, generation: int) -> None:
        try:
            delay = max(0, self.config.popup_delay_ms) / 1000
            if delay:
                time.sleep(delay)
            if generation != self._generation or not self._enabled:
                return

            result = read_selection_via_clipboard(wait_release_ms=0, restore=True)
            if generation != self._generation or not self._enabled:
                return

            text = normalize_text(result.text)
            if len(text) < max(1, self.config.min_selection_chars):
                self.selection_cleared.emit()
                return

            log_event(
                "popup",
                ok=True,
                chars=len(text),
                selection_ms=result.elapsed_ms,
                method=result.method,
                clipboard_restored=result.clipboard_restored,
            )
            self.selection_ready.emit(text, x, y)
        except Exception as exc:
            log.exception("自动取词失败")
            log_event("popup", ok=False, error=type(exc).__name__, message=str(exc)[:200])
            self.selection_cleared.emit()
