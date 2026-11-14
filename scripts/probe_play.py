"""D1 probe: can pygame play the Edge TTS mp3 on this machine?"""

from __future__ import annotations

import time
from pathlib import Path

import pygame

MP3 = Path(__file__).resolve().parent.parent / "probe_zh.mp3"


def main() -> None:
    if not MP3.exists():
        raise SystemExit(f"missing {MP3}; run probe_edge_tts.py first")

    pygame.mixer.init()
    pygame.mixer.music.set_volume(0.6)
    pygame.mixer.music.load(str(MP3))
    start = time.perf_counter()
    pygame.mixer.music.play()
    print(f"playback started in {time.perf_counter() - start:.3f}s")
    while pygame.mixer.music.get_busy():
        time.sleep(0.05)
    print(f"playback finished in {time.perf_counter() - start:.3f}s")
    pygame.mixer.quit()


if __name__ == "__main__":
    main()
