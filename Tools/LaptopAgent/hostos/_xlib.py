"""The few libX11 and libXtst calls the Linux backend needs, through ctypes (both are MIT licensed; nothing loads at
import).

XInitThreads is called before any other Xlib call, and each connect() opens its own display connection, so one
connection is only ever used by one thread at a time.
"""
import ctypes
import ctypes.util
import threading

from .types import Rect

CLIENT_MESSAGE = 33
SUBSTRUCTURE_MASK = (1 << 20) | (1 << 19)  # SubstructureRedirect | SubstructureNotify
ANY_PROPERTY_TYPE = 0

KEY_PRESS = 2
GRAB_MODE_ASYNC = 1

_lib = None
_xtst = None
_handler = None
_load_lock = threading.Lock()
BAD_ACCESS, X_GRAB_KEY = 10, 33
_grab_lock = threading.RLock()
_grab = {"display": None, "refused": False}  # set while grab_key runs: which display, and whether it was refused


class _ClientMessage(ctypes.Structure):
    _fields_ = [("type", ctypes.c_int), ("serial", ctypes.c_ulong), ("send_event", ctypes.c_int),
                ("display", ctypes.c_void_p), ("window", ctypes.c_ulong), ("message_type", ctypes.c_ulong),
                ("format", ctypes.c_int), ("data", ctypes.c_long * 5)]


class _ErrorEvent(ctypes.Structure):
    _fields_ = [("type", ctypes.c_int), ("display", ctypes.c_void_p), ("resourceid", ctypes.c_ulong),
                ("serial", ctypes.c_ulong), ("error_code", ctypes.c_ubyte), ("request_code", ctypes.c_ubyte),
                ("minor_code", ctypes.c_ubyte)]


class _Event(ctypes.Union):
    _fields_ = [("type", ctypes.c_int), ("client", _ClientMessage), ("pad", ctypes.c_long * 24)]


def _on_error(display, event) -> int:
    """The X error handler: it only notes a refused key grab (BadAccess on XGrabKey) on the display being grabbed; every
    other error, from any connection, is ignored (a window can vanish between two calls)."""
    with _grab_lock:
        if (_grab["display"] is not None and display == _grab["display"] and event.error_code == BAD_ACCESS
                and event.request_code == X_GRAB_KEY):
            _grab["refused"] = True
    return 0


def _load():
    if _lib is not None:
        return _lib
    with _load_lock:
        return _load_locked()


