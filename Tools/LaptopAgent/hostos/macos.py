"""macOS backend: screens and windows through Quartz (pyobjc, imported lazily), captured by ffmpeg avfoundation.

Capture, encoding and input (Quartz CGEvents posted at the HID tap) live here, with the approval box (osascript),
the kill-switch hotkey (a listen-only event tap), the permission preflight and the Keychain-backed secrets. Everything
that needs no pyobjc is in _macos_pure.py, _macos_input.py and _macos_secrets.py so it can be tested on any OS; pyobjc
and keyring are imported inside the functions that use them.
"""
import atexit
import asyncio
import logging
import os
import signal
import subprocess
import sys
import threading
import time
from pathlib import Path

import paths

from . import _macos_input as inp
from . import _macos_pure as pure
from . import _macos_secrets as secrets
from .types import Monitor, Rect, Window

log = logging.getLogger("hostos")
exe_suffix = ""
KILL_SWITCH = "Ctrl+Option+Shift+Q"
WATCHER_CHECK_S = 0.2

ENCODERS = [
    ("videotoolbox", "h264_videotoolbox", "nv12"),
    ("x264", "libx264", "yuv420p"),
]
FALLBACK_ENCODERS = [("openh264", "libopenh264", "yuv420p")]

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


_display_cache = inp.TtlCache(1.0, time.monotonic)  # every mouse move asks for the visible rect


def _displays() -> list[pure.Display]:
    return _display_cache.get(_query_displays)


def _query_displays() -> list[pure.Display]:
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


def _trusted() -> bool:
    from ApplicationServices import AXIsProcessTrusted
    return bool(AXIsProcessTrusted())


def check_key_file(path) -> None:
    pass  # the Keychain key protects the file


def preflight() -> list[pure.Row]:
    """Screen Recording, Accessibility and Input Monitoring, each with whether it is granted."""
    q = _quartz()
    return pure.permission_rows(bool(q.CGPreflightScreenCaptureAccess()), _trusted(),
                                bool(q.CGPreflightListenEventAccess()))


# --- Input: CGEvents posted at the HID tap, in global points with a top-left origin (what window_rect reports) ---

_modifiers = inp.Modifiers()
_buttons = inp.ButtonState()
_clicks = inp.ClickCounter()
_wheel = inp.WheelAccumulator()


_last_point = (0, 0)
_cleanup_installed = False


def _release_everything() -> None:
    """Lets go of held modifiers and buttons, so a crashed agent doesn't leave Cmd or a button logically down."""
    for kind, what in inp.release_list(_modifiers.held, _buttons.held):
        try:
            if kind == "key":
                key(what, False)
            else:
                mouse_button(_last_point[0], _last_point[1], what, False)
        except Exception:  # best effort while exiting
            log.debug("release at exit failed", exc_info=True)


def _on_sigterm(signum, frame) -> None:
    _release_everything()
    signal.signal(signal.SIGTERM, signal.SIG_DFL)
    os.kill(os.getpid(), signal.SIGTERM)


def _install_cleanup() -> None:
    global _cleanup_installed
    if _cleanup_installed:
        return
    _cleanup_installed = True
    atexit.register(_release_everything)
    try:
        if signal.getsignal(signal.SIGTERM) == signal.SIG_DFL:  # never override a handler someone else installed
            signal.signal(signal.SIGTERM, _on_sigterm)
    except ValueError:  # not the main thread
        pass


def _post(event) -> None:
    _install_cleanup()
    q = _quartz()
    q.CGEventSetFlags(event, _modifiers.mask)
    q.CGEventPost(q.kCGHIDEventTap, event)


def _mouse_event(kind: int, x: int, y: int, button: str):
    q = _quartz()
    return q.CGEventCreateMouseEvent(None, kind, (float(x), float(y)),
                                     q.kCGMouseButtonRight if button == "right" else q.kCGMouseButtonLeft)


def mouse_move(x, y):
    global _last_point
    _last_point = (x, y)
    kind = _buttons.move_event()
    _post(_mouse_event(kind, x, y, "right" if kind == inp.RIGHT_DRAGGED else "left"))


def mouse_button(x, y, button, down):
    global _last_point
    from AppKit import NSEvent
    q = _quartz()
    _last_point = (x, y)
    if down:
        count = _clicks.press(button, x, y, time.monotonic(), float(NSEvent.doubleClickInterval()))
    else:
        count = _clicks.release()
    _buttons.set(button, down)
    event = _mouse_event(inp.button_event(button, down), x, y, button)
    q.CGEventSetIntegerValueField(event, q.kCGMouseEventClickState, count)
    _post(event)


