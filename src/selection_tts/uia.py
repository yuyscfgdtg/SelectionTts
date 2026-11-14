from __future__ import annotations

import logging
import os
import threading

import uiautomation as auto

from .foreground import get_foreground_info

log = logging.getLogger(__name__)

_MAX_PARENT_DEPTH = 6
_MAX_TEXT_CHARS = 20_000


def _read_selection() -> tuple[str, str]:
    """Read the focused control's text selection through UI Automation.

    Returns (text, detail). Raises nothing that callers must handle.
    """
    auto.InitializeUIAutomationInCurrentThread()
    auto.SetGlobalSearchTimeout(0.4)

    foreground_pid, _class, _title = get_foreground_info()
    if foreground_pid == os.getpid():
        return "", "self"

    control = auto.GetFocusedControl()
    depth = 0
    while control is not None and depth < _MAX_PARENT_DEPTH:
        try:
            if int(control.ProcessId) == os.getpid():
                return "", "self"
        except Exception:
            pass

        try:
            if control.IsPassword:
                return "", "password"
        except Exception:
            pass

        try:
            pattern = control.GetPattern(auto.PatternId.TextPattern)
        except Exception:
            pattern = None

        if pattern is not None:
            try:
                ranges = pattern.GetSelection()
            except Exception:
                ranges = []

            pieces: list[str] = []
            total = 0
            for text_range in ranges or []:
                try:
                    piece = text_range.GetText(_MAX_TEXT_CHARS - total)
                except Exception:
                    continue
                if piece:
                    pieces.append(piece)
                    total += len(piece)
                    if total >= _MAX_TEXT_CHARS:
                        break
            text = "".join(pieces).strip()
            if text:
                detail = control.ControlTypeName or "TextControl"
                return text, detail

        try:
            control = control.GetParentControl()
        except Exception:
            break
        depth += 1

    return "", "no-selection"


def get_selected_text(timeout: float = 1.0) -> tuple[str, str]:
    """Best-effort UIA selection read. Never blocks longer than `timeout`.

    Returns (text, status). Status is "ok" on success, or one of
    "no-selection", "self", "password", "timeout", "error:<type>". Callers
    should not fall back to Ctrl+C for "self" or "password".
    """
    result: dict[str, str] = {}

    def worker() -> None:
        try:
            text, detail = _read_selection()
            result["text"] = text
            result["detail"] = detail
        except Exception as exc:
            result["detail"] = f"error:{type(exc).__name__}"
            log.debug("UIA 取词失败: %s", exc)

    thread = threading.Thread(target=worker, daemon=True, name="uia-read")
    thread.start()
    thread.join(timeout)

    if thread.is_alive():
        log.info("UIA 取词超时（%.1fs），回退剪贴板", timeout)
        return "", "timeout"

    text = result.get("text", "")
    detail = result.get("detail", "error")
    if text:
        log.info("UIA 取词成功: %s 字（%s）", len(text), detail)
        return text, "ok"

    log.debug("UIA 无可用选区: %s", detail)
    return "", detail
