"""Windows notification-area icon (ctypes, no dependency). The icon lives on its own thread with a hidden message
window; menu choices are reported by name through `on_command`, which must only hand them to the Tk thread (a queue).

Importing this module is safe anywhere and shows nothing; only TrayIcon.start() touches Windows.
"""
import ctypes
import logging
import sys
import threading
from ctypes import wintypes
from typing import Callable

log = logging.getLogger("tray")

NIM_ADD, NIM_DELETE = 0, 2
NIF_MESSAGE, NIF_ICON, NIF_TIP = 0x1, 0x2, 0x4
WM_NULL, WM_DESTROY, WM_CLOSE, WM_USER = 0x0000, 0x0002, 0x0010, 0x0400
WM_LBUTTONDBLCLK, WM_RBUTTONUP = 0x0203, 0x0205
CALLBACK_MESSAGE = WM_USER + 1
MF_STRING, TPM_RETURNCMD, TPM_RIGHTBUTTON = 0x0, 0x0100, 0x0002
IDI_APPLICATION = 32512
TIP = "Spatial Launcher Agent"

ID_OPEN, ID_STOP_ALL, ID_QUIT = 1, 2, 3
MENU = [(ID_OPEN, "Open", "open"), (ID_STOP_ALL, "Stop all", "stop_all"), (ID_QUIT, "Quit", "quit")]

LRESULT = ctypes.c_ssize_t
WNDPROC = getattr(ctypes, "WINFUNCTYPE", ctypes.CFUNCTYPE)(LRESULT, wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM)


class NOTIFYICONDATAW(ctypes.Structure):
    _fields_ = [("cbSize", wintypes.DWORD), ("hWnd", wintypes.HWND), ("uID", wintypes.UINT),
                ("uFlags", wintypes.UINT), ("uCallbackMessage", wintypes.UINT), ("hIcon", wintypes.HICON),
                ("szTip", wintypes.WCHAR * 128), ("dwState", wintypes.DWORD), ("dwStateMask", wintypes.DWORD),
                ("szInfo", wintypes.WCHAR * 256), ("uVersion", wintypes.UINT),
                ("szInfoTitle", wintypes.WCHAR * 64), ("dwInfoFlags", wintypes.DWORD),
                ("guidItem", ctypes.c_byte * 16), ("hBalloonIcon", wintypes.HICON)]


class WNDCLASSW(ctypes.Structure):
    _fields_ = [("style", wintypes.UINT), ("lpfnWndProc", WNDPROC), ("cbClsExtra", ctypes.c_int),
                ("cbWndExtra", ctypes.c_int), ("hInstance", wintypes.HINSTANCE), ("hIcon", wintypes.HICON),
                ("hCursor", wintypes.HANDLE), ("hbrBackground", wintypes.HBRUSH), ("lpszMenuName", wintypes.LPCWSTR),
                ("lpszClassName", wintypes.LPCWSTR)]


def notify_data(hwnd, icon, tip: str = TIP, uid: int = 1) -> NOTIFYICONDATAW:
    """The NOTIFYICONDATA for adding (and deleting) our icon: callback message, icon and tooltip."""
    data = NOTIFYICONDATAW()
    data.cbSize = ctypes.sizeof(NOTIFYICONDATAW)
    data.hWnd, data.uID, data.hIcon = hwnd, uid, icon
    data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP
    data.uCallbackMessage = CALLBACK_MESSAGE
    data.szTip = tip[:127]
    return data


def command_name(menu_id: int) -> str | None:
    return next((name for i, _, name in MENU if i == menu_id), None)


def _bind(user32, shell32, kernel32) -> None:
    """Exact prototypes: handles are 64-bit on 64-bit Windows and ctypes would otherwise pass them as C ints."""
    H, U, W, L = wintypes.HANDLE, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM
    for fn, res, args in [
            (user32.RegisterClassW, wintypes.ATOM, [ctypes.POINTER(WNDCLASSW)]),
            (user32.CreateWindowExW, wintypes.HWND, [wintypes.DWORD, wintypes.LPCWSTR, wintypes.LPCWSTR,
                                                     wintypes.DWORD, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                                     ctypes.c_int, wintypes.HWND, wintypes.HMENU,
                                                     wintypes.HINSTANCE, wintypes.LPVOID]),
            (user32.DefWindowProcW, LRESULT, [wintypes.HWND, U, W, L]),
            (user32.DestroyWindow, wintypes.BOOL, [wintypes.HWND]),
            (user32.PostMessageW, wintypes.BOOL, [wintypes.HWND, U, W, L]),
            (user32.GetMessageW, wintypes.BOOL, [ctypes.POINTER(wintypes.MSG), wintypes.HWND, U, U]),
            (user32.TranslateMessage, wintypes.BOOL, [ctypes.POINTER(wintypes.MSG)]),
            (user32.DispatchMessageW, LRESULT, [ctypes.POINTER(wintypes.MSG)]),
            (user32.CreatePopupMenu, wintypes.HMENU, []),
            (user32.AppendMenuW, wintypes.BOOL, [wintypes.HMENU, U, ctypes.c_size_t, wintypes.LPCWSTR]),
            (user32.TrackPopupMenu, wintypes.BOOL, [wintypes.HMENU, U, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                                    wintypes.HWND, wintypes.LPVOID]),
            (user32.DestroyMenu, wintypes.BOOL, [wintypes.HMENU]),
            (user32.SetForegroundWindow, wintypes.BOOL, [wintypes.HWND]),
            (user32.GetCursorPos, wintypes.BOOL, [ctypes.POINTER(wintypes.POINT)]),
            (user32.LoadIconW, wintypes.HICON, [wintypes.HINSTANCE, wintypes.LPVOID]),
            (user32.PostQuitMessage, None, [ctypes.c_int]),
            (user32.RegisterWindowMessageW, U, [wintypes.LPCWSTR]),
            (shell32.Shell_NotifyIconW, wintypes.BOOL, [wintypes.DWORD, ctypes.POINTER(NOTIFYICONDATAW)]),
            (shell32.ExtractIconW, wintypes.HICON, [wintypes.HINSTANCE, wintypes.LPCWSTR, U]),
            (kernel32.GetModuleHandleW, wintypes.HINSTANCE, [wintypes.LPCWSTR])]:
        fn.restype, fn.argtypes = res, args


