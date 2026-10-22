"""The macOS backend's logic that needs no pyobjc: it imports (and is tested) on every OS.

Device-list parsing, crop maths, ffmpeg argument builders, the CGWindow-dict conversions, the approval dialog's
osascript command and output parser, the kill-chord matcher and the permission rows. The dicts use the string keys
Quartz documents (kCGWindowNumber and so on), so no Quartz constant is needed here.
"""
import math
import re
import sys
from dataclasses import dataclass

from ._ffmpeg import common_args
from .types import Monitor, Rect, Window

MIN_CAPTURE = 16  # a window smaller than this (in either direction) counts as minimized


@dataclass(frozen=True)
class Display:
    id: int
    bounds: Rect        # global points, top-left origin (CGDisplayBounds)
    pixels_wide: int    # CGDisplayPixelsWide: on a 2x display this is twice bounds.width


# --- CGWindow dictionaries ---------------------------------------------------------------------

def rect_from_bounds(bounds) -> Rect:
    x, y = float(bounds.get("X", 0)), float(bounds.get("Y", 0))
    return Rect(round(x), round(y), round(x + float(bounds.get("Width", 0))), round(y + float(bounds.get("Height", 0))))


def windows_from_cg(infos, own_pid: int) -> list[Window]:
    """Normal windows (layer 0) of other programs that have an owner or a title; the title is "Owner - Name"."""
    windows = []
    for info in infos:
        if info.get("kCGWindowLayer", 0) != 0 or info.get("kCGWindowOwnerPID") == own_pid:
            continue
        owner, name = str(info.get("kCGWindowOwnerName") or ""), str(info.get("kCGWindowName") or "")
        if not owner and not name:
            continue
        windows.append(Window(int(info["kCGWindowNumber"]), f"{owner} - {name}" if owner and name else owner or name))
    return windows


def window_ids(infos) -> set[int]:
    return {int(info["kCGWindowNumber"]) for info in infos}


def is_minimized(handle: int, all_infos, onscreen_infos) -> bool:
    """The window exists but is not on screen (minimized, hidden, or on another Space)."""
    return handle in window_ids(all_infos) and handle not in window_ids(onscreen_infos)


def bounds_of(handle: int, infos) -> Rect | None:
    for info in infos:
        if int(info["kCGWindowNumber"]) == handle:
            return rect_from_bounds(info.get("kCGWindowBounds") or {})
    return None


def monitors_from_displays(displays: list[Display], main_id: int) -> list[Monitor]:
    return [Monitor(i, d.id, d.bounds, d.id == main_id) for i, d in enumerate(displays)]


# --- Crop maths --------------------------------------------------------------------------------

def _clamp(window: Rect, displays: list[Display]) -> tuple[Display, Rect]:
    """The display holding the window's centre, and the window clamped to it (points)."""
    cx, cy = (window.left + window.right) / 2, (window.top + window.bottom) / 2
    for display in displays:
        b = display.bounds
        if b.left <= cx < b.right and b.top <= cy < b.bottom:
            break
    else:
        raise LookupError("minimized")
    b = display.bounds
    return display, Rect(max(window.left, b.left), max(window.top, b.top),
                         min(window.right, b.right), min(window.bottom, b.bottom))


def visible_rect(window: Rect, displays: list[Display]) -> Rect:
    """The part of the window that is on its display, in points; the output size follows it so nothing is stretched."""
    return _clamp(window, displays)[1]


def crop_window(window: Rect, displays: list[Display]) -> tuple[Display, tuple[int, int, int, int]]:
    """The display holding the window's centre and the (x, y, w, h) pixel crop of that display's capture.
    The window is clamped to the display and the values are floored to even numbers (what the encoders need).
    Raises LookupError("minimized") when no display holds the centre or the result is smaller than 16 pixels."""
    display, clamped = _clamp(window, displays)
    b = display.bounds
    scale = display.pixels_wide / b.width  # pixels_wide is real pixels (2x the points on a Retina display)
    x, y = round((clamped.left - b.left) * scale), round((clamped.top - b.top) * scale)
    w, h = round(clamped.width * scale), round(clamped.height * scale)
    x, y, w, h = x // 2 * 2, y // 2 * 2, w // 2 * 2, h // 2 * 2
    if w < MIN_CAPTURE or h < MIN_CAPTURE:
        raise LookupError("minimized")
    return display, (x, y, w, h)


# --- ffmpeg ------------------------------------------------------------------------------------

_SCREEN = re.compile(r"\[(\d+)\] Capture screen (\d+)")


def parse_screen_devices(stderr: str) -> dict[int, int]:
    """{N: avfoundation index} for every "Capture screen N" in `ffmpeg -f avfoundation -list_devices true -i ""`."""
    return {int(n): int(index) for index, n in _SCREEN.findall(stderr)}


