from __future__ import annotations

import logging
import threading
from pathlib import Path
from uuid import uuid4

from PySide6.QtCore import QObject, Signal

from .config import AppConfig
from .metrics import log_event
from .paths import CACHE_DIR, ensure_dirs
from .player import AudioPlayer
from .selection import read_selection
from .text_utils import normalize_text, truncate_text
from .tts import EdgeTtsEngine

log = logging.getLogger(__name__)


class ReaderService(QObject):
    status = Signal(str)
    error = Signal(str)

    def __init__(self, config: AppConfig, parent: QObject | None = None) -> None:
        super().__init__(parent)
        self.config = config
        self._player = AudioPlayer()
        self._engine = EdgeTtsEngine(proxy=config.edge_proxy)
        self._lock = threading.Lock()
        self._request_id = 0
        self._thread: threading.Thread | None = None

    @property
    def is_busy(self) -> bool:
        thread = self._thread
        return thread is not None and thread.is_alive()

    def read_selection(self, wait_release_ms: int = 250) -> None:
        self._start(wait_release_ms=wait_release_ms, preloaded_text=None, source="clipboard")

    def read_text(self, text: str) -> None:
        """Read text already captured by the selection popup."""
        self._start(wait_release_ms=0, preloaded_text=text, source="popup")

    def _start(self, wait_release_ms: int, preloaded_text: str | None, source: str) -> None:
        # A new request always wins: silence the current audio immediately, then
        # let the older worker notice it is stale and clean up after itself.
        with self._lock:
            self._request_id += 1
            generation = self._request_id
            self._player.stop()
            if self._thread is not None and self._thread.is_alive():
                log.info("新的朗读请求，打断上一次朗读")
        self._thread = threading.Thread(
            target=self._worker,
            args=(wait_release_ms, preloaded_text, source, generation),
            daemon=True,
            name="reader",
        )
        self._thread.start()

    def stop(self) -> None:
        with self._lock:
            self._request_id += 1
            self._player.stop()

    def _is_stale(self, generation: int) -> bool:
        with self._lock:
            return generation != self._request_id

    def _worker(
        self,
        wait_release_ms: int,
        preloaded_text: str | None,
        source: str,
        generation: int,
    ) -> None:
        handed_off = False
        temp_path: Path | None = None
        try:
            if preloaded_text is None:
                self.status.emit("正在读取选中文本…")
                selection = read_selection(
                    prefer_uia=self.config.use_uia,
                    wait_release_ms=wait_release_ms,
                    restore=self.config.clipboard_restore,
                )
                if self._is_stale(generation):
                    return
                raw_text = selection.text
                method = selection.method
                selection_ms = selection.elapsed_ms
                clipboard_restored = selection.clipboard_restored
            else:
                raw_text = preloaded_text
                method = source
                selection_ms = 0
                clipboard_restored = None

            text, truncated = truncate_text(
                normalize_text(raw_text),
                self.config.max_chars,
            )
            if not text:
                if self._is_stale(generation):
                    return
                self.status.emit("没有检测到选中文本")
                log_event(
                    "selection",
                    ok=False,
                    method=method,
                    chars=0,
                    selection_ms=selection_ms,
                )
                return

            if truncated:
                self.status.emit(f"文本较长，仅朗读前 {self.config.max_chars} 字")

            ensure_dirs()
            temp_path = CACHE_DIR / f"read_{uuid4().hex}.mp3"
            self.status.emit(f"正在合成（{len(text)} 字）…")
            synthesis = self._engine.synthesize(
                text,
                self.config.voice,
                self.config.rate,
                temp_path,
            )
            if self._is_stale(generation):
                return

            self.status.emit("开始朗读")
            # Check and hand off under the same lock used by _start, so a newer
            # request cannot slip in between the staleness check and playback.
            with self._lock:
                if generation != self._request_id:
                    return
                self._player.play(
                    temp_path,
                    on_done=lambda ok, err: self._on_done(
                        temp_path, ok, err, generation
                    ),
                )
            handed_off = True

            log_event(
                "read",
                ok=True,
                method=method,
                chars=len(text),
                truncated=truncated,
                selection_ms=selection_ms,
                synthesis_ms=synthesis.elapsed_ms,
                attempts=synthesis.attempts,
                clipboard_restored=clipboard_restored,
                voice=self.config.voice,
                rate=self.config.rate,
            )
        except Exception as exc:
            log.exception("朗读失败")
            if self._is_stale(generation):
                return
            log_event(
                "read",
                ok=False,
                error=type(exc).__name__,
                message=str(exc)[:300],
            )
            self.error.emit(f"朗读失败：{exc}")
            self.status.emit("朗读失败")
        finally:
            if not handed_off and temp_path is not None:
                try:
                    temp_path.unlink(missing_ok=True)
                except Exception:
                    pass

    def _on_done(
        self,
        temp_path: Path,
        ok: bool,
        error: Exception | None,
        generation: int,
    ) -> None:
        try:
            temp_path.unlink(missing_ok=True)
        except Exception:
            pass
        if self._is_stale(generation):
            return
        if error is not None:
            self.error.emit(f"播放失败：{error}")
        self.status.emit("朗读完成" if ok else "已停止")
