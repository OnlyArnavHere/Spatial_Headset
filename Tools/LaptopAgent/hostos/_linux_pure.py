"""The Linux (X11) backend's logic that needs no ctypes or X server: it imports (and is tested) on every OS.

xrandr output parsing, crop maths, ffmpeg x11grab and libx264 arguments, window-property decoding, the
_NET_ACTIVE_WINDOW client message, the identity-key file check, the session rows for the preflight, the Windows
VK to keysym table, wheel and text maths, the kill-hotkey grab masks and the approval-dialog commands.
"""
import math
import re
import sys
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

SESSION, X11GRAB, INPUT, PROMPT = "X11 session", "ffmpeg x11grab", "Input", "Approval prompt"


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


def permission_rows(env, x11grab: bool | None, xtest: bool | None = None, prompt: bool | None = None) -> list[Row]:
    """The X11 session row, plus a row for each probe that ran and failed: x11grab, libXtst (input) and a dialog
    program for the approval prompt. None means the probe wasn't run (or couldn't be)."""
    rows = [session_row(env)]
    if x11grab is not None:
        rows.append(Row(X11GRAB, x11grab, "ffmpeg x11grab support is missing: this ffmpeg build can't capture an X11 "
                        "screen.\n  Install a build with x11grab (for example your distribution's ffmpeg package)."))
    if xtest is False:
        rows.append(Row(INPUT, False, "libXtst is missing, so the headset can't click, type or raise windows.\n  Install "
                        "it (for example libxtst6); view-only streaming works without --input."))
    if prompt is False:
        rows.append(Row(PROMPT, False, "No approval dialog is available: install zenity (or python3-tk). Without one "
                        "every approval request is denied, so no headset can connect."))
    return rows


# --- Keys: Windows virtual-key code -> X keysym ------------------------------------------------

_OEM = {0xBA: 0x3B, 0xBB: 0x3D, 0xBC: 0x2C, 0xBD: 0x2D, 0xBE: 0x2E, 0xBF: 0x2F, 0xC0: 0x60,
        0xDB: 0x5B, 0xDC: 0x5C, 0xDD: 0x5D, 0xDE: 0x27}  # ; = , - . / ` [ \ ] '

VK_TO_KEYSYM: dict[int, int] = {
    **{vk: vk + 0x20 for vk in range(0x41, 0x5B)},  # A-Z -> a-z
    **{vk: vk for vk in range(0x30, 0x3A)},  # 0-9
    **{0x70 + n: 0xFFBE + n for n in range(12)},  # F1-F12
    **_OEM,
    0x0D: 0xFF0D, 0x1B: 0xFF1B, 0x09: 0xFF09, 0x08: 0xFF08, 0x20: 0x20, 0x14: 0xFFE5,
    0x10: 0xFFE1, 0x11: 0xFFE3, 0x12: 0xFFE9,  # Shift, Ctrl, Alt (left ones)
    0xA0: 0xFFE1, 0xA1: 0xFFE2, 0xA2: 0xFFE3, 0xA3: 0xFFE4, 0xA4: 0xFFE9, 0xA5: 0xFFEA,
    0x5B: 0xFFEB, 0x5C: 0xFFEC,  # Windows keys -> Super
    0x25: 0xFF51, 0x26: 0xFF52, 0x27: 0xFF53, 0x28: 0xFF54,
    0x2D: 0xFF63, 0x2E: 0xFFFF, 0x24: 0xFF50, 0x23: 0xFF57, 0x21: 0xFF55, 0x22: 0xFF56,
    0xAD: 0x1008FF12, 0xAE: 0x1008FF11, 0xAF: 0x1008FF13,  # XF86AudioMute, LowerVolume, RaiseVolume
    0xB0: 0x1008FF17, 0xB1: 0x1008FF16, 0xB2: 0x1008FF15, 0xB3: 0x1008FF14,  # Next, Prev, Stop, Play
}
SHIFT_KEYSYM = 0xFFE1


def keysym_for(vk: int) -> int:
    try:
        return VK_TO_KEYSYM[vk]
    except KeyError:
        raise ValueError(f"no X keysym for virtual-key code {vk:#x}") from None


def char_keysym(ch: str) -> int | None:
    """The keysym that types the character, or None for control characters (newline and tab are Return and Tab)."""
    code = ord(ch)
    if ch == "\n":
        return 0xFF0D
    if ch == "\t":
        return 0xFF09
    if code < 0x20 or 0x7F <= code < 0xA0:
        return None
    return code if code <= 0xFF else 0x01000000 + code


def shift_level(keysym: int, level0: int, level1: int) -> bool | None:
    """Whether Shift is needed to type the keysym on the key that has it (its keysyms at level 0 and 1); None if
    neither level has it."""
    if level0 == keysym:
        return False
    return True if level1 == keysym else None


BUTTONS = {"left": 1, "middle": 2, "right": 3}
WHEEL_UP, WHEEL_DOWN, WHEEL_LEFT, WHEEL_RIGHT = 4, 5, 6, 7


def button_number(name: str) -> int:
    try:
        return BUTTONS[name]
    except KeyError:
        raise ValueError(f"no X button for {name!r}") from None


class WheelAccumulator:
    """Notches (positive is up, or right) to whole button clicks, carrying the fraction to the next call."""

    def __init__(self) -> None:
        self._remainder = 0.0

    def buttons(self, notches: float, horizontal: bool = False) -> list[int]:
        total = self._remainder + notches
        whole = int(total)  # towards zero
        self._remainder = total - whole
        positive, negative = (WHEEL_RIGHT, WHEEL_LEFT) if horizontal else (WHEEL_UP, WHEEL_DOWN)
        return [positive if whole > 0 else negative] * abs(whole)


# --- Kill hotkey: Ctrl+Alt+Shift+Q ---------------------------------------------------------------

KILL_KEYSYM = 0x71  # q
SHIFT_MASK, LOCK_MASK, CONTROL_MASK, MOD1_MASK, MOD2_MASK = 1, 2, 4, 8, 16  # Mod1 is Alt, Mod2 is NumLock


def kill_masks() -> list[int]:
    """XGrabKey matches modifiers exactly, so the chord is grabbed with every NumLock and CapsLock combination."""
    base = CONTROL_MASK | MOD1_MASK | SHIFT_MASK
    return [base | extra for extra in (0, LOCK_MASK, MOD2_MASK, LOCK_MASK | MOD2_MASK)]


# --- Approval dialog -------------------------------------------------------------------------------

EXIT_YES, EXIT_NO, EXIT_TIMEOUT = 0, 1, 5  # zenity's codes; the tkinter fallback script uses the same


def timeout_seconds(timeout_ms: int) -> int:
    return max(1, math.ceil(timeout_ms / 1000))


def zenity_argv(text: str, caption: str, timeout_s: int) -> list[str]:
    """Text and title are single argv items (never a shell string); the default button is No."""
    return ["zenity", "--question", "--no-markup", "--default-cancel", f"--timeout={timeout_s}",
            "--ok-label=Yes", "--cancel-label=No", f"--title={caption}", f"--text={text}"]


def tk_argv(script: str, text: str, caption: str, timeout_s: int) -> list[str]:
    if getattr(sys, "frozen", False):
        return [sys.executable, "--_linux_dialog", text, caption, str(timeout_s)]
    return [sys.executable, "-I", script, text, caption, str(timeout_s)]


def answer_for(returncode: int | None) -> str:
    """"yes", "no" or "timeout" from a dialog's exit code. Anything unexpected (a kill, an error) is "no"."""
    return {EXIT_YES: "yes", EXIT_TIMEOUT: "timeout"}.get(returncode, "no")
