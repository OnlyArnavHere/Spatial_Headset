"""The status window: running or idle, paired headsets, the settings toggles, the log.

Tk objects are touched on the main thread only. Everything from other threads (the controller's events, log records,
the slow wired-network check) comes through queues that poll() drains with root.after.
Importing this module opens no window; the helpers below are plain functions.
"""
import logging
import queue
import sys
import threading
import tkinter as tk
from tkinter import messagebox, ttk

import autostart
import hostos
import network
import settings as settings_mod
from hostos import _macos_perms
from gui.pair_dialog import PairDialog

log = logging.getLogger("window")
PAD = 12
POLL_MS = 200
LOG_LINES = 500

STATE_TEXT = {
    "idle": "Idle. Nothing is shared.",
    "starting": "Starting...",
    "running": "Running. Waiting for a paired headset.",
    "error": "Stopped because of a problem.",
}


def status_text(status: dict) -> str:
    text = STATE_TEXT.get(status.get("state"), "Idle. Nothing is shared.")
    message = status.get("message")
    if message and status.get("state") in ("error", "idle"):
        text = f"{text} {message}"
    elif status.get("state") == "running" and status.get("encoder"):
        text += f" (encoder: {status['encoder']})"
    return text


def permission_text(row) -> str:
    label = {"granted": "granted", "missing": "needs permission", "unknown": "not checked"}.get(row.status, row.status)
    if row.name == _macos_perms.LOCAL_NETWORK and row.status == "missing":
        label = "likely denied"
    return label + (" (optional)" if row.optional else "")


def pair_text(pair) -> str:
    always = "   ALWAYS ALLOWED" if getattr(pair, "always_allow", False) else ""
    return f"{pair.name}   {pair.fingerprint}   {pair.added}{always}"


def next_toggles(changed: str, value: bool, current: settings_mod.Settings) -> settings_mod.Settings:
    """The settings after one toggle changed. Allow control and Cast only exclude each other: turning one on turns the
    other off."""
    data = dict(vars(current))
    data[changed] = value
    if value and changed == "allow_control":
        data["cast_only"] = False
    if value and changed == "cast_only":
        data["allow_control"] = False
    return settings_mod.Settings(**data)


class LogQueueHandler(logging.Handler):
    """Collects formatted log lines for the window's log pane (emit may run on any thread)."""

    def __init__(self) -> None:
        super().__init__()
        self.lines: queue.Queue = queue.Queue()
        self.setFormatter(logging.Formatter("%(asctime)s %(name)s: %(message)s", datefmt="%H:%M:%S"))

    def emit(self, record: logging.LogRecord) -> None:
        self.lines.put(self.format(record))


