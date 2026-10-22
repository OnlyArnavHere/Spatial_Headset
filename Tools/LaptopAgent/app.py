"""Entry point of the packaged app (PyInstaller) and of `python app.py`.

Helper processes are this same executable started with a `--_name` flag; they are dispatched here, before anything
heavy (and before tkinter) is imported.

  --_reaper PID                 the child-cleanup watcher (hostos/_reaper.py)
  --_linux_dialog TEXT CAP SEC  the Linux approval dialog (hostos/_linux_dialog.py)
  --version                     print the version and exit
  --selftest                    check the download/unpack code and the frozen helper command lines offline; exit 0/1
  --background                  start hidden (tray icon on Windows); the window opens from the tray or by launching again

SPATIAL_AGENT_SMOKE_QUIT=SECONDS (tests only) quits by itself after that long.
"""
import os
import queue
import sys

VERSION = "0.1.0"
POLL_MS = 200


class App:
    """The window's lifecycle: hide on close, show on request, quit for real. Commands from other threads (the tray,
    a second launch) arrive through `commands` and are run by poll() on the Tk thread. `root` is anything with Tk's
    withdraw / deiconify / lift / after / destroy, so this is testable without a display."""

    def __init__(self, root, controller, window=None, tray=None, hide_on_close: bool = True):
        self.root, self.controller, self.window, self.tray = root, controller, window, tray
        self.hide_on_close = hide_on_close
        self.commands: queue.Queue = queue.Queue()
        self.quit_requested = False

    def post(self, name: str) -> None:
        """Thread-safe: queue a command ("open", "stop_all", "quit") for the Tk thread."""
        self.commands.put(name)

    def hide(self) -> None:
        self.root.withdraw()

    def show(self) -> None:
        self.root.deiconify()
        self.root.lift()
        try:
            self.root.focus_force()
        except Exception:
            pass

    def on_close(self) -> None:
        """The window's close button: hide where something can bring it back (the tray, the Dock), else quit."""
        if self.hide_on_close:
            self.hide()
        else:
            self.quit()

    def handle(self, name: str) -> None:
        if name == "open":
            self.show()
        elif name == "stop_all":
            self.controller.stop_all()
        elif name == "quit":
            self.quit()

    def poll(self) -> None:
        while not self.quit_requested:
            try:
                name = self.commands.get_nowait()
            except queue.Empty:
                break
            self.handle(name)
        if not self.quit_requested:
            self.root.after(POLL_MS, self.poll)

    def relaunch(self) -> None:
        """Quit and start the app again (a new Screen Recording grant only applies to a new process)."""
        import subprocess
        try:
            subprocess.Popen(relaunch_argv(sys.executable, sys.argv[1:]), start_new_session=True)
        except OSError:
            return
        self.quit()

    def quit(self) -> None:
        """Stops the agent (the worker thread is joined for at most 5 s), removes the tray icon and ends mainloop."""
        if self.quit_requested:
            return
        import logging
        logging.getLogger("app").info("Quit requested")
        self.quit_requested = True
        if self.window is not None:
            self.window.close()
        self.controller.shutdown()
        if self.tray is not None:
            self.tray.stop()
        self.root.destroy()


def relaunch_argv(executable: str, args: list[str]) -> list[str]:
    """A command that starts the app again in two seconds, after this process has gone (so the single-instance lock
    is free). Inside a macOS bundle it opens the .app; otherwise it runs the executable."""
    path = executable.replace("\\", "/")
    if ".app/" in path:
        bundle = executable[:path.index(".app/") + 4]
        return ["/bin/sh", "-c", 'sleep 2; open -n "$0"', bundle]
    return ["/bin/sh", "-c", 'sleep 2; exec "$0" "$@"', executable, *args]


