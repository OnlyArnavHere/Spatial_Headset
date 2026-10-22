"""The pairing dialog. Opening it starts pairing (controller.begin_pair); closing it cancels. Nothing pairs without it."""
import tkinter as tk
from tkinter import ttk

PAD = 12
INSTRUCTIONS = ("On the headset open Laptop > Pair new laptop, then type the code it shows here "
                "(XXXX-XXXX-XXXX). If more than one headset answers, type the number of yours.")


class PairDialog:
    def __init__(self, parent, controller, on_closed=None):
        self.controller, self._on_closed = controller, on_closed
        self.finished = False
        self.window = tk.Toplevel(parent)
        self.window.title("Pair a new headset")
        self.window.resizable(False, False)
        self.window.transient(parent)
        self.window.protocol("WM_DELETE_WINDOW", self.close)
        frame = ttk.Frame(self.window, padding=PAD)
        frame.grid(sticky="nsew")
        ttk.Label(frame, text=INSTRUCTIONS, wraplength=380, justify="left").grid(row=0, column=0, columnspan=2,
                                                                                sticky="w")
        self.prompt = tk.StringVar(value="Looking for a headset showing a code...")
        ttk.Label(frame, textvariable=self.prompt, wraplength=380, justify="left").grid(
            row=1, column=0, columnspan=2, sticky="w", pady=(PAD, 4))
        self.code = tk.StringVar()
        self.entry = ttk.Entry(frame, textvariable=self.code, width=30)
        self.entry.grid(row=2, column=0, sticky="ew")
        self.entry.bind("<Return>", lambda _e: self.submit())
        self.send = ttk.Button(frame, text="Pair", command=self.submit)
        self.send.grid(row=2, column=1, padx=(8, 0))
        self.status = tk.StringVar()
        ttk.Label(frame, textvariable=self.status, wraplength=380, justify="left").grid(
            row=3, column=0, columnspan=2, sticky="w", pady=(PAD, 0))
        self.button = ttk.Button(frame, text="Cancel", command=self.close)
        self.button.grid(row=4, column=1, sticky="e", pady=(PAD, 0))
        frame.columnconfigure(0, weight=1)
        self.entry.focus_set()
        self.gen = controller.begin_pair()

    def submit(self) -> None:
        text = self.code.get().strip()
        if text and not self.finished:
            self.controller.submit_code(text)
            self.code.set("")

    def handle_event(self, event: dict) -> None:
        if event.get("gen") != self.gen:
            return  # left over from an earlier pairing
        kind = event.get("type")
        if kind == "pair_say":
            self.status.set(event["text"])
        elif kind == "pair_prompt":
            self.prompt.set(event["text"].strip())
        elif kind == "pair_done":
            self.finished = True
            self.status.set(event["text"])
            self.entry.state(["disabled"])
            self.send.state(["disabled"])
            self.button.configure(text="Close")

    def close(self) -> None:
        if not self.finished:
            self.controller.cancel_pair()
            self.finished = True
        self.window.destroy()
        if self._on_closed:
            self._on_closed()
