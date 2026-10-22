"""Linux (X11) backend, EXPERIMENTAL: screens, windows, capture, input, the kill hotkey, the approval prompt, secrets
and cleanup.

Windows come from the EWMH properties through libX11 (ctypes, in _xlib.py), screens from `xrandr --listmonitors`,
capture is ffmpeg x11grab, input is XTest (libXtst), the hotkey an XGrabKey and the prompt zenity or tkinter.
Wayland sessions are refused by the preflight. Logic that needs no X server is in
_linux_pure.py so it can be tested on any OS.
"""
import atexit
import importlib.util
import logging
import os
import shutil
import subprocess
import sys
import threading
import time
from pathlib import Path

import paths

from . import _linux_pure as pure
from . import _xlib
from ._macos_pure import group_kwargs
from .types import Monitor, Rect, Window

log = logging.getLogger("hostos")

KILL_SWITCH = "Ctrl+Alt+Shift+Q"
exe_suffix = ""
ENCODERS = [("x264", "libx264", "yuv420p")]
FALLBACK_ENCODERS: list = []
_REAPER = Path(__file__).with_name("_reaper.py")
_DIALOG = Path(__file__).with_name("_linux_dialog.py")
FOCUS_WAIT_S = 0.2


def _connect():
    return _xlib.connect()


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


def _dialog_program() -> str | None:
    """"zenity", "tkinter" or None: what the approval prompt can use."""
    if shutil.which("zenity"):
        return "zenity"
    return "tkinter" if importlib.util.find_spec("tkinter") else None


def preflight() -> list[pure.Row]:
    """The X11 session row; ffmpeg's x11grab and libXtst are only asked when there is an X session."""
    prompt = _dialog_program() is not None
    if not pure.session_row(os.environ).ok:
        return pure.permission_rows(os.environ, None, None, prompt)
    return pure.permission_rows(os.environ, _ffmpeg_has_x11grab(), _xlib.has_xtest(), prompt)


# --- Input: XTest, through one shared connection ---------------------------------------------------

_input_lock = threading.Lock()
_input_x = None
_held_keys: set[int] = set()  # keycodes and buttons pressed and not yet released; let go of at exit
_held_buttons: set[int] = set()
_wheel = pure.WheelAccumulator()


def _release_everything() -> None:
    with _input_lock:
        if _input_x is None:
            return
        try:
            for keycode in list(_held_keys):
                _input_x.fake_key(keycode, False)
            for button in list(_held_buttons):
                _input_x.fake_button(button, False)
        except Exception:
            log.exception("releasing held keys and buttons failed")


def _xtest():
    """The shared input connection (the caller holds _input_lock); opened on first use."""
    global _input_x
    if _input_x is None:
        _input_x = _connect()
        atexit.register(_release_everything)
    return _input_x


def mouse_move(x, y):
    with _input_lock:
        _xtest().fake_motion(int(x), int(y))


def mouse_button(x, y, button, down):
    number = pure.button_number(button)
    with _input_lock:
        conn = _xtest()
        conn.fake_motion(int(x), int(y))
        conn.fake_button(number, bool(down))
        (_held_buttons.add if down else _held_buttons.discard)(number)


def mouse_wheel(x, y, notches):
    with _input_lock:
        conn = _xtest()
        conn.fake_motion(int(x), int(y))
        for button in _wheel.buttons(notches):
            conn.fake_button(button, True)
            conn.fake_button(button, False)


def key(vk, down, extended=False):
    keysym = pure.keysym_for(vk)
    with _input_lock:
        conn = _xtest()
        keycode = conn.keysym_to_keycode(keysym)
        if not keycode:
            raise ValueError(f"the keyboard layout has no key for virtual-key code {vk:#x}")
        conn.fake_key(keycode, bool(down))
        (_held_keys.add if down else _held_keys.discard)(keycode)


