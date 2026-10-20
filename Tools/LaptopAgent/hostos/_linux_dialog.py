"""The tkinter approval dialog, run as a child process (python -I _linux_dialog.py TEXT TITLE SECONDS) so it can be
killed. Exit codes match zenity: 0 yes, 1 no (the default), 5 timeout."""
import sys


def main(argv: list[str]) -> int:
    import tkinter
    text, caption, seconds = argv[1], argv[2], int(argv[3])
    root = tkinter.Tk()
    root.title(caption)
    root.attributes("-topmost", True)
    result = {"code": 1}

    def finish(code: int) -> None:
        result["code"] = code
        root.destroy()

    tkinter.Label(root, text=text, justify="left", wraplength=480, padx=20, pady=16).pack()
    row = tkinter.Frame(root)
    row.pack(pady=(0, 14))
    no = tkinter.Button(row, text="No", width=10, command=lambda: finish(1))
    no.pack(side="left", padx=6)
    tkinter.Button(row, text="Yes", width=10, command=lambda: finish(0)).pack(side="left", padx=6)
    no.focus_set()

    def press_focused(event) -> None:  # Return presses the focused button (No at first), not always No
        focused = root.focus_get()
        if isinstance(focused, tkinter.Button):
            focused.invoke()
        else:
            finish(1)

    root.bind("<Return>", press_focused)
    root.bind("<Escape>", lambda event: finish(1))
    root.protocol("WM_DELETE_WINDOW", lambda: finish(1))
    root.after(seconds * 1000, lambda: finish(5))
    root.mainloop()
    return result["code"]


if __name__ == "__main__":
    sys.exit(main(sys.argv))
