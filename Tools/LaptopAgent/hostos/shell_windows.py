"""The laptop shell on Windows: taskbar windows, taskbar pins, Start Menu apps (desktop shortcuts and Store apps), their
icons, launching them and raising, minimizing, restoring or closing windows. The logic that needs no Windows is in
_shell_windows_pure.py.

Launching only ever goes through the shell with something this module found itself: os.startfile on a Start Menu or
taskbar shortcut (re-checked to still be inside those folders), or explorer.exe shell:AppsFolder\\<AUMID> for a Store
app whose AUMID Get-StartApps listed and that matches the AUMID pattern. Nothing the headset sends is ever run.
"""
import ctypes
import ctypes.wintypes as wt
import logging
import ntpath
import os
import subprocess
import threading
import time
import winreg

import paths
from . import _png
from . import _shell_windows_pure as pure
from ._shell_types import AppEntry, ShellBackend, WindowEntry

log = logging.getLogger("shell")

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
shell32 = ctypes.WinDLL("shell32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
ole32 = ctypes.WinDLL("ole32", use_last_error=True)
dwmapi = ctypes.WinDLL("dwmapi")
version = ctypes.WinDLL("version", use_last_error=True)

INSTALLED_TTL_S = 120.0
PINNED_TTL_S = 15.0
SHELL_CLASSES = {"Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow"}
_GWL_EXSTYLE = -20
_WS_EX_TOOLWINDOW = 0x80
_WS_EX_APPWINDOW = 0x40000
_GW_OWNER = 4
_DWMWA_CLOAKED = 14
_SW_MINIMIZE, _SW_RESTORE = 6, 9
_WM_CLOSE = 0x0010
_PROCESS_QUERY_LIMITED_INFORMATION = 0x1000

_WNDENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
user32.EnumWindows.argtypes = [_WNDENUMPROC, wt.LPARAM]
user32.GetWindow.restype = wt.HWND
user32.GetWindow.argtypes = [wt.HWND, wt.UINT]
user32.GetWindowLongW.argtypes = [wt.HWND, ctypes.c_int]
user32.GetWindowTextLengthW.argtypes = [wt.HWND]
user32.GetWindowTextW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
user32.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
user32.IsWindowVisible.argtypes = [wt.HWND]
user32.IsWindow.argtypes = [wt.HWND]
user32.IsIconic.argtypes = [wt.HWND]
user32.GetForegroundWindow.restype = wt.HWND
user32.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]
user32.ShowWindowAsync.argtypes = [wt.HWND, ctypes.c_int]
user32.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
user32.DestroyIcon.argtypes = [wt.HICON]
user32.GetDC.restype = wt.HDC
user32.GetDC.argtypes = [wt.HWND]
user32.ReleaseDC.argtypes = [wt.HWND, wt.HDC]
kernel32.OpenProcess.restype = wt.HANDLE
kernel32.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
kernel32.QueryFullProcessImageNameW.argtypes = [wt.HANDLE, wt.DWORD, wt.LPWSTR, ctypes.POINTER(wt.DWORD)]
kernel32.GetProcessTimes.argtypes = [wt.HANDLE] + [ctypes.POINTER(wt.FILETIME)] * 4
kernel32.CloseHandle.argtypes = [wt.HANDLE]
dwmapi.DwmGetWindowAttribute.argtypes = [wt.HWND, wt.DWORD, ctypes.c_void_p, wt.DWORD]


# --- Windows ------------------------------------------------------------------------------------------------------

def _class_name(hwnd) -> str:
    buffer = ctypes.create_unicode_buffer(64)
    user32.GetClassNameW(hwnd, buffer, 64)
    return buffer.value


def _title(hwnd) -> str:
    length = user32.GetWindowTextLengthW(hwnd)
    buffer = ctypes.create_unicode_buffer(length + 1)
    user32.GetWindowTextW(hwnd, buffer, length + 1)
    return buffer.value


def _on_taskbar(hwnd) -> bool:
    """Roughly the taskbar's own rule: visible, titled, not cloaked, not a tool window, and unowned unless it asks to be
    shown (WS_EX_APPWINDOW). The desktop and the taskbar itself are left out."""
    if not user32.IsWindowVisible(hwnd) or user32.GetWindowTextLengthW(hwnd) == 0:
        return False
    style = user32.GetWindowLongW(hwnd, _GWL_EXSTYLE)
    if style & _WS_EX_TOOLWINDOW:
        return False
    if user32.GetWindow(hwnd, _GW_OWNER) and not style & _WS_EX_APPWINDOW:
        return False
    cloaked = wt.DWORD()
    dwmapi.DwmGetWindowAttribute(hwnd, _DWMWA_CLOAKED, ctypes.byref(cloaked), ctypes.sizeof(cloaked))
    return not cloaked.value and _class_name(hwnd) not in SHELL_CLASSES


