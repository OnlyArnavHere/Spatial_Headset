"""The laptop shell: the headset's search, dock/taskbar and app grid for this laptop, over the session's encrypted
control channel (session.py). It lists running, pinned and installed apps, sends their icons, launches apps and raises,
minimizes, restores or closes windows.

Every request needs the same approval as control: the session must hold GRANTED control (the laptop owner allowed it,
see dispatch.py), the agent must run with --input, and it must not be cast-only. Otherwise the request is refused and
nothing is listed or done. The gate is checked again right before anything with an effect (launch, window actions),
so a revoke or the kill switch that lands while a request waits wins.

The headset never sends paths or commands: apps are named by opaque ids ("app:<16 hex>") the agent assigned in this
session, windows by "window:<handle>" ids the agent listed in this session (and that still exist). Anything else is
"unknown id". Each kind of request has its own rate limit.

Requests (headset -> agent; "rid" is an optional request id, an integer the reply echoes):
  {"type":"apps.running","rid"}        -> {"type":"apps.running","rid","apps":[{"id","name","pinned","launchable",
                                            "windows":[{"id","title","minimized","focused"}]}],"truncated"?}
  {"type":"apps.pinned","rid"}         -> {"type":"apps.pinned","rid","apps":[{"id","name","running"}]}
  {"type":"apps.installed","rid","offset"?}
                                       -> {"type":"apps.installed","rid","apps":[{"id","name","running"}],
                                           "offset","total","next"}   (pages; "next" is null on the last page)
  {"type":"apps.icon","rid","id"}      -> {"type":"apps.icon","rid","id","size","png":"<base64>"}
  {"type":"apps.launch","rid","id"}    -> {"type":"apps.launched","rid","id","window":{"id","title"}|null}
  {"type":"window.raise"|"window.minimize"|"window.restore"|"window.close","rid","id"}
                                       -> {"type":"window.done","rid","action","id","ok"}
  {"type":"search.query","rid","q"}    -> {"type":"search.results","rid","q","results":[{"kind":"app","id","name",
                                           "running"} | {"kind":"window","id","title","app"}]}
  Any refusal or failure               -> {"type":"shell.error","rid","request","reason"}
     reasons: "view only", "cast only", "input off", "stopped", "unsupported", "bad request", "unknown id",
              "rate limited", "busy", "not launchable", "no icon", "failed", "dry run"
With --dry-run (input is logged, not injected) listing works but launches and window actions are refused ("dry run").
"""
import asyncio
import base64
import json
import logging
import re
import secrets
import sys
import time
import unicodedata
from collections import OrderedDict
from concurrent.futures import ThreadPoolExecutor
from typing import Callable

import keys
from hostos._shell_types import AppEntry, ShellBackend, UnsupportedShell, WindowEntry

log = logging.getLogger("shell")

REQUESTS = ("apps.running", "apps.pinned", "apps.installed", "apps.icon", "apps.launch", "window.raise",
            "window.minimize", "window.restore", "window.close", "search.query")
WINDOW_ACTIONS = {"window.raise": "raise", "window.minimize": "minimize", "window.restore": "restore",
                  "window.close": "close"}
MAX_REPLY = 15 * 1024          # under the 16 KiB control-message limit, with room for the envelope
MAX_NAME = 64
MAX_QUERY = 100
MAX_RESULTS = 20
PAGE = 60                      # installed apps per page
MAX_IDS = 4096                 # app ids one session may hold
ICON_SIZE = 48
MAX_ICON_BYTES = 9 * 1024      # PNG bytes; base64 keeps the reply under MAX_REPLY
ICON_CACHE = 256
LAUNCH_WAIT_S = 10.0
LAUNCH_POLL_S = 0.3
MAX_PENDING = 24
_APP_ID = re.compile(r"app:[0-9a-f]{16}")
_WINDOW_ID = re.compile(r"window:[1-9][0-9]{0,19}")
# (rate a second, burst) per kind of request
LIMITS = {"list": (4.0, 12.0), "icon": (30.0, 96.0), "launch": (0.5, 3.0), "window": (4.0, 10.0),
          "search": (10.0, 20.0)}
CATEGORY = {"apps.running": "list", "apps.pinned": "list", "apps.installed": "list", "apps.icon": "icon",
            "apps.launch": "launch", "search.query": "search", **{k: "window" for k in WINDOW_ACTIONS}}


class Refused(Exception):
    def __init__(self, reason: str) -> None:
        super().__init__(reason)
        self.reason = reason


