from __future__ import annotations

import asyncio
import logging
import time
from dataclasses import dataclass
from pathlib import Path

import edge_tts

log = logging.getLogger(__name__)


class TtsError(RuntimeError):
    pass


@dataclass
class SynthesisResult:
    path: Path
    chars: int
    elapsed_ms: int
    attempts: int


class EdgeTtsEngine:
    name = "edge"

    def __init__(self, proxy: str = "") -> None:
        self.proxy = proxy or None

    async def _save(self, text: str, voice: str, rate: str, out_path: Path) -> None:
        kwargs: dict[str, object] = {}
        if self.proxy:
            kwargs["proxy"] = self.proxy
        communicate = edge_tts.Communicate(text, voice, rate=rate, **kwargs)
        await communicate.save(str(out_path))

    def synthesize(
        self,
        text: str,
        voice: str,
        rate: str,
        out_path: Path,
        timeout: float = 25.0,
    ) -> SynthesisResult:
        out_path.parent.mkdir(parents=True, exist_ok=True)
        last_error: Exception | None = None

        for attempt in range(1, 3):
            started = time.perf_counter()
            try:
                asyncio.run(
                    asyncio.wait_for(
                        self._save(text, voice, rate, out_path),
                        timeout=timeout,
                    )
                )
                if not out_path.exists() or out_path.stat().st_size == 0:
                    raise TtsError("Edge TTS 返回了空音频")
                return SynthesisResult(
                    path=out_path,
                    chars=len(text),
                    elapsed_ms=int((time.perf_counter() - started) * 1000),
                    attempts=attempt,
                )
            except Exception as exc:
                last_error = exc
                log.warning("Edge TTS 第 %s 次合成失败: %s", attempt, exc)
                if attempt < 2:
                    time.sleep(0.5)

        raise TtsError(f"Edge TTS 合成失败: {last_error}") from last_error