class TrayIcon:
    def __init__(self, on_command: Callable[[str], None]):
        self._on_command = on_command
        self._thread: threading.Thread | None = None
        self._hwnd = None
        self._ready = threading.Event()
        self._proc = None  # keeps the WNDPROC callback alive for as long as the window exists
        self.ok = False

    def start(self) -> bool:
        """Starts the icon thread; True once the icon is in the notification area."""
        if sys.platform != "win32":
            return False
        self._thread = threading.Thread(target=self._run, name="tray", daemon=True)
        self._thread.start()
        self._ready.wait(5.0)
        return self.ok

    def stop(self) -> None:
        """Removes the icon (the message window's WM_DESTROY does it on the tray thread) and joins the thread."""
        hwnd, thread = self._hwnd, self._thread
        if hwnd and thread is not None and thread.is_alive():
            ctypes.windll.user32.PostMessageW(hwnd, WM_CLOSE, 0, 0)
            thread.join(3.0)

    # --- the icon thread -----------------------------------------------------------------------------------------

    def dispatch(self, menu_id: int) -> None:
        name = command_name(menu_id)
        if name is not None:
            self._on_command(name)

    def handle_callback(self, mouse_message: int, show_menu: Callable[[], int]) -> None:
        """Double click opens the window; right click shows the menu and runs the choice."""
        if mouse_message == WM_LBUTTONDBLCLK:
            self._on_command("open")
        elif mouse_message == WM_RBUTTONUP:
            self.dispatch(show_menu())

    def _icon(self):
        user32, shell32 = ctypes.windll.user32, ctypes.windll.shell32
        icon = shell32.ExtractIconW(None, sys.executable, 0)
        if not icon or icon == 1:
            icon = user32.LoadIconW(None, IDI_APPLICATION)
        return icon

    def _menu(self, hwnd) -> int:
        user32 = ctypes.windll.user32
        menu = user32.CreatePopupMenu()
        for i, text, _ in MENU:
            user32.AppendMenuW(menu, MF_STRING, i, text)
        point = wintypes.POINT()
        user32.GetCursorPos(ctypes.byref(point))
        user32.SetForegroundWindow(hwnd)  # otherwise the menu doesn't close when clicking elsewhere
        choice = user32.TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, point.x, point.y, 0, hwnd, None)
        user32.PostMessageW(hwnd, WM_NULL, 0, 0)  # lets the menu close properly (documented TrackPopupMenu quirk)
        user32.DestroyMenu(menu)
        return choice

    def _run(self) -> None:
        user32, shell32, kernel32 = ctypes.windll.user32, ctypes.windll.shell32, ctypes.windll.kernel32
        state = {"hwnd": None, "icon": None, "taskbar_created": None}

        def proc(hwnd, msg, wparam, lparam):
            try:
                if msg == CALLBACK_MESSAGE:
                    self.handle_callback(lparam & 0xFFFF, lambda: self._menu(hwnd))
                    return 0
                if msg == state["taskbar_created"] and state["icon"]:  # Explorer restarted: put the icon back
                    shell32.Shell_NotifyIconW(NIM_ADD, ctypes.byref(notify_data(hwnd, state["icon"])))
                    return 0
                if msg == WM_CLOSE:
                    user32.DestroyWindow(hwnd)
                    return 0
                if msg == WM_DESTROY:
                    shell32.Shell_NotifyIconW(NIM_DELETE, ctypes.byref(notify_data(hwnd, state["icon"])))
                    user32.PostQuitMessage(0)
                    return 0
            except Exception:
                log.exception("tray message failed")
            return user32.DefWindowProcW(hwnd, msg, wparam, lparam)

        self._proc = WNDPROC(proc)
        try:
            _bind(user32, shell32, kernel32)
            state["taskbar_created"] = user32.RegisterWindowMessageW("TaskbarCreated")
            instance = kernel32.GetModuleHandleW(None)
            cls = WNDCLASSW()
            cls.lpfnWndProc, cls.hInstance, cls.lpszClassName = self._proc, instance, "SpatialLauncherAgentTray"
            user32.RegisterClassW(ctypes.byref(cls))
            hwnd = user32.CreateWindowExW(0, cls.lpszClassName, TIP, 0, 0, 0, 0, 0, None, None, instance, None)
            if not hwnd:
                raise OSError("CreateWindowExW failed")
            state["hwnd"] = self._hwnd = hwnd
            state["icon"] = self._icon()
            self.ok = bool(shell32.Shell_NotifyIconW(NIM_ADD, ctypes.byref(notify_data(hwnd, state["icon"]))))
        except Exception:
            log.exception("tray icon failed to start")
            self._ready.set()
            return
        self._ready.set()
        message = wintypes.MSG()
        while user32.GetMessageW(ctypes.byref(message), None, 0, 0) > 0:
            user32.TranslateMessage(ctypes.byref(message))
            user32.DispatchMessageW(ctypes.byref(message))
        self._hwnd = None