def _filetime_epoch(ft: wt.FILETIME) -> float:
    ticks = (ft.dwHighDateTime << 32) | ft.dwLowDateTime
    return ticks / 1e7 - 11644473600.0 if ticks else 0.0


def _process(pid: int) -> tuple[str | None, float]:
    handle = kernel32.OpenProcess(_PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        return None, 0.0
    try:
        size = wt.DWORD(1024)
        buffer = ctypes.create_unicode_buffer(size.value)
        path = buffer.value if kernel32.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(size)) else None
        times = [wt.FILETIME() for _ in range(4)]
        started = _filetime_epoch(times[0]) if kernel32.GetProcessTimes(handle, *[ctypes.byref(t) for t in times]) else 0.0
        return path, started
    finally:
        kernel32.CloseHandle(handle)


version.GetFileVersionInfoSizeW.argtypes = [wt.LPCWSTR, ctypes.POINTER(wt.DWORD)]
version.GetFileVersionInfoW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, ctypes.c_void_p]
version.VerQueryValueW.argtypes = [ctypes.c_void_p, wt.LPCWSTR, ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(wt.UINT)]


def file_description(path: str) -> str | None:
    """The program's own name from its version resource ("Google Chrome" for chrome.exe), or None."""
    size = version.GetFileVersionInfoSizeW(path, None)
    if not size:
        return None
    data = ctypes.create_string_buffer(size)
    if not version.GetFileVersionInfoW(path, 0, size, data):
        return None
    pointer, length = ctypes.c_void_p(), wt.UINT()
    languages = []
    if version.VerQueryValueW(data, "\\VarFileInfo\\Translation", ctypes.byref(pointer), ctypes.byref(length)) and length.value >= 4:
        words = ctypes.cast(pointer, ctypes.POINTER(wt.WORD))
        languages = [(words[i], words[i + 1]) for i in range(0, length.value // 2, 2)]
    for lang, codepage in languages + [(0x0409, 0x04B0), (0x0409, 0x04E4)]:
        key = f"\\StringFileInfo\\{lang:04x}{codepage:04x}\\FileDescription"
        if version.VerQueryValueW(data, key, ctypes.byref(pointer), ctypes.byref(length)) and length.value > 1:
            text = ctypes.wstring_at(pointer, length.value).rstrip("\x00").strip()
            if text:
                return text
    return None


# --- Icons --------------------------------------------------------------------------------------------------------

class _GUID(ctypes.Structure):
    _fields_ = [("Data1", wt.DWORD), ("Data2", wt.WORD), ("Data3", wt.WORD), ("Data4", ctypes.c_ubyte * 8)]

    @classmethod
    def parse(cls, text: str) -> "_GUID":
        h = text.strip("{}").replace("-", "")
        return cls(int(h[:8], 16), int(h[8:12], 16), int(h[12:16], 16), (ctypes.c_ubyte * 8)(*bytes.fromhex(h[16:])))


class _SHFILEINFOW(ctypes.Structure):
    _fields_ = [("hIcon", wt.HICON), ("iIcon", ctypes.c_int), ("dwAttributes", wt.DWORD),
                ("szDisplayName", wt.WCHAR * 260), ("szTypeName", wt.WCHAR * 80)]


class _ICONINFO(ctypes.Structure):
    _fields_ = [("fIcon", wt.BOOL), ("xHotspot", wt.DWORD), ("yHotspot", wt.DWORD), ("hbmMask", wt.HBITMAP),
                ("hbmColor", wt.HBITMAP)]


class _BITMAP(ctypes.Structure):
    _fields_ = [("bmType", wt.LONG), ("bmWidth", wt.LONG), ("bmHeight", wt.LONG), ("bmWidthBytes", wt.LONG),
                ("bmPlanes", wt.WORD), ("bmBitsPixel", wt.WORD), ("bmBits", ctypes.c_void_p)]


class _BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG), ("biPlanes", wt.WORD),
                ("biBitCount", wt.WORD), ("biCompression", wt.DWORD), ("biSizeImage", wt.DWORD),
                ("biXPelsPerMeter", wt.LONG), ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD),
                ("biClrImportant", wt.DWORD)]


