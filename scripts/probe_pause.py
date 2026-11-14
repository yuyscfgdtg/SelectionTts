"""D6 probe: a fullscreen window suppresses automatic popups.

Requires the tray app running with auto popup enabled and
`pause_in_fullscreen` on.

Usage: uv run python scripts/probe_pause.py
"""

from __future__ import annotations

import ctypes
import json
import os
import sys
import threading
import time
import tkinter as tk

from pynput.mouse import Button, Controller

from selection_tts.fullscreen import is_foreground_fullscreen

user32 = ctypes.windll.user32
user32.FindWindowW.restype = ctypes.c_void_p

METRICS = os.path.join(os.environ["LOCALAPPDATA"], "SelectionTTS", "metrics.jsonl")
POPUP_TITLE = "SelectionTTS Popup"
SAMPLE = "全屏窗口里的这一段文字不应该弹出朗读按钮。"


def metrics_offset() -> int:
    try:
        return os.path.getsize(METRICS)
    except OSError:
        return 0


def new_popup_events(offset: int) -> list[dict]:
    try:
        with open(METRICS, encoding="utf-8") as handle:
            handle.seek(offset)
            events = [json.loads(line) for line in handle if line.strip()]
    except OSError:
        return []
    return [item for item in events if item.get("event") == "popup"]


def popup_visible() -> bool:
    hwnd = int(user32.FindWindowW(None, POPUP_TITLE) or 0)
    return bool(hwnd and user32.IsWindowVisible(ctypes.c_void_p(hwnd)))


def main() -> None:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    offset = metrics_offset()

    root = tk.Tk()
    root.title("SelectionTTS pause probe")
    root.attributes("-topmost", True)
    text = tk.Text(root, height=8, wrap="word")
    text.pack(fill="both", expand=True)
    text.insert("1.0", SAMPLE)
    root.attributes("-fullscreen", True)
    root.lift()
    root.update()

    mouse = Controller()
    old_position = mouse.position
    mouse.position = (root.winfo_screenwidth() // 2, root.winfo_screenheight() // 2)
    mouse.click(Button.left, 1)
    deadline = time.monotonic() + 2.0
    while time.monotonic() < deadline:
        if is_foreground_fullscreen():
            break
        time.sleep(0.2)
        mouse.click(Button.left, 1)
    time.sleep(0.2)
    from ctypes import wintypes

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
    )
    print("  tk geometry:", root.winfo_width(), "x", root.winfo_height())
    checks: dict[str, bool] = {"foreground_fullscreen": is_foreground_fullscreen()}

    def drag_select() -> None:
        x1 = text.winfo_rootx() + 24
        y = text.winfo_rooty() + 24
        x2 = x1 + 420
        mouse.position = (x1, y)
        mouse.press(Button.left)
        for step in range(1, 15):
            mouse.position = (x1 + int((x2 - x1) * step / 14), y)
            time.sleep(0.02)
        mouse.release(Button.left)
        time.sleep(0.4)
        checks["popup_absent"] = not popup_visible()
        checks["no_popup_event"] = not new_popup_events(offset)

    threading.Thread(target=drag_select, daemon=True).start()
    root.after(3500, root.quit)
    root.mainloop()

    mouse.position = old_position
    root.destroy()

    for name, ok in checks.items():
        print(f"  {name}: {'PASS' if ok else 'FAIL'}")
    passed = all(checks.values())
    print("PASS" if passed else "FAIL")
    raise SystemExit(0 if passed else 1)


if __name__ == "__main__":
    main()
