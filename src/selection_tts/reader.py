from __future__ import annotations

import logging
import threading
import time
from pathlib import Path
from uuid import uuid4

from PySide6.QtCore import QObject, Signal

from .config import AppConfig
from .metrics import log_event
from .paths import CACHE_DIR, ensure_dirs
from .player import AudioPlayer
from .selection import read_selection_via_clipboard
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
        self._busy = threading.Lock()
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None

    @property
    def is_busy(self) -> bool:
        return self._busy.locked()

    def read_selection(self, wait_release_ms: int = 250) -> None:
        if not self._busy.acquire(blocking=False):
            self.status.emit("正在朗读中，请先停止")
            return
        self._stop.clear()
        self._thread = threading.Thread(
            target=self._worker,
            args=(wait_release_ms,),
            daemon=True,
            name="reader",
        )
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        self._player.stop()

    def _worker(self, wait_release_ms: int) -> None:
        handed_off = False
        temp_path: Path | None = None
        try:
            self.status.emit("正在读取选中文本…")
            selection = read_selection_via_clipboard(
                wait_release_ms=wait_release_ms,
                restore=self.config.clipboard_restore,
            )
            if self._stop.is_set():
                return

            text, truncated = truncate_text(
                normalize_text(selection.text),
                self.config.max_chars,
            )
            if not text:
                self.status.emit("没有检测到选中文本")
                log_event(
                    "selection",
                    ok=False,
                    method=selection.method,
                    chars=0,
                    selection_ms=selection.elapsed_ms,
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
            if self._stop.is_set():
                return

            self.status.emit("开始朗读")
            self._player.play(
                temp_path,
                on_done=lambda ok, err: self._on_done(temp_path, ok, err),
            )
            handed_off = True

            log_event(
                "read",
                ok=True,
                method=selection.method,
                chars=len(text),
                truncated=truncated,
                selection_ms=selection.elapsed_ms,
                synthesis_ms=synthesis.elapsed_ms,
                attempts=synthesis.attempts,
                clipboard_restored=selection.clipboard_restored,
                voice=self.config.voice,
                rate=self.config.rate,
            )
        except Exception as exc:
            log.exception("朗读失败")
            log_event(
                "read",
                ok=False,
                error=type(exc).__name__,
                message=str(exc)[:300],
            )
            self.error.emit(f"朗读失败：{exc}")
            self.status.emit("朗读失败")
        finally:
            if not handed_off:
                if temp_path is not None:
                    try:
                        temp_path.unlink(missing_ok=True)
                    except Exception:
                        pass
                self._busy.release()

    def _on_done(self, temp_path: Path, ok: bool, error: Exception | None) -> None:
        try:
            temp_path.unlink(missing_ok=True)
        except Exception:
            pass
        if error is not None:
            self.error.emit(f"播放失败：{error}")
        self.status.emit("朗读完成" if ok else "已停止")
        self._busy.release()
