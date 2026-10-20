"""Linux (X11) backend, EXPERIMENTAL: screens, windows, capture, secrets and cleanup. Input, the kill hotkey and the
approval prompt are still stubs (they raise NotImplementedError).

Windows come from the EWMH properties through libX11 (ctypes, in _xlib.py), screens from `xrandr --listmonitors`, and
capture is ffmpeg x11grab. Wayland sessions are refused by the preflight. Logic that needs no X server is in
_linux_pure.py so it can be tested on any OS.
"""
import logging
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

from . import _linux_pure as pure
from . import _xlib
from ._macos_pure import group_kwargs
from ._stub import fill
from .types import Monitor, Rect, Window

log = logging.getLogger("hostos")

fill(globals(), "Linux input, the kill hotkey and the approval prompt land in a later commit")

exe_suffix = ""
ENCODERS = [("x264", "libx264", "yuv420p")]
_REAPER = Path(__file__).with_name("_reaper.py")
FOCUS_WAIT_S = 0.2


def _connect():
    return _xlib.connect()


def watch_kill_switch(on_kill) -> bool:
    return False  # no hotkey until the input part of the backend lands; the agent tells the user to use Ctrl+C


def make_dpi_aware() -> None:
    pass  # X reports pixels


# --- Screens -----------------------------------------------------------------------------------

def list_monitors() -> list[Monitor]:
    try:
        out = subprocess.run(["xrandr", "--listmonitors"], capture_output=True, text=True, timeout=10).stdout
        monitors = pure.parse_listmonitors(out)
    except (OSError, subprocess.SubprocessError):
        monitors = []
    if not monitors:  # no xrandr: the whole X screen is the one monitor
        with _connect() as x:
            width, height = x.screen_size()
        monitors = [Monitor(0, 0, Rect(0, 0, width, height), True)]
    return monitors


def monitor_rect(handle: int) -> Rect | None:
    return next((m.rect for m in list_monitors() if m.handle == handle), None)


# --- Windows -----------------------------------------------------------------------------------

def _client_list(x, stacking: bool = False) -> list[int]:
    return x.property(x.root, "_NET_CLIENT_LIST_STACKING" if stacking else "_NET_CLIENT_LIST") or []


def _title(x, window: int) -> str:
    return pure.window_title(x.property(window, "_NET_WM_NAME"), x.property(window, "WM_NAME"))


def _hidden(x, window: int) -> bool:
    return x.intern("_NET_WM_STATE_HIDDEN") in (x.property(window, "_NET_WM_STATE") or [])


def list_windows() -> list[Window]:
    with _connect() as x:
        titled = [Window(w, _title(x, w)) for w in _client_list(x)]
    return [w for w in titled if w.title]


def find_window(title_part: str) -> Window | None:
    needle = title_part.lower()
    return next((w for w in list_windows() if needle in w.title.lower()), None)


def window_exists(handle: int) -> bool:
    with _connect() as x:
        return handle in _client_list(x)


def window_is_minimized(handle: int) -> bool:
    with _connect() as x:
        return _hidden(x, handle)


def window_rect(handle: int) -> Rect | None:
    with _connect() as x:
        return x.geometry(handle)


def request_repaint(handle: int) -> None:
    pass  # x11grab reads the screen continuously


def visible_rect(rect: Rect) -> Rect:
    return pure.visible_rect(rect, list_monitors())


def foreground_window() -> int:
    with _connect() as x:
        active = x.property(x.root, "_NET_ACTIVE_WINDOW")
    return active[0] if active else 0


def window_at(px, py) -> int:
    """The topmost visible window whose client area contains the point, or 0."""
    with _connect() as x:
        for handle in reversed(_client_list(x, stacking=True)):
            rect = x.geometry(handle)
            if rect and not _hidden(x, handle) and rect.left <= px < rect.right and rect.top <= py < rect.bottom:
                return handle
    return 0


def raise_window(handle: int) -> None:
    fmt, data = pure.active_window_message()
    with _connect() as x:
        x.send_client_message(handle, "_NET_ACTIVE_WINDOW", fmt, data)


