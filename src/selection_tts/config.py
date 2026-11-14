from __future__ import annotations

import json
import logging
from dataclasses import asdict, dataclass, fields
from pathlib import Path

from .paths import CONFIG_PATH, ensure_dirs

log = logging.getLogger(__name__)

VOICES: dict[str, str] = {
    "晓晓（女声）": "zh-CN-XiaoxiaoNeural",
    "晓伊（女声）": "zh-CN-XiaoyiNeural",
    "云希（男声）": "zh-CN-YunxiNeural",
    "云健（男声）": "zh-CN-YunjianNeural",
}

RATES: dict[str, str] = {
    "慢速 -20%": "-20%",
    "正常 +0%": "+0%",
    "快速 +20%": "+20%",
    "快速 +50%": "+50%",
}


@dataclass
class AppConfig:
    voice: str = "zh-CN-XiaoxiaoNeural"
    rate: str = "+0%"
    auto_popup: bool = True
    hotkey_read: str = "<ctrl>+<alt>+r"
    hotkey_toggle: str = "<ctrl>+<alt>+shift+p"
    max_chars: int = 5000
    clipboard_restore: bool = True
    edge_proxy: str = ""
    engine: str = "edge"
    azure_region: str = ""
    azure_key_env: str = "AZURE_SPEECH_KEY"


def _sanitize(config: AppConfig) -> AppConfig:
    if config.voice not in VOICES.values():
        log.warning("未知音色 %s，回退到默认音色", config.voice)
        config.voice = AppConfig.voice
    if config.rate not in RATES.values():
        log.warning("未知语速 %s，回退到默认语速", config.rate)
        config.rate = AppConfig.rate
    config.max_chars = max(100, min(int(config.max_chars), 50_000))
    return config


def load_config(path: Path = CONFIG_PATH) -> AppConfig:
    if not path.exists():
        return AppConfig()
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        log.exception("读取配置失败，使用默认配置")
        return AppConfig()
    known = {item.name for item in fields(AppConfig)}
    kwargs = {key: value for key, value in raw.items() if key in known}
    try:
        return _sanitize(AppConfig(**kwargs))
    except Exception:
        log.exception("配置字段无效，使用默认配置")
        return AppConfig()


def save_config(config: AppConfig, path: Path = CONFIG_PATH) -> None:
    ensure_dirs()
    temp = path.with_suffix(".tmp")
    temp.write_text(
        json.dumps(asdict(config), ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    temp.replace(path)
