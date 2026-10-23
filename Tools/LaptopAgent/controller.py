"""Runs the agent (MediaMTX, capture, the headset link) on a worker thread so a window can drive it.

The GUI thread calls start(), stop_all(), apply(), shutdown() and polls events() / status(); everything else happens on
the worker thread's asyncio loop. This module never imports tkinter. Every external piece is injectable for tests.
"""
import asyncio
import errno
import logging
import queue
import shutil
import socket
import sys
import threading
from types import SimpleNamespace
from typing import Callable

import agent
import approval
import discovery
import hostos
import identity
import pairing
import paths
import tools_fetch
from hostos import _macos_perms as perms_mod
from session import LinkRunner
from streams import StreamManager

log = logging.getLogger("controller")
WATCH_S = 0.5
JOIN_S = 5.0
PAIR_TIMEOUT_S = 120.0
MEDIAMTX_WAIT_S = 2.0  # then kill; must stay well under JOIN_S


def _probe_connect_errno(host: str, port: int):
    """The errno of a plain connect to host:port, or None if it connected."""
    try:
        socket.create_connection((host, port), timeout=3).close()
    except OSError as e:
        return e.errno
    return None


class HelperError(Exception):
    """A helper that would have ended the CLI agent with SystemExit (missing ffmpeg, no encoder, MediaMTX won't start)."""


def _guard(fn: Callable) -> Callable:
    def call(*args, **kwargs):
        try:
            return fn(*args, **kwargs)
        except SystemExit as e:
            raise HelperError(str(e.code)) from None
    return call


class _PairSession:
    """One pairing attempt: its typed-line queue, its generation and whether it was cancelled."""

    def __init__(self, gen: int) -> None:
        self.gen = gen
        self.lines: queue.Queue = queue.Queue()
        self.cancelled = False
        self.task: asyncio.Task | None = None


# agent.py's pairing messages name command-line flags; the window shows these instead (the CLI text is unchanged).
GUI_HINTS = [
    ("or pass --headset IP[:PORT] to connect to a known address. Waiting (Ctrl+C to stop)...",
     "or enter its address under Headset address. Waiting (Cancel to stop)..."),
    ("or use --headset.", "or enter its address under Headset address."),
    ("Run again with --headset IP[:PORT] to choose the headset.",
     "Close this and pair again, or enter the headset's address under Headset address."),
    ("and run --pair again.", "and try again."),
    ("Revoke it first (--revoke NAME).", "Remove it from the paired headsets first."),
]


def gui_hint(text: str) -> str:
    for old, new in GUI_HINTS:
        text = text.replace(old, new)
    return text


