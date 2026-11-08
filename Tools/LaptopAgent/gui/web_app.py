"""The app window drawn with HTML (gui/web/) in the system web view: Edge WebView2 on Windows, WebKit on macOS
(pywebview). The page calls Bridge methods through window.pywebview.api; everything it shows comes from Bridge.poll().

The controller and the agent are the same as with the Tk window (gui/main_window.py), which stays as the fallback when
no web view is available. Bridge holds no Tk and opens nothing, so it is testable on its own.
"""
import logging
import os
import socket
import sys
import threading

import autostart
import settings as settings_mod
from gui.main_window import LogQueueHandler, next_toggles, permission_text, status_text

log = logging.getLogger("window")
WEB_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "web")
LOG_KEEP = 400
SEARCHING = "Looking for a headset showing a code..."
BAD_SAYINGS = ("That code", "Couldn't", "Pairing failed", "No headset", "The headset", "That is not")


class Bridge:
    """Called from the page on pywebview's worker threads; one lock keeps calls in order."""

    def __init__(self, controller, saved: settings_mod.Settings, *, save=settings_mod.save, login=None,
                 wired_report=None, show_permissions: bool | None = None, open_settings=None,
                 on_relaunch=None, on_quit=None, attach_log: bool = True, version: str = "",
                 on_minimize=None, on_close=None, on_shape=None):
        self._c, self._save, self._login = controller, save, login or autostart
        self._wired_report, self._open_settings = wired_report or _usb_report, open_settings
        self._on_relaunch, self._on_quit = on_relaunch, on_quit
        self._on_minimize, self._on_close, self._on_shape = on_minimize, on_close, on_shape
        # True when the OS blurs what is behind the window (gui/glass.py); False: the page draws a solid panel.
        self.glass: bool | None = None
        self._show_permissions = (sys.platform == "darwin") if show_permissions is None else show_permissions
        self._lock = threading.RLock()
        self._settings = saved
        self._version = version
        self._status = controller.status()
        self._download = None
        self._permission_note = ""
        self._wired_text = ""
        self._log: list[str] = []
        self._log_seq = 0
        self._pair = None
        self._perm_rows = {}
        self._log_handler = LogQueueHandler()
        self._sessions = SessionWatch()
        if attach_log:
            logging.getLogger().addHandler(self._log_handler)
            logging.getLogger("session").addHandler(self._sessions)
        if saved.wired:
            self.check_wired()

    # --- what the page shows -------------------------------------------------------------------------------------

    def poll(self, since: int = 0) -> dict:
        with self._lock:
            self._drain()
            pairs = []
            try:
                for p in self._c.pairs():
                    pairs.append({"name": p.name, "fingerprint": p.fingerprint, "added": str(p.added),
                                  "always": bool(getattr(p, "always_allow", False))})
                pairs_error = ""
            except Exception as e:
                pairs_error = f"Can't read the paired headsets: {e}"
            state, login_message = self._login.status()
            first = max(0, len(self._log) - (self._log_seq - since)) if since else 0
            return {
                "status": {"state": self._status.get("state", "idle"), "text": status_text(self._status),
                           "encoder": self._status.get("encoder") or "", "message": self._status.get("message", "")},
                "settings": {k: getattr(self._settings, k) for k in
                             ("allow_control", "cast_only", "start_at_login", "wired")},
                "login": {"enabled": state == autostart.ENABLED, "available": state != autostart.UNAVAILABLE,
                          "message": login_message or ("Available in the installed app"
                                                       if state == autostart.UNAVAILABLE else "")},
                "pairs": pairs, "pairs_error": pairs_error,
                "connected": self._sessions.connected() if self._status.get("state") == "running" else [],
                "pair": dict(self._pair) if self._pair else None,
                "download": self._download,
                "permission_note": self._permission_note,
                "permissions": self._permissions() if self._show_permissions else None,
                "wired_text": self._wired_text,
                "kill_switch": _kill_switch(),
                "laptop": socket.gethostname(),
                "version": self._version,
                "glass": self.glass,
                "platform": sys.platform,
                "log": self._log[first:] if since <= self._log_seq else self._log,
                "log_seq": self._log_seq,
            }

    def _drain(self) -> None:
        for event in self._c.events():
            kind = event.get("type", "")
            if kind == "status":
                self._status = event
            elif kind == "download":
                total, done = event.get("total") or 0, event.get("done") or 0
                self._download = {"name": event.get("name", ""), "percent": int(100 * done / total) if total else None,
                                  "mb": done // (1 << 20)}
            elif kind == "download_end":
                self._download = None
            elif kind == "permission_needed":
                self._permission_note = event.get("message", "")
            elif kind.startswith("pair_"):
                self._pair_event(event)
        while True:
            try:
                line = self._log_handler.lines.get_nowait()
            except Exception:
                break
            self._log.append(line)
            self._log_seq += 1
        if len(self._log) > LOG_KEEP:
            del self._log[:len(self._log) - LOG_KEEP]

    def _permissions(self) -> list:
        try:
            rows = self._c.permission_rows()
        except Exception:
            return []
        self._perm_rows = {row.name: row for row in rows}
        return [{"name": r.name, "state": permission_text(r), "granted": r.status == "granted"} for r in rows]

    # --- pairing ---------------------------------------------------------------------------------------------------

    def _pair_event(self, event: dict) -> None:
        p = self._pair
        if p is None or event.get("gen") != p["gen"]:
            return
        kind = event["type"]
        if kind == "pair_say":
            text = event["text"]
            p.update(status=text, tone="error" if text.startswith(BAD_SAYINGS) else "info")
        elif kind == "pair_prompt":
            p["prompt"], p["searching"] = event["text"].strip(), False
            if p["pending"]:
                p.update(pending=False, status="Sending...", tone="muted")
            else:
                p["asked"] = True
        elif kind == "pair_qr":
            try:
                import qr_image
                p.update(qr=qr_image.svg(event["payload"], scale=5), qr_key=str(p["gen"]))
            except Exception:
                log.exception("Can't draw the pairing QR code")
        elif kind == "pair_done":
            p.update(finished=True, searching=False, ok=bool(event.get("ok")), status=event["text"],
                     tone="ok" if event.get("ok") else "error")

    def pair_start(self) -> dict:
        with self._lock:
            if self._pair is None or self._pair["finished"]:
                gen = self._c.begin_pair()
                self._pair = {"gen": gen, "prompt": SEARCHING, "status": "", "tone": "muted", "searching": True,
                              "asked": False, "pending": False, "finished": False, "ok": False, "qr": "",
                              "qr_key": ""}
            return dict(self._pair)

    def pair_submit(self, text: str) -> dict:
        text = (text or "").strip()
        with self._lock:
            if not text:
                return dict(self._pair) if self._pair else {}
            if self._pair is None or self._pair["finished"]:
                self.pair_start()
            p = self._pair
            self._c.submit_code(text)
            if p["asked"]:
                p.update(asked=False, status="Sending...", tone="muted")
            else:
                p.update(pending=True, status=f"Got {text}. It will be sent as soon as the headset is found.",
                         tone="muted")
            return dict(p)

    def pair_cancel(self) -> None:
        with self._lock:
            if self._pair is not None and not self._pair["finished"]:
                self._c.cancel_pair()
            self._pair = None

    # --- actions -----------------------------------------------------------------------------------------------------

    def start(self) -> None:
        self._c.start()

    def stop_all(self) -> None:
        self._c.stop_all()

    def revoke(self, name: str) -> bool:
        with self._lock:
            return bool(self._c.revoke(name))

    def forget(self, name: str) -> str:
        """Removes a paired headset. Returns an error to show, or ""."""
        with self._lock:
            try:
                if self._c.revoke(name):
                    log.info("Forgot the headset %s", name)
                    return ""
                return f"{name} isn't paired any more."
            except (OSError, ValueError) as e:
                log.warning("Couldn't forget %s: %s", name, e)
                return f"Couldn't forget {name}: {e}"

    def stop_always(self, name: str) -> str:
        with self._lock:
            try:
                self._c.stop_always_allowing(name)
                return ""
            except (ValueError, OSError) as e:
                return f"Couldn't change {name}: {e}"

    def set_toggle(self, name: str, value: bool) -> str:
        """Returns an error to show, or ""."""
        with self._lock:
            if name == "start_at_login":
                ok, message = (self._login.enable if value else self._login.disable)()
                state, _ = self._login.status()
                new = settings_mod.Settings(**{**vars(self._settings), "start_at_login": state == autostart.ENABLED})
                self._settings = new
                try:
                    self._save(new)
                except (OSError, ValueError):
                    log.warning("Couldn't save settings")
                return "" if ok else (message or "Couldn't change it")
            if name not in ("allow_control", "cast_only", "wired"):
                return f"Unknown setting {name}"
            error = self._commit(next_toggles(name, bool(value), self._settings))
            if name == "wired":
                if self._settings.wired:
                    self.check_wired()
                else:
                    self._wired_text = ""
            return error

    def _commit(self, new: settings_mod.Settings) -> str:
        try:
            new.validate()
            effective = self._c.apply(new)
            if effective is not None and (effective.allow_control, effective.cast_only) != (new.allow_control,
                                                                                            new.cast_only):
                new = settings_mod.Settings(**{**vars(new), "allow_control": effective.allow_control,
                                               "cast_only": effective.cast_only})
            self._settings = new
            self._save(new)
        except ValueError as e:
            return str(e)
        except OSError as e:
            return f"Couldn't save settings: {e}"
        return ""

    def check_wired(self) -> None:
        self._wired_text = "Looking for the iPhone on the USB cable..."

        def work() -> None:
            try:
                report = self._wired_report()
                text = report if isinstance(report, str) else "\n".join(report.lines())
            except Exception as e:
                text = f"Couldn't check: {e}"
            self._wired_text = text

        threading.Thread(target=work, daemon=True, name="wired-report").start()

    def open_permission(self, name: str, request: bool = False) -> None:
        if request:
            self._c.request_permission(name)
        row = self._perm_rows.get(name)
        if row is not None and self._open_settings is not None:
            self._open_settings(row.url)

    def minimize(self) -> None:
        """The window's minimize button (the window has no title bar of its own)."""
        if self._on_minimize is not None:
            threading.Thread(target=self._on_minimize, daemon=True, name="minimize").start()

    def set_shape(self, shape) -> None:
        """The page's tile shapes ([[left, top, width, height, radius], ...] CSS px), so the glass fits them (macOS)."""
        if self._on_shape is not None and isinstance(shape, list):
            try:
                self._on_shape(shape)
            except Exception:
                log.debug("Glass shape failed", exc_info=True)

    def close_window(self) -> None:
        """The window's close button: hides it to the tray (Windows) or the Dock (macOS); the agent keeps running."""
        if self._on_close is not None:
            threading.Thread(target=self._on_close, daemon=True, name="close").start()

    def relaunch(self) -> None:
        if self._on_relaunch is not None:
            self._on_relaunch()

    def quit(self) -> None:
        if self._on_quit is not None:
            threading.Thread(target=self._on_quit, daemon=True, name="quit").start()

    def open_log_folder(self) -> None:
        import paths
        _reveal(str(paths.logs_dir()))

    def close(self) -> None:
        logging.getLogger().removeHandler(self._log_handler)
        logging.getLogger("session").removeHandler(self._sessions)
        self.pair_cancel()


class SessionWatch(logging.Handler):
    """Which headsets are linked right now, read from LinkRunner's "Linked with" and "Session with ... ended" log
    records (session.py), so the window can name the headset without a hook into the runner."""

    LINKED = "Linked with %s (%s:%d, %s)"
    ENDED = "Session with %s ended: %s"

    def __init__(self) -> None:
        super().__init__()
        self._live: dict[str, str] = {}
        self._guard = threading.Lock()

    def emit(self, record: logging.LogRecord) -> None:
        args = record.args if isinstance(record.args, tuple) else ()
        with self._guard:
            if record.msg == self.LINKED and len(args) >= 2:
                host = str(args[1])
                self._live[str(args[0])] = "USB cable" if host.startswith("usb:") else "Wi-Fi"
            elif record.msg == self.ENDED and args:
                self._live.pop(str(args[0]), None)

    def connected(self) -> list:
        with self._guard:
            return [{"name": name, "link": link} for name, link in self._live.items()]


def _usb_report() -> str:
    import usbmux
    return usbmux.describe()


def _kill_switch() -> str:
    try:
        import hostos
        return hostos.KILL_SWITCH
    except Exception:
        return "Ctrl+Alt+Shift+Q"


def _reveal(folder: str) -> None:
    import subprocess
    try:
        if sys.platform == "win32":
            os.startfile(folder)  # noqa: S606 (our own data folder)
        elif sys.platform == "darwin":
            subprocess.Popen(["/usr/bin/open", folder])
        else:
            subprocess.Popen(["xdg-open", folder])
    except OSError:
        log.warning("Couldn't open %s", folder)


def _lift_windows(window) -> bool:
    """Brings the window to the front with Win32 calls (they release the GIL). False when it has no handle yet."""
    native = getattr(window, "native", None)
    if native is None:
        return False
    try:
        import ctypes
        hwnd = ctypes.c_void_p(int(native.Handle.ToInt64()))  # reading the handle sends no message
        user32 = ctypes.windll.user32
        flags = 0x0001 | 0x0002 | 0x0040  # NOSIZE | NOMOVE | SHOWWINDOW
        user32.SetWindowPos(hwnd, ctypes.c_void_p(-1), 0, 0, 0, 0, flags)  # HWND_TOPMOST
        user32.SetWindowPos(hwnd, ctypes.c_void_p(-2), 0, 0, 0, 0, flags)  # HWND_NOTOPMOST
        user32.SetForegroundWindow(hwnd)
        return True
    except Exception:
        logging.getLogger("window").debug("Win32 lift failed", exc_info=True)
        return False


class WebRoot:
    """What app.App needs from its window (withdraw, deiconify, lift, focus_force, after, destroy) on a pywebview
    window. after() uses timers: App.poll then runs on a timer thread, and every call it makes is thread-safe."""

    def __init__(self, window, minimize_instead_of_hide: bool = False):
        self.window = window
        self._minimize = minimize_instead_of_hide
        self._destroyed = False

    def withdraw(self) -> None:
        if self._minimize:
            self.window.minimize()
        else:
            self.window.hide()

    def deiconify(self) -> None:
        self.window.show()
        self.window.restore()

    def lift(self) -> None:
        # On Windows, pywebview's on_top sets Form.TopMost from this (non-GUI) thread. Through pythonnet that property set
        # keeps the GIL while it waits for the GUI thread, which needs the GIL to answer: both wait forever and the window
        # stops responding (seen when a second launch asked the running app to show itself). ctypes releases the GIL.
        if sys.platform == "win32" and _lift_windows(self.window):
            return
        self.window.on_top = True
        self.window.on_top = False

    def focus_force(self) -> None:
        pass

    def after(self, ms: int, fn) -> None:
        if self._destroyed:
            return
        t = threading.Timer(ms / 1000.0, fn)
        t.daemon = True
        t.start()

    def destroy(self) -> None:
        if not self._destroyed:
            self._destroyed = True
            self.window.destroy()


def available() -> bool:
    try:
        import webview  # noqa: F401
    except Exception:
        return False
    return os.path.isfile(os.path.join(WEB_DIR, "index.html"))


def icon_path() -> str | None:
    for name in ("icon.ico" if sys.platform == "win32" else "icon.png", "icon.png"):
        path = os.path.join(WEB_DIR, name)
        if os.path.isfile(path):
            return path
    return None
