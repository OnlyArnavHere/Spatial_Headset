"""Windows backend: a thin layer over winapi.py plus the Windows-specific capture and encoder settings.

winapi is only imported as a module here and looked up at call time, so importing it first (it imports
hostos.types) can't trip over a half-initialised package.
"""
import os
from pathlib import Path

import winapi as _w

KILL_SWITCH = "Ctrl+Alt+Shift+Q"  # same as winapi.KILL_SWITCH
exe_suffix = ".exe"

# Probe order: hardware encoders first, then x264. Each entry: (name, ffmpeg encoder, pixel format).
ENCODERS = [
    ("nvenc", "h264_nvenc", "yuv420p"),
    ("qsv", "h264_qsv", "nv12"),
    ("amf", "h264_amf", "nv12"),
    ("x264", "libx264", "yuv420p"),
]


def make_dpi_aware() -> None:
    _w.make_dpi_aware()


def list_monitors():
    return _w.list_monitors()


def monitor_rect(handle):
    return _w.monitor_rect(handle)


def list_windows():
    return _w.list_windows()


def find_window(title_part):
    return _w.find_window(title_part)


def window_exists(handle):
    return _w.window_exists(handle)


def window_is_minimized(handle):
    return _w.window_is_minimized(handle)


def window_rect(handle):
    return _w.window_rect(handle)


def raise_window(handle):
    return _w.raise_window(handle)


def window_at(x, y):
    return _w.window_at(x, y)


def focus_window(handle):
    return _w.focus_window(handle)


def foreground_window():
    return _w.foreground_window()


def request_repaint(handle):
    return _w.request_repaint(handle)


def mouse_move(x, y):
    return _w.mouse_move(x, y)


def mouse_button(x, y, button, down):
    return _w.mouse_button(x, y, button, down)


def mouse_wheel(x, y, notches):
    return _w.mouse_wheel(x, y, notches)


def key(vk, down, extended=False):
    return _w.key(vk, down, extended)


def type_text(text):
    return _w.type_text(text)


def watch_kill_switch(on_pressed) -> bool:
    return _w.watch_kill_switch(on_pressed)


def ask_yes_no(text, caption, timeout_ms):
    return _w.ask_yes_no(text, caption, timeout_ms)


def dismiss_prompt(thread_id) -> bool:
    return _w.dismiss_prompt(thread_id)


def protect(data: bytes) -> bytes:
    return _w.dpapi_protect(data)


def unprotect(blob: bytes) -> bytes:
    return _w.dpapi_unprotect(blob)


def default_home() -> Path:
    base = os.environ.get("LOCALAPPDATA") or str(Path.home() / "AppData" / "Local")
    return Path(base) / "SpatialLauncher" / "agent"


def preflight() -> list[tuple[str, bool, str]]:
    return []


class ChildReaper:
    """Child processes added here die when this process exits, however it exits (a Windows job object)."""

    def __init__(self) -> None:
        self._job = _w.KillOnExitJob()

    def add(self, pid: int) -> None:
        self._job.add(pid)


def capture_input_args(selector: str, fps: int, width: int, height: int, pix_fmt: str) -> list[str]:
    """ffmpeg arguments that capture the target (Windows Graphics Capture) and scale it into the encoder's format."""
    capture = (f"gfxcapture={selector}:max_framerate={fps}:capture_cursor=1,"
               f"hwdownload,format=bgra,scale={width}:{height}:flags=bilinear,format={pix_fmt}[v]")
    return ["-filter_complex", capture, "-map", "[v]", "-fps_mode", "vfr"]


def encoder_args(name: str, codec: str, fps: int, bitrate: int) -> list[str]:
    """Low-latency settings: constant bitrate, a small buffer, no B-frames (WebRTC can't use them),
    a keyframe every second so a reconnecting headset gets a picture quickly, and the baseline profile.
    Keyframes go by time, not frame count: a static window delivers only a couple of frames a second."""
    b, buf, gop = str(bitrate), str(bitrate // 2), str(fps)
    common = ["-b:v", b, "-maxrate", b, "-bufsize", buf, "-g", gop, "-bf", "0",
              "-force_key_frames", "expr:gte(t,n_forced*1)"]
    if name == "nvenc":
        return ["-c:v", codec, "-preset", "p1", "-tune", "ull", "-rc", "cbr", "-zerolatency", "1",
                "-profile:v", "baseline", "-forced-idr", "1"] + common
    if name == "qsv":
        return ["-c:v", codec, "-preset", "veryfast", "-profile:v", "baseline", "-async_depth", "1",
                "-look_ahead", "0"] + common
    if name == "amf":
        return ["-c:v", codec, "-usage", "ultralowlatency", "-quality", "speed", "-rc", "cbr",
                "-profile:v", "constrained_baseline"] + common
    return ["-c:v", codec, "-preset", "ultrafast", "-tune", "zerolatency", "-profile:v", "baseline"] + common
