"""D6 probe: fullscreen detection and process exclusion.

Creates a borderless fullscreen window, checks that the guard reports
fullscreen, then checks the exclusion matcher against the current process.

Usage: uv run python scripts/probe_guard.py
"""

from __future__ import annotations

import os
import sys
import time
import tkinter as tk

from pynput.mouse import Button, Controller

from selection_tts.config import AppConfig, _sanitize
from selection_tts.foreground import get_process_name
from selection_tts.fullscreen import (
    is_foreground_fullscreen,
    is_fullscreen_now,
    is_fullscreen_or_busy,
)
from selection_tts.selection_monitor import SelectionMonitor


def main() -> None:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    checks: dict[str, bool] = {}

    root = tk.Tk()
    root.title("SelectionTTS guard target")
    root.geometry("520x160+260+260")
    root.update()
    checks["windowed_not_fullscreen"] = not is_foreground_fullscreen()

    root.attributes("-fullscreen", True)
    root.attributes("-topmost", True)
    root.lift()
    root.update()
    # A window started from a background process is raised but not activated;
    # click it so it really becomes the foreground window.
    mouse = Controller()
    mouse.position = (root.winfo_screenwidth() // 2, root.winfo_screenheight() // 2)
    mouse.click(Button.left, 1)
    time.sleep(0.4)
    import ctypes
    from ctypes import wintypes

    user32 = ctypes.windll.user32
    hwnd = user32.GetForegroundWindow()
    klass = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(hwnd, klass, 256)
    title = ctypes.create_unicode_buffer(512)
    user32.GetWindowTextW(hwnd, title, 512)
    rect = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    print(
        "  foreground:",
        hex(int(hwnd)),
        klass.value,
        repr(title.value),
        (rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top),
        "visible",
        bool(user32.IsWindowVisible(hwnd)),
        "iconic",
        bool(user32.IsIconic(hwnd)),
    )
    checks["tk_fullscreen_detected"] = is_foreground_fullscreen()
    checks["combined_fullscreen"] = is_fullscreen_now()
    print(
        "  tk fullscreen: geometry",
        root.winfo_width(),
        "x",
        root.winfo_height(),
        "| shquery:",
        is_fullscreen_or_busy(),
    )

    root.attributes("-fullscreen", False)
    root.update()
    checks["restored_not_fullscreen"] = not is_foreground_fullscreen()
    root.destroy()

    process_name = get_process_name(os.getpid())
    print("  current process:", process_name)
    checks["process_name_found"] = process_name.lower().endswith(".exe")

    config = _sanitize(AppConfig(excluded_processes=[process_name.replace(".exe", "")]))
    monitor = SelectionMonitor(config)
    checks["exclusion_matches"] = monitor._pause_reason(os.getpid()) == "excluded"

    other = _sanitize(AppConfig(excluded_processes=["definitely-not-running.exe"]))
    monitor_other = SelectionMonitor(other)
    checks["exclusion_ignores_others"] = (
        monitor_other._pause_reason(os.getpid()) == ""
    )

    for name, ok in checks.items():
        print(f"  {name}: {'PASS' if ok else 'FAIL'}")
    passed = all(checks.values())
    print("PASS" if passed else "FAIL")
    raise SystemExit(0 if passed else 1)


if __name__ == "__main__":
    main()