def type_text(text):
    """Types each character through its key in the current layout (with Shift if needed); the others are skipped."""
    with _input_lock:
        conn = _xtest()
        shift = conn.keysym_to_keycode(pure.SHIFT_KEYSYM)
        for ch in text:
            keysym = pure.char_keysym(ch)
            keycode = conn.keysym_to_keycode(keysym) if keysym else 0
            needs_shift = (pure.shift_level(keysym, conn.keysym_at(keycode, 0), conn.keysym_at(keycode, 1))
                           if keycode else None)
            if needs_shift is None:
                log.warning("type_text: skipping %r, the keyboard layout has no key for it", ch)
                continue
            if needs_shift:
                conn.fake_key(shift, True)
            conn.fake_key(keycode, True)
            conn.fake_key(keycode, False)
            if needs_shift:
                conn.fake_key(shift, False)


# --- Kill switch: Ctrl+Alt+Shift+Q, grabbed on the root window by a thread with its own connection ------

def watch_kill_switch(on_pressed) -> bool:
    """Starts a thread that calls on_pressed (on that thread) each time the chord is pressed, whichever window has
    focus. Returns False if there is no display or another client already holds the grab (BadAccess)."""
    ready, grabbed = threading.Event(), []

    def run() -> None:
        try:
            with _connect() as x:
                keycode = x.keysym_to_keycode(pure.KILL_KEYSYM)
                grabbed.append(bool(keycode) and x.grab_key(keycode, pure.kill_masks()))
                ready.set()
                while grabbed[0]:
                    x.next_key_press()
                    try:
                        on_pressed()
                    except Exception:
                        log.exception("kill switch callback failed")
        except Exception:
            log.exception("kill switch thread failed")
        finally:
            if not ready.is_set():
                grabbed.append(False)
                ready.set()

    threading.Thread(target=run, name="kill-switch", daemon=True).start()
    return ready.wait(5) and grabbed[0]


# --- Approval prompt: zenity, else a tkinter dialog in a child process ---------------------------------

_live_prompts: set = set()
_prompt_atexit = False


def _kill_live_prompts() -> None:
    for process in list(_live_prompts):
        try:
            process.kill()
        except OSError:
            pass


class _Prompt:
    """A Yes/No box in a child process. wait() blocks the calling thread until it is answered, dismissed or times out;
    dismiss() may be called from any thread, before or after wait() starts: a dismissal that comes first stops the box
    from ever opening."""

    def __init__(self, text: str, caption: str, timeout_ms: int) -> None:
        self._timeout_s = pure.timeout_seconds(timeout_ms)
        self._text, self._caption = text, caption
        self._lock = threading.Lock()
        self._proc = None
        self._cancelled = False

    def _argv(self) -> list[str] | None:
        program = _dialog_program()
        if program == "zenity":
            return pure.zenity_argv(self._text, self._caption, self._timeout_s)
        if program == "tkinter":
            return pure.tk_argv(str(_DIALOG), self._text, self._caption, self._timeout_s)
        log.warning("no approval dialog is available (install zenity or python3-tk): denying the request")
        return None

    def wait(self) -> str:
        global _prompt_atexit
        with self._lock:
            argv = None if self._cancelled else self._argv()
            if argv is None:
                return "no"
            try:
                process = subprocess.Popen(argv, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                                           stderr=subprocess.DEVNULL)
            except OSError as e:
                log.warning("can't start the approval dialog (%s): denying the request", e)
                return "no"
            self._proc = process
            _live_prompts.add(process)
            if not _prompt_atexit:
                _prompt_atexit = True
                atexit.register(_kill_live_prompts)
        try:
            process.wait(self._timeout_s + 5)
        except subprocess.TimeoutExpired:  # the dialog hung past its own give-up time
            process.kill()
            process.wait()
            return "timeout"
        finally:
            _live_prompts.discard(process)
        return pure.answer_for(process.returncode)

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
        self._watcher = subprocess.Popen(paths.helper_argv("reaper", _REAPER) + [str(os.getpid())],
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
