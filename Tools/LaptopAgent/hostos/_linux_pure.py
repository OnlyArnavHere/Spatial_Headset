"""The Linux (X11) backend's logic that needs no ctypes or X server: it imports (and is tested) on every OS.

xrandr output parsing, crop maths, ffmpeg x11grab and libx264 arguments, window-property decoding, the
_NET_ACTIVE_WINDOW client message, the identity-key file check and the session rows for the preflight.
"""
import re
from dataclasses import dataclass

from ._ffmpeg import common_args
from .types import Monitor, Rect

MIN_CAPTURE = 16  # a window smaller than this (in either direction) counts as minimized

# --- Monitors ----------------------------------------------------------------------------------

# " 0: +*eDP-1 1920/344x1080/194+0+0  eDP-1": '*' marks the primary output; sizes are pixels/millimetres
_MONITOR = re.compile(r"^\s*(\d+):\s+([+*]*)(\S+)\s+(\d+)/\d+x(\d+)/\d+([+-]\d+)([+-]\d+)")


def parse_listmonitors(text: str) -> list[Monitor]:
    """Monitors from `xrandr --listmonitors`; the handle is the xrandr index. With no '*' the first one is primary."""
    found = []
    for line in text.splitlines():
        m = _MONITOR.match(line)
        if m:
            x, y = int(m.group(6)), int(m.group(7))
            found.append((int(m.group(1)), "*" in m.group(2), Rect(x, y, x + int(m.group(4)), y + int(m.group(5)))))
    any_primary = any(primary for _, primary, _ in found)
    return [Monitor(i, i, rect, primary or (not any_primary and n == 0)) for n, (i, primary, rect) in enumerate(found)]


def visible_rect(window: Rect, monitors: list[Monitor]) -> Rect:
    """The part of the window on the monitor holding its centre. Raises LookupError("minimized") if no monitor does."""
    cx, cy = (window.left + window.right) / 2, (window.top + window.bottom) / 2
    for monitor in monitors:
        b = monitor.rect
        if b.left <= cx < b.right and b.top <= cy < b.bottom:
            return Rect(max(window.left, b.left), max(window.top, b.top), min(window.right, b.right),
                        min(window.bottom, b.bottom))
    raise LookupError("minimized")


def capture_rect(window: Rect, monitors: list[Monitor]) -> Rect:
    """visible_rect with the position and size floored to even numbers (what the encoders need)."""
    r = visible_rect(window, monitors)
    left, top = r.left // 2 * 2, r.top // 2 * 2
    out = Rect(left, top, left + r.width // 2 * 2, top + r.height // 2 * 2)
    if out.width < MIN_CAPTURE or out.height < MIN_CAPTURE:
        raise LookupError("minimized")
    return out


# --- ffmpeg ------------------------------------------------------------------------------------

def x11grab_args(display: str, rect: Rect, fps: int, width: int, height: int, pix_fmt: str) -> list[str]:
    return ["-f", "x11grab", "-draw_mouse", "1", "-framerate", str(fps), "-video_size", f"{rect.width}x{rect.height}",
            "-i", f"{display}+{rect.left},{rect.top}",
            "-vf", f"scale={width}:{height}:flags=bilinear,format={pix_fmt}", "-fps_mode", "vfr"]


def encoder_args(name: str, codec: str, fps: int, bitrate: int) -> list[str]:
    return ["-c:v", codec, "-preset", "ultrafast", "-tune", "zerolatency", "-profile:v", "baseline"] \
        + common_args(fps, bitrate)


def has_x11grab(devices_output: str) -> bool:
    """Whether `ffmpeg -devices` lists x11grab as an input device."""
    return re.search(r"^\s*D\S*\s+x11grab\b", devices_output, re.MULTILINE) is not None


# --- Window properties -------------------------------------------------------------------------

def window_title(net_wm_name: bytes | None, wm_name: bytes | None) -> str:
    raw = net_wm_name or wm_name or b""
    return raw.decode("utf-8", "replace").replace("\0", "").strip()


def active_window_message() -> tuple[int, tuple[int, ...]]:
    """(format, data) of the _NET_ACTIVE_WINDOW client message: source 2 (a pager, so focus isn't refused), no
    timestamp, no currently active window."""
    return 32, (2, 0, 0, 0, 0)


# --- Identity key file ---------------------------------------------------------------------------

def key_file_problem(mode: int, uid: int, my_uid: int) -> str | None:
    """Why identity.key can't be trusted, or None. It must be ours and not readable by group or others."""
    if uid != my_uid:
        return f"it is owned by uid {uid}, not by this user (uid {my_uid})"
    if mode & 0o077:
        return f"its permissions are {mode & 0o777:03o}; it must be readable by its owner only (chmod 600)"
    return None


# --- Session rows --------------------------------------------------------------------------------

SESSION, X11GRAB, INPUT = "X11 session", "ffmpeg x11grab", "Input"


@dataclass(frozen=True)
class Row:
    name: str
    ok: bool
    text: str


def is_wayland(env) -> bool:
    return env.get("XDG_SESSION_TYPE", "").lower() == "wayland" or bool(env.get("WAYLAND_DISPLAY"))


def session_row(env) -> Row:
    if is_wayland(env):
        return Row(SESSION, False,
                   "X11 session is missing: this is a Wayland session, and the Linux backend captures X11 only "
                   "(XWayland would show black).\n  Log in with an X11 (Xorg) session. The future route for Wayland "
                   "is the xdg-desktop-portal ScreenCast and RemoteDesktop interfaces, which are not implemented yet.")
    if not env.get("DISPLAY"):
        return Row(SESSION, False, "X11 session is missing: DISPLAY is not set, so there is no X server to capture.\n"
                   "  Run the agent from a desktop session on X11 (not over plain SSH).")
    return Row(SESSION, True, "")


def permission_rows(env, x11grab: bool | None) -> list[Row]:
    """The X11 session row, and an x11grab row when ffmpeg was probed (None means it wasn't or couldn't be)."""
    rows = [session_row(env)]
    if x11grab is not None:
        rows.append(Row(X11GRAB, x11grab, "ffmpeg x11grab support is missing: this ffmpeg build can't capture an X11 "
                        "screen.\n  Install a build with x11grab (for example your distribution's ffmpeg package)."))
    rows.append(Row(INPUT, False, "Input is not implemented on Linux yet: the headset can't click, type or raise windows "
                    "(view-only streaming works without --input)."))
    return rows
