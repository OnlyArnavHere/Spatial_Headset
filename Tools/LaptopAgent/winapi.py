"""Small ctypes layer over the Win32 calls the agent needs.

Everything works in physical pixels: the process is made per-monitor DPI aware before anything else,
so window and monitor rectangles match what Windows Graphics Capture records and what SendInput expects.
"""
import ctypes
import ctypes.wintypes as wt
import sys
from dataclasses import dataclass

if sys.platform != "win32":
    raise SystemExit("The laptop agent runs on Windows only.")

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

@dataclass(frozen=True)
class Rect:
    left: int
    top: int
    right: int
    bottom: int

    @property
    def width(self) -> int:
        return self.right - self.left

    @property
    def height(self) -> int:
        return self.bottom - self.top

    def __str__(self) -> str:
        return f"{self.width}x{self.height} at ({self.left}, {self.top})"


@dataclass(frozen=True)
class Monitor:
    index: int
    handle: int
    rect: Rect
    primary: bool


@dataclass(frozen=True)
class Window:
    handle: int
    title: str


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


class _INPUTUNION(ctypes.Union):
    # The union must be as large as its largest member (KEYBDINPUT/HARDWAREINPUT are smaller than MOUSEINPUT).
    _fields_ = [("mi", _MOUSEINPUT)]


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