def focus_window(handle: int) -> bool:
    raise_window(handle)
    deadline = time.monotonic() + FOCUS_WAIT_S
    while True:
        if foreground_window() == handle:
            return True
        if time.monotonic() >= deadline:
            return False
        time.sleep(0.01)


def window_selector(handle: int) -> str:
    return f"window={handle}"


def monitor_selector(handle: int) -> str:
    return f"monitor={handle}"


# --- Capture and encoding ----------------------------------------------------------------------

def capture_input_args(selector: str, fps: int, width: int, height: int, pix_fmt: str,
                       ffmpeg: str = "ffmpeg") -> list[str]:
    kind, _, number = selector.partition("=")
    handle, monitors = int(number), list_monitors()
    if kind == "monitor":
        rect = monitor_rect(handle)
        if rect is None:
            raise LookupError("gone")
    else:
        with _connect() as x:
            window = x.geometry(handle)
            if window is None:
                raise LookupError("gone")
            if _hidden(x, handle):
                raise LookupError("minimized")
        rect = pure.capture_rect(window, monitors)
    return pure.x11grab_args(os.environ.get("DISPLAY", ":0"), rect, fps, width, height, pix_fmt)


def encoder_args(name: str, codec: str, fps: int, bitrate: int) -> list[str]:
    return pure.encoder_args(name, codec, fps, bitrate)


# --- Preflight ---------------------------------------------------------------------------------

def _ffmpeg_has_x11grab() -> bool | None:
    """None when there is no ffmpeg to ask (the agent reports that itself)."""
    local = Path(__file__).resolve().parent.parent / "bin" / "ffmpeg"
    ffmpeg = str(local) if local.exists() else shutil.which("ffmpeg")
    if not ffmpeg:
        return None
    try:
        out = subprocess.run([ffmpeg, "-hide_banner", "-devices"], capture_output=True, text=True, timeout=15).stdout
    except (OSError, subprocess.SubprocessError):
        return None
    return pure.has_x11grab(out)


def preflight() -> list[pure.Row]:
    """The X11 session row, and whether ffmpeg can x11grab (only asked when there is an X session)."""
    session = pure.session_row(os.environ)
    return pure.permission_rows(os.environ, _ffmpeg_has_x11grab() if session.ok else None)


# --- Secrets: identity.key is a plain file the OS keeps private (mode 0600) -------------------------

def protect(data: bytes) -> bytes:
    return data  # identity.atomic_write creates the file through mkstemp, which is 0600


def unprotect(blob: bytes) -> bytes:
    return blob


def check_key_file(path) -> None:
    """Called by identity before it reads the key file: refuses one that is group/other-accessible or not ours."""
    info = Path(path).stat()
    problem = pure.key_file_problem(info.st_mode, info.st_uid, os.getuid())
    if problem:
        raise OSError(f"{path} can't be trusted: {problem}")


def default_home() -> Path:
    base = os.environ.get("XDG_DATA_HOME") or str(Path.home() / ".local" / "share")
    return Path(base) / "SpatialLauncher" / "agent"


# --- Child cleanup: process groups, ended by a small watcher process (_reaper.py) -------------------

class ChildReaper:
    """Children get a process group each; a watcher process (_reaper.py) kills the groups when this process exits,
    however it exits."""

    def __init__(self) -> None:
        self._watcher = subprocess.Popen([sys.executable, "-I", str(_REAPER), str(os.getpid())],
                                         stdin=subprocess.PIPE, start_new_session=True)
        try:
            code = self._watcher.wait(0.2)
        except subprocess.TimeoutExpired:
            return
        log.error("child cleanup watcher failed: it exited at once with code %s", code)
        raise OSError("child cleanup watcher is not running")

    def spawn(self, command, **kwargs) -> subprocess.Popen:
        process = subprocess.Popen(command, **group_kwargs(), **kwargs)
        try:
            code = self._watcher.poll()
            if code is not None:
                raise OSError(f"it exited with code {code}")
            self._watcher.stdin.write(f"{process.pid}\n".encode("ascii"))
            self._watcher.stdin.flush()
        except OSError as e:
            log.error("child cleanup watcher failed: %s", e)
            process.kill()
            process.wait()
            raise OSError("child cleanup watcher is not running") from e
        return process
