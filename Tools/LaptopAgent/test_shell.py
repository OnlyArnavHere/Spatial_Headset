"""Unit tests for the laptop shell protocol (shell.py) with a fake backend: every request, the control gate (view only,
cast only, input off, revoked), id validation, rate limits, reply sizes, and the session wiring. Nothing here lists,
launches or touches anything on this machine.

  python -m unittest test_shell -v        (from Tools/LaptopAgent)
"""
import asyncio
import base64
import json
import unittest

import dispatch
import session
import shell
from hostos import _png
from hostos._shell_types import AppEntry, ShellBackend, UnsupportedShell, WindowEntry
from streams import StreamManager

PNG = _png.encode_rgba(2, 2, b"\xff\x00\x00\xff" * 4)
CHROME = AppEntry("lnk:chrome", "Google Chrome", "c:\\chrome.exe", ("lnk", "chrome.lnk"), ("path", "chrome.lnk"))
EDGE = AppEntry("lnk:edge", "Microsoft Edge", "c:\\edge.exe", ("lnk", "edge.lnk"), ("path", "edge.lnk"))
NOTEPAD = AppEntry("lnk:notepad", "Notepad", "c:\\notepad.exe", ("lnk", "notepad.lnk"), ("path", "notepad.lnk"))
STORE = AppEntry("uwp:Store_8wekyb3d8bbwe!App", "Microsoft Store", None, ("aumid", "Store_8wekyb3d8bbwe!App"),
                 ("aumid", "Store_8wekyb3d8bbwe!App"))
VSCODE = AppEntry("lnk:code", "Visual Studio Code", "c:\\code.exe", ("lnk", "code.lnk"), ("path", "code.lnk"))


def window(handle, title, program, app_name="", started=0.0, minimized=False, focused=False):
    return WindowEntry(handle, title, program, app_name or title, minimized, focused, started, 100 + handle,
                       ("path", program) if program else None)


class FakeBackend(ShellBackend):
    os_name = "windows"
    supported = True

    def __init__(self):
        self.calls = []
        self.installed_apps = [CHROME, EDGE, NOTEPAD, STORE, VSCODE]
        self.pinned_apps = [EDGE, CHROME]
        self.open_windows = [window(11, "Notes.txt - Notepad", "c:\\notepad.exe", "Notepad", started=30),
                             window(12, "GitHub - Google Chrome", "c:\\chrome.exe", "Google Chrome", started=20),
                             window(13, "Some tool", "c:\\tool.exe", "Tool", started=10),
                             window(14, "Inbox - Google Chrome", "c:\\chrome.exe", "Google Chrome", started=20,
                                    focused=True)]
        self.icon = PNG
        self.on_launch = None
        self.action_result = True

    def installed(self):
        self.calls.append(("installed",))
        return list(self.installed_apps)

    def pinned(self):
        self.calls.append(("pinned",))
        return list(self.pinned_apps)

    def windows(self):
        self.calls.append(("windows",))
        return list(self.open_windows)

    def app_icon(self, app, size):
        self.calls.append(("app_icon", app.key, size))
        return self.icon(size) if callable(self.icon) else self.icon

    def window_icon(self, w, size):
        self.calls.append(("window_icon", w.handle, size))
        return self.icon(size) if callable(self.icon) else self.icon

    def launch(self, app):
        self.calls.append(("launch", app.key))
        if self.on_launch:
            self.on_launch(app)

    def window_action(self, handle, action):
        self.calls.append(("window_action", handle, action))
        return self.action_result

    def effects(self):
        return [c for c in self.calls if c[0] in ("launch", "window_action")]


async def direct(func, *args):
    return func(*args)


