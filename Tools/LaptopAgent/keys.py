"""Keyboard events from the headset: the key names the protocol accepts, validation, and the rate limit.

Plain Python with no Windows calls, so it can be tested anywhere. dispatch.py turns accepted events into SendInput
calls through hostos (its Windows backend is winapi.py).

Keys are named, not sent as Windows virtual-key codes, so the headset doesn't depend on Windows. Only the keys below
exist: there is no Windows key, no Apps/menu key, no Print Screen and no media or power keys, so a headset can't open
the Start menu, Run dialog or other system shortcuts (Win+R, Win+L...). Ctrl+Alt+Del can't be injected on Windows at
all.
"""
import time
from dataclasses import dataclass, field
from typing import Callable

# name -> (Windows virtual-key code, extended key). Extended keys are the navigation block and arrows: without the flag
# Windows reads them as the numeric keypad's keys.
KEY_CODES: dict[str, tuple[int, bool]] = {
    **{chr(c): (c, False) for c in range(ord("A"), ord("Z") + 1)},
    **{str(d): (0x30 + d, False) for d in range(10)},
    **{f"F{n}": (0x6F + n, False) for n in range(1, 13)},
    "Enter": (0x0D, False),
    "Backspace": (0x08, False),
    "Tab": (0x09, False),
    "Escape": (0x1B, False),
    "Space": (0x20, False),
    "Shift": (0x10, False),
    "Ctrl": (0x11, False),
    "Alt": (0x12, False),
    "Left": (0x25, True),
    "Up": (0x26, True),
    "Right": (0x27, True),
    "Down": (0x28, True),
    "Insert": (0x2D, True),
    "Delete": (0x2E, True),
    "Home": (0x24, True),
    "End": (0x23, True),
    "PageUp": (0x21, True),
    "PageDown": (0x22, True),
    # US-layout punctuation (OEM keys).
    "Semicolon": (0xBA, False),
    "Equals": (0xBB, False),
    "Comma": (0xBC, False),
    "Minus": (0xBD, False),
    "Period": (0xBE, False),
    "Slash": (0xBF, False),
    "Backquote": (0xC0, False),
    "LeftBracket": (0xDB, False),
    "Backslash": (0xDC, False),
    "RightBracket": (0xDD, False),
    "Quote": (0xDE, False),
}

MODIFIERS = ("Shift", "Ctrl", "Alt")

# Cast mode (agent.py --cast-only): the only input a cast window may send, as {"type": "media", "action": "<name>"}.
# Media and volume keys go to Windows' media session (whatever is playing); seeking is the Left/Right arrow, which video
# players and browsers use for a few seconds back/forward, sent to the cast window only. Each is a single press and
# release with no modifier, so no combination (Win, Alt, Ctrl...) can ever be formed from them.
MEDIA_ACTIONS: dict[str, tuple[int, bool]] = {
    "play_pause": (0xB3, True),     # VK_MEDIA_PLAY_PAUSE
    "next": (0xB0, True),           # VK_MEDIA_NEXT_TRACK
    "previous": (0xB1, True),       # VK_MEDIA_PREV_TRACK
    "volume_up": (0xAF, True),      # VK_VOLUME_UP
    "volume_down": (0xAE, True),    # VK_VOLUME_DOWN
    "mute": (0xAD, True),           # VK_VOLUME_MUTE
    "seek_back": (0x25, True),      # Left arrow, to the cast window
    "seek_forward": (0x27, True),   # Right arrow, to the cast window
}
# These go to the cast window (it is brought to the front first); the rest are global media keys.
WINDOW_MEDIA_ACTIONS = ("seek_back", "seek_forward")
# Text longer than this per message is refused (the headset splits long text).
MAX_TEXT = 64
# At most this many keys held down at once per connection: enough for Ctrl+Shift+arrow, not for mashing.
MAX_HELD_KEYS = 6


@dataclass(frozen=True)
class KeyEvent:
    code: str
    vk: int
    extended: bool
    down: bool


def parse_key(message: dict) -> KeyEvent | None:
    """{"type": "key", "code": "<name>", "down": true|false} -> KeyEvent, or None if anything is off."""
    code = message.get("code")
    down = message.get("down")
    if not isinstance(code, str) or not isinstance(down, bool) or code not in KEY_CODES:
        return None
    vk, extended = KEY_CODES[code]
    return KeyEvent(code, vk, extended, down)


@dataclass(frozen=True)
class MediaAction:
    action: str
    vk: int
    extended: bool

    @property
    def to_window(self) -> bool:
        return self.action in WINDOW_MEDIA_ACTIONS


def parse_media(message: dict) -> MediaAction | None:
    """{"type": "media", "action": "<name from MEDIA_ACTIONS>"} -> MediaAction, or None for anything else."""
    action = message.get("action")
    if message.get("type") != "media" or not isinstance(action, str) or action not in MEDIA_ACTIONS:
        return None
    vk, extended = MEDIA_ACTIONS[action]
    return MediaAction(action, vk, extended)


def parse_text(message: dict) -> str | None:
    """{"type": "text", "text": "..."} -> the printable text, or None if empty, too long or not a string.
    Control characters (newline, tab, escape...) are removed: those are keys, sent as key events."""
    text = message.get("text")
    if not isinstance(text, str) or len(text) > MAX_TEXT:
        return None
    printable = "".join(c for c in text if c.isprintable())
    return printable or None


@dataclass
class RateLimiter:
    """Token bucket: `rate` events a second on average, bursts of up to `burst`. A typed character counts as one event,
    so text and key presses share the budget. Releasing a held key never costs anything (see dispatch.py)."""
    rate: float = 30.0
    burst: float = 60.0
    clock: Callable[[], float] = time.monotonic
    _tokens: float = field(default=-1.0, repr=False)
    _last: float = field(default=0.0, repr=False)

    def allow(self, cost: float = 1.0) -> bool:
        now = self.clock()
        if self._tokens < 0:
            self._tokens, self._last = self.burst, now
        self._tokens = min(self.burst, self._tokens + (now - self._last) * self.rate)
        self._last = now
        if cost > self._tokens:
            return False
        self._tokens -= cost
        return True