class _BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", _BITMAPINFOHEADER), ("bmiColors", wt.DWORD * 3)]


_IID_IImageList = _GUID.parse("46EB5926-582E-4017-9FDF-E8998DAA0950")
_SHIL_EXTRALARGE = 2  # 48x48
_SHGFI_SYSICONINDEX = 0x4000
_SHGFI_PIDL = 0x8
_ILD_TRANSPARENT = 0x1
_COINIT_APARTMENTTHREADED = 0x2
shell32.SHGetFileInfoW.argtypes = [ctypes.c_void_p, wt.DWORD, ctypes.POINTER(_SHFILEINFOW), wt.UINT, wt.UINT]
shell32.SHGetFileInfoW.restype = ctypes.c_size_t
shell32.SHGetImageList.argtypes = [ctypes.c_int, ctypes.POINTER(_GUID), ctypes.POINTER(ctypes.c_void_p)]
shell32.SHGetImageList.restype = ctypes.c_long
shell32.SHParseDisplayName.argtypes = [wt.LPCWSTR, ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p), wt.ULONG,
                                       ctypes.POINTER(wt.ULONG)]
shell32.SHParseDisplayName.restype = ctypes.c_long
ole32.CoInitializeEx.argtypes = [ctypes.c_void_p, wt.DWORD]
ole32.CoInitializeEx.restype = ctypes.c_long
ole32.CoTaskMemFree.argtypes = [ctypes.c_void_p]
gdi32.GetObjectW.argtypes = [wt.HANDLE, ctypes.c_int, ctypes.c_void_p]
gdi32.GetDIBits.argtypes = [wt.HDC, wt.HBITMAP, wt.UINT, wt.UINT, ctypes.c_void_p, ctypes.POINTER(_BITMAPINFO), wt.UINT]
gdi32.DeleteObject.argtypes = [wt.HANDLE]
user32.GetIconInfo.argtypes = [wt.HICON, ctypes.POINTER(_ICONINFO)]
_thread_state = threading.local()


def _com() -> None:
    if not getattr(_thread_state, "com", False):
        ole32.CoInitializeEx(None, _COINIT_APARTMENTTHREADED)
        _thread_state.com = True


def _bits(hdc, bitmap, width: int, height: int) -> bytes | None:
    info = _BITMAPINFO()
    info.bmiHeader.biSize = ctypes.sizeof(_BITMAPINFOHEADER)
    info.bmiHeader.biWidth, info.bmiHeader.biHeight = width, -height  # top-down
    info.bmiHeader.biPlanes, info.bmiHeader.biBitCount = 1, 32
    buffer = ctypes.create_string_buffer(width * height * 4)
    if gdi32.GetDIBits(hdc, bitmap, 0, height, buffer, ctypes.byref(info), 0) != height:
        return None
    return buffer.raw


def _hicon_png(hicon, size: int) -> bytes | None:
    info = _ICONINFO()
    if not user32.GetIconInfo(hicon, ctypes.byref(info)):
        return None
    try:
        if not info.hbmColor:
            return None  # monochrome icon
        bitmap = _BITMAP()
        gdi32.GetObjectW(info.hbmColor, ctypes.sizeof(bitmap), ctypes.byref(bitmap))
        width, height = bitmap.bmWidth, abs(bitmap.bmHeight)
        if not 0 < width <= 512 or not 0 < height <= 512:
            return None
        hdc = user32.GetDC(None)
        try:
            color = _bits(hdc, info.hbmColor, width, height)
            mask = _bits(hdc, info.hbmMask, width, height) if info.hbmMask else None
        finally:
            user32.ReleaseDC(None, hdc)
        if color is None:
            return None
        rgba = pure.icon_rgba(color, mask, width, height)
        width, height, rgba = _png.downscale(width, height, rgba, size)
        return _png.encode_rgba(width, height, rgba)
    finally:
        if info.hbmColor:
            gdi32.DeleteObject(info.hbmColor)
        if info.hbmMask:
            gdi32.DeleteObject(info.hbmMask)