class MainWindow:
    def __init__(self, root: tk.Misc, controller, settings: settings_mod.Settings | None = None, *,
                 save=settings_mod.save, wired_report=network.wired_report, kill_switch: str | None = None,
                 poll_ms: int = POLL_MS, attach_log: bool = True, login=None, show_permissions: bool | None = None,
                 open_settings=_macos_perms.open_settings, on_relaunch=None):
        self.root, self.controller, self._save = root, controller, save
        self._login = login or autostart
        self._open_settings, self._on_relaunch = open_settings, on_relaunch
        self._show_permissions = (sys.platform == "darwin") if show_permissions is None else show_permissions
        self._perm_widgets: dict = {}
        self._perm_tick = 0
        self._wired_report, self._poll_ms = wired_report, poll_ms
        self.settings = settings or settings_mod.Settings()
        self.dialog: PairDialog | None = None
        self._wired_results: queue.Queue = queue.Queue()
        self._after = None
        self._closed = False
        self.log_handler = LogQueueHandler()
        if attach_log:
            logging.getLogger().addHandler(self.log_handler)
        root.title("Spatial Launcher Agent")
        root.minsize(520, 560)
        self._build(kill_switch if kill_switch is not None else hostos.KILL_SWITCH)
        self.refresh_pairs()
        self._show_status(self.controller.status())
        self._refresh_login()
        self.refresh_permissions()
        if self.settings.wired:
            self._check_wired()
        self._after = root.after(poll_ms, self.poll)

    # --- layout --------------------------------------------------------------------------------------------------

    def _build(self, kill_switch: str) -> None:
        outer = ttk.Frame(self.root, padding=PAD)
        outer.pack(fill="both", expand=True)
        outer.columnconfigure(0, weight=1)
        outer.rowconfigure(5, weight=1)

        top = ttk.Frame(outer)
        top.grid(row=0, column=0, sticky="ew")
        top.columnconfigure(0, weight=1)
        self.status = tk.StringVar()
        ttk.Label(top, textvariable=self.status, font=("TkDefaultFont", 11, "bold"), wraplength=330,
                  justify="left").grid(row=0, column=0, sticky="w")
        self.start_button = ttk.Button(top, text="Start", command=self.controller.start)
        self.start_button.grid(row=0, column=1, padx=(8, 0))
        self.stop_button = ttk.Button(top, text="Stop all", command=self.controller.stop_all)
        self.stop_button.grid(row=0, column=2, padx=(8, 0))

        pairs = ttk.LabelFrame(outer, text="Paired headsets", padding=PAD)
        pairs.grid(row=1, column=0, sticky="ew", pady=(PAD, 0))
        pairs.columnconfigure(0, weight=1)
        self.pair_list = tk.Listbox(pairs, height=4, activestyle="none", exportselection=False)
        self.pair_list.grid(row=0, column=0, rowspan=3, sticky="ew")
        ttk.Button(pairs, text="Pair new headset...", command=self.open_pair_dialog).grid(
            row=0, column=1, padx=(8, 0), sticky="ew")
        ttk.Button(pairs, text="Remove", command=self.remove_selected).grid(
            row=1, column=1, padx=(8, 0), pady=(6, 0), sticky="ew")
        ttk.Button(pairs, text="Stop always allowing", command=self.stop_always_selected).grid(
            row=2, column=1, padx=(8, 0), pady=(6, 0), sticky="ew")

        options = ttk.LabelFrame(outer, text="Settings", padding=PAD)
        options.grid(row=2, column=0, sticky="ew", pady=(PAD, 0))
        options.columnconfigure(1, weight=1)
        self.vars = {name: tk.BooleanVar(value=getattr(self.settings, name))
                     for name in ("allow_control", "cast_only", "start_at_login", "wired")}
        rows = [("allow_control", "Allow control (mouse and keyboard from the headset; asked each time)"),
                ("cast_only", "Cast only (view plus play/pause, seek and volume)"),
                ("start_at_login", "Start when I log in (available in the installed app)")]
        for i, (name, text) in enumerate(rows):
            button = ttk.Checkbutton(options, text=text, variable=self.vars[name],
                                     command=lambda n=name: self.on_toggle(n))
            button.grid(row=i, column=0, columnspan=3, sticky="w", pady=2)
            if name == "start_at_login":
                self.login_button = button
        self.login_note = tk.StringVar()
        ttk.Label(options, textvariable=self.login_note, foreground="gray30", wraplength=470).grid(
            row=2, column=1, columnspan=2, sticky="e")
        ttk.Label(options, text="Headset address").grid(row=3, column=0, sticky="w", pady=(8, 0))
        self.address = tk.StringVar(value=self.settings.headset_address)
        entry = ttk.Entry(options, textvariable=self.address)
        entry.grid(row=3, column=1, sticky="ew", padx=8, pady=(8, 0))
        entry.bind("<Return>", lambda _e: self.on_address())
        ttk.Button(options, text="Apply", command=self.on_address).grid(row=3, column=2, pady=(8, 0))
        ttk.Label(options, text="Leave empty to find headsets automatically.", foreground="gray40").grid(
            row=4, column=1, columnspan=2, sticky="w", padx=8)
        ttk.Checkbutton(options, text="Wired (USB Personal Hotspot)", variable=self.vars["wired"],
                        command=lambda: self.on_toggle("wired")).grid(row=5, column=0, columnspan=3, sticky="w",
                                                                      pady=(8, 2))
        self.wired_text = tk.StringVar()
        ttk.Label(options, textvariable=self.wired_text, wraplength=470, justify="left",
                  foreground="gray30").grid(row=6, column=0, columnspan=3, sticky="w")

        self.permission_note = tk.StringVar()
        ttk.Label(outer, textvariable=self.permission_note, foreground="#9a3412", wraplength=480,
                  justify="left").grid(row=6, column=0, sticky="w")
        if self._show_permissions:
            self._build_permissions(outer)
        self.download_text = tk.StringVar()
        self.download_frame = ttk.Frame(outer)
        self.download_frame.grid(row=8, column=0, sticky="ew", pady=(PAD, 0))
        ttk.Label(self.download_frame, textvariable=self.download_text).pack(anchor="w")
        self.download_bar = ttk.Progressbar(self.download_frame, mode="determinate", maximum=100)
        self.download_bar.pack(fill="x", pady=(2, 0))
        self.download_frame.grid_remove()
        ttk.Label(outer, text=f"Emergency stop: press {kill_switch} anywhere to end every session.").grid(
            row=3, column=0, sticky="w", pady=(PAD, 0))
        ttk.Label(outer, text="Log").grid(row=4, column=0, sticky="w", pady=(PAD, 2))
        logframe = ttk.Frame(outer)
        logframe.grid(row=5, column=0, sticky="nsew")
        logframe.columnconfigure(0, weight=1)
        logframe.rowconfigure(0, weight=1)
        self.log_text = tk.Text(logframe, height=8, wrap="word", state="disabled", relief="flat",
                                borderwidth=1, highlightthickness=1)
        self.log_text.grid(row=0, column=0, sticky="nsew")
        scroll = ttk.Scrollbar(logframe, command=self.log_text.yview)
        scroll.grid(row=0, column=1, sticky="ns")
        self.log_text.configure(yscrollcommand=scroll.set)

    def _build_permissions(self, outer) -> None:
        box = ttk.LabelFrame(outer, text="macOS permissions", padding=PAD)
        box.grid(row=7, column=0, sticky="ew", pady=(PAD, 0))
        box.columnconfigure(1, weight=1)
        for i, name in enumerate(_macos_perms.NAMES):
            label = ttk.Label(box, text=name)
            label.grid(row=i, column=0, sticky="w", pady=2)
            state = tk.StringVar()
            ttk.Label(box, textvariable=state, foreground="gray30").grid(row=i, column=1, sticky="w", padx=8)
            frame = ttk.Frame(box)
            frame.grid(row=i, column=2, sticky="e")
            self._perm_widgets[name] = state
            if name == _macos_perms.LISTEN:
                ttk.Button(frame, text="Enable hotkey", command=lambda n=name: self.on_permission(n, True)).pack(
                    side="left", padx=(0, 4))
            ttk.Button(frame, text="Open Settings", command=lambda n=name: self.on_permission(n)).pack(side="left")
            if name == _macos_perms.SCREEN:
                ttk.Button(frame, text="Quit & Reopen", command=self.relaunch).pack(side="left", padx=(4, 0))

    # --- actions -------------------------------------------------------------------------------------------------

    def refresh_pairs(self) -> None:
        self.pair_list.delete(0, "end")
        try:
            for pair in self.controller.pairs():
                self.pair_list.insert("end", pair_text(pair))
            self._pair_names = [p.name for p in self.controller.pairs()]
        except Exception as e:
            self._pair_names = []
            self.pair_list.insert("end", f"(can't read the paired headsets: {e})")

    def remove_selected(self) -> None:
        selection = self.pair_list.curselection()
        if not selection or selection[0] >= len(self._pair_names):
            return
        name = self._pair_names[selection[0]]
        if messagebox.askyesno("Remove headset", f"Forget {name}? It will have to pair again.", parent=self.root):
            self.controller.revoke(name)
            self.refresh_pairs()

    def stop_always_selected(self) -> None:
        """The selected headset keeps its pair but needs a Yes on this laptop again before it takes control."""
        selection = self.pair_list.curselection()
        if not selection or selection[0] >= len(self._pair_names):
            return
        name = self._pair_names[selection[0]]
        try:
            self.controller.stop_always_allowing(name)
        except (ValueError, OSError) as e:
            messagebox.showerror("Paired headsets", f"Couldn't change {name}: {e}", parent=self.root)
        self.refresh_pairs()

    def open_pair_dialog(self) -> None:
        if self.dialog is None:
            self.dialog = PairDialog(self.root, self.controller, on_closed=self._dialog_closed)

    def _dialog_closed(self) -> None:
        self.dialog = None
        self.refresh_pairs()

    def refresh_permissions(self) -> None:
        if not self._show_permissions:
            return
        try:
            rows = self.controller.permission_rows()
        except Exception:
            return
        self._perm_rows = {row.name: row for row in rows}
        for row in rows:
            var = self._perm_widgets.get(row.name)
            if var is not None:
                var.set(permission_text(row))

    def on_permission(self, name: str, request: bool = False) -> None:
        """Open Settings on the row's pane; the Input Monitoring button also asks the system first (the only place that
        permission is ever requested)."""
        if request:
            self.controller.request_permission(name)
        row = getattr(self, "_perm_rows", {}).get(name)
        if row is not None:
            self._open_settings(row.url)

    def relaunch(self) -> None:
        """Screen Recording only takes effect after a restart of the app."""
        if self._on_relaunch is not None:
            self._on_relaunch()

    def _refresh_login(self) -> None:
        state, message = self._login.status()
        self.vars["start_at_login"].set(state == autostart.ENABLED)
        self.login_note.set(message)
        self.login_button.state(["disabled"] if state == autostart.UNAVAILABLE else ["!disabled"])
        if state == autostart.UNAVAILABLE:
            self.login_note.set("unavailable (not the packaged app)" if not message else message)

    def _toggle_login(self) -> None:
        wanted = bool(self.vars["start_at_login"].get())
        ok, message = (self._login.enable if wanted else self._login.disable)()
        self._refresh_login()
        if not ok and message:
            self.login_note.set(message)
        new = settings_mod.Settings(**{**vars(self.settings), "start_at_login": self.vars["start_at_login"].get()})
        self.settings = new
        try:
            self._save(new)
        except (OSError, ValueError):
            log.warning("Couldn't save settings")

    def on_toggle(self, name: str) -> None:
        if name == "start_at_login":
            self._toggle_login()
            return
        new = next_toggles(name, bool(self.vars[name].get()), self.settings)
        for key in ("allow_control", "cast_only"):
            self.vars[key].set(getattr(new, key))
        self._commit(new)
        if name == "wired" and new.wired:
            self._check_wired()
        elif name == "wired":
            self.wired_text.set("")

    def on_address(self) -> None:
        text = self.address.get().strip()
        self._commit(settings_mod.Settings(**{**vars(self.settings), "headset_address": text}))

    def _commit(self, new: settings_mod.Settings) -> bool:
        try:
            new.validate()
            effective = self.controller.apply(new)
            if effective is not None and (effective.allow_control, effective.cast_only) != (new.allow_control,
                                                                                            new.cast_only):
                # the system permission is missing: control stays off and the toggles say so
                new = settings_mod.Settings(**{**vars(new), "allow_control": effective.allow_control,
                                               "cast_only": effective.cast_only})
                for key in ("allow_control", "cast_only"):
                    self.vars[key].set(getattr(new, key))
            self._save(new)
        except ValueError as e:
            messagebox.showerror("Settings", str(e), parent=self.root)
            self.address.set(self.settings.headset_address)
            for key in self.vars:
                self.vars[key].set(getattr(self.settings, key))
            return False
        except OSError as e:
            # apply() ran before save(), so the new settings are in effect for this run; only the file failed
            messagebox.showerror("Settings", f"Couldn't save settings: {e}", parent=self.root)
        self.settings = new
        self.address.set(new.headset_address)
        return True

    def _check_wired(self) -> None:
        self.wired_text.set("Checking the USB hotspot...")

        def work() -> None:
            try:
                lines = "\n".join(self._wired_report().lines())
            except Exception as e:
                lines = f"Couldn't check: {e}"
            self._wired_results.put(lines)

        threading.Thread(target=work, daemon=True, name="wired-report").start()

    # --- polling -------------------------------------------------------------------------------------------------

    def _show_status(self, status: dict) -> None:
        self.status.set(status_text(status))
        running = status.get("state") in ("starting", "running")
        self.start_button.state(["disabled"] if running else ["!disabled"])
        self.stop_button.state(["!disabled"] if running else ["disabled"])

    def poll(self) -> None:
        """Drains the controller's events and the queues; reschedules itself."""
        if self._closed:
            return
        for event in self.controller.events():
            kind = event.get("type", "")
            if kind == "status":
                self._show_status(event)
            elif kind == "download":
                self._show_download(event)
            elif kind == "download_end":
                self.download_frame.grid_remove()
            elif kind == "permission_needed":
                self.permission_note.set(event.get("message", ""))
                self.refresh_permissions()
            elif kind == "permission":
                self.refresh_permissions()
            elif kind.startswith("pair_"):
                if self.dialog is not None:
                    self.dialog.handle_event(event)
                if kind == "pair_done":
                    self.refresh_pairs()
        self._drain_log()
        self._perm_tick += 1
        if self._perm_tick % 15 == 0:  # about every 3 s: the user may have granted something in System Settings
            self.refresh_permissions()
        while True:
            try:
                self.wired_text.set(self._wired_results.get_nowait())
            except queue.Empty:
                break
        self._after = self.root.after(self._poll_ms, self.poll)

    def _show_download(self, event: dict) -> None:
        self.download_frame.grid()
        total, done = event.get("total") or 0, event.get("done") or 0
        if total:
            self.download_bar.configure(mode="determinate", value=min(100, 100 * done / total))
            self.download_text.set(f"Downloading {event['name']}... {int(100 * done / total)}%")
        else:
            self.download_bar.configure(mode="indeterminate")
            self.download_text.set(f"Downloading {event['name']}... {done // (1 << 20)} MB")

    def _drain_log(self) -> None:
        lines = []
        while True:
            try:
                lines.append(self.log_handler.lines.get_nowait())
            except queue.Empty:
                break
        if not lines:
            return
        self.log_text.configure(state="normal")
        self.log_text.insert("end", "\n".join(lines) + "\n")
        extra = int(self.log_text.index("end-1c").split(".")[0]) - LOG_LINES
        if extra > 0:
            self.log_text.delete("1.0", f"{extra + 1}.0")
        self.log_text.see("end")
        self.log_text.configure(state="disabled")

    def close(self) -> None:
        """Stops polling and detaches the log handler (the caller destroys the root)."""
        self._closed = True
        if self.dialog is not None:
            self.dialog.close()
        if self._after is not None:
            try:
                self.root.after_cancel(self._after)
            except tk.TclError:
                pass
        logging.getLogger().removeHandler(self.log_handler)
