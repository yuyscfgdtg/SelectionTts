from __future__ import annotations

import ctypes
from ctypes import wintypes

_user32 = ctypes.windll.user32
_kernel32 = ctypes.windll.kernel32

_user32.GetForegroundWindow.restype = wintypes.HWND
_user32.GetWindowThreadProcessId.argtypes = [
    wintypes.HWND,
    ctypes.POINTER(wintypes.DWORD),
]
_user32.GetClassNameW.argtypes = [wintypes.HWND, ctypes.c_wchar_p, ctypes.c_int]
_user32.GetWindowTextW.argtypes = [wintypes.HWND, ctypes.c_wchar_p, ctypes.c_int]

_kernel32.OpenProcess.restype = wintypes.HANDLE
_kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
_kernel32.QueryFullProcessImageNameW.argtypes = [
    wintypes.HANDLE,
    wintypes.DWORD,
    ctypes.c_wchar_p,
    ctypes.POINTER(wintypes.DWORD),
]
_kernel32.CloseHandle.argtypes = [wintypes.HANDLE]

_PROCESS_QUERY_LIMITED_INFORMATION = 0x1000


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


def get_process_name(pid: int) -> str:
    """Return the executable name (no directory, with .exe) for a process id."""
    if pid <= 0:
        return ""
    handle = _kernel32.OpenProcess(_PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        return ""
    try:
        buffer = ctypes.create_unicode_buffer(1024)
        size = wintypes.DWORD(len(buffer))
        if not _kernel32.QueryFullProcessImageNameW(
            handle, 0, buffer, ctypes.byref(size)
        ):
            return ""
        return buffer.value.rsplit("\\", 1)[-1]
    except Exception:
        return ""
    finally:
        _kernel32.CloseHandle(handle)


def get_foreground_process_name() -> str:
    pid, _class, _title = get_foreground_info()
    return get_process_name(pid)
