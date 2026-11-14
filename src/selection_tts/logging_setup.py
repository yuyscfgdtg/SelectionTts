from __future__ import annotations

import logging
from logging.handlers import RotatingFileHandler

from .paths import LOG_DIR, ensure_dirs


def setup_logging(level: int = logging.INFO) -> None:
    ensure_dirs()
    root = logging.getLogger()
    if getattr(root, "_selection_tts_configured", False):
        return
    root.setLevel(level)
    formatter = logging.Formatter("%(asctime)s %(levelname)s %(name)s: %(message)s")

    file_handler = RotatingFileHandler(
        LOG_DIR / "app.log",
        maxBytes=1_000_000,
        backupCount=3,
        encoding="utf-8",
    )
    file_handler.setFormatter(formatter)
    root.addHandler(file_handler)

    console_handler = logging.StreamHandler()
    console_handler.setFormatter(formatter)
    root.addHandler(console_handler)

    root._selection_tts_configured = True  # type: ignore[attr-defined]
