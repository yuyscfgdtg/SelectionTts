from __future__ import annotations

import ctypes
import logging
import time
from dataclasses import dataclass

import win32clipboard as wc
import win32con

log = logging.getLogger(__name__)

_user32 = ctypes.windll.user32
_MAX_SNAPSHOT_BYTES = 8 * 1024 * 1024

# Formats that are GDI/USER handles rather than global memory. They cannot be
# restored with SetClipboardData(fmt, bytes) and are skipped by the snapshot.
_HANDLE_FORMATS = {
    win32con.CF_BITMAP,
    win32con.CF_PALETTE,
    win32con.CF_METAFILEPICT,
    win32con.CF_ENHMETAFILE,
    win32con.CF_OWNERDISPLAY,
    win32con.CF_DSPBITMAP,
    win32con.CF_DSPMETAFILEPICT,
    win32con.CF_DSPENHMETAFILE,
}


@dataclass
class ClipboardItem:
    fmt: int
    name: str | None
    data: bytes | str


@dataclass
class ClipboardSnapshot:
    items: list[ClipboardItem]


def get_sequence_number() -> int:
    return int(_user32.GetClipboardSequenceNumber())


def _open_clipboard(retries: int = 20, delay: float = 0.02) -> bool:
    for _ in range(retries):
        try:
            wc.OpenClipboard()
            return True
        except Exception:
            time.sleep(delay)
    return False


def read_clipboard_text() -> str:
    if not _open_clipboard():
        return ""
    try:
        if wc.IsClipboardFormatAvailable(wc.CF_UNICODETEXT):
            data = wc.GetClipboardData(wc.CF_UNICODETEXT)
            if isinstance(data, str):
                return data.rstrip("\x00")
        if wc.IsClipboardFormatAvailable(wc.CF_TEXT):
            data = wc.GetClipboardData(wc.CF_TEXT)
            if isinstance(data, bytes):
                return data.decode("mbcs", errors="replace").rstrip("\x00")
        return ""
    except Exception:
        log.exception("读取剪贴板文本失败")
        return ""
    finally:
        try:
            wc.CloseClipboard()
        except Exception:
            pass


def snapshot_clipboard() -> ClipboardSnapshot | None:
    """Best-effort snapshot of clipboard formats that live in global memory."""
    if not _open_clipboard():
        log.warning("无法打开剪贴板，跳过快照")
        return None
    items: list[ClipboardItem] = []
    try:
        fmt = 0
        total = 0
        while True:
            fmt = wc.EnumClipboardFormats(fmt)
            if not fmt:
                break
            if fmt in _HANDLE_FORMATS:
                continue
            try:
                data = wc.GetClipboardData(fmt)
            except Exception:
                continue
            if isinstance(data, str):
                size = len(data.encode("utf-16-le", errors="ignore"))
            elif isinstance(data, bytes):
                size = len(data)
            else:
                continue
            total += size
            if total > _MAX_SNAPSHOT_BYTES:
                log.warning("剪贴板内容超过 %s 字节，跳过完整快照", _MAX_SNAPSHOT_BYTES)
                return None
            name = None
            if fmt >= 0xC000:
                try:
                    name = wc.GetClipboardFormatName(fmt)
                except Exception:
                    name = None
            items.append(ClipboardItem(fmt=fmt, name=name, data=data))
        return ClipboardSnapshot(items=items)
    except Exception:
        log.exception("快照剪贴板失败")
        return None
    finally:
        try:
            wc.CloseClipboard()
        except Exception:
            pass


def restore_clipboard(snapshot: ClipboardSnapshot) -> bool:
    if not _open_clipboard():
        log.warning("无法打开剪贴板，跳过恢复")
        return False
    ok = True
    try:
        wc.EmptyClipboard()
        for item in snapshot.items:
            try:
                target = wc.RegisterClipboardFormat(item.name) if item.name else item.fmt
                wc.SetClipboardData(target, item.data)
            except Exception:
                ok = False
                log.warning("恢复剪贴板格式 %s 失败", item.name or item.fmt)
    except Exception:
        log.exception("恢复剪贴板失败")
        ok = False
    finally:
        try:
            wc.CloseClipboard()
        except Exception:
            pass
    return ok