def selftest() -> int:
    """Offline check for the packaged build: the downloader against a fake server and a tiny generated archive (in a
    temporary data folder, never the real one), and the helper command lines as the frozen app builds them."""
    import hashlib
    import io
    import os
    import tempfile
    import zipfile
    import paths
    import tools_fetch

    problems = []

    def check(ok: bool, what: str) -> None:
        print(("ok   " if ok else "FAIL ") + what)
        if not ok:
            problems.append(what)

    class FakeResponse(io.BytesIO):
        headers = {}

        def __enter__(self):
            return self

        def __exit__(self, *exc):
            self.close()

    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as zf:
        zf.writestr("tool-1/bin/fake.exe", b"binary")
        zf.writestr("tool-1/LICENSE.txt", b"licence")
    data = buffer.getvalue()
    sha = hashlib.sha256(data).hexdigest()
    fake_entry = {"version": "selftest", "url": "https://example.invalid/fake.zip", "sha256": sha,
                  "extract": {"tool-1/bin/fake.exe": "fake.exe", "tool-1/LICENSE.txt": "LICENSE-fake.txt"},
                  "binary": "fake.exe"}
    opened = []

    def opener(request):
        opened.append(request.full_url)
        return FakeResponse(data)

    saved_env = os.environ.get("SPATIAL_LAUNCHER_HOME")
    saved = (tools_fetch.MANIFEST, paths.frozen, getattr(sys, "frozen", None))
    with tempfile.TemporaryDirectory() as home:
        os.environ["SPATIAL_LAUNCHER_HOME"] = home
        try:
            tools_fetch.MANIFEST = {tools_fetch.platform_key(): {"fake": fake_entry}}
            paths.frozen = lambda: True
            progress = []
            result = tools_fetch.ensure(["fake"], progress=lambda *a: progress.append(a), opener=opener)
            target = result["fake"]
            check(target.is_file() and target.read_bytes() == b"binary", "download, verify and unpack")
            check(target.parent == paths.tools_dir() == paths.data_dir() / "bin", "installed under the tools folder")
            check((target.parent / "LICENSE-fake.txt").is_file(), "licence file copied next to the binary")
            check(bool(progress), "progress is reported")
            tools_fetch.ensure(["fake"], opener=opener)
            check(len(opened) == 1, "second run downloads nothing")
            fake_entry["sha256"] = "0" * 64
            try:
                tools_fetch.ensure(["fake"], opener=lambda r: FakeResponse(data))
                check(False, "a wrong checksum is refused")
            except tools_fetch.ToolFetchError:
                check(True, "a wrong checksum is refused")
        except Exception as e:
            check(False, f"downloader raised {type(e).__name__}: {e}")
        finally:
            tools_fetch.MANIFEST, paths.frozen = saved[0], saved[1]
            if saved_env is None:
                os.environ.pop("SPATIAL_LAUNCHER_HOME", None)
            else:
                os.environ["SPATIAL_LAUNCHER_HOME"] = saved_env

    sys.frozen = True
    try:
        check(paths.helper_argv("reaper", "x.py") == [sys.executable, "--_reaper"], "frozen helper_argv for the reaper")
        check(paths.helper_argv("linux_dialog", "x.py") == [sys.executable, "--_linux_dialog"],
              "frozen helper_argv for the dialog")
    finally:
        if saved[2] is None:
            del sys.frozen
        else:
            sys.frozen = saved[2]
    check(paths.helper_argv("reaper", "x.py") == [sys.executable, "-I", "x.py"], "unfrozen helper_argv")
    print("selftest: " + ("FAILED (" + "; ".join(problems) + ")" if problems else "all checks passed"))
    return 1 if problems else 0


