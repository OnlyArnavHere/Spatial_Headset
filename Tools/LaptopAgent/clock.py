"""Test clock for estimating end-to-end delay, and a click target for checking input mapping.

  python clock.py                 then: python agent.py --window "Spatial Launcher Test Clock"

The window shows this laptop's clock to the millisecond. Put the headset view (or the Unity editor) next to it and
take one photo or screenshot: the difference between the two clocks is the video delay. The Laptop window also
shows the laptop's clock as estimated over the input connection, so a single screenshot of the headset view works too.

Clicks are marked where they land and printed as normalized (0-1, top-left origin) positions, so injected clicks can
be checked against what the headset sent.
"""
import argparse
import time
import tkinter as tk

TITLE = "Spatial Launcher Test Clock"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--topmost", action="store_true", help="keep the window above others")
    parser.add_argument("--size", default="960x540", help="window size (default 960x540)")
    args = parser.parse_args()

    try:  # sharp text and true pixel positions on scaled displays
        import ctypes
        import sys
        if sys.platform != "win32":
            raise OSError("per-monitor DPI awareness is a Windows call")
        ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
    except (AttributeError, OSError):
        pass

    root = tk.Tk()
    root.title(TITLE)
    root.geometry(args.size)
    root.configure(bg="black")
    root.attributes("-topmost", args.topmost)

    canvas = tk.Canvas(root, bg="black", highlightthickness=0)
    canvas.pack(fill="both", expand=True)
    clock = canvas.create_text(0, 0, fill="white", font=("Consolas", 64, "bold"))
    frame_counter = canvas.create_text(0, 0, fill="#7fd4ff", font=("Consolas", 20))
    hint = canvas.create_text(0, 0, fill="#888888", font=("Segoe UI", 12),
                              text="Compare with the headset view. Clicks are marked and printed.")
    last_click = canvas.create_text(0, 0, fill="#ffc94d", font=("Consolas", 16), text="")
    frames = 0

    def layout(_event=None):
        w, h = canvas.winfo_width(), canvas.winfo_height()
        canvas.coords(clock, w / 2, h / 2 - 20)
        canvas.coords(frame_counter, w / 2, h / 2 + 50)
        canvas.coords(hint, w / 2, h - 24)
        canvas.coords(last_click, w / 2, 30)

    def tick():
        nonlocal frames
        frames += 1
        now = time.time()
        canvas.itemconfigure(clock, text=time.strftime("%H:%M:%S", time.localtime(now)) + f".{int(now * 1000) % 1000:03d}")
        canvas.itemconfigure(frame_counter, text=f"frame {frames}")
        root.after(4, tick)

    def on_click(event, button):
        w, h = canvas.winfo_width(), canvas.winfo_height()
        nx, ny = event.x / max(1, w), event.y / max(1, h)
        canvas.create_oval(event.x - 6, event.y - 6, event.x + 6, event.y + 6, outline="#ffc94d", width=2)
        text = f"{button} click at ({nx:.3f}, {ny:.3f})"
        canvas.itemconfigure(last_click, text=text)
        print(f"CLICK {button} {nx:.4f} {ny:.4f} at {time.time():.3f}", flush=True)

    canvas.bind("<Configure>", layout)
    canvas.bind("<Button-1>", lambda e: on_click(e, "left"))
    canvas.bind("<Button-3>", lambda e: on_click(e, "right"))
    tick()
    root.mainloop()


if __name__ == "__main__":
    main()