def mouse_wheel(x, y, notches):
    q = _quartz()
    lines = _wheel.lines(notches)
    if lines == 0:
        return
    mouse_move(x, y)  # a warp would suppress local events for 0.25 s and skip the drag state
    event = q.CGEventCreateScrollWheelEvent(None, q.kCGScrollEventUnitLine, 1, lines)
    q.CGEventSetLocation(event, (float(x), float(y)))
    _post(event)


def key(vk, down, extended=False):
    q = _quartz()
    nx = inp.MEDIA_VK_TO_NX.get(vk)
    if nx is not None:
        from AppKit import NSEvent
        data1 = inp.media_data1(nx, down)
        event = NSEvent.otherEventWithType_location_modifierFlags_timestamp_windowNumber_context_subtype_data1_data2_(
            14, (0, 0), inp.media_flags(down), 0, 0, None, 8, data1, -1)
        q.CGEventPost(q.kCGHIDEventTap, event.CGEvent())
        return
    kvk = inp.kvk_for(vk)
    _modifiers.update(vk, down)
    _post(q.CGEventCreateKeyboardEvent(None, kvk, bool(down)))


def type_text(text):
    q = _quartz()
    for chunk in inp.unicode_chunks(text):
        units = inp.utf16_length(chunk)
        for down in (True, False):
            event = q.CGEventCreateKeyboardEvent(None, 0, down)
            q.CGEventKeyboardSetUnicodeString(event, units, chunk)
            q.CGEventSetFlags(event, 0)
            q.CGEventPost(q.kCGHIDEventTap, event)


# --- Windows: stacking order, activation and focus -----------------------------------------------

def window_at(x, y):
    return inp.window_at(x, y, _onscreen())


def foreground_window():
    from AppKit import NSWorkspace
    app = NSWorkspace.sharedWorkspace().frontmostApplication()
    return inp.frontmost_window(int(app.processIdentifier()), _onscreen()) if app else 0


def raise_window(handle):
    from AppKit import NSRunningApplication
    import ApplicationServices as ax
    info = inp.info_of(handle, _all())
    if info is None:
        return
    pid, name, bounds = int(info["kCGWindowOwnerPID"]), info.get("kCGWindowName"), info.get("kCGWindowBounds") or {}
    app = NSRunningApplication.runningApplicationWithProcessIdentifier_(pid)
    if app is not None:
        if hasattr(app, "activate"):  # macOS 14+
            app.activate()
        else:
            from AppKit import NSApplicationActivateIgnoringOtherApps
            app.activateWithOptions_(NSApplicationActivateIgnoringOtherApps)
    root = ax.AXUIElementCreateApplication(pid)

    def attribute(element, attr):
        err, value = ax.AXUIElementCopyAttributeValue(element, attr, None)
        return value if err == 0 else None

    def unpack(value, kind):
        if value is None:
            return None
        ok, result = ax.AXValueGetValue(value, kind, None)
        return result if ok else None

    candidates = []
    for window in attribute(root, ax.kAXWindowsAttribute) or []:
        position = unpack(attribute(window, ax.kAXPositionAttribute), ax.kAXValueCGPointType)
        size = unpack(attribute(window, ax.kAXSizeAttribute), ax.kAXValueCGSizeType)
        if position is not None and size is not None:
            candidates.append((window, attribute(window, ax.kAXTitleAttribute), (position.x, position.y),
                               (size.width, size.height)))
    window = inp.pick_ax_window(candidates, name, bounds)
    if window is not None:
        ax.AXUIElementPerformAction(window, ax.kAXRaiseAction)
        ax.AXUIElementSetAttributeValue(window, ax.kAXMainAttribute, True)


FOCUS_WAIT_S = 0.2


def _on_event_loop() -> bool:
    try:
        asyncio.get_running_loop()
        return True
    except RuntimeError:
        return False


