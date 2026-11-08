"""Draws the pairing QR code (segno, pure Python): SVG for the web window, PNG for the Tk window, text for --pair."""
import base64
import io

import segno


def _make(payload: str):
    return segno.make(payload, error="m", micro=False)


def svg(payload: str, scale: int = 6) -> str:
    """Inline SVG markup, dark modules on white with the 4-module quiet zone scanners need."""
    out = io.BytesIO()
    _make(payload).save(out, kind="svg", scale=scale, border=4, dark="#111111", light="#ffffff", xmldecl=False,
                        svgns=True, nl=False, omitsize=True)  # a viewBox, no fixed size: the page sizes it
    return out.getvalue().decode("utf-8")


def png_base64(payload: str, scale: int = 6) -> str:
    """A PNG as base64, for tkinter.PhotoImage(data=...)."""
    out = io.BytesIO()
    _make(payload).save(out, kind="png", scale=scale, border=4, dark="#111111", light="#ffffff")
    return base64.b64encode(out.getvalue()).decode("ascii")


def terminal(payload: str) -> None:
    """Prints the QR in the terminal (dark modules on a light background)."""
    _make(payload).terminal(compact=True, border=2)
