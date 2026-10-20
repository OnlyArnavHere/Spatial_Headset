"""The parts of macOS input that need no pyobjc, so they are tested on any OS: the Windows-VK to kVK table, modifier
state, media-key data, text chunks, wheel maths, click counting and drag selection, and window-list lookups."""
# Windows virtual-key code -> ANSI kVK_* from HIToolbox/Events.h. The extended flag is ignored on macOS.
_LETTERS = {"A": 0x00, "S": 0x01, "D": 0x02, "F": 0x03, "H": 0x04, "G": 0x05, "Z": 0x06, "X": 0x07, "C": 0x08,
            "V": 0x09, "B": 0x0B, "Q": 0x0C, "W": 0x0D, "E": 0x0E, "R": 0x0F, "Y": 0x10, "T": 0x11, "O": 0x1F,
            "U": 0x20, "I": 0x22, "P": 0x23, "L": 0x25, "J": 0x26, "K": 0x28, "N": 0x2D, "M": 0x2E}
_DIGITS = [0x1D, 0x12, 0x13, 0x14, 0x15, 0x17, 0x16, 0x1A, 0x1C, 0x19]  # 0..9
_FUNCTION = [0x7A, 0x78, 0x63, 0x76, 0x60, 0x61, 0x62, 0x64, 0x65, 0x6D, 0x67, 0x6F]  # F1..F12

VK_TO_KVK: dict[int, int] = {
    **{ord(letter): kvk for letter, kvk in _LETTERS.items()},
    **{0x30 + d: kvk for d, kvk in enumerate(_DIGITS)},
    **{0x70 + n: kvk for n, kvk in enumerate(_FUNCTION)},
    0x0D: 0x24,  # Enter -> Return
    0x08: 0x33,  # Backspace -> Delete
    0x09: 0x30, 0x1B: 0x35, 0x20: 0x31,
    0x10: 0x38, 0x11: 0x37, 0x12: 0x3A,  # Shift, Ctrl -> Command, Alt -> Option
    0x25: 0x7B, 0x26: 0x7E, 0x27: 0x7C, 0x28: 0x7D,
    0x2D: 0x72,  # Insert -> Help
    0x2E: 0x75,  # Delete -> forward delete
    0x24: 0x73, 0x23: 0x77, 0x21: 0x74, 0x22: 0x79,
    0xBA: 0x29, 0xBB: 0x18, 0xBC: 0x2B, 0xBD: 0x1B, 0xBE: 0x2F, 0xBF: 0x2C, 0xC0: 0x32,
    0xDB: 0x21, 0xDC: 0x2A, 0xDD: 0x1E, 0xDE: 0x27,
}


def kvk_for(vk: int) -> int:
    try:
        return VK_TO_KVK[vk]
    except KeyError:
        raise ValueError(f"no macOS key for virtual-key code {vk:#x}") from None


# --- Modifiers ---------------------------------------------------------------------------------

# Ctrl acts as Command, Alt as Option; the Mac Control key is unreachable by design.
FLAG_SHIFT, FLAG_ALTERNATE, FLAG_COMMAND = 0x20000, 0x80000, 0x100000
MODIFIER_FLAGS = {0x10: FLAG_SHIFT, 0x11: FLAG_COMMAND, 0x12: FLAG_ALTERNATE}


class Modifiers:
    """Which modifier keys are held; mask is what every key and mouse event carries."""

    def __init__(self) -> None:
        self._held: set[int] = set()

    def update(self, vk: int, down: bool) -> int:
        if vk in MODIFIER_FLAGS:
            (self._held.add if down else self._held.discard)(vk)
        return self.mask

    @property
    def held(self) -> set[int]:
        return set(self._held)

    @property
    def mask(self) -> int:
        mask = 0
        for vk in self._held:
            mask |= MODIFIER_FLAGS[vk]
        return mask


# --- Media keys --------------------------------------------------------------------------------

MEDIA_VK_TO_NX = {0xB3: 16, 0xB0: 17, 0xB1: 18, 0xAF: 0, 0xAE: 1, 0xAD: 7}  # NX_KEYTYPE_*


def media_data1(nx: int, down: bool) -> int:
    return (nx << 16) | ((0xA if down else 0xB) << 8)


def media_flags(down: bool) -> int:
    return 0xA00 if down else 0xB00


# --- Text --------------------------------------------------------------------------------------

def unicode_chunks(text: str, limit: int = 20) -> list[str]:
    """Pieces of at most `limit` UTF-16 units; a surrogate pair is never split."""
    chunks, current, units = [], [], 0
    for ch in text:
        n = 2 if ord(ch) > 0xFFFF else 1
        if units + n > limit:
            chunks.append("".join(current))
            current, units = [], 0
        current.append(ch)
        units += n
    if current:
        chunks.append("".join(current))
    return chunks


def utf16_length(text: str) -> int:
    return sum(2 if ord(ch) > 0xFFFF else 1 for ch in text)


# --- Mouse -------------------------------------------------------------------------------------

LINES_PER_NOTCH = 3


