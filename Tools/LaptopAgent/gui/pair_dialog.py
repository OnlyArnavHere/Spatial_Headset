"""The pairing dialog. Opening it starts pairing (controller.begin_pair); closing it cancels. Nothing pairs without it.

A code typed before the headset is found is kept and sent once it is found; the dialog says so, so a click on Pair is
never silent."""
import tkinter as tk
from tkinter import ttk

from gui import theme

PAD = theme.PAD
WRAP = 400
STEPS = ("1.  On the headset open Laptop > Pair new laptop.\n"
         "2.  Over a USB cable: turn on Wired (USB cable) in the main window first.\n"
         "3.  Look at the QR code with the headset: it pairs by itself. Or type the code the\n"
         "     headset shows (XXXX-XXXX), press Pair, and confirm on the headset.")
SEARCHING = "Looking for a headset showing a code..."


class PairDialog:
    def __init__(self, parent, controller, on_closed=None):
        self.controller, self._on_closed = controller, on_closed
        self.finished = False
        self.asked = False      # the pairing is waiting for a typed line
        self.pending = False    # a line was typed before it asked
        self.window = tk.Toplevel(parent)
        self.window.title("Pair a new headset")
        self.window.configure(background=theme.BG)
        self.window.resizable(False, False)
        self.window.transient(parent)
        self.window.protocol("WM_DELETE_WINDOW", self.close)
        frame = ttk.Frame(self.window, padding=PAD + 6)
        frame.grid(sticky="nsew")
        frame.columnconfigure(0, weight=1)

        ttk.Label(frame, text="Pair a new headset", style="Title.TLabel").grid(row=0, column=0, columnspan=2,
                                                                            sticky="w")
        ttk.Label(frame, text=STEPS, wraplength=WRAP, justify="left", style="Muted.TLabel").grid(
            row=1, column=0, columnspan=2, sticky="w", pady=(6, PAD))

        self.qr_image = None
        self.qr_label = ttk.Label(frame)  # the QR, once the pairing has made one
        self.qr_label.grid(row=1, column=2, rowspan=6, sticky="n", padx=(PAD + 6, 0))

        self.prompt = tk.StringVar(value=SEARCHING)
        ttk.Label(frame, textvariable=self.prompt, wraplength=WRAP, justify="left").grid(
            row=2, column=0, columnspan=2, sticky="w")
        self.progress = ttk.Progressbar(frame, mode="indeterminate", length=WRAP)
        self.progress.grid(row=3, column=0, columnspan=2, sticky="ew", pady=(6, PAD))
        self.progress.start(15)

        self.code = tk.StringVar()
        self.entry = ttk.Entry(frame, textvariable=self.code, width=24, style="Code.TEntry",
                               font=("Consolas", 14) if self.window.tk.call("tk", "windowingsystem") == "win32"
                               else ("Menlo", 14))
        self.entry.grid(row=4, column=0, sticky="ew")
        self.entry.bind("<Return>", lambda _e: self.submit())
        self.send = ttk.Button(frame, text="Pair", style="Accent.TButton", command=self.submit)
        self.send.grid(row=4, column=1, padx=(8, 0), sticky="ns")

        self.status = tk.StringVar()
        self.status_label = ttk.Label(frame, textvariable=self.status, wraplength=WRAP, justify="left")
        self.status_label.grid(row=5, column=0, columnspan=2, sticky="w", pady=(PAD, 0))
        self.button = ttk.Button(frame, text="Cancel", command=self.close)
        self.button.grid(row=6, column=1, sticky="e", pady=(PAD, 0))
        self.entry.focus_set()
        self.gen = controller.begin_pair()

    def submit(self) -> None:
        text = self.code.get().strip()
        if not text or self.finished:
            return
        self.controller.submit_code(text)
        self.code.set("")
        if self.asked:
            self.asked = False
            self._say("Sending...", "Muted.TLabel")
        else:
            self.pending = True
            self._say(f"Got {text}. It will be sent as soon as the headset is found.", "Muted.TLabel")

    def _say(self, text: str, style: str = "TLabel") -> None:
        self.status.set(text)
        self.status_label.configure(style=style)

    def handle_event(self, event: dict) -> None:
        if event.get("gen") != self.gen:
            return  # left over from an earlier pairing
        kind = event.get("type")
        if kind == "pair_say":
            text = event["text"]
            bad = text.startswith(("That code", "Couldn't", "Pairing failed", "No headset", "The headset"))
            self._say(text, "Error.TLabel" if bad else "TLabel")
        elif kind == "pair_prompt":
            self.prompt.set(event["text"].strip())
            self.progress.stop()
            self.progress.grid_remove()
            if self.pending:
                self.pending = False  # the typed line answers this prompt at once
                self._say("Sending...", "Muted.TLabel")
            else:
                self.asked = True
                self.entry.focus_set()
        elif kind == "pair_qr":
            try:
                import qr_image
                self.qr_image = tk.PhotoImage(data=qr_image.png_base64(event["payload"], scale=5))
                self.qr_label.configure(image=self.qr_image)
            except Exception:
                self.qr_label.configure(text="(The QR code can't be drawn.\nType the code instead.)")
        elif kind == "pair_done":
            self.finished = True
            self.qr_label.configure(image="")
            self.progress.stop()
            self.progress.grid_remove()
            self._say(event["text"], "Ok.TLabel" if event.get("ok") else "Error.TLabel")
            self.entry.state(["disabled"])
            self.send.state(["disabled"])
            self.button.configure(text="Close", style="Accent.TButton")

    def close(self) -> None:
        if not self.finished:
            self.controller.cancel_pair()
            self.finished = True
        self.progress.stop()
        self.window.destroy()
        if self._on_closed:
            self._on_closed()