def default_backend() -> ShellBackend:
    """The shell backend for this OS (shared by every session of a run, so its caches survive reconnects)."""
    global _default
    if _default is None:
        if sys.platform == "win32":
            from hostos.shell_windows import WindowsShell
            _default = WindowsShell()
        elif sys.platform == "darwin":
            try:
                from hostos.shell_macos import MacShell
                _default = MacShell()
            except ImportError:
                _default = UnsupportedShell("macos")
        else:
            _default = UnsupportedShell("linux")
    return _default


_default: ShellBackend | None = None


# --- Search ---------------------------------------------------------------------------------------------------------

def normalize(text: str) -> str:
    return " ".join(unicodedata.normalize("NFKC", str(text)).casefold().split())


_BOUNDARY = " -_.()[]/\\:"


def _subsequence(q: str, t: str) -> float | None:
    """Every character of q in order in t: points per match, more at word starts and for runs."""
    score, ti, previous, first = 0.0, 0, -2, None
    for ch in q:
        found = t.find(ch, ti)
        if found < 0:
            return None
        first = found if first is None else first
        if found - first + 1 > len(q) * 4 + 4:
            return None  # letters scattered over a long name aren't a match
        score += 10
        if found == 0 or t[found - 1] in _BOUNDARY:
            score += 15
        if found == previous + 1:
            score += 8
        score -= min(5, found - ti)  # gaps cost a little
        previous, ti = found, found + 1
    return score


def fuzzy_score(query: str, text: str) -> float | None:
    """How well `text` matches `query` (higher is better), or None. Exact > prefix > word start > substring >
    initials > ordered letters; every word of a multi-word query has to match."""
    q, t = normalize(query), normalize(text)
    if not q or not t:
        return None
    if t == q:
        return 1000.0
    if t.startswith(q):
        return 900.0 - min(len(t), 100) * 0.5
    found = t.find(q)
    if found > 0 and t[found - 1] in _BOUNDARY:
        return 800.0 - min(found, 100)
    if found > 0:
        return 650.0 - min(found, 100)
    initials = "".join(w[0] for w in re.split(r"[ \-_.()\[\]/\\:]+", t) if w)
    if " " not in q and len(q) >= 2 and initials.startswith(q):
        return 700.0 - len(initials)
    words = q.split(" ")
    if len(words) > 1:
        parts = [fuzzy_score(w, t) for w in words]
        return None if any(p is None for p in parts) else min(600.0, sum(parts) / len(parts) * 0.9)
    sub = _subsequence(q, t)
    if sub is None or sub < len(q) * 6:
        return None
    return min(500.0, sub * 500.0 / (len(q) * 33))


# --- Service ----------------------------------------------------------------------------------------------------------

