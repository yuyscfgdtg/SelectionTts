"""D5 scenario matrix: how is text captured in common Windows apps?

Runs each scenario in turn, selects text, and calls the same read_selection()
used by the product. No audio is played. Windows are closed afterwards.

Usage:
    uv run python scripts/probe_matrix.py            # all scenarios
    uv run python scripts/probe_matrix.py chrome     # one or more names
"""

from __future__ import annotations

import ctypes
import os
import shutil
import subprocess
import sys
import tempfile
import time
from ctypes import wintypes
from pathlib import Path

from pynput.keyboard import Controller as KeyboardController
from pynput.keyboard import Key
from pynput.mouse import Button, Controller as MouseController

from selection_tts.selection import SelectionResult, read_selection

user32 = ctypes.windll.user32
user32.GetWindowTextW.argtypes = [wintypes.HWND, ctypes.c_wchar_p, ctypes.c_int]
user32.GetClassNameW.argtypes = [wintypes.HWND, ctypes.c_wchar_p, ctypes.c_int]
user32.GetForegroundWindow.restype = wintypes.HWND
user32.SetForegroundWindow.argtypes = [wintypes.HWND]
user32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
user32.SetWindowPos.argtypes = [
    wintypes.HWND,
    wintypes.HWND,
    ctypes.c_int,
    ctypes.c_int,
    ctypes.c_int,
    ctypes.c_int,
    wintypes.UINT,
]

CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"
EDGE = r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
WM_CLOSE = 0x0010
CREATE_NEW_CONSOLE = 0x00000010
HWND_TOPMOST = ctypes.c_void_p(-1)
SWP_NOMOVE = 0x0002
SWP_NOSIZE = 0x0001
SWP_SHOWWINDOW = 0x0040

_ENUM_PROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)


def window_title(hwnd: int) -> str:
    buffer = ctypes.create_unicode_buffer(512)
    user32.GetWindowTextW(ctypes.c_void_p(hwnd), buffer, 512)
    return buffer.value


def window_class(hwnd: int) -> str:
    buffer = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(ctypes.c_void_p(hwnd), buffer, 256)
    return buffer.value


def window_rect(hwnd: int) -> tuple[int, int, int, int]:
    rect = wintypes.RECT()
    user32.GetWindowRect(ctypes.c_void_p(hwnd), ctypes.byref(rect))
    return rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top


def find_window(title_part: str, timeout: float = 12.0) -> int:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        found: list[int] = []

        def callback(hwnd, _lparam):  # type: ignore[no-untyped-def]
            if not user32.IsWindowVisible(hwnd):
                return True
            if title_part.lower() in window_title(int(hwnd)).lower():
                found.append(int(hwnd))
            return True

        user32.EnumWindows(_ENUM_PROC(callback), 0)
        if found:
            return found[0]
        time.sleep(0.15)
    return 0


def click_at(x: int, y: int) -> None:
    mouse = MouseController()
    old = mouse.position
    mouse.position = (x, y)
    mouse.click(Button.left, 1)
    time.sleep(0.3)
    mouse.position = old


