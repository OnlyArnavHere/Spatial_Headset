"""Small ctypes layer over the Win32 calls the agent needs.

Everything works in physical pixels: the process is made per-monitor DPI aware before anything else,
so window and monitor rectangles match what Windows Graphics Capture records and what SendInput expects.
"""
import ctypes
import ctypes.wintypes as wt
import sys

if sys.platform != "win32":
    raise SystemExit("The laptop agent runs on Windows only.")

from hostos.types import Monitor, Rect, Window  # noqa: E402  (moved there; re-exported so winapi.Rect still works)

user32 = ctypes.WinDLL("user32", use_last_error=True)
dwmapi = ctypes.WinDLL("dwmapi")

# --- DPI awareness ---------------------------------------------------------------------------

_DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = ctypes.c_void_p(-4)


def make_dpi_aware() -> None:
    """Without this, rectangles are scaled to 96 DPI and clicks land in the wrong place on scaled displays."""
    try:
        user32.SetProcessDpiAwarenessContext(_DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)
    except AttributeError:  # very old Windows 10
        ctypes.windll.shcore.SetProcessDpiAwareness(2)


# --- Rectangles, monitors and windows --------------------------------------------------------

class _MONITORINFO(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("rcMonitor", wt.RECT), ("rcWork", wt.RECT), ("dwFlags", wt.DWORD)]


_MONITORINFOF_PRIMARY = 1
_MonitorEnumProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HMONITOR, wt.HDC, ctypes.POINTER(wt.RECT), wt.LPARAM)
_WndEnumProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)


def _rect(r: wt.RECT) -> Rect:
    return Rect(r.left, r.top, r.right, r.bottom)


def list_monitors() -> list[Monitor]:
    """Monitors in enumeration order; index 0 is not necessarily the primary one."""
    handles: list[int] = []

    def collect(hmon, _hdc, _rect, _data):
        handles.append(hmon)
        return True

    user32.EnumDisplayMonitors(None, None, _MonitorEnumProc(collect), 0)
    monitors = []
    for index, handle in enumerate(handles):
        info = _MONITORINFO()
        info.cbSize = ctypes.sizeof(info)
        user32.GetMonitorInfoW(wt.HMONITOR(handle), ctypes.byref(info))
        monitors.append(Monitor(index, handle, _rect(info.rcMonitor), bool(info.dwFlags & _MONITORINFOF_PRIMARY)))
    return monitors


def monitor_rect(handle: int) -> Rect | None:
    info = _MONITORINFO()
    info.cbSize = ctypes.sizeof(info)
    if not user32.GetMonitorInfoW(wt.HMONITOR(handle), ctypes.byref(info)):
        return None
    return _rect(info.rcMonitor)


def _title(hwnd) -> str:
    length = user32.GetWindowTextLengthW(hwnd)
    buffer = ctypes.create_unicode_buffer(length + 1)
    user32.GetWindowTextW(hwnd, buffer, length + 1)
    return buffer.value


_DWMWA_CLOAKED = 14
_DWMWA_EXTENDED_FRAME_BOUNDS = 9
_GWL_EXSTYLE = -20
_WS_EX_TOOLWINDOW = 0x00000080


def list_windows() -> list[Window]:
    """Visible, titled, top-level windows a user would recognise (no tool windows, no cloaked UWP shells)."""
    windows: list[Window] = []

    def collect(hwnd, _data):
        if not user32.IsWindowVisible(hwnd) or user32.GetWindowTextLengthW(hwnd) == 0:
            return True
        if user32.GetWindowLongW(hwnd, _GWL_EXSTYLE) & _WS_EX_TOOLWINDOW:
            return True
        cloaked = wt.DWORD()
        dwmapi.DwmGetWindowAttribute(hwnd, _DWMWA_CLOAKED, ctypes.byref(cloaked), ctypes.sizeof(cloaked))
        if cloaked.value:
            return True
        windows.append(Window(int(hwnd), _title(hwnd)))
        return True

    user32.EnumWindows(_WndEnumProc(collect), 0)
    return windows