class ShellService:
    """One per session. `gate()` returns None when shell requests are allowed, else the refusal reason."""

    def __init__(self, backend: ShellBackend, send: Callable[[dict], None], gate: Callable[[], str | None], *,
                 clock: Callable[[], float] = time.monotonic, sleep=asyncio.sleep, run_blocking=None,
                 launch_wait_s: float = LAUNCH_WAIT_S, dry_run: bool = False):
        self.backend = backend
        self.dry_run = dry_run
        self.send = send
        self.gate = gate
        self.clock = clock
        self.sleep = sleep
        self.launch_wait_s = launch_wait_s
        self._executor = None
        self._run_blocking = run_blocking or self._in_thread
        self._buckets = {name: keys.RateLimiter(rate=rate, burst=burst, clock=clock) for name, (rate, burst) in LIMITS.items()}
        self._app_ids: dict[str, str] = {}             # backend key -> "app:..."
        self._apps: dict[str, AppEntry] = {}           # "app:..." -> entry
        self._windows: dict[int, WindowEntry] = {}     # handles listed in this session -> last seen entry
        self._first_seen: dict[str, int] = {}          # program -> order it was first seen running (stable order)
        self._icons: OrderedDict = OrderedDict()       # icon cache key -> (size, png) or None
        self._pending = 0
        self._launching = False
        self.refused: dict[str, int] = {}              # reason -> count, for logs and tests

    @property
    def info(self) -> dict:
        """What the welcome says about the shell."""
        return {"os": self.backend.os_name, "available": bool(self.backend.supported)}

    def close(self) -> None:
        if self._executor is not None:
            self._executor.shutdown(wait=False, cancel_futures=True)
            self._executor = None

    async def _in_thread(self, func, *args):
        if self._executor is None:
            self._executor = ThreadPoolExecutor(max_workers=2, thread_name_prefix="shell")
        return await asyncio.get_running_loop().run_in_executor(self._executor, func, *args)

    # --- Entry ------------------------------------------------------------------------------------------------------

    def accepts(self, kind: str) -> bool:
        return kind in REQUESTS

    async def handle(self, kind: str, message: dict) -> None:
        rid = message.get("rid")
        if not (isinstance(rid, int) and not isinstance(rid, bool) and 0 <= rid < 2 ** 31):
            rid = None
        try:
            if self._pending >= MAX_PENDING:
                raise Refused("busy")
            self._pending += 1
            try:
                self._check_gate()
                if not self.backend.supported:
                    raise Refused("unsupported")
                if not self._buckets[CATEGORY[kind]].allow(1):
                    raise Refused("rate limited")
                await self._dispatch(kind, message, rid)
            finally:
                self._pending -= 1
        except Refused as refusal:
            self._refuse(kind, rid, refusal.reason)
        except asyncio.CancelledError:
            raise
        except Exception:
            log.exception("Shell request %s failed", kind)
            self._refuse(kind, rid, "failed")

    def _check_gate(self) -> None:
        reason = self.gate()
        if reason is not None:
            raise Refused(reason)

    def _refuse(self, kind: str, rid, reason: str) -> None:
        self.refused[reason] = self.refused.get(reason, 0) + 1
        if reason not in ("rate limited", "no icon"):
            log.info("Shell request %s refused: %s", kind, reason)
        self._reply({"type": "shell.error", "request": kind, "reason": reason}, rid)

    def _reply(self, message: dict, rid) -> None:
        if rid is not None:
            message = {"type": message["type"], "rid": rid, **{k: v for k, v in message.items() if k != "type"}}
        self.send(message)

    async def _dispatch(self, kind: str, message: dict, rid) -> None:
        if kind == "apps.running":
            self._reply(self._fit({"type": "apps.running", "apps": await self._running()}, "apps"), rid)
        elif kind == "apps.pinned":
            self._reply(self._fit({"type": "apps.pinned", "apps": await self._pinned()}, "apps"), rid)
        elif kind == "apps.installed":
            await self._installed(message, rid)
        elif kind == "apps.icon":
            await self._icon(message, rid)
        elif kind == "apps.launch":
            await self._launch(message, rid)
        elif kind in WINDOW_ACTIONS:
            await self._window_action(kind, message, rid)
        elif kind == "search.query":
            await self._search(message, rid)

    # --- Ids --------------------------------------------------------------------------------------------------------

    def _app_id(self, entry: AppEntry) -> str:
        token = self._app_ids.get(entry.key)
        if token is None:
            if len(self._app_ids) >= MAX_IDS:
                raise Refused("busy")
            token = f"app:{secrets.token_hex(8)}"
            self._app_ids[entry.key] = token
        self._apps[token] = entry
        return token

    def _window_id(self, window: WindowEntry) -> str:
        self._windows[window.handle] = window
        return f"window:{window.handle}"

    @staticmethod
    def _id_of(message: dict) -> str:
        value = message.get("id")
        if not isinstance(value, str) or len(value) > 40 or not (_APP_ID.fullmatch(value) or _WINDOW_ID.fullmatch(value)):
            raise Refused("bad request")
        return value

    def _known_app(self, value: str) -> AppEntry:
        entry = self._apps.get(value) if _APP_ID.fullmatch(value) else None
        if entry is None:
            raise Refused("unknown id")
        return entry

    def _known_window(self, value: str) -> int:
        handle = int(value[7:]) if _WINDOW_ID.fullmatch(value) else 0
        if handle not in self._windows:
            raise Refused("unknown id")
        return handle

    # --- Listing ----------------------------------------------------------------------------------------------------

    @staticmethod
    def _name(text) -> str:
        text = "".join(c for c in str(text or "") if unicodedata.category(c)[0] != "C")
        text = " ".join(text.split())
        return text[:MAX_NAME] or "Untitled"

    def _fit(self, message: dict, list_key: str) -> dict:
        """Drops items off the end until the reply fits in one control message."""
        items = message[list_key]
        while items and len(json.dumps(message, separators=(",", ":")).encode("utf-8")) > MAX_REPLY:
            del items[len(items) * 9 // 10:]
            message["truncated"] = True
        return message

    async def _catalog(self) -> tuple[list[AppEntry], list[AppEntry], list[WindowEntry]]:
        installed = await self._run_blocking(self.backend.installed)  # first: Store-app pins are found through it
        pinned = await self._run_blocking(self.backend.pinned)
        windows = await self._run_blocking(self.backend.windows)
        return list(pinned), list(installed), list(windows)

    def _by_program(self, pinned, installed) -> dict[str, AppEntry]:
        owners: dict[str, AppEntry] = {}
        for entry in list(installed) + list(pinned):  # pinned wins
            if entry.program:
                owners[entry.program] = entry
        return owners

    def _group(self, pinned, installed, windows) -> list[tuple[AppEntry, bool, list[WindowEntry]]]:
        """Running windows grouped by app, in taskbar/Dock order: pinned apps in pin order, then the others in the
        order their programs started (new ones join at the end, like the taskbar)."""
        owners = self._by_program(pinned, installed)
        pinned_keys = {p.key for p in pinned}
        groups: "OrderedDict[str, tuple[AppEntry, list[WindowEntry]]]" = OrderedDict()
        for window in windows:
            program = window.program or f"window:{window.handle}"
            owner = owners.get(window.program) if window.program else None
            if owner is None:
                owner = AppEntry(f"running:{program}", window.app_name or window.title, window.program, None,
                                 window.icon)
            if program not in self._first_seen:
                self._first_seen[program] = len(self._first_seen)
            groups.setdefault(owner.key, (owner, []))[1].append(window)
        pin_rank = {p.key: i for i, p in enumerate(pinned)}

        def order(item):
            entry, wins = item
            if entry.key in pin_rank:
                return (0, pin_rank[entry.key], 0.0, 0)
            started = min((w.started for w in wins if w.started), default=0.0)
            return (1, 0, started or float("inf"), self._first_seen.get(wins[0].program or f"window:{wins[0].handle}", 0))

        return [(entry, entry.key in pinned_keys, wins) for entry, wins in sorted(groups.values(), key=order)]

    async def _running(self) -> list[dict]:
        pinned, installed, windows = await self._catalog()
        out = []
        for entry, is_pinned, wins in self._group(pinned, installed, windows):
            out.append({"id": self._app_id(entry), "name": self._name(entry.name), "pinned": is_pinned,
                        "launchable": entry.launch is not None,
                        "windows": [{"id": self._window_id(w), "title": self._name(w.title), "minimized": w.minimized,
                                     "focused": w.focused} for w in wins]})
        return out

    async def _pinned(self) -> list[dict]:
        pinned, installed, windows = await self._catalog()
        running = {w.program for w in windows if w.program}
        return [{"id": self._app_id(p), "name": self._name(p.name), "running": bool(p.program and p.program in running)}
                for p in pinned]

    async def _installed(self, message: dict, rid) -> None:
        offset = message.get("offset", 0)
        if not isinstance(offset, int) or isinstance(offset, bool) or not 0 <= offset <= 100000:
            raise Refused("bad request")
        _, installed, windows = await self._catalog()
        running = {w.program for w in windows if w.program}
        apps = sorted(installed, key=lambda a: normalize(a.name))
        page = apps[offset:offset + PAGE]
        reply = self._fit({"type": "apps.installed", "offset": offset, "total": len(apps), "apps": [
            {"id": self._app_id(a), "name": self._name(a.name), "running": bool(a.program and a.program in running)}
            for a in page]}, "apps")
        end = offset + len(reply["apps"])
        reply["next"] = end if end < len(apps) else None
        reply.pop("truncated", None)
        self._reply(reply, rid)

    # --- Icons ------------------------------------------------------------------------------------------------------

    async def _icon(self, message: dict, rid) -> None:
        value = self._id_of(message)
        if value.startswith("app:"):
            entry = self._known_app(value)
            cache_key, fetch, subject = ("app", entry.key), self.backend.app_icon, entry
        else:
            window = self._windows[self._known_window(value)]
            cache_key = ("program", window.program) if window.program else ("window", window.handle)
            fetch, subject = self.backend.window_icon, window
        if cache_key in self._icons:
            self._icons.move_to_end(cache_key)
            found = self._icons[cache_key]
        else:
            found = None
            for size in (ICON_SIZE, 32):
                png = await self._run_blocking(fetch, subject, size)
                if png and len(png) <= MAX_ICON_BYTES:
                    found = (size, png)
                    break
                if not png:
                    break
            self._icons[cache_key] = found
            while len(self._icons) > ICON_CACHE:
                self._icons.popitem(last=False)
        if found is None:
            raise Refused("no icon")
        size, png = found
        self._reply({"type": "apps.icon", "id": value, "size": size, "png": base64.b64encode(png).decode("ascii")}, rid)

    # --- Actions ----------------------------------------------------------------------------------------------------

    async def _launch(self, message: dict, rid) -> None:
        value = self._id_of(message)
        entry = self._known_app(value)
        if entry.launch is None:
            raise Refused("not launchable")
        if self.dry_run:
            raise Refused("dry run")
        if self._launching:
            raise Refused("busy")
        self._launching = True
        try:
            before = {w.handle for w in await self._run_blocking(self.backend.windows)}
            self._check_gate()  # a revoke while listing wins
            log.info("Launching %s for the headset", entry.name)
            try:
                await self._run_blocking(self.backend.launch, entry)
            except OSError as e:
                log.warning("Couldn't launch %s: %s", entry.name, e)
                raise Refused("failed")
            window = await self._wait_for_window(entry, before)
        finally:
            self._launching = False
        self._reply({"type": "apps.launched", "id": value,
                     "window": {"id": self._window_id(window), "title": self._name(window.title)} if window else None}, rid)

    async def _wait_for_window(self, entry: AppEntry, before: set) -> WindowEntry | None:
        """The window the launch opened: a new one of the same program (or, for apps we can't match, a new window
        whose title names the app). A single-instance app that only brought its window forward counts too."""
        deadline = self.clock() + self.launch_wait_s
        name = normalize(entry.name)
        while True:
            windows = await self._run_blocking(self.backend.windows)
            fresh = [w for w in windows if w.handle not in before]
            if entry.program:
                match = next((w for w in fresh if w.program == entry.program), None)
                match = match or next((w for w in windows if w.program == entry.program and w.focused), None)
            else:
                match = next((w for w in fresh if name and name in normalize(w.title)), None)
            if match is not None:
                return match
            if self.clock() >= deadline:
                return None
            await self.sleep(LAUNCH_POLL_S)

    async def _window_action(self, kind: str, message: dict, rid) -> None:
        value = self._id_of(message)
        handle = self._known_window(value)
        if self.dry_run:
            raise Refused("dry run")
        windows = await self._run_blocking(self.backend.windows)
        current = next((w for w in windows if w.handle == handle), None)
        if current is None:
            self._windows.pop(handle, None)
            raise Refused("unknown id")  # gone, or no longer a window the shell may touch
        self._windows[handle] = current
        self._check_gate()
        action = WINDOW_ACTIONS[kind]
        log.info("Window %s: %s for the headset", action, current.title[:60])
        ok = bool(await self._run_blocking(self.backend.window_action, handle, action))
        self._reply({"type": "window.done", "action": action, "id": value, "ok": ok}, rid)

    # --- Search -----------------------------------------------------------------------------------------------------

    async def _search(self, message: dict, rid) -> None:
        query = message.get("q")
        if not isinstance(query, str) or len(query) > MAX_QUERY:
            raise Refused("bad request")
        query = "".join(c for c in query if unicodedata.category(c)[0] != "C").strip()
        results = []
        if query:
            pinned, installed, windows = await self._catalog()
            running = {w.program for w in windows if w.program}
            seen, scored = set(), []
            for entry in list(pinned) + list(installed):
                if entry.key in seen:
                    continue
                seen.add(entry.key)
                score = fuzzy_score(query, entry.name)
                if score is not None:
                    scored.append((score + (5 if entry.program in running else 0), 0, entry))
            for window in windows:
                score = fuzzy_score(query, window.title)
                if score is not None:
                    scored.append((score - 1, 1, window))
            scored.sort(key=lambda s: (-s[0], s[1], normalize(getattr(s[2], "name", "") or getattr(s[2], "title", ""))))
            for _, kind, item in scored[:MAX_RESULTS]:
                if kind == 0:
                    results.append({"kind": "app", "id": self._app_id(item), "name": self._name(item.name),
                                    "running": bool(item.program and item.program in running)})
                else:
                    results.append({"kind": "window", "id": self._window_id(item), "title": self._name(item.title),
                                    "app": self._name(item.app_name)})
        self._reply(self._fit({"type": "search.results", "q": query[:MAX_QUERY], "results": results}, "results"), rid)