class Base(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.backend = FakeBackend()
        self.sent = []
        self.gate_reason = None
        self.now = [0.0]
        self.service = self.make()

    def make(self, backend=None, **kw):
        async def no_sleep(_):
            self.now[0] += shell.LAUNCH_POLL_S
        return shell.ShellService(backend or self.backend, self.sent.append, lambda: self.gate_reason,
                                  clock=lambda: self.now[0], sleep=no_sleep, run_blocking=direct, **kw)

    async def ask(self, kind, **fields):
        self.sent.clear()
        await self.service.handle(kind, {"type": kind, **fields})
        self.assertEqual(len(self.sent), 1, self.sent)
        return self.sent[0]

    def assertRefused(self, reply, reason):
        self.assertEqual(reply["type"], "shell.error", reply)
        self.assertEqual(reply["reason"], reason, reply)

    async def ids(self):
        running = await self.ask("apps.running")
        return {a["name"]: a["id"] for a in running["apps"]}, running


class Requests(Base):
    async def test_running_groups_windows_by_app_in_taskbar_order(self):
        _, reply = await self.ids()
        self.assertEqual(reply["type"], "apps.running")
        names = [a["name"] for a in reply["apps"]]
        # pinned first in pin order (Edge isn't running, so only Chrome), then the others by process start
        self.assertEqual(names, ["Google Chrome", "Tool", "Notepad"])
        chrome = reply["apps"][0]
        self.assertTrue(chrome["pinned"])
        self.assertTrue(chrome["launchable"])
        self.assertEqual([w["id"] for w in chrome["windows"]], ["window:12", "window:14"])
        self.assertTrue(chrome["windows"][1]["focused"])
        tool = reply["apps"][1]
        self.assertFalse(tool["launchable"], "seen running only: nothing to launch it with")
        self.assertTrue(all(a["id"].startswith("app:") and len(a["id"]) == 20 for a in reply["apps"]))

    async def test_running_order_is_stable_for_programs_without_a_start_time(self):
        self.backend.open_windows = [window(21, "B", "c:\\b.exe"), window(22, "A", "c:\\a.exe")]
        first = [a["name"] for a in (await self.ask("apps.running"))["apps"]]
        self.backend.open_windows.reverse()
        second = [a["name"] for a in (await self.ask("apps.running"))["apps"]]
        self.assertEqual(first, ["B", "A"])
        self.assertEqual(second, first, "first-seen order holds when the z-order changes")

    async def test_ids_are_stable_within_a_session_and_not_across(self):
        first, _ = await self.ids()
        again, _ = await self.ids()
        self.assertEqual(first, again)
        other = self.make()
        self.service = other
        fresh, _ = await self.ids()
        self.assertNotEqual(first["Google Chrome"], fresh["Google Chrome"])

    async def test_ids_never_carry_paths(self):
        _, reply = await self.ids()
        text = json.dumps(reply) + json.dumps(await self.ask("apps.installed")) + json.dumps(await self.ask("apps.pinned"))
        for secret in ("chrome.exe", "chrome.lnk", "lnk:", "uwp:", "8wekyb3d8bbwe"):
            self.assertNotIn(secret, text)

    async def test_pinned_with_running_dots(self):
        reply = await self.ask("apps.pinned")
        self.assertEqual([(a["name"], a["running"]) for a in reply["apps"]],
                         [("Microsoft Edge", False), ("Google Chrome", True)])

    async def test_installed_pages(self):
        self.backend.installed_apps = [AppEntry(f"lnk:{i}", f"App {i:03}") for i in range(150)]
        first = await self.ask("apps.installed")
        self.assertEqual((first["offset"], first["total"], len(first["apps"]), first["next"]), (0, 150, shell.PAGE, shell.PAGE))
        self.assertEqual(first["apps"][0]["name"], "App 000")
        last = await self.ask("apps.installed", offset=120)
        self.assertEqual((len(last["apps"]), last["next"]), (30, None))
        self.assertEqual(last["apps"][-1]["name"], "App 149")
        names = sorted(a["name"] for a in (await self.ask("apps.installed"))["apps"])
        self.assertEqual(names, [f"App {i:03}" for i in range(60)])

    async def test_installed_sorted_by_name_with_running_flag(self):
        reply = await self.ask("apps.installed")
        self.assertEqual([a["name"] for a in reply["apps"]],
                         ["Google Chrome", "Microsoft Edge", "Microsoft Store", "Notepad", "Visual Studio Code"])
        self.assertEqual({a["name"]: a["running"] for a in reply["apps"]}["Notepad"], True)

    async def test_icon_for_app_and_window_is_cached(self):
        ids, _ = await self.ids()
        reply = await self.ask("apps.icon", id=ids["Google Chrome"], rid=7)
        self.assertEqual((reply["type"], reply["rid"], reply["size"]), ("apps.icon", 7, shell.ICON_SIZE))
        self.assertEqual(base64.b64decode(reply["png"]), PNG)
        await self.ask("apps.icon", id=ids["Google Chrome"])
        self.assertEqual(len([c for c in self.backend.calls if c[0] == "app_icon"]), 1)
        reply = await self.ask("apps.icon", id="window:12")
        self.assertEqual(reply["type"], "apps.icon")
        await self.ask("apps.icon", id="window:14")  # same program: cached
        self.assertEqual(len([c for c in self.backend.calls if c[0] == "window_icon"]), 1)

    async def test_oversized_icon_falls_back_to_32_then_none(self):
        ids, _ = await self.ids()
        self.backend.icon = lambda size: PNG if size == 32 else b"x" * (shell.MAX_ICON_BYTES + 1)
        reply = await self.ask("apps.icon", id=ids["Notepad"])
        self.assertEqual(reply["size"], 32)
        self.backend.icon = lambda size: b"x" * (shell.MAX_ICON_BYTES + 1)
        self.assertRefused(await self.ask("apps.icon", id=ids["Tool"]), "no icon")
        self.backend.icon = None
        self.assertRefused(await self.ask("apps.icon", id=ids["Google Chrome"]), "no icon")

    async def test_icon_reply_fits_one_control_message(self):
        ids, _ = await self.ids()
        self.backend.icon = b"\xff" * shell.MAX_ICON_BYTES
        await self.ask("apps.icon", id=ids["Notepad"])
        self.assertLess(len(json.dumps(self.sent[0]).encode()), session.MAX_CONTROL_MESSAGE)

    async def test_launch_returns_the_new_window(self):
        ids = {a["name"]: a["id"] for a in (await self.ask("apps.installed"))["apps"]}

        def opens(app):
            self.backend.open_windows.append(window(99, "Untitled - Visual Studio Code", "c:\\code.exe"))
        self.backend.on_launch = opens
        reply = await self.ask("apps.launch", id=ids["Visual Studio Code"], rid=3)
        self.assertEqual(reply["type"], "apps.launched")
        self.assertEqual(reply["window"]["id"], "window:99")
        self.assertIn(("launch", "lnk:code"), self.backend.calls)
        # the window it returned may be raised straight away
        self.assertEqual((await self.ask("window.raise", id="window:99"))["ok"], True)

    async def test_launch_of_a_single_instance_app_returns_its_focused_window(self):
        ids, _ = await self.ids()
        reply = await self.ask("apps.launch", id=ids["Google Chrome"])
        self.assertEqual(reply["window"]["id"], "window:14")

    async def test_launch_of_a_store_app_matches_by_title(self):
        ids = {a["name"]: a["id"] for a in (await self.ask("apps.installed"))["apps"]}
        self.backend.on_launch = lambda app: self.backend.open_windows.append(window(77, "Microsoft Store", None))
        self.assertEqual((await self.ask("apps.launch", id=ids["Microsoft Store"]))["window"]["id"], "window:77")

    async def test_launch_without_a_window_says_null(self):
        ids = {a["name"]: a["id"] for a in (await self.ask("apps.installed"))["apps"]}
        reply = await self.ask("apps.launch", id=ids["Microsoft Edge"])
        self.assertIsNone(reply["window"])

    async def test_launch_failure_and_not_launchable(self):
        ids, _ = await self.ids()
        self.assertRefused(await self.ask("apps.launch", id=ids["Tool"]), "not launchable")

        def fails(app):
            raise OSError("nope")
        self.backend.on_launch = fails
        self.assertRefused(await self.ask("apps.launch", id=ids["Notepad"]), "failed")

    async def test_window_actions(self):
        await self.ids()
        for kind, action in shell.WINDOW_ACTIONS.items():
            reply = await self.ask(kind, id="window:11", rid=1)
            self.assertEqual((reply["type"], reply["action"], reply["ok"]), ("window.done", action, True))
        self.assertEqual([c for c in self.backend.effects()],
                         [("window_action", 11, a) for a in shell.WINDOW_ACTIONS.values()])
        self.backend.action_result = False
        self.assertFalse((await self.ask("window.close", id="window:11"))["ok"])

    async def test_search_ranks_apps_and_windows(self):
        reply = await self.ask("search.query", q="chrome")
        self.assertEqual(reply["type"], "search.results")
        kinds = [(r["kind"], r.get("name") or r.get("title")) for r in reply["results"]]
        self.assertEqual(kinds[0], ("app", "Google Chrome"))
        self.assertIn(("window", "GitHub - Google Chrome"), kinds)
        self.assertTrue(reply["results"][0]["running"])
        initials = await self.ask("search.query", q="vsc")
        self.assertEqual(initials["results"][0]["name"], "Visual Studio Code")
        fuzzy = await self.ask("search.query", q="ntpd")
        self.assertEqual(fuzzy["results"][0]["name"], "Notepad")
        nothing = await self.ask("search.query", q="zzzzqqq")
        self.assertEqual(nothing["results"], [])
        empty = await self.ask("search.query", q="   ")
        self.assertEqual(empty["results"], [])

    async def test_search_results_can_be_used_as_ids(self):
        result = (await self.ask("search.query", q="notepad"))["results"]
        app = next(r for r in result if r["kind"] == "app")
        win = next(r for r in result if r["kind"] == "window")
        self.assertEqual((await self.ask("apps.icon", id=app["id"]))["type"], "apps.icon")
        self.assertEqual((await self.ask("window.minimize", id=win["id"]))["type"], "window.done")

    async def test_replies_fit_in_a_control_message(self):
        self.backend.installed_apps = [AppEntry(f"lnk:{i}", "W" * 200 + str(i)) for i in range(400)]
        self.backend.open_windows = [window(1000 + i, "T" * 300, f"c:\\p{i}.exe") for i in range(300)]
        for kind, extra in (("apps.running", {}), ("apps.installed", {}), ("search.query", {"q": "w"})):
            reply = await self.ask(kind, **extra)
            self.assertLess(len(json.dumps(reply, separators=(",", ":")).encode()), session.MAX_CONTROL_MESSAGE, kind)
        self.assertTrue((await self.ask("apps.running")).get("truncated"))
        reply = await self.ask("apps.installed")
        self.assertEqual(reply["next"], reply["offset"] + len(reply["apps"]))

    async def test_names_are_plain_and_short(self):
        self.backend.open_windows = [window(5, "a\x00b\u202e\nc" + "x" * 200, "c:\\x.exe", "\x1bEvil\u200b")]
        reply = await self.ask("apps.running")
        self.assertEqual(reply["apps"][0]["name"], "Evil")
        title = reply["apps"][0]["windows"][0]["title"]
        self.assertTrue(title.startswith("abc") and len(title) == shell.MAX_NAME)

    async def test_dry_run_lists_but_changes_nothing(self):
        self.service = self.make(dry_run=True)
        ids, _ = await self.ids()
        self.assertRefused(await self.ask("apps.launch", id=ids["Notepad"]), "dry run")
        for kind in shell.WINDOW_ACTIONS:
            self.assertRefused(await self.ask(kind, id="window:11"), "dry run")
        self.assertEqual((await self.ask("apps.icon", id=ids["Notepad"]))["type"], "apps.icon")
        self.assertEqual(self.backend.effects(), [])

    async def test_rid_is_echoed_and_bad_rids_are_ignored(self):
        self.assertEqual((await self.ask("apps.pinned", rid=42))["rid"], 42)
        for bad in (True, -1, 2 ** 31, "1", 1.5):
            self.assertNotIn("rid", await self.ask("apps.pinned", rid=bad))


class Gate(Base):
    async def assert_refused_everything(self, reason):
        ids, _ = await self.ids_while_open()
        self.gate_reason = reason
        self.backend.calls.clear()
        for kind in shell.REQUESTS:
            reply = await self.ask(kind, id=ids["Notepad"] if kind.startswith("apps.") else "window:11", q="a")
            self.assertRefused(reply, reason)
            self.assertEqual(reply["request"], kind)
        self.assertEqual(self.backend.calls, [], "a refused request never reaches the backend")

    async def ids_while_open(self):
        saved, self.gate_reason = self.gate_reason, None
        try:
            return await self.ids()
        finally:
            self.gate_reason = saved

    async def test_view_only_refuses_everything(self):
        await self.assert_refused_everything("view only")

    async def test_cast_only_refuses_everything(self):
        await self.assert_refused_everything("cast only")

    async def test_input_off_refuses_everything(self):
        await self.assert_refused_everything("input off")

    async def test_revoked_refuses_everything(self):
        await self.assert_refused_everything("stopped")

    async def test_revoke_during_launch_wins(self):
        ids, _ = await self.ids()
        calls = []

        def windows():
            calls.append(1)
            self.gate_reason = "stopped"  # the kill switch fires while the agent lists windows before launching
            return []
        self.backend.windows = windows
        self.assertRefused(await self.ask("apps.launch", id=ids["Notepad"]), "stopped")
        self.assertEqual(self.backend.effects(), [])

    async def test_revoke_during_window_action_wins(self):
        await self.ids()
        original = self.backend.windows

        def windows():
            self.gate_reason = "view only"  # the headset released control in the meantime
            return original()
        self.backend.windows = windows
        self.assertRefused(await self.ask("window.close", id="window:11"), "view only")
        self.assertEqual(self.backend.effects(), [])

    async def test_unsupported_backend(self):
        self.service = self.make(UnsupportedShell("linux"))
        self.assertEqual(self.service.info, {"os": "linux", "available": False})
        for kind in shell.REQUESTS:
            self.assertRefused(await self.ask(kind, id="window:1", q="a"), "unsupported")


class Validation(Base):
    async def test_ids_must_be_ones_the_agent_listed(self):
        await self.ids()
        cases = {
            "app:0123456789abcdef": "unknown id",          # well formed, never listed
            "window:999": "unknown id",                    # never listed
            "C:\\Windows\\System32\\cmd.exe": "bad request",
            "cmd.exe /c del *": "bad request",
            "lnk:c:\\chrome.lnk": "bad request",           # the backend key itself is not an id
            "uwp:Store_8wekyb3d8bbwe!App": "bad request",
            "app:0123456789ABCDEF": "bad request",
            "app:0123456789abcdef0": "bad request",
            "window:0": "bad request",
            "window:-1": "bad request",
            "window:12 ": "bad request",
            "window:1" + "0" * 40: "bad request",
            "": "bad request",
        }
        for value, reason in cases.items():
            for kind in ("apps.launch", "apps.icon", "window.close"):
                self.now[0] += 100  # invalid requests use up rate budget too; keep it out of this test
                self.assertRefused(await self.ask(kind, id=value), reason)
        for value in (None, 5, ["app:0123456789abcdef"], {"id": "window:11"}, True):
            self.now[0] += 100
            self.assertRefused(await self.ask("apps.launch", id=value), "bad request")
        self.assertEqual(self.backend.effects(), [])

    async def test_invalid_requests_use_up_the_rate_budget(self):
        await self.ids()
        burst = int(shell.LIMITS["launch"][1])
        for _ in range(burst):
            self.assertRefused(await self.ask("apps.launch", id="app:0123456789abcdef"), "unknown id")
        self.assertRefused(await self.ask("apps.launch", id="app:0123456789abcdef"), "rate limited")

    async def test_window_ids_are_not_app_ids_and_back(self):
        ids, _ = await self.ids()
        self.assertRefused(await self.ask("apps.launch", id="window:11"), "unknown id")
        self.assertRefused(await self.ask("window.close", id=ids["Notepad"]), "unknown id")

    async def test_a_window_that_went_away_is_unknown(self):
        await self.ids()
        self.backend.open_windows = [w for w in self.backend.open_windows if w.handle != 11]
        self.assertRefused(await self.ask("window.close", id="window:11"), "unknown id")
        self.assertRefused(await self.ask("window.raise", id="window:11"), "unknown id")
        self.assertEqual(self.backend.effects(), [])

    async def test_bad_search_and_offset(self):
        for q in (None, 3, ["a"], "x" * (shell.MAX_QUERY + 1)):
            self.assertRefused(await self.ask("search.query", q=q), "bad request")
        for offset in (-1, "0", 1.0, True, 10 ** 9):
            self.assertRefused(await self.ask("apps.installed", offset=offset), "bad request")
        reply = await self.ask("search.query", q="note\x00\x07pad")
        self.assertEqual(reply["q"], "notepad")


class RateLimits(Base):
    async def test_launches_are_rate_limited(self):
        ids, _ = await self.ids()
        burst = int(shell.LIMITS["launch"][1])
        replies = [await self.ask("apps.launch", id=ids["Google Chrome"]) for _ in range(burst + 2)]
        self.assertTrue(all(r["type"] == "apps.launched" for r in replies[:burst]))
        self.assertRefused(replies[burst], "rate limited")
        launches = len([c for c in self.backend.calls if c[0] == "launch"])
        self.assertEqual(launches, burst)
        self.now[0] += 2.0 / shell.LIMITS["launch"][0]
        self.assertEqual((await self.ask("apps.launch", id=ids["Google Chrome"]))["type"], "apps.launched")

    async def test_each_kind_has_its_own_budget(self):
        ids, _ = await self.ids()
        for _ in range(int(shell.LIMITS["window"][1])):
            await self.ask("window.raise", id="window:11")
        self.assertRefused(await self.ask("window.raise", id="window:11"), "rate limited")
        self.assertEqual((await self.ask("apps.icon", id=ids["Notepad"]))["type"], "apps.icon")
        self.assertEqual((await self.ask("search.query", q="n"))["type"], "search.results")

    async def test_listing_flood_is_limited(self):
        replies = [await self.ask("apps.running") for _ in range(int(shell.LIMITS["list"][1]) + 5)]
        self.assertTrue(any(r["type"] == "shell.error" and r["reason"] == "rate limited" for r in replies))

    async def test_one_launch_at_a_time(self):
        ids, _ = await self.ids()
        release = asyncio.Event()

        async def slow(func, *args):
            if func == self.backend.launch:
                await release.wait()
            return func(*args)
        self.service._run_blocking = slow
        first = asyncio.ensure_future(self.service.handle("apps.launch", {"type": "apps.launch", "id": ids["Notepad"]}))
        await asyncio.sleep(0)
        self.sent.clear()
        await self.service.handle("apps.launch", {"type": "apps.launch", "id": ids["Notepad"]})
        self.assertRefused(self.sent[0], "busy")
        release.set()
        await first

    async def test_too_many_pending_requests_are_busy(self):
        gate = asyncio.Event()

        async def stuck(func, *args):
            await gate.wait()
            return func(*args)
        self.service._run_blocking = stuck
        self.service._buckets["list"] = shell.keys.RateLimiter(rate=1000, burst=1000, clock=lambda: self.now[0])
        tasks = [asyncio.ensure_future(self.service.handle("apps.pinned", {"type": "apps.pinned"}))
                 for _ in range(shell.MAX_PENDING + 3)]
        await asyncio.sleep(0)
        busy = [m for m in self.sent if m.get("reason") == "busy"]
        self.assertEqual(len(busy), 3)
        gate.set()
        await asyncio.gather(*tasks)


class Fuzzy(unittest.TestCase):
    def test_order_of_match_kinds(self):
        s = shell.fuzzy_score
        self.assertGreater(s("notepad", "Notepad"), s("note", "Notepad"))
        self.assertGreater(s("note", "Notepad"), s("pad", "Note pad++"))
        self.assertGreater(s("pad", "Note pad"), s("pad", "Notepad"))
        self.assertGreater(s("vsc", "Visual Studio Code"), s("vsc", "vasculature"))
        self.assertIsNotNone(s("ntpd", "Notepad"))
        self.assertIsNone(s("xyz", "Notepad"))
        self.assertIsNone(s("", "Notepad"))
        self.assertIsNotNone(s("google chr", "Google Chrome"))
        self.assertIsNotNone(s("chrome google", "Google Chrome"))
        self.assertIsNone(s("chrome firefox", "Google Chrome"))

    def test_case_and_width_insensitive(self):
        self.assertEqual(shell.fuzzy_score("ＮＯＴＥＰＡＤ", "notepad"), 1000.0)

    def test_scattered_letters_dont_match(self):
        self.assertIsNone(shell.fuzzy_score("ae", "a" + "x" * 50 + "e"))


class SessionWiring(unittest.IsolatedAsyncioTestCase):
    def make(self, **kw):
        backend = FakeBackend()
        link = session.LinkSession(StreamManager(lambda t, p: (None, (0, 0))), 8554, max_windows=1,
                                   shell_backend=backend, **kw)
        sent = []
        link._send = sent.append
        link.shell.send = sent.append
        return link, backend, sent

    async def roundtrip(self, link, sent, message):
        sent.clear()
        link._on_control_message(json.dumps(message))
        await asyncio.gather(*link._shell_tasks)
        return sent

    async def test_gate_follows_the_control_state(self):
        link, backend, sent = self.make(input_enabled=True)
        self.assertRefusedOnly(await self.roundtrip(link, sent, {"type": "apps.pinned"}), "view only")
        link.dispatcher.state = dispatch.ControlState.GRANTED
        self.assertEqual((await self.roundtrip(link, sent, {"type": "apps.pinned"}))[0]["type"], "apps.pinned")
        link.dispatcher.release_control()
        self.assertRefusedOnly(await self.roundtrip(link, sent, {"type": "apps.pinned"}), "view only")
        link.dispatcher.state = dispatch.ControlState.GRANTED
        link.dispatcher.revoke("kill switch")
        sent.clear()
        self.assertRefusedOnly(await self.roundtrip(link, sent, {"type": "apps.pinned"}), "stopped")
        await link.close()

    async def test_cast_only_and_no_input_sessions_refuse(self):
        for kw, reason in (({"cast_only": True, "input_enabled": True}, "cast only"), ({}, "input off")):
            link, backend, sent = self.make(**kw)
            link.dispatcher.state = dispatch.ControlState.GRANTED  # even if something forced the state
            self.assertRefusedOnly(await self.roundtrip(link, sent, {"type": "apps.running"}), reason)
            self.assertEqual(backend.calls, [])
            await link.close()

    async def test_welcome_announces_the_shell(self):
        link, _, sent = self.make(input_enabled=True)
        link.slots = []
        link._on_control_open()
        self.assertEqual(sent[0]["shell"], {"os": "windows", "available": True})
        await link.close()

    async def test_shell_requests_dont_use_the_control_request_pool(self):
        link, _, sent = self.make(input_enabled=True)
        link.dispatcher.state = dispatch.ControlState.GRANTED
        for _ in range(session.MAX_SHELL_TASKS + 2):
            link._on_control_message(json.dumps({"type": "search.query", "q": "a", "rid": 5}))
        self.assertEqual(len(link._shell_tasks), session.MAX_SHELL_TASKS)
        self.assertEqual(len(link._requests), 0)
        busy = [m for m in sent if m.get("reason") == "busy"]
        self.assertEqual([(m["rid"], m["request"]) for m in busy], [(5, "search.query")] * 2)
        await asyncio.gather(*link._shell_tasks)
        await link.close()

    def assertRefusedOnly(self, sent, reason):
        self.assertEqual(len(sent), 1, sent)
        self.assertEqual((sent[0]["type"], sent[0]["reason"]), ("shell.error", reason))


if __name__ == "__main__":
    unittest.main()
