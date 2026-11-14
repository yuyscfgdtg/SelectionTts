"""D3 probe: drag-select in a Tk window, expect a no-focus popup, click 朗读.

Requires the tray app to be running with auto popup enabled.

The probe verifies:
1. a drag selection is detected and the popup window appears;
2. the popup does not steal foreground focus from the source window;
3. metrics.jsonl records a `popup` event;
4. clicking 朗读 records a `read` event with method="popup".

Usage: uv run python scripts/probe_popup.py
"""

from __future__ import annotations

import ctypes
import json
import os
import threading
import time
import tkinter as tk
from ctypes import wintypes

from pynput.mouse import Button, Controller

user32 = ctypes.windll.user32
user32.FindWindowW.restype = ctypes.c_void_p
user32.GetForegroundWindow.restype = ctypes.c_void_p
user32.GetWindowTextW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_int]

METRICS = os.path.join(os.environ["LOCALAPPDATA"], "SelectionTTS", "metrics.jsonl")
POPUP_TITLE = "SelectionTTS Popup"
SOURCE_TITLE = "SelectionTTS - popup probe"
SAMPLE = "这是一段用于验证自动弹窗的测试文本。拖动鼠标选择这一行，应该出现朗读按钮。"


def metrics_offset() -> int:
    try:
        return os.path.getsize(METRICS)
    except OSError:
        return 0


def read_new_metrics(offset: int) -> list[dict]:
    try:
        with open(METRICS, encoding="utf-8") as handle:
            handle.seek(offset)
            return [json.loads(line) for line in handle if line.strip()]
    except OSError:
        return []


def find_popup() -> int:
    hwnd = int(user32.FindWindowW(None, POPUP_TITLE) or 0)
    if hwnd and user32.IsWindowVisible(ctypes.c_void_p(hwnd)):
        return hwnd
    return 0


def window_rect(hwnd: int) -> tuple[int, int, int, int]:
    rect = wintypes.RECT()
    user32.GetWindowRect(ctypes.c_void_p(hwnd), ctypes.byref(rect))
    return rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top


def foreground_title() -> str:
    hwnd = user32.GetForegroundWindow()
    if not hwnd:
        return ""
    buffer = ctypes.create_unicode_buffer(512)
    user32.GetWindowTextW(hwnd, buffer, 512)
    return buffer.value


def main() -> None:
    offset = metrics_offset()
    start = time.monotonic()

    root = tk.Tk()
    root.title(SOURCE_TITLE)
    root.geometry("760x220+320+260")
    root.attributes("-topmost", True)

    text = tk.Text(root, height=6, wrap="word")
    text.pack(fill="both", expand=True, padx=8, pady=8)
    text.insert("1.0", SAMPLE)
    root.update()

    mouse = Controller()
    old_position = mouse.position
    mouse.position = (
        text.winfo_rootx() + text.winfo_width() // 2,
        text.winfo_rooty() + text.winfo_height() // 2,
    )
    mouse.click(Button.left, 1)
    time.sleep(0.25)
    root.update()

    state: dict[str, object] = {
        "popup": 0,
        "focus_stolen": False,
        "popup_event": None,
        "read_event": None,
        "saw_popup": time.monotonic(),
        "clicked": False,
    }

    def drag_select() -> None:
        """Drag across the first line so Tk selects text."""
        x1 = text.winfo_rootx() + 16
        y = text.winfo_rooty() + 14
        x2 = text.winfo_rootx() + min(560, text.winfo_width() - 24)
        mouse.position = (x1, y)
        time.sleep(0.05)
        mouse.press(Button.left)
        steps = 14
        for step in range(1, steps + 1):
            mouse.position = (x1 + int((x2 - x1) * step / steps), y)
            time.sleep(0.02)
        mouse.release(Button.left)

    def click_read_button() -> None:
        hwnd = state["popup"]
        if not hwnd:
            return
        left, top, width, height = window_rect(int(hwnd))
        # 朗读 button sits on the left of the card.
        point = (left + min(48, max(24, width // 4)), top + height // 2)
        print(f"[{time.monotonic() - start:.2f}s] click popup at {point} rect=", (left, top, width, height))
        mouse.position = point
        time.sleep(0.08)
        mouse.click(Button.left, 1)
        state["clicked"] = True

    def finish() -> None:
        mouse.position = old_position
        root.destroy()

    def tick() -> None:
        elapsed = time.monotonic() - float(state["saw_popup"])

        if not state["popup"]:
            hwnd = find_popup()
            if hwnd:
                state["popup"] = hwnd
                print(f"[{time.monotonic() - start:.2f}s] popup found")
                # Give the popup a moment to settle, then verify focus.
                root.after(150, check_focus)

        new_events = read_new_metrics(offset)
        for event in new_events:
            if event.get("event") == "popup" and state["popup_event"] is None:
                state["popup_event"] = event
            if (
                event.get("event") == "read"
                and event.get("method") == "popup"
                and state["read_event"] is None
            ):
                state["read_event"] = event

        if state["popup"] and state["popup_event"] and not state["clicked"]:
            click_read_button()

        if state["read_event"] is not None:
            report()
            return
        if elapsed > 20:
            report()
            return
        root.after(100, tick)

    def check_focus() -> None:
        if foreground_title() != SOURCE_TITLE:
            state["focus_stolen"] = True
            state["focus_after"] = foreground_title()

    def report() -> None:
        popup_event = state["popup_event"]
        read_event = state["read_event"]
        checks = {
            "popup_window_visible": bool(state["popup"]),
            "popup_metrics_event": bool(popup_event),
            "focus_not_stolen": not state["focus_stolen"],
            "read_event_method_popup": bool(read_event),
            "read_ok": bool(read_event and read_event.get("ok")),
        }
        print("popup window      :", hex(int(state["popup"])) if state["popup"] else "not found")
        print("popup metrics     :", popup_event)
        print("read metrics      :", read_event)
        print("focus stolen      :", state["focus_stolen"], state.get("focus_after", ""))
        for name, ok in checks.items():
            print(f"  {name}: {'PASS' if ok else 'FAIL'}")
        passed = all(checks.values())
        print("PASS" if passed else "FAIL")
        finish()

    threading.Thread(target=drag_select, daemon=True).start()
    root.after(100, tick)
    root.after(25000, lambda: (print("TIMEOUT"), finish()))
    root.mainloop()


if __name__ == "__main__":
    main()