def _load_locked():
    global _lib, _handler
    if _lib is not None:
        return _lib
    name = ctypes.util.find_library("X11")
    if not name:
        raise OSError("libX11 is not installed")
    lib = ctypes.CDLL(name)
    lib.XInitThreads()
    c = ctypes
    sigs = {
        "XOpenDisplay": (c.c_void_p, [c.c_char_p]), "XCloseDisplay": (c.c_int, [c.c_void_p]),
        "XDefaultRootWindow": (c.c_ulong, [c.c_void_p]), "XInternAtom": (c.c_ulong, [c.c_void_p, c.c_char_p, c.c_int]),
        "XFree": (c.c_int, [c.c_void_p]), "XFlush": (c.c_int, [c.c_void_p]),
        "XDisplayWidth": (c.c_int, [c.c_void_p, c.c_int]), "XDisplayHeight": (c.c_int, [c.c_void_p, c.c_int]),
        "XGetWindowProperty": (c.c_int, [c.c_void_p, c.c_ulong, c.c_ulong, c.c_long, c.c_long, c.c_int, c.c_ulong,
                                         c.POINTER(c.c_ulong), c.POINTER(c.c_int), c.POINTER(c.c_ulong),
                                         c.POINTER(c.c_ulong), c.POINTER(c.POINTER(c.c_ubyte))]),
        "XGetGeometry": (c.c_int, [c.c_void_p, c.c_ulong, c.POINTER(c.c_ulong), c.POINTER(c.c_int),
                                   c.POINTER(c.c_int), c.POINTER(c.c_uint), c.POINTER(c.c_uint),
                                   c.POINTER(c.c_uint), c.POINTER(c.c_uint)]),
        "XTranslateCoordinates": (c.c_int, [c.c_void_p, c.c_ulong, c.c_ulong, c.c_int, c.c_int, c.POINTER(c.c_int),
                                            c.POINTER(c.c_int), c.POINTER(c.c_ulong)]),
        "XSendEvent": (c.c_int, [c.c_void_p, c.c_ulong, c.c_int, c.c_long, c.POINTER(_Event)]),
        "XKeysymToKeycode": (c.c_ubyte, [c.c_void_p, c.c_ulong]),
        "XkbKeycodeToKeysym": (c.c_ulong, [c.c_void_p, c.c_uint, c.c_uint, c.c_uint]),
        "XGrabKey": (c.c_int, [c.c_void_p, c.c_int, c.c_uint, c.c_ulong, c.c_int, c.c_int, c.c_int]),
        "XSync": (c.c_int, [c.c_void_p, c.c_int]), "XNextEvent": (c.c_int, [c.c_void_p, c.POINTER(_Event)]),
    }
    for fn, (res, args) in sigs.items():
        getattr(lib, fn).restype, getattr(lib, fn).argtypes = res, args
    # The default error handler exits the process, so replace it; a window can vanish between two calls
    handler = c.CFUNCTYPE(c.c_int, c.c_void_p, c.POINTER(_ErrorEvent))(
        lambda display, event: _on_error(display, event.contents))
    lib.XSetErrorHandler.argtypes = [c.c_void_p]
    lib.XSetErrorHandler(handler)
    _handler = handler  # kept alive for the life of the process
    _lib = lib  # last, so no thread sees a half-initialised library
    return lib


def _load_xtst():
    global _xtst
    if _xtst is None:
        name = ctypes.util.find_library("Xtst")
        if not name:
            raise OSError("libXtst is not installed")
        lib, c = ctypes.CDLL(name), ctypes
        sigs = {"XTestFakeKeyEvent": [c.c_void_p, c.c_uint, c.c_int, c.c_ulong],
                "XTestFakeButtonEvent": [c.c_void_p, c.c_uint, c.c_int, c.c_ulong],
                "XTestFakeMotionEvent": [c.c_void_p, c.c_int, c.c_int, c.c_int, c.c_ulong]}
        for fn, args in sigs.items():
            getattr(lib, fn).restype, getattr(lib, fn).argtypes = c.c_int, args
        _xtst = lib
    return _xtst


def has_xtest() -> bool:
    return ctypes.util.find_library("Xtst") is not None


