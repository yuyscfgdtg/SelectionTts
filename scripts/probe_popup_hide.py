"""D3 probe: the popup hides itself after popup_timeout_ms.

Runs the popup directly (no tray app, no mouse hook), so it is unaffected by
the auto-popup toggle state.

Usage: uv run python scripts/probe_popup_hide.py
"""

from __future__ import annotations

import ctypes
import time

from PySide6.QtCore import QTimer
from PySide6.QtWidgets import QApplication

from selection_tts.config import AppConfig
from selection_tts.popup import SelectionPopup

user32 = ctypes.windll.user32


def is_visible(popup: SelectionPopup) -> bool:
    return bool(user32.IsWindowVisible(ctypes.c_void_p(int(popup.winId()))))


def main() -> None:
    app = QApplication([])
    config = AppConfig()
    config.popup_timeout_ms = 1200
    popup = SelectionPopup(config)

    popup.show_for("自动隐藏测试文本。", 400, 400)
    started = time.monotonic()
    print("shown visible:", is_visible(popup))

    def check() -> None:
        elapsed = time.monotonic() - started
        visible = is_visible(popup)
        print(f"after {elapsed:.2f}s visible:", visible)
        passed = elapsed > 1.2 and not visible
        print("PASS" if passed else "FAIL")
        popup.hide()
        app.exit(0 if passed else 1)

    QTimer.singleShot(2000, check)
    raise SystemExit(app.exec())


if __name__ == "__main__":
    main()
