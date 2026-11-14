from __future__ import annotations

import json
import threading
from datetime import datetime, timezone

from .paths import METRICS_PATH, ensure_dirs

_lock = threading.Lock()


def log_event(event: str, **fields: object) -> None:
    """Append one JSONL metric row. Never pass selected text here."""
    try:
        ensure_dirs()
        record = {
            "ts": datetime.now(timezone.utc).isoformat(timespec="milliseconds"),
            "event": event,
            **fields,
        }
        line = json.dumps(record, ensure_ascii=False, separators=(",", ":")) + "\n"
        with _lock:
            with METRICS_PATH.open("a", encoding="utf-8") as handle:
                handle.write(line)
    except Exception:
        # Metrics must never break reading.
        pass
