from __future__ import annotations

import ctypes
from ctypes import wintypes

_shell32 = ctypes.windll.shell32
_shell32.SHQueryUserNotificationState.argtypes = [ctypes.POINTER(wintypes.DWORD)]
_shell32.SHQueryUserNotificationState.restype = ctypes.c_long

_QUNS_BUSY = 2
_QUNS_RUNNING_D3D_FULL_SCREEN = 3
_QUNS_PRESENTATION_MODE = 4


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