def focus_window(handle):
    raise_window(handle)
    if _on_event_loop():  # the data-channel handlers call this on the asyncio thread: never sleep there
        return foreground_window() == handle
    deadline = time.monotonic() + FOCUS_WAIT_S
    while True:
        if foreground_window() == handle:
            return True
        if time.monotonic() >= deadline:
            return False
        time.sleep(0.01)


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
        self._watcher = subprocess.Popen(paths.helper_argv("reaper", _REAPER) + [str(os.getpid())],
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


# --- Approval box -------------------------------------------------------------------------------

_live_prompts: set = set()  # osascript processes that are showing a box right now
_prompt_atexit = False


def _kill_live_prompts() -> None:
    for process in list(_live_prompts):
        try:
            process.kill()
        except OSError:
            pass


class _Prompt:
    """A Yes/No box shown by osascript. wait() blocks the calling thread until it is answered, dismissed or times out;
    dismiss() may be called from any thread, before or after wait() starts: a dismissal that comes first stops the box
    from ever opening."""

    def __init__(self, text: str, caption: str, timeout_ms: int) -> None:
        self._timeout_s = pure.timeout_seconds(timeout_ms)
        self._argv = pure.osascript_argv(text, caption, self._timeout_s)
        self._lock = threading.Lock()
        self._proc = None
        self._cancelled = False

    def wait(self) -> str:
        global _prompt_atexit
        with self._lock:
            if self._cancelled:
                return "no"
            process = subprocess.Popen(self._argv, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                       stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace")
            self._proc = process
            _live_prompts.add(process)
            if not _prompt_atexit:
                _prompt_atexit = True
                atexit.register(_kill_live_prompts)
        try:
            out, err = process.communicate(timeout=self._timeout_s + 5)
        except subprocess.TimeoutExpired:  # osascript hung past its own give-up time
            try:
                process.kill()
            except OSError:
                pass
            try:
                process.communicate(timeout=5)
            except Exception:
                pass
            return "timeout"
        finally:
            _live_prompts.discard(process)
        return pure.parse_dialog(process.returncode, out, err)

    def dismiss(self) -> bool:
        with self._lock:
            self._cancelled = True
            process = self._proc
            if process is None:
                return True  # wait() hasn't started and now never opens a box
            if process.poll() is not None:
                return False
            try:
                process.kill()
            except OSError:
                return False
            return True


def open_prompt(text: str, caption: str, timeout_ms: int) -> _Prompt:
    return _Prompt(text, caption, timeout_ms)


# --- Kill switch: Ctrl+Option+Shift+Q, heard by a listen-only event tap (needs Input Monitoring) --------

_tap = _tap_callback = _tap_source = None  # kept alive for the life of the process


def watch_kill_switch(on_pressed) -> bool:
    """Starts a thread that calls on_pressed (on that thread, like the Windows hotkey) each time the chord is pressed,
    whichever app has focus. Returns False if the tap can't be created (Input Monitoring not granted)."""
    global _tap, _tap_callback, _tap_source
    q = _quartz()

    def callback(proxy, event_type, event, refcon):
        try:
            if event_type in (pure.TAP_DISABLED_BY_TIMEOUT, pure.TAP_DISABLED_BY_USER_INPUT):
                q.CGEventTapEnable(_tap, True)  # the system switches slow or interrupted taps off
            elif event_type == q.kCGEventKeyDown:
                if pure.is_kill_chord(q.CGEventGetFlags(event),
                                      q.CGEventGetIntegerValueField(event, q.kCGKeyboardEventKeycode),
                                      bool(q.CGEventGetIntegerValueField(event, q.kCGKeyboardEventAutorepeat))):
                    on_pressed()
        except Exception:  # never let an exception escape into the run loop
            log.exception("kill switch callback failed")
        return event

    tap = q.CGEventTapCreate(q.kCGSessionEventTap, q.kCGHeadInsertEventTap, q.kCGEventTapOptionListenOnly,
                             1 << q.kCGEventKeyDown, callback, None)
    if tap is None:
        return False
    source = q.CFMachPortCreateRunLoopSource(None, tap, 0)
    _tap, _tap_callback, _tap_source = tap, callback, source

    def run() -> None:
        q.CFRunLoopAddSource(q.CFRunLoopGetCurrent(), source, q.kCFRunLoopCommonModes)
        q.CGEventTapEnable(tap, True)
        q.CFRunLoopRun()

    threading.Thread(target=run, name="kill-switch", daemon=True).start()
    return True


# --- Secrets: identity.key is encrypted under a random key kept in the login Keychain ---------------

def _keyring():
    try:
        import keyring
        import keyring.errors  # noqa: F401  (load_key looks for keyring.errors.KeyringError)
    except ImportError as e:
        raise OSError("the keyring package is missing; run: pip install -r requirements.txt") from e
    return keyring


def protect(data: bytes) -> bytes:
    return secrets.protect(_keyring(), data)


def unprotect(blob: bytes) -> bytes:
    return secrets.unprotect(_keyring(), blob)


def default_home() -> Path:
    return Path.home() / "Library" / "Application Support" / "SpatialLauncher" / "agent"
