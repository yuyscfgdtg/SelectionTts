from __future__ import annotations

import ctypes
from ctypes import wintypes

from PySide6.QtCore import QPoint, QTimer, Qt, Signal
from PySide6.QtGui import QGuiApplication
from PySide6.QtWidgets import QFrame, QHBoxLayout, QPushButton, QWidget

from .config import AppConfig

_GWL_EXSTYLE = -20
_WS_EX_NOACTIVATE = 0x08000000
_WS_EX_TOOLWINDOW = 0x00000080
_SWP_NOSIZE = 0x0001
_SWP_NOMOVE = 0x0002
_SWP_NOZORDER = 0x0004
_SWP_NOACTIVATE = 0x0010
_SWP_FRAMECHANGED = 0x0020


class SelectionPopup(QWidget):
    read_requested = Signal(str)
    shown = Signal(object)
    hidden = Signal()

    def __init__(self, config: AppConfig, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self.config = config
        self._text = ""

        self.setWindowTitle("SelectionTTS Popup")
        self.setWindowFlags(
            Qt.WindowType.Tool
            | Qt.WindowType.FramelessWindowHint
            | Qt.WindowType.WindowStaysOnTopHint
            | Qt.WindowType.WindowDoesNotAcceptFocus
        )
        self.setAttribute(Qt.WidgetAttribute.WA_ShowWithoutActivating, True)
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)

        self._hide_timer = QTimer(self)
        self._hide_timer.setSingleShot(True)
        self._hide_timer.timeout.connect(self.hide)

        outer = QHBoxLayout(self)
        outer.setContentsMargins(0, 0, 0, 0)

        card = QFrame(self)
        card.setObjectName("card")
        outer.addWidget(card)

        layout = QHBoxLayout(card)
        layout.setContentsMargins(10, 6, 8, 6)
        layout.setSpacing(6)

        self.read_button = QPushButton("🔊  朗读", card)
        self.read_button.setObjectName("readButton")
        self.read_button.setCursor(Qt.CursorShape.PointingHandCursor)
        self.read_button.clicked.connect(self._on_read)

        self.close_button = QPushButton("×", card)
        self.close_button.setObjectName("closeButton")
        self.close_button.setFixedWidth(24)
        self.close_button.setCursor(Qt.CursorShape.PointingHandCursor)
        self.close_button.clicked.connect(self.hide)

        layout.addWidget(self.read_button)
        layout.addWidget(self.close_button)

        self.setStyleSheet(
            """
            QFrame#card {
                background: #ffffff;
                border: 1px solid #d0d5dd;
                border-radius: 10px;
            }
            QPushButton#readButton {
                border: none;
                background: #2563eb;
                color: #ffffff;
                padding: 6px 12px;
                border-radius: 7px;
                font-size: 13px;
            }
            QPushButton#readButton:hover {
                background: #1d4ed8;
            }
            QPushButton#closeButton {
                border: none;
                color: #667085;
                font-size: 16px;
                padding: 2px 4px;
            }
            QPushButton#closeButton:hover {
                color: #111827;
            }
            """
        )

    def show_for(self, text: str, x: int, y: int) -> None:
        """Show the popup near a physical-pixel screen coordinate.

        The mouse hook reports physical pixels while Qt positions windows in
        device-independent pixels, so the coordinate is divided by the device
        pixel ratio before `move()`.
        """
        self._text = text
        self.setToolTip(text[:200])
        self.adjustSize()

        screen = QGuiApplication.screenAt(QPoint(x, y)) or QGuiApplication.primaryScreen()
        ratio = screen.devicePixelRatio() or 1.0
        area = screen.availableGeometry()

        width = max(self.sizeHint().width(), 120)
        height = max(self.sizeHint().height(), 36)
        point_x = x / ratio
        point_y = y / ratio

        left = point_x + 12
        top = point_y - height - 12
        left = min(max(left, area.left() + 6), area.right() - width - 6)
        top = min(max(top, area.top() + 6), area.bottom() - height - 6)

        self.move(int(left), int(top))
        self.show()
        self.raise_()
        self._hide_timer.start(max(500, self.config.popup_timeout_ms))
        self.shown.emit(self._physical_rect())

    def _physical_rect(self) -> tuple[int, int, int, int]:
        """Window rectangle in physical pixels for the mouse-hook hit test."""
        try:
            rect = wintypes.RECT()
            ctypes.windll.user32.GetWindowRect(int(self.winId()), ctypes.byref(rect))
            return (
                int(rect.left),
                int(rect.top),
                int(rect.right - rect.left),
                int(rect.bottom - rect.top),
            )
        except Exception:
            return tuple(self.frameGeometry().getRect())  # type: ignore[return-value]

    def showEvent(self, event) -> None:  # type: ignore[override]
        super().showEvent(event)
        self._apply_no_activate()

    def _apply_no_activate(self) -> None:
        try:
            hwnd = int(self.winId())
            user32 = ctypes.windll.user32
            ex_style = user32.GetWindowLongW(hwnd, _GWL_EXSTYLE)
            user32.SetWindowLongW(
                hwnd,
                _GWL_EXSTYLE,
                ex_style | _WS_EX_NOACTIVATE | _WS_EX_TOOLWINDOW,
            )
            user32.SetWindowPos(
                hwnd,
                0,
                0,
                0,
                0,
                0,
                _SWP_NOMOVE
                | _SWP_NOSIZE
                | _SWP_NOZORDER
                | _SWP_NOACTIVATE
                | _SWP_FRAMECHANGED,
            )
        except Exception:
            pass

    def hideEvent(self, event) -> None:  # type: ignore[override]
        self._hide_timer.stop()
        self.hidden.emit()
        super().hideEvent(event)

    def _on_read(self) -> None:
        text = self._text
        self.hide()
        if text:
            self.read_requested.emit(text)
