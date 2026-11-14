"""D4 probe: Ctrl+Alt+R reads a WPF selection through UIA end to end.

Requires the tray app to be running.

Usage: uv run python scripts/probe_hotkey_uia.py
"""

from __future__ import annotations

import ctypes
import json
import os
import subprocess
import sys
import time
from ctypes import wintypes
from pathlib import Path

from pynput.keyboard import Controller as KeyboardController
from pynput.keyboard import Key
from pynput.mouse import Button, Controller as MouseController

user32 = ctypes.windll.user32

SAMPLE = "热键 UIA 端到端测试：这段文字应该不经过剪贴板就被朗读出来。"
TARGET = Path(__file__).with_name("uia_target.ps1")
METRICS = os.path.join(os.environ["LOCALAPPDATA"], "SelectionTTS", "metrics.jsonl")


def window_rect(hwnd: int) -> tuple[int, int, int, int]:
    rect = wintypes.RECT()
    user32.GetWindowRect(ctypes.c_void_p(hwnd), ctypes.byref(rect))
    return rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top


def main() -> None:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    offset = os.path.getsize(METRICS) if os.path.exists(METRICS) else 0

    env = dict(os.environ)
    env["SELECTION_TTS_UIA_TEXT"] = SAMPLE

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

        time.sleep(0.5)
        left, top, width, height = window_rect(hwnd)
        mouse = MouseController()
        old = mouse.position
        mouse.position = (left + width // 2, top + height // 2)
        time.sleep(0.15)
        mouse.click(Button.left, 1)
        time.sleep(0.25)
        mouse.position = old

        keyboard = KeyboardController()
        with keyboard.pressed(Key.ctrl):
            keyboard.press("a")
            keyboard.release("a")
        time.sleep(0.3)

        with keyboard.pressed(Key.ctrl), keyboard.pressed(Key.alt):
            keyboard.press("r")
            keyboard.release("r")
        print("sent Ctrl+Alt+R")

        event = None
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            with open(METRICS, encoding="utf-8") as handle:
                handle.seek(offset)
                for line in handle:
                    if not line.strip():
                        continue
                    record = json.loads(line)
                    if record.get("event") == "read":
                        event = record
            if event is not None:
                break
            time.sleep(0.2)

        print("read event     :", event)
        checks = {
            "read_ok": bool(event and event.get("ok")),
            "method_uia": bool(event and event.get("method") == "uia"),
            "chars_match": bool(event and event.get("chars") == len(SAMPLE)),
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