class AgentController:
    def __init__(self, *, loop_factory=None, tool=None, probe=None, start_mediamtx=None, capture_launcher=None,
                 reaper=None, streams_factory=None, runner_factory=None, kill_switch=None, load_identity=None,
                 store_factory=None, approver_factory=None, watch_s: float = WATCH_S, pair_command=None, pair_timeout: float = PAIR_TIMEOUT_S,
                 fetch=None, frozen=None, platform: str | None = None, gate=None, perm_probe=None,
                 connect_probe=None):
        self._loop_factory = loop_factory or agent.LOOP_FACTORY
        self._tool = tool or agent.tool
        self._probe = probe or agent.probe_encoder
        self._start_mediamtx = start_mediamtx or agent.start_mediamtx
        self._capture_launcher = capture_launcher or agent.capture_launcher
        self._reaper = reaper or hostos.ChildReaper
        self._streams_factory = streams_factory or StreamManager
        self._runner_factory = runner_factory or LinkRunner
        self._kill_switch = kill_switch or hostos.watch_kill_switch
        self._load_identity = load_identity or (lambda: identity.load_or_create(identity.default_home()))
        self._store_factory = store_factory or (lambda: pairing.PairStore(identity.default_home()))
        self._approver_factory = approver_factory or (
            lambda keyboard: approval.LaptopApprover(keyboard=keyboard, store=self._store or self._store_factory()))
        self._watch_s = watch_s
        self._pair_command = pair_command or agent.pair_command
        self._fetch = fetch or tools_fetch.ensure
        self._frozen = frozen or paths.frozen
        self._platform = platform or sys.platform
        self._gate = gate or perms_mod.PermissionGate()
        self._perm_probe = perm_probe
        self._connect_probe = connect_probe or _probe_connect_errno
        self._local_network_denied = False
        self._tools_ready = False
        self._fetch_idle = threading.Event()  # set while no download runs on an executor thread
        self._fetch_idle.set()
        self._pair_timeout = pair_timeout
        self._pair_guard = threading.Lock()
        self._pair_gen = 0
        self._pair_session: _PairSession | None = None
        self._pair_tasks: set = set()
        self._events: queue.Queue = queue.Queue()
        self._lock = threading.Lock()
        self._state, self._message = "idle", ""
        self._settings = SimpleNamespace(allow_control=False, cast_only=False, headset_address="", wired=False)
        self._loop: asyncio.AbstractEventLoop | None = None
        self._thread: threading.Thread | None = None
        self._ready = threading.Event()
        # worker-thread state
        self._args = None
        self._job = self._mediamtx = self._streams = self._runner = self._watch_task = None
        self._encoder = None
        self._ident = self._store = None
        self._options_key = None
        self._kill_registered = False
        self._closing = False

    # --- GUI-thread API ------------------------------------------------------------------------------------------

    def start(self) -> None:
        """Starts (or restarts after stop_all) the agent. Returns at once; watch status() and events()."""
        self._submit(self._start())

    def stop_all(self) -> None:
        """Ends every session and every capture; MediaMTX stays up, so start() is quick."""
        if self._loop is not None:
            self._submit(self._stop_runner("Stopped."))

    def shutdown(self) -> None:
        """Stops everything, MediaMTX included, and joins the worker thread (at most 5 s)."""
        loop, thread = self._loop, self._thread
        if loop is None or thread is None:
            return
        self._closing = True
        try:
            asyncio.run_coroutine_threadsafe(self._shutdown(), loop).result(JOIN_S)
        except Exception:
            log.debug("shutdown did not finish cleanly", exc_info=True)
        loop.call_soon_threadsafe(loop.stop)
        thread.join(JOIN_S)
        self._fetch_idle.wait(2.0)  # a download notices the cancel at its next chunk and cleans up its .part file
        self._loop = self._thread = None
        self._closing = False

    def status(self) -> dict:
        with self._lock:
            return {"state": self._state, "message": self._message,
                    "encoder": self._encoder[0] if self._encoder else None}

    def events(self) -> list[dict]:
        """Everything that happened since the last call, oldest first."""
        out = []
        while True:
            try:
                out.append(self._events.get_nowait())
            except queue.Empty:
                return out

    def pairs(self) -> list:
        return (self._store or self._store_factory()).list()

    def revoke(self, name: str) -> bool:
        return (self._store or self._store_factory()).revoke(name)

    def stop_always_allowing(self, name: str) -> bool:
        """The pair stays; its control requests get the laptop's Yes/No box again."""
        return (self._store or self._store_factory()).set_always_allow(name, False)

    # Pairing: the dialog calls these from the GUI thread; pair_command runs on the agent loop. Progress arrives as
    # events: {"type": "pair_say", "text"}, {"type": "pair_prompt", "text"}, {"type": "pair_done", "ok", "text"}.

    def begin_pair(self) -> int:
        """Starts pairing (the pair dialog must be open) and returns its generation number, which tags every pair_*
        event so a dialog can drop a previous run's leftovers. Gives up after 120 s; ignored while one is running
        (the running one's number is returned)."""
        with self._pair_guard:
            if self._pair_session is not None:
                return self._pair_session.gen
            self._pair_gen += 1
            session = self._pair_session = _PairSession(self._pair_gen)
        self._submit(self._begin_pair(session))
        return session.gen

    def submit_code(self, text: str) -> None:
        """Delivers a typed line (the code, or the number of the chosen headset) to the waiting pairing."""
        session = self._pair_session
        if session is not None:
            session.lines.put(text)

    def cancel_pair(self) -> None:
        """Always effective, even before the worker has started the pairing: the session is flagged and its input is
        released at once, and the worker checks the flag before it runs pair_command."""
        with self._pair_guard:
            session, self._pair_session = self._pair_session, None
        if session is None:
            return
        session.cancelled = True
        session.lines.put(None)
        loop = self._loop
        if loop is not None:
            try:
                loop.call_soon_threadsafe(self._cancel_session, session)
            except RuntimeError:
                pass  # the loop is closed: nothing is running

    def apply(self, settings, user_initiated: bool = True):
        """Takes new settings (allow_control, cast_only, headset_address, wired) and returns the ones in effect. On
        macOS, control and Cast only stay off until Accessibility is granted (the system prompt is requested once, and
        only when user_initiated). Running: rebuilds the link runner if the options changed, keeping MediaMTX and the
        encoder."""
        merged = SimpleNamespace(**vars(self._settings))
        for key in vars(merged):
            if hasattr(settings, key):
                setattr(merged, key, getattr(settings, key))
        if merged.allow_control and merged.cast_only:
            raise ValueError("Allow control and Cast only can't both be on.")
        if merged.headset_address:
            discovery.parse_headset(merged.headset_address)
        if (merged.allow_control or merged.cast_only) and not self._accessibility_granted():
            if user_initiated:
                self._gate.ensure(perms_mod.ACCESSIBILITY)
            merged.allow_control = merged.cast_only = False
            self._events.put({"type": "permission_needed", "name": perms_mod.ACCESSIBILITY,
                              "message": "Allow Spatial Launcher under Accessibility in System Settings, then turn "
                                         "this on again."})
        self._settings = merged
        if self._loop is not None:
            self._submit(self._rebuild_if_changed())
        return merged

    # Permissions (macOS only; every method is a no-op or empty elsewhere)

    def _mac(self) -> bool:
        return self._platform == "darwin"

    def _accessibility_granted(self) -> bool:
        if not self._mac():
            return True
        for row in self.permission_rows():
            if row.name == perms_mod.ACCESSIBILITY:
                return row.status == perms_mod.GRANTED
        return False

    def permission_rows(self) -> list:
        return perms_mod.rows(self._perm_probe, self._local_network_denied, self._platform)

    def request_permission(self, name: str) -> bool:
        """Asks the system for one permission, at most once per run. Used by the Input Monitoring button only."""
        return self._mac() and self._gate.ensure(name)

    def _gated(self, launch):
        """Screen Recording is requested when the first stream starts (the system asks once and remembers)."""
        if not self._mac():
            return launch

        def gated(target, path):
            self._gate.ensure(perms_mod.SCREEN)
            return launch(target, path)
        return gated

    async def _ensure_tools(self) -> None:
        """Frozen builds ship without ffmpeg and MediaMTX: download them once (progress goes out as events)."""
        if self._tools_ready or not self._frozen():
            return
        loop = asyncio.get_running_loop()

        def progress(name, done, total):
            if self._closing:  # the app is quitting: stop the download (tools_fetch removes its .part file)
                raise tools_fetch.ToolFetchError("Cancelled")
            self._events.put({"type": "download", "name": name, "done": done, "total": total})

        def fetch(name):
            self._fetch_idle.clear()
            try:
                return self._fetch([name], progress)
            finally:
                self._fetch_idle.set()

        for name in ("ffmpeg", "mediamtx"):
            try:
                await loop.run_in_executor(None, fetch, name)
            except tools_fetch.ToolUnavailable as e:
                if name == "ffmpeg" and shutil.which("ffmpeg"):
                    log.info("Using the ffmpeg found on PATH (%s)", e)
                    continue
                hint = " Install it (for example with Homebrew: brew install ffmpeg) and start again." \
                    if name == "ffmpeg" else ""
                raise HelperError(f"{e}{hint}") from None
            except tools_fetch.ToolFetchError as e:
                raise HelperError(str(e)) from None
            except OSError as e:
                raise HelperError(f"Couldn't download {name}: {e}. Check the internet connection and start again.") \
                    from None
            finally:
                self._events.put({"type": "download_end", "name": name})
        self._tools_ready = True

    def _note_connect_failure(self, host: str, port: int) -> None:
        """A Bonjour-found headset refused every connection: on macOS that can be the Local Network permission. The
        error number is lost inside the handshake, so look once more with a plain connect."""
        if not self._mac() or self._local_network_denied:
            return
        code = self._connect_probe(host, port)
        if code in (errno.EHOSTUNREACH, errno.ENETUNREACH):
            self._local_network_denied = True
            log.warning("Connecting to %s:%d failed with 'no route': Local Network access is probably denied", host, port)
            self._events.put({"type": "permission", "name": perms_mod.LOCAL_NETWORK, "status": perms_mod.MISSING})

    # --- worker thread -------------------------------------------------------------------------------------------

    def _submit(self, coro) -> None:
        self._ensure_thread()
        asyncio.run_coroutine_threadsafe(coro, self._loop)

    def _ensure_thread(self) -> None:
        if self._thread is not None:
            return
        self._loop = self._loop_factory()
        self._thread = threading.Thread(target=self._run_loop, name="agent", daemon=True)
        self._thread.start()
        self._ready.wait(JOIN_S)

    def _run_loop(self) -> None:
        asyncio.set_event_loop(self._loop)
        self._loop.call_soon(self._ready.set)
        try:
            self._loop.run_forever()
        finally:
            self._loop.close()

    def _set(self, state: str, message: str = "") -> None:
        with self._lock:
            self._state, self._message = state, message
        self._events.put({"type": "status", "state": state, "message": message})

    def _make_args(self):
        args = agent.parse_args([])
        s = self._settings
        args.input = args.keyboard = bool(s.allow_control)
        args.cast_only = bool(s.cast_only)
        args.wired = bool(s.wired)
        args.headset = [discovery.parse_headset(s.headset_address)] if s.headset_address else None
        return args

    @staticmethod
    def _key(args):
        return (args.input, args.cast_only, args.wired, tuple(args.headset or ()))

    def _new_runner(self, args):
        approver = self._approver_factory(args.keyboard) if args.input else None
        return self._runner_factory(self._ident, self._store, self._streams, agent.RTSP_PORT,
                                    max_windows=args.max_windows, input_enabled=args.input,
                                    keyboard_enabled=args.keyboard, cast_only=args.cast_only,
                                    dry_run=False, pinned=args.headset, approver=approver,
                                    discover=not args.headset, on_connect_failed=self._on_connect_failed)

    async def _start(self) -> None:
        if self._state in ("starting", "running") or self._closing:
            return
        self._set("starting")
        loop = asyncio.get_running_loop()
        await self._stop_runner(None)
        try:
            self._args = args = self._make_args()
            self._ident = await loop.run_in_executor(None, self._load_identity)
            self._store = self._store_factory()
            await self._ensure_tools()
            if self._mediamtx is None or self._mediamtx.poll() is not None:
                await self._stop_media()
                ffmpeg = await loop.run_in_executor(None, _guard(self._tool), "ffmpeg")
                self._encoder = await loop.run_in_executor(None, _guard(self._probe), ffmpeg, args.encoder)
                if self._job is None:
                    self._job = self._reaper()
                self._mediamtx = await loop.run_in_executor(None, _guard(self._start_mediamtx), args, self._job)
            if self._closing:  # shutdown() ran while the helpers were starting
                await self._stop_media()
                return
            self._streams = _guard(self._streams_factory)(
                self._gated(_guard(self._capture_launcher)(args, self._encoder, self._job)))
            self._runner = self._new_runner(args)
            self._options_key = self._key(args)
            self._register_kill_switch()
            self._watch_task = asyncio.create_task(self._watch(self._runner))
        except HelperError as e:
            await self._fail(str(e))
            return
        except Exception as e:
            log.exception("Start failed")
            await self._fail(" ".join(str(e).split()) or type(e).__name__)
            return
        self._set("running")

    async def _fail(self, message: str) -> None:
        await self._stop_runner(None)
        self._set("error", message)

    def _on_connect_failed(self, host: str, port: int, discovered: bool) -> None:
        """LinkRunner's callback (any thread): a Bonjour-found headset refused every connection. On macOS that can be
        the Local Network permission, so look at the error with a plain connect, off the loop."""
        loop = self._loop
        if not discovered or not self._mac() or loop is None:
            return
        try:
            loop.call_soon_threadsafe(lambda: loop.run_in_executor(None, self._note_connect_failure, host, port))
        except RuntimeError:
            pass  # the loop closed

    def _register_kill_switch(self) -> None:
        """The hotkey is registered once for the whole life of the controller and routed to the current runner."""
        if self._kill_registered:
            return
        self._kill_registered = True
        if not self._kill_switch(self._hotkey):
            log.warning("Kill switch %s is unavailable; use Stop all in the window", hostos.KILL_SWITCH)

    def _hotkey(self) -> None:
        """Runs on the hotkey thread; finds the current loop at press time, so it survives shutdown and restart."""
        loop = self._loop
        if loop is None:
            return
        try:
            loop.call_soon_threadsafe(self._on_kill)
        except RuntimeError:
            pass  # the loop closed in between

    def _on_kill(self) -> None:
        if self._runner is not None:
            self._runner.kill_switch()
            self._events.put({"type": "kill"})
            asyncio.ensure_future(self._stop_runner("Stopped by the kill switch."))

    async def _watch(self, runner) -> None:
        """Runs the runner, ticks the streams and reports a dead MediaMTX or a failed runner."""
        task = asyncio.create_task(runner.run())
        try:
            while True:
                if self._mediamtx is not None and self._mediamtx.poll() is not None:
                    raise HelperError(f"MediaMTX stopped (exit code {self._mediamtx.returncode}).")
                if task.done():
                    task.result()
                    return
                if self._streams is not None:
                    self._streams.tick()
                await asyncio.sleep(self._watch_s)
        except asyncio.CancelledError:
            raise
        except Exception as e:
            log.error("Agent stopped: %s", e)
            if runner is self._runner:
                self._watch_task = None
                await self._fail(str(e))
        finally:
            task.cancel()
            await asyncio.gather(task, return_exceptions=True)

    async def _stop_runner(self, message: str | None) -> None:
        task, self._watch_task = self._watch_task, None
        if task is not None and task is not asyncio.current_task():
            task.cancel()
            await asyncio.gather(task, return_exceptions=True)
        self._runner = None
        if self._streams is not None:
            self._streams.stop_all()
        if message is not None:
            self._set("idle", message)

    async def _stop_media(self) -> None:
        mediamtx, self._mediamtx = self._mediamtx, None
        if mediamtx is None:
            return
        if mediamtx.poll() is None:
            mediamtx.terminate()
        try:
            await asyncio.get_running_loop().run_in_executor(None, lambda: mediamtx.wait(MEDIAMTX_WAIT_S))
        except Exception:
            mediamtx.kill()

    async def _rebuild_if_changed(self) -> None:
        if self._state != "running":
            return
        args = self._make_args()
        if self._key(args) == self._options_key:
            return
        self._options_key = self._key(args)  # before the first await, so a second apply sees it
        old, self._watch_task = self._watch_task, None
        if old is not None:
            old.cancel()
            await asyncio.gather(old, return_exceptions=True)
        self._streams.stop_all()
        self._args = args
        self._runner = self._new_runner(args)
        self._watch_task = asyncio.create_task(self._watch(self._runner))
        self._events.put({"type": "restarted"})

    @staticmethod
    def _cancel_session(session) -> None:
        session.cancelled = True
        if session.task is not None:
            session.task.cancel()

    def _pair_event(self, session, **event) -> None:
        self._events.put({"gen": session.gen, **event})

    async def _begin_pair(self, session) -> None:
        if session.cancelled:
            return
        lines = session.lines

        def read_line(prompt: str) -> str:
            self._pair_event(session, type="pair_prompt", text=prompt)
            line = lines.get()
            if line is None:
                raise EOFError
            return line

        def say(text: str) -> None:
            self._pair_event(session, type="pair_say", text=gui_hint(text))

        async def run() -> tuple[bool, str]:
            loop = asyncio.get_running_loop()
            try:
                ident = await loop.run_in_executor(None, self._load_identity)
                if session.cancelled:
                    return False, "Pairing cancelled."
                store = self._store or self._store_factory()
                address = getattr(self._settings, "headset_address", "")
                headset = discovery.parse_headset(address) if address else None
                inner = asyncio.ensure_future(
                    self._pair_command(headset, ident, store, socket.gethostname(), read_line=read_line, say=say))
                timed_out = []
                timer = loop.call_later(self._pair_timeout, lambda: (timed_out.append(1), inner.cancel()))
                try:
                    code = await inner  # pair_command turns a cancel into "Pairing cancelled." and returns 1
                finally:
                    timer.cancel()
                if timed_out:
                    return False, "Pairing timed out. Nothing was paired."
                if session.cancelled:
                    return False, "Pairing cancelled."
                return code == 0, "Paired." if code == 0 else "Pairing did not finish."
            except asyncio.CancelledError:
                return False, "Pairing cancelled."
            except Exception as e:
                log.exception("Pairing failed")
                return False, " ".join(str(e).split()) or type(e).__name__
            finally:
                lines.put(None)  # frees the input thread

        async def wrapper() -> None:
            ok, text = await run()
            with self._pair_guard:
                if self._pair_session is session:
                    self._pair_session = None
            self._pair_event(session, type="pair_done", ok=ok, text=text)

        session.task = asyncio.create_task(wrapper())
        self._pair_tasks.add(session.task)
        session.task.add_done_callback(self._pair_tasks.discard)

    async def _shutdown(self) -> None:
        with self._pair_guard:
            session, self._pair_session = self._pair_session, None
        if session is not None:
            session.cancelled = True
            session.lines.put(None)
        tasks = list(self._pair_tasks)
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        await self._stop_runner(None)
        await self._stop_media()
        others = [t for t in asyncio.all_tasks() if t is not asyncio.current_task()]
        for task in others:  # anything still in flight (a settings rebuild, the identity load) ends with the loop
            task.cancel()
        await asyncio.gather(*others, return_exceptions=True)
        self._set("idle", "Shut down.")
