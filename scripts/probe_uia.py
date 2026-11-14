"""D4 probe: read a WPF text selection through UI Automation (no clipboard).

Launches scripts/uia_target.ps1 (a WPF TextBox, which implements ITextProvider),
selects all text with Ctrl+A, then asks selection_tts to read the selection.

Usage: uv run python scripts/probe_uia.py
"""

from __future__ import annotations

import ctypes
import os
import subprocess
import sys
import time
from ctypes import wintypes
from pathlib import Path

import pyperclip
from pynput.keyboard import Controller as KeyboardController
from pynput.keyboard import Key
from pynput.mouse import Button, Controller as MouseController

from selection_tts.clipboard import read_clipboard_text
from selection_tts.selection import read_selection

user32 = ctypes.windll.user32

SAMPLE = "UIA 取词测试：这是 WPF 文本框里的一段中文文本，用来验证不经过剪贴板也能读取选区。"
TARGET = Path(__file__).with_name("uia_target.ps1")


def window_rect(hwnd: int) -> tuple[int, int, int, int]:
    rect = wintypes.RECT()
    user32.GetWindowRect(ctypes.c_void_p(hwnd), ctypes.byref(rect))
    return rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top


def click_window(hwnd: int) -> None:
    left, top, width, height = window_rect(hwnd)
    mouse = MouseController()
    old = mouse.position
    mouse.position = (left + width // 2, top + height // 2)
    mouse.click(Button.left, 1)
    time.sleep(0.25)
    mouse.position = old


def select_all() -> None:
    keyboard = KeyboardController()
    with keyboard.pressed(Key.ctrl):
        keyboard.press("a")
        keyboard.release("a")


def main() -> None:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    env = dict(os.environ)
    env["SELECTION_TTS_UIA_TEXT"] = SAMPLE

    pyperclip.copy("ORIGINAL_CLIPBOARD")
    clipboard_before = read_clipboard_text()

    process = subprocess.Popen(
        [
            "powershell.exe",
            "-NoProfile",
            "-STA",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            str(TARGET),
        ],
        stdout=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        env=env,
    )
    try:
        line = process.stdout.readline().strip() if process.stdout else ""
        if not line.isdigit():
            raise SystemExit(f"FAIL: target window did not start ({line!r})")
        hwnd = int(line)
        print("window         : SelectionTTS UIA target", hex(hwnd))

        time.sleep(0.5)
        click_window(hwnd)
        select_all()
        time.sleep(0.4)

        started = time.perf_counter()
        result = read_selection(prefer_uia=True, wait_release_ms=0)
        elapsed = int((time.perf_counter() - started) * 1000)
        clipboard_after = read_clipboard_text()

        print("method         :", result.method)
        print("chars          :", len(result.text))
        print("text           :", repr(result.text[:60]))
        print("elapsed        :", elapsed, "ms (result:", result.elapsed_ms, "ms)")
        print("clipboard same :", clipboard_after == clipboard_before)

        checks = {
            "method_uia": result.method == "uia",
            "text_matches": result.text.strip() == SAMPLE,
            "clipboard_untouched": clipboard_after == clipboard_before,
            "fast": elapsed < 1500,
        }
        for name, ok in checks.items():
            print(f"  {name}: {'PASS' if ok else 'FAIL'}")
        passed = all(checks.values())
        print("PASS" if passed else "FAIL")
        raise SystemExit(0 if passed else 1)
    finally:
        process.terminate()
        try:
            process.wait(timeout=5)
        except Exception:
            process.kill()


if __name__ == "__main__":
    main()
