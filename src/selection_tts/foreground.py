from __future__ import annotations

import ctypes
from ctypes import wintypes

_user32 = ctypes.windll.user32


def get_foreground_info() -> tuple[int, str, str]:
    """Return (pid, window class, window title) of the foreground window."""
    hwnd = _user32.GetForegroundWindow()
    if not hwnd:
        return 0, "", ""
    pid = wintypes.DWORD()
    _user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    window_class = ctypes.create_unicode_buffer(256)
    _user32.GetClassNameW(hwnd, window_class, 256)
    title = ctypes.create_unicode_buffer(512)
    _user32.GetWindowTextW(hwnd, title, 512)
    return int(pid.value), window_class.value, title.value
