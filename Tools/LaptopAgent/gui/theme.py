"""One light look for every window of the app: Tk's plain 'clam' theme recoloured, the system UI font, more padding.
apply(root) is idempotent; call it once a Tk root exists."""
import sys
import tkinter as tk
from tkinter import font as tkfont
from tkinter import ttk

BG = "#f4f5f7"
SURFACE = "#ffffff"
BORDER = "#d8dce2"
TEXT = "#1f2328"
MUTED = "#5b6470"
ACCENT = "#2563eb"
ACCENT_ACTIVE = "#1d4ed8"
ACCENT_TEXT = "#ffffff"
DANGER = "#b42318"
OK = "#15803d"
WARN = "#9a3412"
PAD = 14

FAMILY = "Segoe UI" if sys.platform == "win32" else None  # macOS and Linux keep their own UI font


def apply(root: tk.Misc) -> ttk.Style:
    style = ttk.Style(root)
    try:
        style.theme_use("clam")
    except tk.TclError:
        return style
    for name in ("TkDefaultFont", "TkTextFont", "TkMenuFont", "TkHeadingFont"):
        try:
            f = tkfont.nametofont(name, root=root)
            if FAMILY:
                f.configure(family=FAMILY)
            f.configure(size=10)
        except tk.TclError:
            pass
    base = tkfont.nametofont("TkDefaultFont", root=root).actual()["family"]
    root.configure(background=BG)

    style.configure(".", background=BG, foreground=TEXT, bordercolor=BORDER, lightcolor=BG, darkcolor=BG,
                    troughcolor=BORDER, focuscolor=ACCENT, selectbackground=ACCENT, selectforeground=ACCENT_TEXT)
    style.configure("TFrame", background=BG)
    style.configure("Card.TFrame", background=SURFACE, relief="solid", borderwidth=1)
    style.configure("TLabel", background=BG, foreground=TEXT)
    style.configure("Muted.TLabel", foreground=MUTED)
    style.configure("Title.TLabel", font=(base, 15, "bold"))
    style.configure("Status.TLabel", font=(base, 11, "bold"))
    style.configure("Error.TLabel", foreground=DANGER)
    style.configure("Ok.TLabel", foreground=OK)
    style.configure("Warn.TLabel", foreground=WARN)
    style.configure("TLabelframe", background=BG, bordercolor=BORDER, relief="solid", borderwidth=1)
    style.configure("TLabelframe.Label", background=BG, foreground=MUTED, font=(base, 10, "bold"))

    style.configure("TButton", padding=(12, 6), background=SURFACE, foreground=TEXT, bordercolor=BORDER,
                    lightcolor=SURFACE, darkcolor=SURFACE, focusthickness=1)
    style.map("TButton", background=[("disabled", BG), ("pressed", "#e7eaee"), ("active", "#eef1f4")],
              foreground=[("disabled", "#9aa3ad")])
    style.configure("Accent.TButton", background=ACCENT, foreground=ACCENT_TEXT, bordercolor=ACCENT,
                    lightcolor=ACCENT, darkcolor=ACCENT)
    style.map("Accent.TButton", background=[("disabled", "#a9bdf0"), ("pressed", ACCENT_ACTIVE),
                                            ("active", ACCENT_ACTIVE)],
              foreground=[("disabled", ACCENT_TEXT)], bordercolor=[("active", ACCENT_ACTIVE)])

    style.configure("TCheckbutton", background=BG, foreground=TEXT, indicatorbackground=SURFACE,
                    indicatorforeground=ACCENT, indicatorsize=15, indicatormargin=(0, 0, 8, 0),
                    padding=(0, 3))
    style.map("TCheckbutton", background=[("active", BG)],
              indicatorbackground=[("selected", SURFACE), ("disabled", BG)])
    style.configure("TEntry", fieldbackground=SURFACE, bordercolor=BORDER, lightcolor=SURFACE, padding=(8, 5))
    style.map("TEntry", bordercolor=[("focus", ACCENT)], lightcolor=[("focus", ACCENT)])
    style.configure("Code.TEntry", padding=(10, 8))
    style.configure("TScrollbar", background=BG, troughcolor=BG, bordercolor=BG, arrowcolor=MUTED)
    style.configure("Horizontal.TProgressbar", background=ACCENT, troughcolor="#e3e7ec", bordercolor="#e3e7ec",
                    lightcolor=ACCENT, darkcolor=ACCENT)
    return style


def style_list(widget: tk.Widget) -> None:
    """Plain Tk widgets (Listbox, Text) don't follow ttk styles."""
    widget.configure(background=SURFACE, foreground=TEXT, relief="flat", borderwidth=0, highlightthickness=1,
                     highlightbackground=BORDER, highlightcolor=ACCENT, selectbackground=ACCENT,
                     selectforeground=ACCENT_TEXT)