def _image_list_icon(index: int, size: int) -> bytes | None:
    image_list = ctypes.c_void_p()
    if shell32.SHGetImageList(_SHIL_EXTRALARGE, ctypes.byref(_IID_IImageList), ctypes.byref(image_list)) != 0:
        return None
    vtable = ctypes.cast(ctypes.cast(image_list, ctypes.POINTER(ctypes.c_void_p))[0], ctypes.POINTER(ctypes.c_void_p))
    get_icon = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_int, wt.UINT, ctypes.POINTER(wt.HICON))(vtable[10])
    release = ctypes.WINFUNCTYPE(wt.ULONG, ctypes.c_void_p)(vtable[2])
    hicon = wt.HICON()
    try:
        if get_icon(image_list, index, _ILD_TRANSPARENT, ctypes.byref(hicon)) != 0 or not hicon:
            return None
        try:
            return _hicon_png(hicon, size)
        finally:
            user32.DestroyIcon(hicon)
    finally:
        release(image_list)


def path_icon(path: str, size: int) -> bytes | None:
    """The 48 px shell icon of a file (an .exe, or a shortcut: its own icon, without the arrow)."""
    _com()
    info = _SHFILEINFOW()
    if not shell32.SHGetFileInfoW(path, 0, ctypes.byref(info), ctypes.sizeof(info), _SHGFI_SYSICONINDEX):
        return None
    return _image_list_icon(info.iIcon, size)


def aumid_icon(aumid: str, size: int) -> bytes | None:
    """A Store app's icon, through its AppsFolder shell item."""
    _com()
    pidl = ctypes.c_void_p()
    if shell32.SHParseDisplayName(f"shell:AppsFolder\\{aumid}", None, ctypes.byref(pidl), 0, None) != 0 or not pidl:
        return None
    try:
        info = _SHFILEINFOW()
        if not shell32.SHGetFileInfoW(pidl, 0, ctypes.byref(info), ctypes.sizeof(info), _SHGFI_SYSICONINDEX | _SHGFI_PIDL):
            return None
        return _image_list_icon(info.iIcon, size)
    finally:
        ole32.CoTaskMemFree(pidl)


# --- Backend ------------------------------------------------------------------------------------------------------

def start_menu_roots() -> list[str]:
    roots = []
    for base in (os.environ.get("ProgramData"), os.environ.get("APPDATA")):
        if base:
            roots.append(ntpath.join(base, "Microsoft", "Windows", "Start Menu", "Programs"))
    return roots


def pinned_folder() -> str | None:
    base = os.environ.get("APPDATA")
    return ntpath.join(base, "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar") if base else None


def _shortcuts(root: str, depth: int = 4):
    for folder, dirs, files in os.walk(root):
        if folder[len(root):].count(os.sep) >= depth:
            dirs[:] = []
        for name in sorted(files):
            if name.lower().endswith(".lnk"):
                yield ntpath.join(folder, name)


def _read(path: str) -> bytes:
    with open(path, "rb") as f:
        return f.read(1 << 20)


def _taskband_favorites() -> bytes | None:
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband") as key:
            value, kind = winreg.QueryValueEx(key, "Favorites")
            return bytes(value) if kind == winreg.REG_BINARY else None
    except OSError:
        return None


def _inside(path: str, roots: list[str]) -> bool:
    real = ntpath.normcase(os.path.realpath(path))
    for root in roots:
        base = ntpath.normcase(os.path.realpath(root))
        if real.startswith(base.rstrip("\\") + "\\"):
            return True
    return False


