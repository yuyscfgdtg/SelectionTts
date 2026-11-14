"""D2 probe: toggle auto-popup with Ctrl+Alt+Shift+P and check config."""

from __future__ import annotations

import json
import os
import threading
import time
import tkinter as tk

from pynput.keyboard import Controller, Key
from pynput.mouse import Button, Controller as MouseController

CONFIG = os.path.join(os.environ["APPDATA"], "SelectionTTS", "config.json")


def read_auto_popup() -> object:
    with open(CONFIG, encoding="utf-8") as handle:
        return json.load(handle).get("auto_popup")


def send_toggle() -> None:
    kb = Controller()
    kb.press(Key.ctrl)
    kb.press(Key.alt)
    kb.press(Key.shift)
    time.sleep(0.1)
    kb.press("p")
    time.sleep(0.1)
    kb.release("p")
    kb.release(Key.shift)
    kb.release(Key.alt)
    kb.release(Key.ctrl)


def main() -> None:
    root = tk.Tk()
    root.title("TTS toggle probe")
    root.geometry("480x120+320+320")
    root.attributes("-topmost", True)
    label = tk.Label(root, text="toggle probe - keep this window focused")
    label.pack(fill="both", expand=True)
    root.update()

    mouse = MouseController()
    old = mouse.position
    mouse.position = (root.winfo_rootx() + 100, root.winfo_rooty() + 60)
    mouse.click(Button.left, 1)
    time.sleep(0.2)
    mouse.position = old
    root.update()

    results: list[object] = []

    def worker() -> None:
        time.sleep(0.4)
        send_toggle()
        time.sleep(1.0)
        results.append(read_auto_popup())
        send_toggle()
        time.sleep(1.0)
        results.append(read_auto_popup())
        root.after(0, root.destroy)

    threading.Thread(target=worker, daemon=True).start()
    root.after(6000, root.destroy)
    root.mainloop()

    print("auto_popup sequence:", results)
    print("PASS" if results == [False, True] else "FAIL")


if __name__ == "__main__":
    main()
