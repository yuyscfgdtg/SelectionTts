from __future__ import annotations

import re

_ZERO_WIDTH = "\u200b\u200c\u200d\ufeff"
_HYPHEN_BREAK = re.compile(r"(?<=[A-Za-z])-\n(?=[A-Za-z])")
_SPACES = re.compile(r"[ \t]+")
_BLANK_LINES = re.compile(r"\n{3,}")


def normalize_text(text: str) -> str:
    text = text.replace("\r\n", "\n").replace("\r", "\n")
    text = _HYPHEN_BREAK.sub("", text)
    text = "".join(ch for ch in text if ch not in _ZERO_WIDTH)
    text = _SPACES.sub(" ", text)
    text = _BLANK_LINES.sub("\n\n", text)
    return text.strip()


def truncate_text(text: str, max_chars: int) -> tuple[str, bool]:
    if max_chars <= 0 or len(text) <= max_chars:
        return text, False
    return text[:max_chars], True