def find_window(title_part: str) -> Window | None:
    """The first visible window whose title contains title_part (case-insensitive)."""
    needle = title_part.lower()
    for window in list_windows():
        if needle in window.title.lower():
            return window
    return None


def window_exists(handle: int) -> bool:
    return bool(user32.IsWindow(wt.HWND(handle)))


def window_is_minimized(handle: int) -> bool:
    return bool(user32.IsIconic(wt.HWND(handle)))


_HWND_TOP = 0
_SWP_NOSIZE, _SWP_NOMOVE, _SWP_NOACTIVATE = 0x0001, 0x0002, 0x0010
_GA_ROOT = 2
user32.WindowFromPoint.argtypes = [wt.POINT]
user32.WindowFromPoint.restype = wt.HWND
user32.GetAncestor.restype = wt.HWND


def raise_window(handle: int) -> None:
    """Puts the window on top of the others without activating it (a click then activates it as usual)."""
    user32.SetWindowPos(wt.HWND(handle), wt.HWND(_HWND_TOP), 0, 0, 0, 0, _SWP_NOSIZE | _SWP_NOMOVE | _SWP_NOACTIVATE)


def window_at(x: int, y: int) -> int:
    """The top-level window that owns the screen pixel (x, y), or 0."""
    hwnd = user32.WindowFromPoint(wt.POINT(x, y))
    return int(user32.GetAncestor(hwnd, _GA_ROOT) or 0) if hwnd else 0


_RDW_INVALIDATE, _RDW_ALLCHILDREN, _RDW_FRAME = 0x0001, 0x0080, 0x0400


def request_repaint(handle: int) -> None:
    """Asks the window to repaint itself, without waiting for it (a hung app can't block the agent). Windows Graphics
    Capture delivers a frame only when a window changes, so a static window (a document nobody is typing in, or one
    behind other windows) would otherwise stream nothing at all; measured on Notepad: 1-2 frames in 5 s without,
    one frame per request with."""
    user32.RedrawWindow(wt.HWND(handle), None, None, _RDW_INVALIDATE | _RDW_ALLCHILDREN | _RDW_FRAME)


def window_frame_rect(handle: int) -> Rect | None:
    """The window's visible outer bounds (DWM extended frame, without the invisible resize border)."""
    r = wt.RECT()
    hr = dwmapi.DwmGetWindowAttribute(wt.HWND(handle), _DWMWA_EXTENDED_FRAME_BOUNDS, ctypes.byref(r), ctypes.sizeof(r))
    if hr != 0:
        if not user32.GetWindowRect(wt.HWND(handle), ctypes.byref(r)):
            return None
    return _rect(r)


def window_rect(handle: int) -> Rect | None:
    """The window's client area in screen pixels: the area ffmpeg's gfxcapture records by default
    (capture_border=0 leaves out the title bar and borders; measured: an 800x450 client area captures as 800x450)."""
    r = wt.RECT()
    if not user32.GetClientRect(wt.HWND(handle), ctypes.byref(r)):
        return None
    origin = wt.POINT(0, 0)
    if not user32.ClientToScreen(wt.HWND(handle), ctypes.byref(origin)):
        return None
    return Rect(origin.x, origin.y, origin.x + r.right, origin.y + r.bottom)


# --- Child process lifetime ------------------------------------------------------------------

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
kernel32.CreateJobObjectW.restype = wt.HANDLE
kernel32.OpenProcess.restype = wt.HANDLE

_JobObjectExtendedLimitInformation = 9
_JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000
_PROCESS_SET_QUOTA = 0x0100
_PROCESS_TERMINATE = 0x0001


class _IO_COUNTERS(ctypes.Structure):
    _fields_ = [(n, ctypes.c_ulonglong) for n in ("ReadOperationCount", "WriteOperationCount", "OtherOperationCount",
                                                  "ReadTransferCount", "WriteTransferCount", "OtherTransferCount")]


