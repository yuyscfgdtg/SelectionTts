"""D2 probe: verify Ctrl+C selection capture and clipboard restore with Tk.

The Tk event loop must stay alive while Ctrl+C is synthesized, otherwise Tk
never processes the key event. This is why the read runs in a worker thread.
"""

from __future__ import annotations

import threading
import time
import tkinter as tk

import pyperclip
from pynput.mouse import Button, Controller

from selection_tts.clipboard import read_clipboard_text
from selection_tts.selection import SelectionResult, read_selection_via_clipboard

SAMPLE = "这是一段用于验证划词复制的测试文本。"


def main() -> None:
    pyperclip.copy("ORIGINAL_CLIPBOARD")

    root = tk.Tk()
    root.title("Selection TTS - clipboard probe")
    root.geometry("560x180+240+240")
    root.attributes("-topmost", True)

    text = tk.Text(root, height=4, wrap="word")
    text.pack(fill="both", expand=True, padx=8, pady=8)
    text.insert("1.0", SAMPLE)
    root.update()

    # Bring the Tk window to the real foreground so the synthetic Ctrl+C is
    # delivered here instead of to the terminal that launched the probe.
    mouse = Controller()
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

    before = read_clipboard_text()
    holder: dict[str, SelectionResult] = {}

    def worker() -> None:
        holder["result"] = read_selection_via_clipboard(
            wait_release_ms=0,
            restore=True,
        )

    def poll() -> None:
        if "result" in holder:
            finish()
        else:
            root.after(50, poll)

    def finish() -> None:
        result = holder["result"]
        after = read_clipboard_text()
        print("selected        :", repr(result.text))
        print("selected hex    :", result.text.encode("utf-8").hex())
        print("sample hex      :", SAMPLE.encode("utf-8").hex())
        print("lengths         :", len(result.text), len(SAMPLE))
        print("method          :", result.method)
        print("selection_ms    :", result.elapsed_ms)
        print("restored        :", result.clipboard_restored)
        print("clipboard before:", repr(before))
        print("clipboard after :", repr(after))
        print("clipboard equal :", after == before)
        print("text equal      :", result.text.strip() == SAMPLE)
        passed = result.text.strip() == SAMPLE and after == before
        print("PASS" if passed else "FAIL")
        root.destroy()
        if not passed:
            raise SystemExit(1)

    threading.Thread(target=worker, daemon=True).start()
    root.after(50, poll)
    root.after(8000, lambda: (print("TIMEOUT"), root.destroy()))
    root.mainloop()


if __name__ == "__main__":
    main()
