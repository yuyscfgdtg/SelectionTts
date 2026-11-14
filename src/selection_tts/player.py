from __future__ import annotations

import logging
import os
import threading
import time
from pathlib import Path
from typing import Callable

os.environ.setdefault("PYGAME_HIDE_SUPPORT_PROMPT", "1")
import pygame

log = logging.getLogger(__name__)

DoneCallback = Callable[[bool, Exception | None], None]


class AudioPlayer:
    """Single-track MP3 player with a stop switch and generation guard."""

    def __init__(self) -> None:
        self._stop = threading.Event()
        self._lock = threading.Lock()
        self._generation = 0
        self._thread: threading.Thread | None = None

    def play(self, path: Path, on_done: DoneCallback | None = None) -> None:
        self.stop()
        with self._lock:
            self._generation += 1
            generation = self._generation
            self._stop.clear()
        self._thread = threading.Thread(
            target=self._run,
            args=(Path(path), generation, on_done),
            daemon=True,
            name="audio-player",
        )
        self._thread.start()

    def _run(self, path: Path, generation: int, on_done: DoneCallback | None) -> None:
        ok = False
        error: Exception | None = None
        try:
            if not pygame.mixer.get_init():
                pygame.mixer.init()
            with self._lock:
                if generation != self._generation:
                    return
                pygame.mixer.music.load(str(path))
                pygame.mixer.music.set_volume(0.85)
                pygame.mixer.music.play()

            while pygame.mixer.music.get_busy():
                if self._stop.is_set():
                    pygame.mixer.music.stop()
                    break
                time.sleep(0.05)
            ok = not self._stop.is_set()
        except Exception as exc:
            error = exc
            log.exception("播放失败")
        finally:
            with self._lock:
                if generation == self._generation:
                    try:
                        pygame.mixer.music.unload()
                    except Exception:
                        pass
            if on_done is not None:
                try:
                    on_done(ok, error)
                except Exception:
                    log.exception("播放完成回调失败")

    def stop(self) -> None:
        self._stop.set()
        with self._lock:
            try:
                if pygame.mixer.get_init():
                    pygame.mixer.music.stop()
            except Exception:
                pass