class _JOBOBJECT_BASIC_LIMIT_INFORMATION(ctypes.Structure):
    _fields_ = [("PerProcessUserTimeLimit", ctypes.c_longlong), ("PerJobUserTimeLimit", ctypes.c_longlong),
                ("LimitFlags", wt.DWORD), ("MinimumWorkingSetSize", ctypes.c_size_t),
                ("MaximumWorkingSetSize", ctypes.c_size_t), ("ActiveProcessLimit", wt.DWORD),
                ("Affinity", ctypes.c_size_t), ("PriorityClass", wt.DWORD), ("SchedulingClass", wt.DWORD)]


class _JOBOBJECT_EXTENDED_LIMIT_INFORMATION(ctypes.Structure):
    _fields_ = [("BasicLimitInformation", _JOBOBJECT_BASIC_LIMIT_INFORMATION), ("IoInfo", _IO_COUNTERS),
                ("ProcessMemoryLimit", ctypes.c_size_t), ("JobMemoryLimit", ctypes.c_size_t),
                ("PeakProcessMemoryUsed", ctypes.c_size_t), ("PeakJobMemoryUsed", ctypes.c_size_t)]


class KillOnExitJob:
    """Child processes added here die when this process exits, however it exits (Ctrl+C, closed console,
    crash, Task Manager). Without it a killed agent would leave MediaMTX and ffmpeg streaming the screen."""

    def __init__(self) -> None:
        self.handle = kernel32.CreateJobObjectW(None, None)
        if not self.handle:
            raise OSError(ctypes.get_last_error(), "CreateJobObject failed")
        info = _JOBOBJECT_EXTENDED_LIMIT_INFORMATION()
        info.BasicLimitInformation.LimitFlags = _JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if not kernel32.SetInformationJobObject(self.handle, _JobObjectExtendedLimitInformation,
                                                ctypes.byref(info), ctypes.sizeof(info)):
            raise OSError(ctypes.get_last_error(), "SetInformationJobObject failed")
        # The handle is deliberately never closed: the OS closes it when this process ends, killing the children.

    def add(self, pid: int) -> None:
        process = kernel32.OpenProcess(_PROCESS_SET_QUOTA | _PROCESS_TERMINATE, False, pid)
        if not process:
            raise OSError(ctypes.get_last_error(), f"OpenProcess({pid}) failed")
        try:
            if not kernel32.AssignProcessToJobObject(self.handle, process):
                raise OSError(ctypes.get_last_error(), f"AssignProcessToJobObject({pid}) failed")
        finally:
            kernel32.CloseHandle(process)


# --- Mouse injection -------------------------------------------------------------------------

_INPUT_MOUSE = 0
_MOUSEEVENTF_MOVE = 0x0001
_MOUSEEVENTF_LEFTDOWN = 0x0002
_MOUSEEVENTF_LEFTUP = 0x0004
_MOUSEEVENTF_RIGHTDOWN = 0x0008
_MOUSEEVENTF_RIGHTUP = 0x0010
_MOUSEEVENTF_WHEEL = 0x0800
_MOUSEEVENTF_VIRTUALDESK = 0x4000
_MOUSEEVENTF_ABSOLUTE = 0x8000
_WHEEL_DELTA = 120

_SM_XVIRTUALSCREEN, _SM_YVIRTUALSCREEN, _SM_CXVIRTUALSCREEN, _SM_CYVIRTUALSCREEN = 76, 77, 78, 79

_BUTTON_FLAGS = {
    "left": (_MOUSEEVENTF_LEFTDOWN, _MOUSEEVENTF_LEFTUP),
    "right": (_MOUSEEVENTF_RIGHTDOWN, _MOUSEEVENTF_RIGHTUP),
}


class _MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", wt.LONG), ("dy", wt.LONG), ("mouseData", wt.DWORD), ("dwFlags", wt.DWORD),
                ("time", wt.DWORD), ("dwExtraInfo", ctypes.c_size_t)]


