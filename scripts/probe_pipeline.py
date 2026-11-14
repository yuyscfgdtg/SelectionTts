"""D2 probe: synthesis -> playback pipeline without the tray UI."""

from __future__ import annotations

import threading
import time

from selection_tts.config import AppConfig
from selection_tts.paths import CACHE_DIR, ensure_dirs
from selection_tts.player import AudioPlayer
from selection_tts.tts import EdgeTtsEngine

TEXT = "流水线测试：这是划词朗读的第二段验证文本。"


def main() -> None:
    ensure_dirs()
    config = AppConfig()
    engine = EdgeTtsEngine()
    path = CACHE_DIR / "probe_pipeline.mp3"

    started = time.perf_counter()
    result = engine.synthesize(TEXT, config.voice, config.rate, path, timeout=25)
    print(f"synthesis: {result.elapsed_ms} ms, attempts={result.attempts}, bytes={path.stat().st_size}")

    done = threading.Event()
    player = AudioPlayer()

    def on_done(ok: bool, error: Exception | None) -> None:
        print(f"playback done: ok={ok} error={error}")
        done.set()

    player.play(path, on_done=on_done)
    if not done.wait(timeout=30):
        print("playback TIMEOUT")
        player.stop()
    print(f"total: {int((time.perf_counter() - started) * 1000)} ms")
    path.unlink(missing_ok=True)


if __name__ == "__main__":
    main()