def capture_args(index: int, fps: int, width: int, height: int, pix_fmt: str,
                 crop: tuple[int, int, int, int] | None) -> list[str]:
    chain = "[0:v]"
    if crop is not None:
        x, y, w, h = crop
        chain += f"crop={w}:{h}:{x}:{y},"
    chain += f"scale={width}:{height}:flags=bilinear,format={pix_fmt}[v]"
    return ["-f", "avfoundation", "-capture_cursor", "1", "-framerate", str(fps), "-pixel_format", "bgr0",
            "-i", f"{index}:none", "-filter_complex", chain, "-map", "[v]", "-fps_mode", "vfr"]


def encoder_args(name: str, codec: str, fps: int, bitrate: int) -> list[str]:
    if name == "videotoolbox":
        head = ["-c:v", codec, "-realtime", "1", "-prio_speed", "1", "-profile:v", "baseline"]
    else:
        head = ["-c:v", codec, "-preset", "ultrafast", "-tune", "zerolatency", "-profile:v", "baseline"]
    return head + common_args(fps, bitrate)


# --- Child process groups ----------------------------------------------------------------------

def group_kwargs(version: tuple | None = None) -> dict:
    """Popen arguments that give a child a process group (and session) of its own, so the reaper can kill the group."""
    version = tuple(version or sys.version_info)
    return {"process_group": 0} if version[:2] >= (3, 11) else {"start_new_session": True}


# --- Approval dialog (osascript) -----------------------------------------------------------------

# The text, title and timeout travel as argv items, never inside the script, so no text can change what runs.
# No `tell application`: that would ask for the Automation permission.
SCRIPT = ('on run argv\n'
          'display dialog (item 1 of argv) with title (item 2 of argv) buttons {"No", "Yes"} default button "No" '
          'cancel button "No" with icon caution giving up after (item 3 of argv as integer)\n'
          'end run')


def osascript_argv(text: str, caption: str, timeout_s: int) -> list[str]:
    return ["osascript", "-e", SCRIPT, "--", text, caption, str(timeout_s)]


def timeout_seconds(timeout_ms: int) -> int:
    return max(1, math.ceil(timeout_ms / 1000))


def parse_dialog(returncode: int, stdout: str, stderr: str) -> str:
    """"yes", "no" or "timeout". Anything unexpected (a kill, empty output, an error) is "no"."""
    if returncode == 0:
        out = stdout or ""
        if "gave up:true" in out:
            return "timeout"
        if "button returned:Yes" in out:
            return "yes"
    return "no"  # includes the Cancel button, which osascript reports as error (-128) with code 1


# --- Kill chord (Ctrl+Option+Shift+Q) ------------------------------------------------------------

KILL_KEYCODE = 12  # kVK_ANSI_Q
FLAG_CONTROL, FLAG_OPTION, FLAG_SHIFT_KEY, FLAG_COMMAND_KEY = 0x40000, 0x80000, 0x20000, 0x100000
TAP_DISABLED_BY_TIMEOUT, TAP_DISABLED_BY_USER_INPUT = 0xFFFFFFFE, 0xFFFFFFFF


def is_kill_chord(flags: int, keycode: int, autorepeat: bool = False) -> bool:
    """Control, Option and Shift held and Command not (the headset's Ctrl becomes Command, so it can't match)."""
    if autorepeat or keycode != KILL_KEYCODE:
        return False
    needed = FLAG_CONTROL | FLAG_OPTION | FLAG_SHIFT_KEY
    return flags & needed == needed and not flags & FLAG_COMMAND_KEY


# --- Permission rows -----------------------------------------------------------------------------

SCREEN, ACCESSIBILITY, LISTEN = "Screen Recording", "Accessibility", "Input Monitoring"
_PANE = "x-apple.systempreferences:com.apple.preference.security?Privacy_"


@dataclass(frozen=True)
class Row:
    name: str
    ok: bool
    settings_path: str
    url: str
    blocks: str  # what doesn't work without it

    @property
    def text(self) -> str:
        return (f"{self.name} permission is missing: {self.blocks}.\n"
                f"  Allow it in {self.settings_path}. It belongs to the app that started Python (Terminal, iTerm, "
                f"VS Code ...), so quit and reopen that app after granting it.\n"
                f"  Shortcut: open \"{self.url}\"")


def permission_rows(screen: bool, ax: bool, listen: bool) -> list[Row]:
    base = "System Settings > Privacy & Security > "
    return [
        Row(SCREEN, screen, base + "Screen & System Audio Recording (Screen Recording on macOS 13-14)",
            _PANE + "ScreenCapture", "windows and screens can't be captured or listed by name"),
        Row(ACCESSIBILITY, ax, base + "Accessibility", _PANE + "Accessibility",
            "the headset can't click, type or raise windows"),
        Row(LISTEN, listen, base + "Input Monitoring", _PANE + "ListenEvent",
            "the kill-switch hotkey can't be heard (Ctrl+C in this terminal still stops the agent)"),
    ]