class _KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wt.WORD), ("wScan", wt.WORD), ("dwFlags", wt.DWORD), ("time", wt.DWORD),
                ("dwExtraInfo", ctypes.c_size_t)]


class _INPUTUNION(ctypes.Union):
    # The union must be as large as its largest member (MOUSEINPUT; HARDWAREINPUT is smaller and unused).
    _fields_ = [("mi", _MOUSEINPUT), ("ki", _KEYBDINPUT)]


class _INPUT(ctypes.Structure):
    _fields_ = [("type", wt.DWORD), ("u", _INPUTUNION)]


def _virtual_screen() -> Rect:
    x = user32.GetSystemMetrics(_SM_XVIRTUALSCREEN)
    y = user32.GetSystemMetrics(_SM_YVIRTUALSCREEN)
    return Rect(x, y, x + user32.GetSystemMetrics(_SM_CXVIRTUALSCREEN), y + user32.GetSystemMetrics(_SM_CYVIRTUALSCREEN))


def _send(flags: int, x: int | None = None, y: int | None = None, data: int = 0) -> None:
    dx = dy = 0
    if x is not None and y is not None:
        # Absolute coordinates are 0..65535 across the whole virtual desktop (all monitors).
        vs = _virtual_screen()
        dx = round((x - vs.left) * 65535 / max(1, vs.width - 1))
        dy = round((y - vs.top) * 65535 / max(1, vs.height - 1))
        flags |= _MOUSEEVENTF_MOVE | _MOUSEEVENTF_ABSOLUTE | _MOUSEEVENTF_VIRTUALDESK
    event = _INPUT(_INPUT_MOUSE, _INPUTUNION(_MOUSEINPUT(dx, dy, ctypes.c_uint32(data).value, flags, 0, 0)))
    if user32.SendInput(1, ctypes.byref(event), ctypes.sizeof(event)) != 1:
        raise OSError(ctypes.get_last_error(), "SendInput failed (blocked by UIPI? a higher-privilege window may have focus)")


def mouse_move(x: int, y: int) -> None:
    _send(0, x, y)


def mouse_button(x: int, y: int, button: str, down: bool) -> None:
    down_flag, up_flag = _BUTTON_FLAGS[button]
    _send(down_flag if down else up_flag, x, y)


def mouse_wheel(x: int, y: int, notches: float) -> None:
    _send(_MOUSEEVENTF_WHEEL, x, y, int(round(notches * _WHEEL_DELTA)))


# --- Keyboard injection ----------------------------------------------------------------------

_INPUT_KEYBOARD = 1
_KEYEVENTF_EXTENDEDKEY = 0x0001
_KEYEVENTF_KEYUP = 0x0002
_KEYEVENTF_UNICODE = 0x0004
_MAPVK_VK_TO_VSC = 0


def _send_keys(events: list[_KEYBDINPUT]) -> None:
    array = (_INPUT * len(events))(*(_INPUT(_INPUT_KEYBOARD, _INPUTUNION(ki=e)) for e in events))
    if user32.SendInput(len(events), array, ctypes.sizeof(_INPUT)) != len(events):
        raise OSError(ctypes.get_last_error(), "SendInput failed (blocked by UIPI? a higher-privilege window may have focus)")


def key(vk: int, down: bool, extended: bool = False) -> None:
    """Presses or releases one key, by virtual-key code, in the foreground window."""
    flags = (_KEYEVENTF_EXTENDEDKEY if extended else 0) | (0 if down else _KEYEVENTF_KEYUP)
    scan = user32.MapVirtualKeyW(vk, _MAPVK_VK_TO_VSC)
    _send_keys([_KEYBDINPUT(vk, scan, flags, 0, 0)])


