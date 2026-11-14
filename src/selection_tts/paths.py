from __future__ import annotations

import os
from pathlib import Path

APP_NAME = "SelectionTTS"


def _root(env_name: str) -> Path:
    base = os.environ.get(env_name)
    if base:
        return Path(base) / APP_NAME
    return Path.home() / f".{APP_NAME.lower()}"


APP_DATA_DIR = _root("APPDATA")
LOCAL_DATA_DIR = _root("LOCALAPPDATA")
CONFIG_PATH = APP_DATA_DIR / "config.json"
LOG_DIR = LOCAL_DATA_DIR / "logs"
CACHE_DIR = LOCAL_DATA_DIR / "cache"
METRICS_PATH = LOCAL_DATA_DIR / "metrics.jsonl"


def ensure_dirs() -> None:
    for path in (APP_DATA_DIR, LOG_DIR, CACHE_DIR):
        path.mkdir(parents=True, exist_ok=True)