class WindowsShell(ShellBackend):
    os_name = "windows"
    supported = True

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._installed: tuple[float, list[AppEntry]] | None = None
        self._packaged: list[AppEntry] = []
        self._pinned: tuple[float, list[AppEntry]] | None = None
        self._names: dict[str, str] = {}
        self._own_pid = os.getpid()

    # Listing

    def installed(self) -> list[AppEntry]:
        with self._lock:
            if self._installed and time.monotonic() - self._installed[0] < INSTALLED_TTL_S:
                return self._installed[1]
        entries = []
        count = 0
        for root in start_menu_roots():
            for path in _shortcuts(root):
                count += 1
                if count > pure.MAX_SHORTCUTS:
                    break
                try:
                    entries.append(pure.shortcut_entry(path, _read(path), os.path.expandvars))
                except OSError:
                    continue
        packaged = self._get_start_apps()
        result = pure.dedupe(entries + packaged)
        with self._lock:
            self._installed = (time.monotonic(), result)
            self._packaged = packaged
        return result

    @staticmethod
    def _get_start_apps() -> list[AppEntry]:
        """Store apps, from PowerShell's Get-StartApps (no input from the headset goes into this command)."""
        command = ["powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                   "Get-StartApps | Select-Object Name, AppID | ConvertTo-Json -Compress"]
        try:
            done = subprocess.run(command, capture_output=True, timeout=20, **paths.no_window({}))
        except (OSError, subprocess.TimeoutExpired) as e:
            log.info("Get-StartApps failed (%s): Store apps are left out", e)
            return []
        return pure.packaged_apps(done.stdout.decode("utf-8", "replace"))

    def pinned(self) -> list[AppEntry]:
        with self._lock:
            if self._pinned and time.monotonic() - self._pinned[0] < PINNED_TTL_S:
                return self._pinned[1]
            packaged = list(self._packaged)
        folder = pinned_folder()
        shortcuts = []
        if folder and os.path.isdir(folder):
            for path in _shortcuts(folder, depth=1):
                try:
                    entry = pure.shortcut_entry(path, _read(path), os.path.expandvars)
                except OSError:
                    continue
                if entry is not None:
                    shortcuts.append(entry)
        result = pure.pin_order(shortcuts, packaged, _taskband_favorites())
        with self._lock:
            self._pinned = (time.monotonic(), result)
        return result

    def _app_name(self, program: str | None, title: str) -> str:
        if not program:
            return title
        name = self._names.get(program)
        if name is None:
            stem = ntpath.splitext(ntpath.basename(program))[0]
            name = file_description(program) or stem[:1].upper() + stem[1:]
            if stem.lower() == "applicationframehost":
                name = ""  # a Store app's frame: the window title names the app
            self._names[program] = name
        return name or title

    def windows(self) -> list[WindowEntry]:
        handles = []

        def collect(hwnd, _):
            if _on_taskbar(hwnd):
                handles.append(hwnd)
            return True

        user32.EnumWindows(_WNDENUMPROC(collect), 0)
        foreground = user32.GetForegroundWindow()
        out = []
        for hwnd in handles:
            pid = wt.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
            if pid.value == self._own_pid:
                continue  # the agent's own windows (status window, approval box) are never offered
            path, started = _process(pid.value)
            program = pure.program_key(path)
            if program and ntpath.basename(program) == "applicationframehost.exe":
                program = None  # every Store app shares this host: match nothing, name by title
            title = _title(hwnd)
            out.append(WindowEntry(int(hwnd), title, program, self._app_name(pure.program_key(path), title),
                                   bool(user32.IsIconic(hwnd)), hwnd == foreground, started, pid.value,
                                   ("path", path) if path else None))
        return out

    # Icons

    def app_icon(self, app: AppEntry, size: int) -> bytes | None:
        if not app.icon:
            return None
        kind, value = app.icon
        if kind == "path":
            return path_icon(value, size)
        if kind == "aumid" and pure.AUMID.fullmatch(value):
            return aumid_icon(value, size)
        return None

    def window_icon(self, window: WindowEntry, size: int) -> bytes | None:
        return path_icon(window.icon[1], size) if window.icon else None

    # Actions

    def launch(self, app: AppEntry) -> None:
        if not app.launch:
            raise OSError("not launchable")
        kind, value = app.launch
        if kind == "lnk":
            roots = start_menu_roots() + ([pinned_folder()] if pinned_folder() else [])
            if not value.lower().endswith(".lnk") or not os.path.isfile(value) or not _inside(value, roots):
                raise OSError("the shortcut is no longer in the Start Menu or on the taskbar")
            os.startfile(value)
        elif kind == "aumid":
            if not pure.AUMID.fullmatch(value):
                raise OSError("not a Store app id")
            explorer = ntpath.join(os.environ.get("WINDIR", r"C:\Windows"), "explorer.exe")
            subprocess.Popen([explorer, f"shell:AppsFolder\\{value}"], **paths.no_window({}))
        else:
            raise OSError("not launchable")

    def window_action(self, handle: int, action: str) -> bool:
        hwnd = wt.HWND(handle)
        if not user32.IsWindow(hwnd) or not _on_taskbar(hwnd):
            return False
        pid = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value == self._own_pid:
            return False
        if action == "minimize":
            return bool(user32.ShowWindowAsync(hwnd, _SW_MINIMIZE)) or bool(user32.IsIconic(hwnd))
        if action == "restore":
            user32.ShowWindowAsync(hwnd, _SW_RESTORE)
            return True
        if action == "close":
            return bool(user32.PostMessageW(hwnd, _WM_CLOSE, 0, 0))
        if action == "raise":
            import winapi
            if user32.IsIconic(hwnd):
                user32.ShowWindowAsync(hwnd, _SW_RESTORE)
                end = time.monotonic() + 1.0
                while user32.IsIconic(hwnd) and time.monotonic() < end:
                    time.sleep(0.05)
            return winapi.focus_window(handle)
        return False