def type_text(text: str) -> None:
    """Types text in the foreground window as Unicode characters, whatever the keyboard layout (no virtual keys, so
    modifier keys held at the time don't change what's typed)."""
    units = text.encode("utf-16-le")
    events = []
    for i in range(0, len(units), 2):
        unit = int.from_bytes(units[i:i + 2], "little")
        events.append(_KEYBDINPUT(0, unit, _KEYEVENTF_UNICODE, 0, 0))
        events.append(_KEYBDINPUT(0, unit, _KEYEVENTF_UNICODE | _KEYEVENTF_KEYUP, 0, 0))
    if events:
        _send_keys(events)


user32.GetForegroundWindow.restype = wt.HWND
user32.GetWindowThreadProcessId.restype = wt.DWORD
kernel32.GetCurrentThreadId.restype = wt.DWORD


def foreground_window() -> int:
    return int(user32.GetForegroundWindow() or 0)


def focus_window(handle: int) -> bool:
    """Makes the window the foreground window (keyboard input goes there) and says whether it is. Windows only lets the
    foreground app hand the foreground over, so this joins the foreground window's input queue for the call."""
    if foreground_window() == handle:
        return True
    hwnd = wt.HWND(handle)
    if user32.IsIconic(hwnd):
        return False
    current = user32.GetForegroundWindow()
    theirs = user32.GetWindowThreadProcessId(current, None) if current else 0
    ours = kernel32.GetCurrentThreadId()
    attached = bool(theirs) and theirs != ours and bool(user32.AttachThreadInput(ours, theirs, True))
    try:
        user32.BringWindowToTop(hwnd)
        user32.SetForegroundWindow(hwnd)
    finally:
        if attached:
            user32.AttachThreadInput(ours, theirs, False)
    return foreground_window() == handle


# --- Kill-switch hotkey ----------------------------------------------------------------------

_MOD_ALT, _MOD_CONTROL, _MOD_SHIFT, _MOD_NOREPEAT = 0x0001, 0x0002, 0x0004, 0x4000
_VK_Q = 0x51
_WM_HOTKEY = 0x0312
KILL_SWITCH = "Ctrl+Alt+Shift+Q"


def watch_kill_switch(on_pressed) -> bool:
    """Registers Ctrl+Alt+Shift+Q as a system-wide hotkey on a background thread and calls on_pressed (on that thread)
    each time it's pressed, whichever window has focus. Returns False if another program already owns the hotkey."""
    import threading
    registered = threading.Event()
    result = {"ok": False}

    def run():
        result["ok"] = bool(user32.RegisterHotKey(None, 1, _MOD_CONTROL | _MOD_ALT | _MOD_SHIFT | _MOD_NOREPEAT, _VK_Q))
        registered.set()
        if not result["ok"]:
            return
        msg = wt.MSG()
        while user32.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
            if msg.message == _WM_HOTKEY:
                on_pressed()

    threading.Thread(target=run, name="kill-switch", daemon=True).start()
    registered.wait(2.0)
    return result["ok"]


# --- Yes/No prompt ---------------------------------------------------------------------------

_MB_YESNO, _MB_ICONWARNING, _MB_DEFBUTTON2, _MB_TOPMOST = 0x4, 0x30, 0x100, 0x40000
_IDYES, _IDNO, _MB_TIMEDOUT = 6, 7, 32000
_WM_COMMAND = 0x0111
_DIALOG_CLASS = "#32770"
_ENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)

user32.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
user32.PostMessageW.restype = wt.BOOL
user32.EnumThreadWindows.argtypes = [wt.DWORD, _ENUMPROC, wt.LPARAM]
user32.EnumThreadWindows.restype = wt.BOOL
user32.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
user32.GetClassNameW.restype = ctypes.c_int
user32.MessageBoxW.argtypes = [wt.HWND, wt.LPCWSTR, wt.LPCWSTR, wt.UINT]
user32.MessageBoxW.restype = ctypes.c_int
_FLAGS = _MB_YESNO | _MB_ICONWARNING | _MB_DEFBUTTON2 | _MB_TOPMOST  # no MB_SETFOREGROUND: the box doesn't take focus itself


