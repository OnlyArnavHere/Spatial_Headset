"""macOS backend: screens and windows through Quartz (pyobjc, imported lazily), captured by ffmpeg avfoundation.

Capture and encoding live here; input, prompts, the kill switch and secrets are still stubs (they raise
NotImplementedError). Everything that needs no pyobjc is in _macos_pure.py so it can be tested on any OS.
"""
import logging
import os
import subprocess
import sys
from pathlib import Path

from . import _macos_pure as pure
from ._stub import fill
from .types import Monitor, Rect, Window

fill(globals(), "macOS support lands in a later commit")

log = logging.getLogger("hostos")
exe_suffix = ""
WATCHER_CHECK_S = 0.2

ENCODERS = [
    ("videotoolbox", "h264_videotoolbox", "nv12"),
    ("x264", "libx264", "yuv420p"),
]

_REAPER = Path(__file__).with_name("_reaper.py")


def _quartz():
    import Quartz
    return Quartz


def _infos(options: int):
    q = _quartz()
    return list(q.CGWindowListCopyWindowInfo(options, q.kCGNullWindowID) or [])


def _onscreen():
    q = _quartz()
    return _infos(q.kCGWindowListOptionOnScreenOnly | q.kCGWindowListExcludeDesktopElements)


def _all():
    return _infos(_quartz().kCGWindowListOptionAll)


def make_dpi_aware() -> None:
    pass  # Quartz reports points; the crop maths converts to pixels


def list_windows() -> list[Window]:
    return pure.windows_from_cg(_onscreen(), os.getpid())


def find_window(title_part: str) -> Window | None:
    needle = title_part.lower()
    for window in list_windows():
        if needle in window.title.lower():
            return window
    return None


def window_exists(handle: int) -> bool:
    return handle in pure.window_ids(_all())


def window_is_minimized(handle: int) -> bool:
    return pure.is_minimized(handle, _all(), _onscreen())


def window_rect(handle: int) -> Rect | None:
    return pure.bounds_of(handle, _all())


def request_repaint(handle: int) -> None:
    pass  # avfoundation delivers frames continuously; nothing to poke


def _displays() -> list[pure.Display]:
    q = _quartz()
    err, ids, count = q.CGGetActiveDisplayList(16, None, None)
    if err != 0:
        raise OSError(err, "CGGetActiveDisplayList failed")
    displays = []
    for display_id in ids[:count]:
        b = q.CGDisplayBounds(display_id)
        rect = Rect(round(b.origin.x), round(b.origin.y), round(b.origin.x + b.size.width), round(b.origin.y + b.size.height))
        # CGDisplayPixelsWide reports points on HiDPI modes; the display mode knows the real pixel width.
        pixels = q.CGDisplayModeGetPixelWidth(q.CGDisplayCopyDisplayMode(display_id))
        displays.append(pure.Display(int(display_id), rect, int(pixels)))
    return displays


def list_monitors() -> list[Monitor]:
    return pure.monitors_from_displays(_displays(), int(_quartz().CGMainDisplayID()))


def monitor_rect(handle: int) -> Rect | None:
    for display in _displays():
        if display.id == handle:
            return display.bounds
    return None


def window_selector(handle: int) -> str:
    return f"window={handle}"


def monitor_selector(handle: int) -> str:
    return f"display={handle}"


def preflight() -> list[tuple[str, bool, str]]:
    return [("Screen Recording permission", bool(_quartz().CGPreflightScreenCaptureAccess()),
             "System Settings > Privacy & Security > Screen & System Audio Recording: allow the app you start the "
             "agent from (Terminal, iTerm, ...), then restart that app")]


# --- Capture and encoding ----------------------------------------------------------------------

_screen_devices: dict[int, int] | None = None  # screen N -> avfoundation index, probed once per run
_probed_for: tuple[int, ...] = ()               # the active display ids when it was probed


def _probe_devices(ffmpeg: str) -> dict[int, int]:
    result = subprocess.run([ffmpeg, "-hide_banner", "-f", "avfoundation", "-list_devices", "true", "-i", ""],
                            capture_output=True, text=True, timeout=30)
    return pure.parse_screen_devices(result.stderr)


def _device_index(ffmpeg: str, display_id: int, displays: list[pure.Display]) -> int:
    global _screen_devices, _probed_for
    ids = tuple(d.id for d in displays)
    if display_id not in ids:
        raise LookupError("gone")
    n = ids.index(display_id)  # "Capture screen N" is the N-th active display
    if _screen_devices is None or n not in _screen_devices or ids != _probed_for:  # displays came, went or moved
        _screen_devices, _probed_for = _probe_devices(ffmpeg), ids
    if n not in _screen_devices:
        raise LookupError("gone")
    return _screen_devices[n]


def visible_rect(rect: Rect) -> Rect:
    """The part of the window that is on its display, in points: the picture ffmpeg will crop out. The output size
    follows this, so a window hanging off the edge isn't stretched."""
    return pure.visible_rect(rect, _displays())


def capture_input_args(selector: str, fps: int, width: int, height: int, pix_fmt: str,
                       ffmpeg: str = "ffmpeg") -> list[str]:
    kind, _, number = selector.partition("=")
    handle, displays = int(number), _displays()
    if kind == "display":
        display = next((d for d in displays if d.id == handle), None)
        if display is None:
            raise LookupError("gone")
        crop = None
    else:
        rect = window_rect(handle)
        if rect is None:
            raise LookupError("gone")
        display, crop = pure.crop_window(rect, displays)
    return pure.capture_args(_device_index(ffmpeg, display.id, displays), fps, width, height, pix_fmt, crop)


def encoder_args(name: str, codec: str, fps: int, bitrate: int) -> list[str]:
    return pure.encoder_args(name, codec, fps, bitrate)


class ChildReaper:
    """Children get a process group each; a small watcher process (_reaper.py) kills the groups when this process
    exits, however it exits."""

    def __init__(self) -> None:
        self._watcher = subprocess.Popen([sys.executable, "-I", str(_REAPER), str(os.getpid())],
                                         stdin=subprocess.PIPE, start_new_session=True)
        try:  # like CreateJobObject failing on Windows: no cleanup, no children
            code = self._watcher.wait(WATCHER_CHECK_S)
        except subprocess.TimeoutExpired:
            return
        log.error("child cleanup watcher failed: it exited at once with code %s", code)
        raise OSError("child cleanup watcher is not running")

    def spawn(self, command, **kwargs) -> subprocess.Popen:
        process = subprocess.Popen(command, **pure.group_kwargs(), **kwargs)
        # A child started in the instant before the agent is SIGKILLed (before its group id reaches the watcher)
        # can survive; every other way of exiting is covered.
        try:
            code = self._watcher.poll()
            if code is not None:
                raise OSError(f"it exited with code {code}")
            self._watcher.stdin.write(f"{process.pid}\n".encode("ascii"))
            self._watcher.stdin.flush()
        except OSError as e:  # BrokenPipeError: the watcher died
            log.error("child cleanup watcher failed: %s", e)
            process.kill()
            process.wait()
            raise OSError("child cleanup watcher is not running") from e
        return process
