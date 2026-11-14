"""D1 probe: Edge TTS connectivity, voice list, synthesis and first-audio latency.

Usage: uv run python scripts/probe_edge_tts.py
"""

from __future__ import annotations

import asyncio
import time
from pathlib import Path

import edge_tts

TEXT = "这是一段划词朗读的测试文本。今天天气不错，适合测试语音合成的延迟和音质。"
VOICE = "zh-CN-XiaoxiaoNeural"
OUT = Path(__file__).resolve().parent.parent / "probe_zh.mp3"


async def list_chinese_voices() -> None:
    voices = await edge_tts.list_voices()
    zh = [v for v in voices if str(v.get("Locale", "")).startswith("zh-CN")]
    print(f"zh-CN voices: {len(zh)}")
    for v in zh[:30]:
        print(f"  {v['ShortName']:<34} {v['Gender']:<7} {v.get('FriendlyName', '')}")


async def measure_stream() -> None:
    communicate = edge_tts.Communicate(TEXT, VOICE, rate="+0%")
    start = time.perf_counter()
    first_audio_at: float | None = None
    first_word_at: float | None = None
    audio_bytes = 0
    chunks: list[bytes] = []

    async for chunk in communicate.stream():
        if chunk["type"] == "audio":
            if first_audio_at is None:
                first_audio_at = time.perf_counter() - start
            audio_bytes += len(chunk["data"])
            chunks.append(chunk["data"])
        elif chunk["type"] == "WordBoundary" and first_word_at is None:
            first_word_at = time.perf_counter() - start

    total = time.perf_counter() - start
    OUT.write_bytes(b"".join(chunks))
    print(f"text chars           : {len(TEXT)}")
    print(f"first audio chunk    : {first_audio_at:.3f}s" if first_audio_at else "first audio chunk    : n/a")
    print(f"first word boundary  : {first_word_at:.3f}s" if first_word_at else "first word boundary  : n/a")
    print(f"total synthesis      : {total:.3f}s")
    print(f"audio bytes          : {audio_bytes}")
    print(f"saved                : {OUT}")


async def main() -> None:
    print("== voices ==")
    await list_chinese_voices()
    print("== synthesis ==")
    await measure_stream()


if __name__ == "__main__":
    asyncio.run(main())