def ask_yes_no(text: str, caption: str, timeout_ms: int) -> str:
    """A top-most Yes/No box (default No) that blocks the calling thread: "yes", "no" or "timeout".
    Doesn't take focus by itself; if Windows activates it, "y" answers Yes and Enter/Space choose the default, No. Call it on a thread of its own."""
    import threading
    try:
        func = user32.MessageBoxTimeoutW
    except AttributeError:
        func = None
    if func is not None:
        func.argtypes = [wt.HWND, wt.LPCWSTR, wt.LPCWSTR, wt.UINT, wt.WORD, wt.DWORD]
        func.restype = ctypes.c_int
        result = func(None, text, caption, _FLAGS, 0, int(timeout_ms))
        return "yes" if result == _IDYES else "timeout" if result == _MB_TIMEDOUT else "no"
    # No timeout export: close the box ourselves after the time is up.
    thread_id, timed_out, done = threading.get_native_id(), [False], threading.Event()

    def expire() -> None:
        if not done.wait(timeout_ms / 1000.0):
            timed_out[0] = True
            while not done.wait(0.05) and not dismiss_prompt(thread_id):
                pass

    threading.Thread(target=expire, name="prompt-timeout", daemon=True).start()
    try:
        result = user32.MessageBoxW(None, text, caption, _FLAGS)
    finally:
        done.set()
    return "yes" if result == _IDYES else "timeout" if timed_out[0] else "no"


def dismiss_prompt(thread_id: int) -> bool:
    """Answers No on a message box owned by that thread (a Yes/No box ignores WM_CLOSE). True if one was found."""
    found = []

    def visit(hwnd, _) -> bool:
        name = ctypes.create_unicode_buffer(16)
        if user32.GetClassNameW(hwnd, name, 16) and name.value == _DIALOG_CLASS:
            user32.PostMessageW(hwnd, _WM_COMMAND, _IDNO, 0)
            found.append(hwnd)
        return True

    user32.EnumThreadWindows(thread_id, _ENUMPROC(visit), 0)
    return bool(found)


# --- DPAPI (per-user encryption at rest) -----------------------------------------------------

DPAPI_ENTROPY = b"SpatialLauncher.agent.identity.v1"
_CRYPTPROTECT_UI_FORBIDDEN = 0x1


class _DATA_BLOB(ctypes.Structure):
    _fields_ = [("cbData", wt.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]


def _dpapi(func_name: str, data: bytes, entropy: bytes) -> bytes:
    if sys.platform != "win32":
        raise OSError("DPAPI is only available on Windows")
    crypt32 = ctypes.WinDLL("crypt32", use_last_error=True)
    func = getattr(crypt32, func_name)
    func.restype = wt.BOOL
    buffers = [ctypes.create_string_buffer(data, len(data)), ctypes.create_string_buffer(entropy, len(entropy))]
    blobs = [_DATA_BLOB(len(b.raw), ctypes.cast(b, ctypes.POINTER(ctypes.c_char))) for b in buffers]
    out = _DATA_BLOB()
    # Arguments: in, description, entropy, reserved, prompt, flags, out.
    ok = func(ctypes.byref(blobs[0]), None, ctypes.byref(blobs[1]), None, None, _CRYPTPROTECT_UI_FORBIDDEN, ctypes.byref(out))
    if not ok:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        return ctypes.string_at(out.pbData, out.cbData)
    finally:
        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.LocalFree.argtypes = [ctypes.c_void_p]
        k32.LocalFree(ctypes.cast(out.pbData, ctypes.c_void_p))


def dpapi_protect(data: bytes, entropy: bytes = DPAPI_ENTROPY) -> bytes:
    """Encrypts data so only this Windows user can decrypt it (CryptProtectData, user scope)."""
    return _dpapi("CryptProtectData", data, entropy)


def dpapi_unprotect(blob: bytes, entropy: bytes = DPAPI_ENTROPY) -> bytes:
    """Reverses dpapi_protect; raises OSError if the blob is corrupt, another user's, or the entropy differs."""
    return _dpapi("CryptUnprotectData", blob, entropy)
