from __future__ import annotations

import ctypes
from ctypes import wintypes

_user32 = ctypes.windll.user32
_shell32 = ctypes.windll.shell32


class MONITORINFO(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.DWORD),
        ("rcMonitor", wintypes.RECT),
        ("rcWork", wintypes.RECT),
        ("dwFlags", wintypes.DWORD),
    ]


_shell32.SHQueryUserNotificationState.argtypes = [ctypes.POINTER(wintypes.DWORD)]
_shell32.SHQueryUserNotificationState.restype = ctypes.c_long

_user32.GetForegroundWindow.restype = wintypes.HWND
_user32.GetClassNameW.argtypes = [wintypes.HWND, ctypes.c_wchar_p, ctypes.c_int]
_user32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
_user32.MonitorFromWindow.restype = wintypes.HANDLE
_user32.MonitorFromWindow.argtypes = [wintypes.HWND, wintypes.DWORD]
_user32.GetMonitorInfoW.argtypes = [wintypes.HANDLE, ctypes.POINTER(MONITORINFO)]
_user32.GetMonitorInfoW.restype = wintypes.BOOL
_user32.IsWindowVisible.argtypes = [wintypes.HWND]
_user32.IsIconic.argtypes = [wintypes.HWND]

_QUNS_BUSY = 2
_QUNS_RUNNING_D3D_FULL_SCREEN = 3
_QUNS_PRESENTATION_MODE = 4

_MONITOR_DEFAULTTONEAREST = 2
_EDGE_TOLERANCE_PX = 2

_SHELL_CLASSES = {
    "Progman",
    "WorkerW",
    "Shell_TrayWnd",
}


def is_fullscreen_or_busy() -> bool:
    """Best-effort check for games, fullscreen video and presentation mode."""
    state = wintypes.DWORD()
    try:
        result = _shell32.SHQueryUserNotificationState(ctypes.byref(state))
    except Exception:
        return False
    return result == 0 and state.value in {
        _QUNS_BUSY,
        _QUNS_RUNNING_D3D_FULL_SCREEN,
        _QUNS_PRESENTATION_MODE,
    }


def _window_class(hwnd: int) -> str:
    buffer = ctypes.create_unicode_buffer(256)
    _user32.GetClassNameW(ctypes.c_void_p(hwnd), buffer, 256)
    return buffer.value


def is_foreground_fullscreen() -> bool:
    """True when the foreground window covers its whole monitor.

    Covers borderless fullscreen windows (most games and video players) that
    SHQueryUserNotificationState does not report as fullscreen.
    """
    hwnd = _user32.GetForegroundWindow()
    if not hwnd:
        return False
    if not _user32.IsWindowVisible(hwnd) or _user32.IsIconic(hwnd):
        return False
    if _window_class(int(hwnd)) in _SHELL_CLASSES:
        return False

    rect = wintypes.RECT()
    if not _user32.GetWindowRect(hwnd, ctypes.byref(rect)):
        return False
    width = rect.right - rect.left
    height = rect.bottom - rect.top
    if width <= 0 or height <= 0:
        return False

    monitor = _user32.MonitorFromWindow(hwnd, _MONITOR_DEFAULTTONEAREST)
    if not monitor:
        return False
    info = MONITORINFO()
    info.cbSize = ctypes.sizeof(MONITORINFO)
    if not _user32.GetMonitorInfoW(monitor, ctypes.byref(info)):
        return False

    monitor_rect = info.rcMonitor
    return (
        abs(rect.left - monitor_rect.left) <= _EDGE_TOLERANCE_PX
        and abs(rect.top - monitor_rect.top) <= _EDGE_TOLERANCE_PX
        and abs(rect.right - monitor_rect.right) <= _EDGE_TOLERANCE_PX
        and abs(rect.bottom - monitor_rect.bottom) <= _EDGE_TOLERANCE_PX
    )


def is_fullscreen_now() -> bool:
    """Combined fullscreen check used to pause automatic popups."""
    return is_fullscreen_or_busy() or is_foreground_fullscreen()
