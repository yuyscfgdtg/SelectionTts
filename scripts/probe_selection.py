"""D4 probe: UIA fails on Tk, so clipboard fallback must still work.

Launches scripts/tk_target.py in a separate process (a Tk window has no UIA
TextPattern), activates it, then reads the selection.

Usage: uv run python scripts/probe_selection.py
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
from pynput.mouse import Button, Controller

from selection_tts.clipboard import read_clipboard_text
from selection_tts.foreground import get_foreground_info
from selection_tts.selection import read_selection

user32 = ctypes.windll.user32

SAMPLE = "这是一段用于验证划词复制兜底的测试文本。"
TARGET = Path(__file__).with_name("tk_target.py")


def activate_from_title_bar(hwnd: int) -> None:
    rect = wintypes.RECT()
    user32.GetWindowRect(ctypes.c_void_p(hwnd), ctypes.byref(rect))
    mouse = Controller()
    old = mouse.position
    mouse.position = ((rect.left + rect.right) // 2, rect.top + 10)
    mouse.click(Button.left, 1)
    time.sleep(0.25)
    mouse.position = old


def main() -> None:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    pyperclip.copy("ORIGINAL_CLIPBOARD")
    before = read_clipboard_text()

    env = dict(os.environ)
    env["SELECTION_TTS_SAMPLE"] = SAMPLE
    process = subprocess.Popen(
        [sys.executable, str(TARGET)],
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

        time.sleep(0.4)
        activate_from_title_bar(hwnd)
        print("foreground     :", get_foreground_info()[1:])

        started = time.perf_counter()
        result = read_selection(prefer_uia=True, wait_release_ms=0, restore=True)
        elapsed = int((time.perf_counter() - started) * 1000)
        after = read_clipboard_text()

        print("selected        :", repr(result.text))
        print("method          :", result.method)
        print("selection_ms    :", result.elapsed_ms, "(wall", elapsed, "ms)")
        print("restored        :", result.clipboard_restored)
        print("clipboard before:", repr(before))
        print("clipboard after :", repr(after))

        checks = {
            "text_matches": result.text.strip() == SAMPLE,
            "method_clipboard": result.method == "clipboard",
            "clipboard_restored": after == before,
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
