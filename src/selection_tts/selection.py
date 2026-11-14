from __future__ import annotations

import logging
import os
import time
from dataclasses import dataclass

from pynput.keyboard import Controller, Key

from .clipboard import (
    get_sequence_number,
    read_clipboard_text,
    restore_clipboard,
    snapshot_clipboard,
)
from .foreground import get_foreground_info
from .uia import get_selected_text

log = logging.getLogger(__name__)

_CONSOLE_CLASSES = {
    "ConsoleWindowClass",
    "CASCADIA_HOSTING_WINDOW_CLASS",
}

# File managers copy CF_HDROP (a list of file paths), never text. Sending
# Ctrl+C there would replace the user's clipboard with files and still yield no
# readable text, so the clipboard fallback is skipped entirely.
_FILE_SELECTION_CLASSES = {
    "CabinetWClass",
    "ExploreWClass",
    "Progman",
    "WorkerW",
}


@dataclass
class SelectionResult:
    text: str
    method: str
    elapsed_ms: int
    clipboard_restored: bool | None = None


def _send_copy() -> None:
    controller = Controller()
    # A global hotkey such as Ctrl+Alt+R can still be physically held when the
    # callback runs. Release the modifiers first so the target sees plain Ctrl+C.
    for key in (Key.alt, Key.ctrl):
        try:
            controller.release(key)
        except Exception:
            pass
    time.sleep(0.03)
    with controller.pressed(Key.ctrl):
        controller.press("c")
        controller.release("c")


def read_selection(
    prefer_uia: bool = True,
    wait_release_ms: int = 220,
    timeout_ms: int = 1200,
    restore: bool = True,
    uia_timeout: float = 1.0,
) -> SelectionResult:
    """Read the foreground selection, preferring UIA over clipboard copying.

    UIA needs no clipboard access and works in terminal windows, but many
    apps (most browsers without accessibility enabled, Qt, Tk, games) do not
    expose TextPattern. When UIA yields nothing, fall back to Ctrl+C.
    """
    if prefer_uia:
        started = time.perf_counter()
        text, status = get_selected_text(uia_timeout)
        if text:
            return SelectionResult(
                text=text,
                method="uia",
                elapsed_ms=int((time.perf_counter() - started) * 1000),
            )
        if status in {"self", "password"}:
            log.info("UIA 状态为 %s，跳过剪贴板兜底", status)
            return SelectionResult(
                text="",
                method="uia",
                elapsed_ms=int((time.perf_counter() - started) * 1000),
            )

    return read_selection_via_clipboard(
        wait_release_ms=wait_release_ms,
        timeout_ms=timeout_ms,
        restore=restore,
    )


def read_selection_via_clipboard(
    wait_release_ms: int = 220,
    timeout_ms: int = 1200,
    restore: bool = True,
) -> SelectionResult:
    """Copy the foreground selection with Ctrl+C and read it from the clipboard.

    The clipboard is snapshotted first and restored afterwards when possible.
    This is the Phase 0 fallback; UIA will be tried before it in Phase 1/D4.
    """
    started = time.perf_counter()
    if wait_release_ms > 0:
        time.sleep(wait_release_ms / 1000)

    foreground_pid, foreground_class, foreground_title = get_foreground_info()
    if foreground_pid == os.getpid():
        log.info("前台窗口属于本程序，跳过 Ctrl+C 兜底")
        return SelectionResult(
            text="",
            method="clipboard",
            elapsed_ms=int((time.perf_counter() - started) * 1000),
        )
    if foreground_class in _CONSOLE_CLASSES:
        log.info(
            "前台是终端窗口（class=%s, title=%r），跳过 Ctrl+C 兜底以避免发送中断信号",
            foreground_class,
            foreground_title,
        )
        return SelectionResult(
            text="",
            method="clipboard",
            elapsed_ms=int((time.perf_counter() - started) * 1000),
        )
    if foreground_class in _FILE_SELECTION_CLASSES:
        log.info(
            "前台是文件管理器（class=%s, title=%r），选中的是文件而不是文本，跳过 Ctrl+C 兜底",
            foreground_class,
            foreground_title,
        )
        return SelectionResult(
            text="",
            method="clipboard",
            elapsed_ms=int((time.perf_counter() - started) * 1000),
        )

    before = get_sequence_number()
    snapshot = snapshot_clipboard() if restore else None

    _send_copy()

    deadline = time.perf_counter() + timeout_ms / 1000
    changed = False
    copied_sequence = before
    while time.perf_counter() < deadline:
        copied_sequence = get_sequence_number()
        if copied_sequence != before:
            changed = True
            break
        time.sleep(0.02)

    elapsed_ms = int((time.perf_counter() - started) * 1000)
    if not changed:
        log.info("Ctrl+C 未改变剪贴板，可能没有选中文本或目标窗口拒绝复制")
        return SelectionResult(text="", method="clipboard", elapsed_ms=elapsed_ms)

    # Give the owner a moment to finish writing all clipboard formats. Excel and
    # some other apps render clipboard data lazily, so the first read is often
    # empty while the sequence number has already changed.
    text = read_clipboard_text()
    read_deadline = time.perf_counter() + 0.6
    while not text and time.perf_counter() < read_deadline:
        time.sleep(0.05)
        text = read_clipboard_text()
    restored: bool | None = None
    if snapshot is not None:
        if get_sequence_number() == copied_sequence:
            restored = restore_clipboard(snapshot)
        else:
            log.info("读取后剪贴板又被其他程序修改，跳过恢复")

    return SelectionResult(
        text=text,
        method="clipboard",
        elapsed_ms=int((time.perf_counter() - started) * 1000),
        clipboard_restored=restored,
    )
