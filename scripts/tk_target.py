"""Tk window used as a clipboard-fallback target (no UIA TextPattern).

Prints the top-level window handle, selects all sample text, and stays alive
so it can process a synthetic Ctrl+C.

Test helper, not part of the product.
"""

from __future__ import annotations

import os
import sys
import tkinter as tk


def main() -> None:
    sample = os.environ.get("SELECTION_TTS_SAMPLE", "Tk 剪贴板兜底测试文本。")
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    root = tk.Tk()
    root.title("SelectionTTS clipboard target")
    root.geometry("640x200+300+260")
    root.attributes("-topmost", True)

    text = tk.Text(root, height=5, wrap="word")
    text.pack(fill="both", expand=True, padx=8, pady=8)
    text.insert("1.0", sample)
    root.update()
    text.focus_force()
    text.tag_add("sel", "1.0", "end-1c")
    root.update()

    print(int(root.wm_frame(), 16), flush=True)
    root.mainloop()


if __name__ == "__main__":
    main()