class WheelAccumulator:
    """Notches (a Windows WHEEL_DELTA of 120 is one notch) to whole lines, carrying the fraction to the next call."""

    def __init__(self) -> None:
        self._remainder = 0.0

    def lines(self, notches: float) -> int:
        total = self._remainder + notches * LINES_PER_NOTCH
        whole = int(total)  # towards zero
        self._remainder = total - whole
        return whole


# Quartz event types (CGEventType) and the buttons that go with them.
LEFT_DOWN, LEFT_UP, RIGHT_DOWN, RIGHT_UP, MOUSE_MOVED, LEFT_DRAGGED, RIGHT_DRAGGED = 1, 2, 3, 4, 5, 6, 7


def button_event(button: str, down: bool) -> int:
    if button == "right":
        return RIGHT_DOWN if down else RIGHT_UP
    return LEFT_DOWN if down else LEFT_UP


class ButtonState:
    """Held buttons: a move while one is held is a drag, and a drag of the left button wins over the right."""

    def __init__(self) -> None:
        self._held: set[str] = set()

    def set(self, button: str, down: bool) -> None:
        (self._held.add if down else self._held.discard)(button)

    @property
    def held(self):
        return set(self._held)

    def move_event(self) -> int:
        if "left" in self._held:
            return LEFT_DRAGGED
        if "right" in self._held:
            return RIGHT_DRAGGED
        return MOUSE_MOVED


class ClickCounter:
    """kCGMouseEventClickState: 2 for a double click and so on. A press continues the count when it comes within the
    double-click interval and 4 points of the previous press (same button); the release repeats the press's count."""

    DISTANCE = 4.0

    def __init__(self) -> None:
        self._last: tuple[str, float, float, float] | None = None
        self._count = 1

    def press(self, button: str, x: float, y: float, now: float, interval: float) -> int:
        last = self._last
        if (last and last[0] == button and now - last[3] <= interval
                and abs(x - last[1]) <= self.DISTANCE and abs(y - last[2]) <= self.DISTANCE):
            self._count += 1
        else:
            self._count = 1
        self._last = (button, x, y, now)
        return self._count

    def release(self) -> int:
        return self._count


# --- Window lists (CGWindowListCopyWindowInfo dictionaries) --------------------------------------

def _contains(bounds: dict, x: float, y: float) -> bool:
    left, top = bounds.get("X", 0.0), bounds.get("Y", 0.0)
    return left <= x < left + bounds.get("Width", 0.0) and top <= y < top + bounds.get("Height", 0.0)


def window_at(x: float, y: float, onscreen: list[dict]) -> int:
    """The first window, front to back, that has a visible pixel at the point; 0 if none."""
    for info in onscreen:
        if (info.get("kCGWindowLayer", 0) >= 0 and info.get("kCGWindowAlpha", 1.0) > 0
                and _contains(info.get("kCGWindowBounds") or {}, x, y)):
            return int(info["kCGWindowNumber"])
    return 0


def frontmost_window(pid: int, onscreen: list[dict]) -> int:
    for info in onscreen:
        if info.get("kCGWindowLayer", 0) == 0 and info.get("kCGWindowOwnerPID") == pid:
            return int(info["kCGWindowNumber"])
    return 0


def info_of(handle: int, infos: list[dict]) -> dict | None:
    for info in infos:
        if info.get("kCGWindowNumber") == handle:
            return info
    return None


def ax_matches(title, position, size, name, bounds: dict, tolerance: float = 1.0) -> bool:
    """Whether an accessibility window (title, (x, y), (w, h)) is the CG window with this name and bounds."""
    return ((title or "") == (name or "")
            and abs(position[0] - bounds.get("X", 0.0)) <= tolerance and abs(position[1] - bounds.get("Y", 0.0)) <= tolerance
            and abs(size[0] - bounds.get("Width", 0.0)) <= tolerance and abs(size[1] - bounds.get("Height", 0.0)) <= tolerance)



def pick_ax_window(candidates: list[tuple], name, bounds: dict, tolerance: float = 1.0):
    """The accessibility window for a CG window. candidates are (element, title, (x, y), (w, h)). A window whose title
    and bounds both match wins; failing that, a window with an empty AX title is matched on bounds alone, but only
    when exactly one such window fits. Returns the element or None."""
    for element, title, position, size in candidates:
        if ax_matches(title, position, size, name, bounds, tolerance):
            return element
    bare = [element for element, title, position, size in candidates
            if not title and ax_matches("", position, size, "", bounds, tolerance)]
    return bare[0] if len(bare) == 1 else None


def release_list(held_modifiers, held_buttons) -> list[tuple]:
    """What to post to let go of everything still held: ("key", vk) for each modifier, then ("button", name)."""
    return [("key", vk) for vk in sorted(held_modifiers)] + [("button", b) for b in sorted(held_buttons)]


class TtlCache:
    """Remembers one value for `ttl` seconds."""

    def __init__(self, ttl: float, clock) -> None:
        self._ttl, self._clock = ttl, clock
        self._value, self._at = None, None

    def get(self, make):
        now = self._clock()
        if self._at is None or now - self._at >= self._ttl:
            self._value, self._at = make(), now
        return self._value