def activate(hwnd: int) -> bool:
    """Raise the window and click its title bar so it really takes focus.

    Calling SetForegroundWindow from a background process is refused by
    Windows, but a click in an already-raised window transfers activation.
    """
    user32.SetWindowPos(
        ctypes.c_void_p(hwnd),
        HWND_TOPMOST,
        0,
        0,
        0,
        0,
        SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW,
    )
    time.sleep(0.35)
    left, top, width, _height = window_rect(hwnd)
    click_at(left + width // 2, top + 8)
    deadline = time.monotonic() + 2.0
    while time.monotonic() < deadline:
        if int(user32.GetForegroundWindow() or 0) == hwnd:
            return True
        time.sleep(0.1)
    return False


def click_body(hwnd: int, fx: float, fy: float) -> None:
    left, top, width, height = window_rect(hwnd)
    click_at(left + int(width * fx), top + int(height * fy))


def select_all() -> None:
    keyboard = KeyboardController()
    with keyboard.pressed(Key.ctrl):
        keyboard.press("a")
        keyboard.release("a")
    time.sleep(0.35)


def terminal_select_all(hwnd: int, klass: str) -> None:
    """Windows Terminal selects all with Ctrl+Shift+A; conhost needs a drag."""
    if klass == "CASCADIA_HOSTING_WINDOW_CLASS":
        keyboard = KeyboardController()
        with keyboard.pressed(Key.ctrl), keyboard.pressed(Key.shift):
            keyboard.press("a")
            keyboard.release("a")
        time.sleep(0.5)
        return

    left, top, width, height = window_rect(hwnd)
    start = (left + 12, top + 60)
    end = (left + width - 20, top + height - 20)
    mouse = MouseController()
    mouse.position = start
    mouse.press(Button.left)
    for step in range(1, 21):
        mouse.position = (
            start[0] + (end[0] - start[0]) * step // 20,
            start[1] + (end[1] - start[1]) * step // 20,
        )
        time.sleep(0.02)
    mouse.release(Button.left)
    time.sleep(0.4)


def close_window(hwnd: int) -> None:
    try:
        user32.PostMessageW(ctypes.c_void_p(hwnd), WM_CLOSE, 0, 0)
    except Exception:
        pass


def capture(
    hwnd: int,
    fx: float = 0.5,
    fy: float = 0.5,
    terminal: bool = False,
) -> SelectionResult:
    focused = activate(hwnd)
    print(f"  activated={focused}")
    click_body(hwnd, fx, fy)
    if terminal:
        terminal_select_all(hwnd, window_class(hwnd))
    else:
        select_all()
    return read_selection(prefer_uia=True, wait_release_ms=0, restore=True)


def run_notepad(work: Path) -> dict[str, object]:
    path = work / "tts_matrix_notepad.txt"
    text = "MATRIX-NOTEPAD 记事本取词测试文本。"
    path.write_text(text, encoding="utf-8")
    subprocess.Popen(["notepad.exe", str(path)])
    hwnd = find_window("tts_matrix_notepad.txt")
    if not hwnd:
        return {"name": "notepad", "ok": False, "note": "window not found"}
    try:
        result = capture(hwnd)
        return {
            "name": "notepad",
            "ok": "MATRIX-NOTEPAD" in result.text,
            "method": result.method,
            "chars": len(result.text),
            "selection_ms": result.elapsed_ms,
            "note": result.text[:40],
            "window_class": window_class(hwnd),
        }
    finally:
        close_window(hwnd)
        time.sleep(0.4)


def _browser_scenario(name: str, exe: str, work: Path) -> dict[str, object]:
    title = f"SelectionTTS Matrix {name.title()}"
    page = work / f"matrix_{name}.html"
    page.write_text(
        "<!doctype html><html><head><meta charset='utf-8'>"
        f"<title>{title}</title></head>"
        "<body style='font-size:24px;margin:40px;font-family:sans-serif'>"
        f"<p>MATRIX-{name.upper()} 浏览器取词测试文本，包含中文和 English words。</p>"
        "<p>第二段用于验证多段选区是否都能取到。</p></body></html>",
        encoding="utf-8",
    )
    subprocess.Popen([exe, "--new-window", "--no-first-run", page.as_uri()])
    hwnd = find_window(title, timeout=15)
    if not hwnd:
        return {"name": name, "ok": False, "note": "window not found"}
    try:
        result = capture(hwnd, 0.35, 0.35)
        return {
            "name": name,
            "ok": f"MATRIX-{name.upper()}" in result.text,
            "method": result.method,
            "chars": len(result.text),
            "selection_ms": result.elapsed_ms,
            "note": result.text[:40],
            "window_class": window_class(hwnd),
        }
    finally:
        close_window(hwnd)
        time.sleep(1.0)


def run_chrome(work: Path) -> dict[str, object]:
    return _browser_scenario("chrome", CHROME, work)


def run_edge(work: Path) -> dict[str, object]:
    if not Path(EDGE).exists():
        return {"name": "edge", "ok": False, "note": "edge not installed"}
    return _browser_scenario("edge", EDGE, work)


def run_explorer(work: Path) -> dict[str, object]:
    folder = work / "MATRIX-EXPLORER"
    folder.mkdir()
    (folder / "MATRIX-EXPLORER-说明.txt").write_text("hello", encoding="utf-8")
    (folder / "MATRIX-EXPLORER-notes.txt").write_text("hello", encoding="utf-8")
    subprocess.Popen(["explorer.exe", str(folder)])
    hwnd = find_window("MATRIX-EXPLORER")
    if not hwnd:
        return {"name": "explorer", "ok": False, "note": "window not found"}
    try:
        result = capture(hwnd, 0.5, 0.6)
        # Explorer has no text selection: copying yields file paths (CF_HDROP),
        # so the product intentionally skips it. Empty text is the pass case.
        clean = not result.text.strip()
        return {
            "name": "explorer",
            "ok": clean,
            "skip": clean,
            "method": result.method,
            "chars": len(result.text),
            "selection_ms": result.elapsed_ms,
            "note": "file selection is not text" if clean else result.text[:60],
            "window_class": window_class(hwnd),
        }
    finally:
        close_window(hwnd)
        time.sleep(0.6)


def run_console(work: Path) -> dict[str, object]:
    title = "SelectionTTS Matrix Terminal"
    command = f'title {title} & echo MATRIX-TERMINAL console text 12345 & pause'
    subprocess.Popen(
        ["cmd.exe", "/c", command],
        creationflags=CREATE_NEW_CONSOLE,
    )
    hwnd = find_window(title)
    if not hwnd:
        return {"name": "console", "ok": False, "note": "window not found"}
    try:
        result = capture(hwnd, 0.35, 0.5, terminal=True)
        return {
            "name": "console",
            "ok": "MATRIX-TERMINAL" in result.text,
            "method": result.method,
            "chars": len(result.text),
            "selection_ms": result.elapsed_ms,
            "note": f"{window_class(hwnd)}: {result.text[:40]}",
            "window_class": window_class(hwnd),
        }
    finally:
        close_window(hwnd)
        time.sleep(0.6)


def root_window(hwnd: int) -> int:
    user32.GetAncestor.restype = wintypes.HWND
    return int(user32.GetAncestor(ctypes.c_void_p(hwnd), 2) or hwnd)  # GA_ROOT


def _office_capture(hwnd: int, marker: str, fx: float, fy: float) -> dict[str, object]:
    result = capture(hwnd, fx, fy)
    return {
        "ok": marker in result.text,
        "method": result.method,
        "chars": len(result.text),
        "selection_ms": result.elapsed_ms,
        "note": result.text[:60].replace("\r", " ").replace("\n", " "),
        "window_class": window_class(hwnd),
    }


def run_word(work: Path) -> dict[str, object]:
    import win32com.client

    text = "MATRIX-WORD 这是 Word 文档里的中文段落，用于验证 Office 取词。"
    word = win32com.client.DispatchEx("Word.Application")
    word.Visible = True
    word.DisplayAlerts = 0
    try:
        document = word.Documents.Add()
        document.Content.Text = text
        time.sleep(1.0)
        hwnd = root_window(int(word.ActiveWindow.Hwnd))
        print("  word hwnd", hex(hwnd), window_class(hwnd))
        data = _office_capture(hwnd, "MATRIX-WORD", 0.5, 0.4)
        return {"name": "word", **data}
    finally:
        try:
            if word.Documents.Count:
                word.ActiveDocument.Close(False)
        except Exception:
            pass
        try:
            word.Quit()
        except Exception:
            pass
        time.sleep(0.5)


def run_pdf(work: Path) -> dict[str, object]:
    """Export a Word document to PDF, then read a selection in Edge's viewer."""
    import win32com.client

    text = "MATRIXPDF 这是 PDF 阅读器里的中文文本，用来验证 PDF 取词。"
    pdf = work / "MATRIX-PDF.pdf"
    word = win32com.client.DispatchEx("Word.Application")
    word.Visible = False
    word.DisplayAlerts = 0
    try:
        document = word.Documents.Add()
        document.Content.Text = text
        document.SaveAs2(str(pdf), FileFormat=17)  # wdFormatPDF
        document.Close(False)
    finally:
        try:
            word.Quit()
        except Exception:
            pass
    if not pdf.exists():
        return {"name": "pdf", "ok": False, "note": "pdf export failed"}

    subprocess.Popen([EDGE, "--new-window", "--no-first-run", pdf.as_uri()])
    hwnd = find_window("MATRIX-PDF", timeout=20)
    if not hwnd:
        return {"name": "pdf", "ok": False, "note": "window not found"}
    try:
        result = capture(hwnd, 0.5, 0.45)
        return {
            "name": "pdf",
            "ok": "MATRIXPDF" in result.text,
            "method": result.method,
            "chars": len(result.text),
            "selection_ms": result.elapsed_ms,
            "note": result.text[:50].replace("\r", " ").replace("\n", " "),
            "window_class": window_class(hwnd),
        }
    finally:
        close_window(hwnd)
        time.sleep(1.0)


def run_excel(work: Path) -> dict[str, object]:
    import win32com.client

    excel = win32com.client.DispatchEx("Excel.Application")
    excel.Visible = True
    excel.DisplayAlerts = False
    try:
        book = excel.Workbooks.Add()
        sheet = book.Worksheets(1)
        sheet.Range("A1").Value = "MATRIX-EXCEL 表格取词测试"
        sheet.Range("A2").Value = "第二行内容"
        sheet.Range("A1").Select()
        time.sleep(1.0)
        hwnd = int(excel.Hwnd)
        print("  excel hwnd", hex(hwnd), window_class(hwnd))
        data = _office_capture(hwnd, "MATRIX-EXCEL", 0.5, 0.4)
        return {"name": "excel", **data}
    finally:
        try:
            excel.ActiveWorkbook.Close(False)
        except Exception:
            pass
        try:
            excel.Quit()
        except Exception:
            pass
        time.sleep(0.5)


SCENARIOS = {
    "notepad": run_notepad,
    "chrome": run_chrome,
    "edge": run_edge,
    "explorer": run_explorer,
    "console": run_console,
    "word": run_word,
    "pdf": run_pdf,
    "excel": run_excel,
}


def main() -> None:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    names = sys.argv[1:] or list(SCENARIOS)
    work = Path(tempfile.mkdtemp(prefix="tts_matrix_"))
    results: list[dict[str, object]] = []
    try:
        for name in names:
            runner = SCENARIOS.get(name)
            if runner is None:
                print(f"unknown scenario: {name}")
                continue
            print(f"--- {name} ---")
            try:
                result = runner(work)
            except Exception as exc:
                result = {"name": name, "ok": False, "note": f"error: {exc!r}"}
            results.append(result)
            print(result)
    finally:
        time.sleep(0.5)
        shutil.rmtree(work, ignore_errors=True)

    print("\n| scenario | window class | method | chars | selection_ms | result | note |")
    print("|---|---|---|---|---|---|---|")
    for item in results:
        if item.get("ok"):
            status = "SKIP" if item.get("skip") else "PASS"
        else:
            status = "FAIL"
        print(
            f"| {item['name']} | {item.get('window_class', '-')} | {item.get('method', '-')} "
            f"| {item.get('chars', 0)} | {item.get('selection_ms', '-')} | {status} "
            f"| {item.get('note', '')} |"
        )


if __name__ == "__main__":
    main()
