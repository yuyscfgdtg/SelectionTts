"""D2 end-to-end probe: put a selection on screen, press Ctrl+Alt+R, stay alive."""

from __future__ import annotations

import time
import tkinter as tk

import pyperclip
from pynput.keyboard import Controller as KeyboardController
from pynput.keyboard import Key
from pynput.mouse import Button, Controller as MouseController

from selection_tts.clipboard import read_clipboard_text

SAMPLE = "端到端测试：这是划词朗读的第三段验证文本，请确认是否能听到声音。"


def main() -> None:
    pyperclip.copy("ORIGINAL_CLIPBOARD")

    root = tk.Tk()
    root.title("Selection TTS - hotkey probe")
    root.geometry("700x180+220+220")
    root.attributes("-topmost", True)

    text = tk.Text(root, height=4, wrap="word")
    text.pack(fill="both", expand=True, padx=8, pady=8)
    text.insert("1.0", SAMPLE)
    root.update()

    mouse = MouseController()
    old_position = mouse.position
    mouse.position = (
        text.winfo_rootx() + text.winfo_width() // 2,
        text.winfo_rooty() + text.winfo_height() // 2,
    )
    mouse.click(Button.left, 1)
    time.sleep(0.25)
    mouse.position = old_position
    root.update()

    text.focus_force()
    text.tag_add("sel", "1.0", "end-1c")
    root.update()
    time.sleep(0.25)

    print("sample utf8:", SAMPLE.encode("utf-8").hex())
    print("clipboard before:", repr(read_clipboard_text()))

    keyboard = KeyboardController()
    keyboard.press(Key.ctrl)
    keyboard.press(Key.alt)
    keyboard.press("r")
    keyboard.release("r")
    keyboard.release(Key.alt)
    keyboard.release(Key.ctrl)
    print("sent Ctrl+Alt+R")

    def finish() -> None:
        print("clipboard after :", repr(read_clipboard_text()))
        print("clipboard restored:", read_clipboard_text() == "ORIGINAL_CLIPBOARD")
        root.destroy()

    root.after(15000, finish)
    root.mainloop()


if __name__ == "__main__":
    main()
