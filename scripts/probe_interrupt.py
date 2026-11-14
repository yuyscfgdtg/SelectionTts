"""D6+ probe: a new read request interrupts the one in progress.

Uses the real Edge TTS engine but a silent fake player, so the preemption
logic can be checked without sound or mouse input.

Usage: uv run python scripts/probe_interrupt.py
"""

from __future__ import annotations

import threading
import time
from pathlib import Path

from PySide6.QtCore import QCoreApplication, Qt

from selection_tts.config import load_config
from selection_tts.reader import ReaderService

FIRST = "第一段测试文本，用来验证新的朗读请求会打断当前朗读。"
SECOND = "第二段测试文本，应该在旧音频停止后立即开始。"


class FakePlayer:
    """Stands in for AudioPlayer; records calls and finishes silently."""

    def __init__(self) -> None:
        self.plays: list[Path] = []
        self.stops = 0
        self._lock = threading.Lock()

    def stop(self) -> None:
        with self._lock:
            self.stops += 1

    def play(self, path: Path, on_done=None) -> None:  # type: ignore[no-untyped-def]
        with self._lock:
            self.plays.append(path)
            index = len(self.plays)
        print(f"  play #{index}: {path.name}")
        if on_done is not None:
            def finish() -> None:
                time.sleep(1.5)
                on_done(True, None)

            threading.Thread(target=finish, daemon=True).start()


def main() -> None:
    app = QCoreApplication([])
    config = load_config()
    reader = ReaderService(config)
    fake = FakePlayer()
    reader._player = fake  # type: ignore[assignment]

    statuses: list[str] = []
    reader.status.connect(
        lambda message: statuses.append(message),
        Qt.ConnectionType.DirectConnection,
    )

    reader.read_text(FIRST)
    deadline = time.monotonic() + 15
    while "开始朗读" not in statuses and time.monotonic() < deadline:
        app.processEvents()
        time.sleep(0.05)

    first_started = "开始朗读" in statuses
    print("  first started:", first_started)
    reader.read_text(SECOND)

    deadline = time.monotonic() + 20
    while statuses.count("开始朗读") < 2 and time.monotonic() < deadline:
        app.processEvents()
        time.sleep(0.05)
    time.sleep(2.0)
    app.processEvents()

    checks = {
        "first_started": first_started,
        "second_started": statuses.count("开始朗读") >= 2,
        "two_plays": len(fake.plays) == 2,
        "player_stopped": fake.stops >= 2,
        "second_won": len(fake.plays) == 2 and fake.plays[-1].name != fake.plays[0].name,
        "finished_once": statuses.count("朗读完成") == 1,
    }
    print("  statuses:", statuses)
    for name, ok in checks.items():
        print(f"  {name}: {'PASS' if ok else 'FAIL'}")
    passed = all(checks.values())
    print("PASS" if passed else "FAIL")
    reader.stop()
    raise SystemExit(0 if passed else 1)


if __name__ == "__main__":
    main()