class Connection:
    def __init__(self, display: str | None = None) -> None:
        self._x = _load()
        self._display = self._x.XOpenDisplay(display.encode() if display else None)
        if not self._display:
            raise OSError("can't open the X display (is DISPLAY set and the X server running?)")
        self.root = int(self._x.XDefaultRootWindow(self._display))
        self._atoms: dict[str, int] = {}

    def __enter__(self):
        return self

    def __exit__(self, *exc) -> None:
        self.close()

    def close(self) -> None:
        if self._display:
            self._x.XCloseDisplay(self._display)
            self._display = None

    def intern(self, name: str) -> int:
        if name not in self._atoms:
            self._atoms[name] = int(self._x.XInternAtom(self._display, name.encode(), 0))
        return self._atoms[name]

    def screen_size(self) -> tuple[int, int]:
        return int(self._x.XDisplayWidth(self._display, 0)), int(self._x.XDisplayHeight(self._display, 0))

    def property(self, window: int, name: str) -> list[int] | bytes | None:
        """A window property: a list of ints for 32-bit formats (atoms, windows), else bytes; None if absent."""
        kind, fmt = ctypes.c_ulong(), ctypes.c_int()
        count, after = ctypes.c_ulong(), ctypes.c_ulong()
        data = ctypes.POINTER(ctypes.c_ubyte)()
        status = self._x.XGetWindowProperty(self._display, window, self.intern(name), 0, 65536, 0, ANY_PROPERTY_TYPE,
                                            ctypes.byref(kind), ctypes.byref(fmt), ctypes.byref(count),
                                            ctypes.byref(after), ctypes.byref(data))
        if status != 0 or not data:
            return None
        try:
            if fmt.value == 0 or count.value == 0:
                return None
            if fmt.value == 32:  # Xlib hands 32-bit items over as C longs
                return list(ctypes.cast(data, ctypes.POINTER(ctypes.c_ulong))[:count.value])
            return ctypes.string_at(data, count.value * fmt.value // 8)
        finally:
            self._x.XFree(data)

    def geometry(self, window: int) -> Rect | None:
        """The window's client area in root-window coordinates, or None if the window is gone."""
        root, x, y = ctypes.c_ulong(), ctypes.c_int(), ctypes.c_int()
        w, h, border, depth = (ctypes.c_uint() for _ in range(4))
        if not self._x.XGetGeometry(self._display, window, ctypes.byref(root), ctypes.byref(x), ctypes.byref(y),
                                    ctypes.byref(w), ctypes.byref(h), ctypes.byref(border), ctypes.byref(depth)):
            return None
        left, top, child = ctypes.c_int(), ctypes.c_int(), ctypes.c_ulong()
        if not self._x.XTranslateCoordinates(self._display, window, self.root, 0, 0, ctypes.byref(left),
                                             ctypes.byref(top), ctypes.byref(child)):
            return None
        return Rect(left.value, top.value, left.value + w.value, top.value + h.value)

    def send_client_message(self, window: int, message: str, fmt: int, data) -> None:
        """A ClientMessage sent through the root window (how a window manager is asked to act), then a flush."""
        event = _Event()
        event.client.type, event.client.send_event = CLIENT_MESSAGE, 1
        event.client.display, event.client.window = self._display, window
        event.client.message_type, event.client.format = self.intern(message), fmt
        for i, value in enumerate(data):
            event.client.data[i] = value
        self._x.XSendEvent(self._display, self.root, 0, SUBSTRUCTURE_MASK, ctypes.byref(event))
        self._x.XFlush(self._display)

    # --- XTest input -----------------------------------------------------------------------------

    def keysym_to_keycode(self, keysym: int) -> int:
        """The keycode that produces the keysym at some shift level; 0 if the keymap has none."""
        return int(self._x.XKeysymToKeycode(self._display, keysym))

    def keysym_at(self, keycode: int, level: int) -> int:
        return int(self._x.XkbKeycodeToKeysym(self._display, keycode, 0, level))

    def fake_key(self, keycode: int, down: bool) -> None:
        _load_xtst().XTestFakeKeyEvent(self._display, keycode, int(down), 0)
        self._x.XFlush(self._display)

    def fake_button(self, button: int, down: bool) -> None:
        _load_xtst().XTestFakeButtonEvent(self._display, button, int(down), 0)
        self._x.XFlush(self._display)

    def fake_motion(self, x: int, y: int) -> None:
        _load_xtst().XTestFakeMotionEvent(self._display, -1, x, y, 0)  # -1: the current screen
        self._x.XFlush(self._display)

    # --- Passive key grab on the root window ----------------------------------------------------

    def grab_key(self, keycode: int, masks: list[int]) -> bool:
        """Grabs the key with each modifier mask on the root window; False if the server refused (BadAccess)."""
        with _grab_lock:
            _grab.update(display=self._display, refused=False)
            try:
                for mask in masks:
                    self._x.XGrabKey(self._display, keycode, mask, self.root, 0, GRAB_MODE_ASYNC, GRAB_MODE_ASYNC)
                self._x.XSync(self._display, 0)
                return not _grab["refused"]
            finally:
                _grab.update(display=None, refused=False)

    def next_key_press(self) -> None:
        """Blocks until a KeyPress arrives (only grabbed keys are delivered to this connection)."""
        event = _Event()
        while True:
            self._x.XNextEvent(self._display, ctypes.byref(event))
            if event.type == KEY_PRESS:
                return


def connect(display: str | None = None) -> Connection:
    return Connection(display)