def run(background: bool = False) -> int:
    import logging
    import paths
    log_path = paths.setup_logging()
    log = logging.getLogger("app")
    log.info("Spatial Launcher Agent %s starting (frozen=%s, python %s, data folder %s, log %s)", VERSION,
             paths.frozen(), sys.version.split()[0], paths.data_dir(), log_path)
    if paths.frozen():
        try:
            import tools_fetch
            log.info("Tools: %s", {k: ("installed" if v["installed"] else "to be downloaded" if v["available"]
                                       else "not available") for k, v in tools_fetch.status().items()})
        except Exception as e:
            log.info("Tools: status unavailable (%s)", e)
    import instance as instance_mod
    single = instance_mod.SingleInstance()
    if not single.acquire():
        log.info("Already running: asking it to show its window")
        if not single.signal_existing():
            log.warning("The running instance did not answer; if no window appears, end it in Task Manager")
        return 0

    import tkinter as tk
    import controller as controller_mod
    import hostos
    import settings as settings_mod
    from gui.main_window import MainWindow
    app = None
    try:
        try:
            hostos.make_dpi_aware()
        except Exception:
            log.debug("DPI awareness failed", exc_info=True)
        root = tk.Tk()
        controller = controller_mod.AgentController()
        saved = settings_mod.load()
        effective = controller.apply(saved, user_initiated=False)  # never prompts at start; may force control off
        saved.allow_control, saved.cast_only = effective.allow_control, effective.cast_only
        app = App(root, controller, None, hide_on_close=sys.platform in ("win32", "darwin"))
        window = MainWindow(root, controller, saved, on_relaunch=app.relaunch)
        app.window = window
        single.serve(lambda: app.post("open"))
        if sys.platform == "win32":
            from gui.tray_win import TrayIcon
            tray = TrayIcon(app.post)
            if tray.start():
                app.tray = tray
            else:
                log.warning("No tray icon; closing the window will quit the app")
                app.hide_on_close = False
        root.protocol("WM_DELETE_WINDOW", app.on_close)
        if sys.platform == "darwin":
            root.createcommand("::tk::mac::ReopenApplication", app.show)
            root.createcommand("::tk::mac::Quit", lambda: app.post("quit"))
        smoke = os.environ.get("SPATIAL_AGENT_SMOKE_QUIT")
        if smoke:
            root.after(int(float(smoke) * 1000), lambda: app.post("quit"))
        if background:
            app.hide()
            if app.tray is None:
                log.warning("Started hidden without a tray icon: launch the app again to bring the window back")
        app.poll()
        controller.start()
        root.mainloop()
    finally:
        if app is not None and not app.quit_requested:  # mainloop ended some other way
            app.quit_requested = True
            app.controller.shutdown()
            if app.tray is not None:
                app.tray.stop()
        single.release()
    return 0


def main(argv: list[str] | None = None) -> int:
    argv = sys.argv[1:] if argv is None else list(argv)
    if argv[:1] == ["--_reaper"]:
        from hostos import _reaper
        pid = int(argv[1])
        (_reaper.main_linux if sys.platform.startswith("linux") else _reaper.main)(pid)
        return 0
    if argv[:1] == ["--_linux_dialog"]:
        from hostos import _linux_dialog
        return _linux_dialog.main(["_linux_dialog.py", *argv[1:]]) or 0
    if argv[:1] == ["--version"]:
        print(VERSION)
        return 0
    if argv[:1] == ["--selftest"]:
        return selftest()
    return run(background="--background" in argv)


def finish(code) -> None:
    """Ends the process the hard way (an executor thread still probing ffmpeg would otherwise keep it open), but first
    runs the atexit handlers, which release held keys and buttons and close approval prompts, and flushes the log.
    Child processes die with us (job object / reaper)."""
    import atexit
    import logging
    logging.getLogger("app").info("Exiting with code %s", code)
    try:
        atexit._run_exitfuncs()  # private, but it is the only way to run them without exiting normally
    except Exception:
        logging.getLogger("app").exception("exit handlers failed")
    logging.shutdown()
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.flush()
        except Exception:
            pass
    os._exit(code if isinstance(code, int) else 1)


def entry() -> None:
    try:
        result = main()
    except SystemExit as e:
        result = e.code if isinstance(e.code, int) else (0 if e.code is None else 1)
    except BaseException:
        import logging
        logging.getLogger("app").exception("The app stopped because of an error")
        result = 1
    finish(result)


if __name__ == "__main__":
    entry()
