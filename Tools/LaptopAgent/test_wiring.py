"""The downloader, the macOS permission gate and start-at-login wired into the controller, the window and the app."""
import errno
import shutil
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

import agent
import app
import autostart
import controller
import hostos
import paths
import settings as settings_mod
import tools_fetch
from hostos import _macos_perms as perms
from test_controller import FakeStreams, Rig, wait_for


class FakeGate:
    def __init__(self):
        self.asked = []

    def ensure(self, name):
        if name in self.asked:
            return False
        self.asked.append(name)
        return True


def make_rows_probe(granted):
    return lambda name: name in granted


class DownloadWiringTests(unittest.TestCase):
    def rig(self, **over):
        r = Rig(**over)
        self.addCleanup(r.c.shutdown)
        return r

    def start(self, r):
        r.c.start()
        return wait_for(lambda: r.c.status()["state"] in ("running", "error"))

    def test_frozen_start_fetches_each_tool_once_with_progress_events(self):
        calls = []

        def fetch(names, progress=None, opener=None):
            calls.append(names)
            progress(names[0], 5, 10)
            return {}

        r = self.rig(fetch=fetch, frozen=lambda: True)
        self.assertTrue(self.start(r))
        self.assertEqual(r.c.status()["state"], "running")
        self.assertEqual(calls, [["ffmpeg"], ["mediamtx"]])
        kinds = [(e["type"], e.get("name")) for e in r.c.events() if e["type"].startswith("download")]
        self.assertEqual(kinds, [("download", "ffmpeg"), ("download_end", "ffmpeg"),
                                 ("download", "mediamtx"), ("download_end", "mediamtx")])
        r.c.stop_all()
        r.c.start()
        self.assertTrue(wait_for(lambda: r.c.status()["state"] == "running"))
        self.assertEqual(len(calls), 2)  # not fetched again

    def test_not_frozen_never_downloads(self):
        r = self.rig(fetch=lambda *a, **k: self.fail("must not download from source"), frozen=lambda: False)
        self.assertTrue(self.start(r))
        self.assertEqual(r.c.status()["state"], "running")

    def test_unavailable_ffmpeg_falls_back_to_path(self):
        def fetch(names, progress=None, opener=None):
            if names == ["ffmpeg"]:
                raise tools_fetch.ToolUnavailable("ffmpeg is not yet available for darwin-arm64.")
            return {}

        r = self.rig(fetch=fetch, frozen=lambda: True)
        with mock.patch("controller.shutil.which", return_value="/opt/homebrew/bin/ffmpeg"):
            self.assertTrue(self.start(r))
        self.assertEqual(r.c.status()["state"], "running")

    def test_unavailable_ffmpeg_without_path_gives_a_clear_message(self):
        def fetch(names, progress=None, opener=None):
            raise tools_fetch.ToolUnavailable("ffmpeg is not yet available for darwin-arm64.")

        r = self.rig(fetch=fetch, frozen=lambda: True)
        with mock.patch("controller.shutil.which", return_value=None):
            self.assertTrue(self.start(r))
        status = r.c.status()
        self.assertEqual(status["state"], "error")
        self.assertIn("not yet available", status["message"])
        self.assertIn("brew install ffmpeg", status["message"])

    def test_fetch_errors_become_error_status(self):
        for error, text in [(tools_fetch.ToolFetchError("Checksum mismatch"), "Checksum mismatch"),
                            (OSError("no network"), "Check the internet connection")]:
            def fetch(names, progress=None, opener=None, error=error):
                raise error
            r = self.rig(fetch=fetch, frozen=lambda: True)
            self.assertTrue(self.start(r))
            self.assertEqual(r.c.status()["state"], "error")
            self.assertIn(text, r.c.status()["message"])

    def test_tools_fetch_and_agent_tool_agree_on_file_names_and_folder(self):
        with tempfile.TemporaryDirectory() as home, mock.patch("identity.default_home", return_value=Path(home)), \
                mock.patch.object(paths, "frozen", lambda: True):
            folder = paths.tools_dir()
            self.assertEqual(folder, Path(home) / "bin")
            folder.mkdir(parents=True)
            for entries in tools_fetch.MANIFEST.values():
                for name, entry in entries.items():
                    self.assertTrue(entry["binary"].startswith(name))
            expected = {"win32": ".exe"}.get(sys.platform, "")
            for name in ("ffmpeg", "mediamtx"):
                (folder / f"{name}{expected}").write_bytes(b"x")
                found = agent.tool(name)
                self.assertEqual(Path(found), folder / f"{name}{hostos.exe_suffix}")
            key = tools_fetch.platform_key()
            if key in tools_fetch.MANIFEST:
                for name, entry in tools_fetch.MANIFEST[key].items():
                    self.assertEqual(entry["binary"], f"{name}{hostos.exe_suffix}")


class ShutdownMidDownloadTests(unittest.TestCase):
    def test_shutdown_cancels_a_download_and_removes_the_part_file(self):
        import hashlib
        import io
        import threading
        import time
        import zipfile

        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w") as zf:
            zf.writestr("tool.exe", b"x")
        entry = {"version": "1", "url": "https://example.invalid/tool.zip", "extract": {"tool.exe": "tool.exe"},
                 "binary": "tool.exe", "sha256": hashlib.sha256(buf.getvalue()).hexdigest()}

        class Slow(io.RawIOBase):
            headers = {"Content-Length": "999999999"}

            def read(self, n=-1):
                time.sleep(0.05)
                return b"x" * 1024  # never ends by itself

            def __enter__(self):
                return self

            def __exit__(self, *exc):
                return False

        with tempfile.TemporaryDirectory() as home, \
                mock.patch.object(paths, "data_dir", return_value=Path(home)), \
                mock.patch.object(paths, "tools_dir", return_value=Path(home) / "bin"), \
                mock.patch.dict(tools_fetch.MANIFEST, {"testplat": {"ffmpeg": entry, "mediamtx": entry}}), \
                mock.patch.object(tools_fetch, "platform_key", return_value="testplat"):
            r = Rig(frozen=lambda: True,
                    fetch=lambda names, progress=None: tools_fetch.ensure(names, progress, opener=lambda req: Slow()))
            r.c.start()
            seen = []
            self.assertTrue(wait_for(lambda: seen.extend(e for e in r.c.events() if e["type"] == "download") or seen))
            parts = list((Path(home) / "downloads").glob("*.part"))
            self.assertTrue(parts)  # the download is under way
            started = time.monotonic()
            r.c.shutdown()
            self.assertLess(time.monotonic() - started, 4)
            self.assertEqual(list((Path(home) / "downloads").glob("*.part")), [])
            self.assertTrue(r.c._fetch_idle.is_set())


class ConnectFailureCallbackTests(unittest.TestCase):
    """The real LinkRunner reports a failed dial through its public callback; the controller turns it into the Local
    Network row on macOS."""

    def run_runner(self, pinned, browse_hosts):
        import asyncio
        from session import LinkRunner
        calls = []

        async def browse(pairing=False):
            for host in browse_hosts:
                yield host

        async def go():
            runner = LinkRunner("ident", None, None, 1, pinned=pinned, discover=bool(browse_hosts), browse=browse,
                                wired=lambda: [], on_connect_failed=lambda *a: calls.append(a))

            async def attempt(key):
                return "failed", "can't connect"
            runner._attempt = attempt
            task = asyncio.ensure_future(runner.run())
            await asyncio.sleep(0.3)
            task.cancel()
            await asyncio.gather(task, return_exceptions=True)

        asyncio.run(go())
        return calls

    def test_runner_reports_pinned_and_discovered_headsets(self):
        calls = self.run_runner([("10.0.0.9", 47810)], [("10.0.0.5", 47810)])
        self.assertIn(("10.0.0.9", 47810, False), calls)
        self.assertIn(("10.0.0.5", 47810, True), calls)

    def test_other_failures_are_not_reported(self):
        import asyncio
        from session import LinkRunner
        calls = []

        async def go():
            runner = LinkRunner("ident", None, None, 1, pinned=[("10.0.0.9", 47810)],
                                on_connect_failed=lambda *a: calls.append(a))

            async def attempt(key):
                return "failed", "wrong code"
            runner._attempt = attempt
            task = asyncio.ensure_future(runner.run())
            await asyncio.sleep(0.2)
            task.cancel()
            await asyncio.gather(task, return_exceptions=True)

        asyncio.run(go())
        self.assertEqual(calls, [])

    def test_controller_marks_local_network_from_the_runner_callback(self):
        r = Rig(platform="darwin", gate=FakeGate(), perm_probe=make_rows_probe(set()),
                connect_probe=lambda h, p: errno.EHOSTUNREACH)
        self.addCleanup(r.c.shutdown)
        r.c.start()
        self.assertTrue(wait_for(lambda: r.c.status()["state"] == "running"))
        callback = r.runners[0].options["on_connect_failed"]
        callback("10.0.0.9", 47810, False)  # pinned by hand: not a Bonjour result, so not a permission hint
        callback("10.0.0.5", 47810, True)
        def denied():
            return any(x.name == perms.LOCAL_NETWORK and x.status == perms.MISSING for x in r.c.permission_rows())
        self.assertTrue(wait_for(denied))
        self.assertEqual(sum(e["type"] == "permission" for e in r.c.events()), 1)


class PermissionWiringTests(unittest.TestCase):
    def rig(self, platform="darwin", granted=(), **over):
        self.gate = FakeGate()
        self.launched = []
        self.made = []

        def streams(launcher):
            s = FakeStreams(launcher)
            self.made.append(s)
            return s

        r = Rig(platform=platform, gate=self.gate, perm_probe=make_rows_probe(set(granted)), streams_factory=streams,
                capture_launcher=lambda a, e, j: (lambda target, path: self.launched.append(path)), **over)
        self.addCleanup(r.c.shutdown)
        return r

    def test_nothing_is_requested_at_start_and_screen_recording_on_the_first_stream(self):
        r = self.rig()
        r.c.start()
        self.assertTrue(wait_for(lambda: r.c.status()["state"] == "running"))
        self.assertEqual(self.gate.asked, [])
        launcher = self.made[0].launcher
        launcher("t", "one")
        launcher("t", "two")
        self.assertEqual(self.gate.asked, [perms.SCREEN])  # once
        self.assertEqual(self.launched, ["one", "two"])

    def test_windows_requests_nothing_and_has_no_rows(self):
        r = self.rig(platform="win32")
        r.c.start()
        self.assertTrue(wait_for(lambda: r.c.status()["state"] == "running"))
        self.made[0].launcher("t", "p")
        r.c.apply(SimpleNamespace(allow_control=True))
        self.assertEqual(self.gate.asked, [])
        self.assertEqual(r.c.permission_rows(), [])
        self.assertFalse(r.c.request_permission(perms.LISTEN))
        self.assertTrue(r.c._settings.allow_control)

    def test_control_stays_off_until_accessibility_is_granted(self):
        r = self.rig()
        effective = r.c.apply(SimpleNamespace(allow_control=True))
        self.assertFalse(effective.allow_control)
        self.assertEqual(self.gate.asked, [perms.ACCESSIBILITY])
        events = [e for e in r.c.events() if e["type"] == "permission_needed"]
        self.assertEqual(len(events), 1)
        self.assertIn("Accessibility", events[0]["message"])
        r.c.apply(SimpleNamespace(cast_only=True))
        self.assertEqual(self.gate.asked, [perms.ACCESSIBILITY])  # the request is made once

    def test_granted_accessibility_lets_control_on(self):
        r = self.rig(granted={perms.ACCESSIBILITY})
        self.assertTrue(r.c.apply(SimpleNamespace(allow_control=True)).allow_control)
        self.assertEqual(self.gate.asked, [])

    def test_loading_saved_settings_does_not_prompt(self):
        r = self.rig()
        effective = r.c.apply(SimpleNamespace(allow_control=True), user_initiated=False)
        self.assertFalse(effective.allow_control)
        self.assertEqual(self.gate.asked, [])

    def test_input_monitoring_only_from_the_button(self):
        r = self.rig()
        r.c.start()
        self.assertTrue(wait_for(lambda: r.c.status()["state"] == "running"))
        r.c.apply(SimpleNamespace(allow_control=True))
        self.assertNotIn(perms.LISTEN, self.gate.asked)
        self.assertTrue(r.c.request_permission(perms.LISTEN))
        self.assertFalse(r.c.request_permission(perms.LISTEN))

    def test_local_network_is_likely_denied_on_no_route_errors(self):
        for code, denied in [(errno.EHOSTUNREACH, True), (errno.ENETUNREACH, True), (errno.ECONNREFUSED, False),
                             (None, False)]:
            r = self.rig(connect_probe=lambda host, port, code=code: code)
            r.c._note_connect_failure("10.0.0.5", 47810)
            row = next(x for x in r.c.permission_rows() if x.name == perms.LOCAL_NETWORK)
            self.assertEqual(row.status, perms.MISSING if denied else perms.UNKNOWN, code)
            self.assertEqual(any(e["type"] == "permission" for e in r.c.events()), denied)

    def test_local_network_check_is_skipped_off_macos(self):
        r = self.rig(platform="win32", connect_probe=lambda h, p: self.fail("no probe off macOS"))
        r.c._note_connect_failure("10.0.0.5", 47810)


class LoginWiringTests(unittest.TestCase):
    def test_settings_load_mirrors_the_system_state(self):
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "settings.json"
            settings_mod.save(settings_mod.Settings(start_at_login=False), path)
            self.assertTrue(settings_mod.load(path, login_status=lambda: (autostart.ENABLED, "")).start_at_login)
            settings_mod.save(settings_mod.Settings(start_at_login=True), path)
            self.assertFalse(settings_mod.load(path, login_status=lambda: (autostart.DISABLED, "")).start_at_login)
            self.assertFalse(settings_mod.load(path, login_status=lambda: (autostart.UNAVAILABLE, "x")).start_at_login)

            def broken():
                raise OSError("registry")
            self.assertFalse(settings_mod.load(path, login_status=broken).start_at_login)

    def test_not_packaged_means_unavailable(self):
        self.assertEqual(autostart.status()[0], autostart.UNAVAILABLE)


def make_root():
    import tkinter as tk
    try:
        root = tk.Tk()
    except tk.TclError as e:
        raise unittest.SkipTest(f"Tk is unavailable: {e}")
    root.withdraw()
    return root


class FakeUiController:
    def __init__(self, rows=(), effective=None):
        self._events, self.rows, self.effective = [], list(rows), effective
        self.requested, self.applied = [], []

    def status(self):
        return {"state": "idle", "message": "", "encoder": None}

    def events(self):
        out, self._events = self._events, []
        return out

    def pairs(self):
        return []

    def apply(self, s):
        self.applied.append(s)
        return self.effective

    def permission_rows(self):
        return self.rows

    def request_permission(self, name):
        self.requested.append(name)
        return True

    def start(self):
        pass

    def stop_all(self):
        pass


class FakeLogin:
    def __init__(self, state=autostart.DISABLED, message="", ok=True):
        self.state, self.message, self.ok, self.calls = state, message, ok, []

    def status(self):
        return self.state, self.message

    def enable(self):
        self.calls.append("enable")
        if self.ok:
            self.state = autostart.ENABLED
        return self.ok, "" if self.ok else self.message

    def disable(self):
        self.calls.append("disable")
        self.state = autostart.DISABLED
        return True, ""


class WindowWiringTests(unittest.TestCase):
    def build(self, controller_=None, login=None, **kw):
        from gui import main_window as mw
        self.root = make_root()
        self.addCleanup(self._release)
        self.c = controller_ or FakeUiController()
        self.saved = []
        self.login = login or FakeLogin()
        self.w = mw.MainWindow(self.root, self.c, settings_mod.Settings(), save=self.saved.append, kill_switch="K",
                               login=self.login, poll_ms=10, **kw)
        self.addCleanup(self.w.close)
        self.addCleanup(self.root.destroy)
        return self.w

    def _release(self):
        import gc
        self.w = self.root = None
        gc.collect()

    def test_login_toggle_enables_and_disables_through_autostart(self):
        w = self.build()
        w.vars["start_at_login"].set(True)
        w.on_toggle("start_at_login")
        w.vars["start_at_login"].set(False)
        w.on_toggle("start_at_login")
        self.assertEqual(self.login.calls, ["enable", "disable"])
        self.assertEqual([s.start_at_login for s in self.saved], [True, False])

    def test_login_toggle_is_disabled_and_explained_when_unavailable(self):
        w = self.build(login=FakeLogin(autostart.UNAVAILABLE, ""))
        self.assertIn("disabled", w.login_button.state())
        self.assertIn("unavailable", w.login_note.get())

    def test_task_manager_message_is_shown(self):
        w = self.build(login=FakeLogin(autostart.DISABLED, autostart.TASK_MANAGER_OFF))
        self.assertIn("turned off in Task Manager", w.login_note.get())

    def test_failed_enable_reverts_and_shows_the_reason(self):
        w = self.build(login=FakeLogin(ok=False, message="Move the app to Applications first."))
        w.vars["start_at_login"].set(True)
        w.on_toggle("start_at_login")
        self.assertFalse(w.vars["start_at_login"].get())
        self.assertIn("Applications", w.login_note.get())

    def test_download_progress_is_shown_then_hidden(self):
        w = self.build()
        self.c._events.append({"type": "download", "name": "ffmpeg", "done": 50, "total": 100})
        w.poll()
        self.assertIn("50%", w.download_text.get())
        self.assertTrue(w.download_frame.winfo_manager())
        self.c._events.append({"type": "download_end", "name": "ffmpeg"})
        w.poll()
        self.assertFalse(w.download_frame.winfo_manager())

    def test_no_permission_section_off_macos(self):
        w = self.build(show_permissions=False)
        self.assertEqual(w._perm_widgets, {})

    def test_permission_section_buttons(self):
        rows = [perms.PermRow(perms.SCREEN, "missing", "why", "url-screen"),
                perms.PermRow(perms.ACCESSIBILITY, "granted", "why", "url-ax"),
                perms.PermRow(perms.LISTEN, "unknown", "why", "url-listen", True),
                perms.PermRow(perms.LOCAL_NETWORK, "missing", "why", "url-net")]
        opened, relaunched = [], []
        w = self.build(FakeUiController(rows), show_permissions=True, open_settings=opened.append,
                       on_relaunch=lambda: relaunched.append(1))
        self.assertEqual(w._perm_widgets[perms.LOCAL_NETWORK].get(), "likely denied")
        self.assertEqual(w._perm_widgets[perms.SCREEN].get(), "needs permission")
        self.assertIn("optional", w._perm_widgets[perms.LISTEN].get())
        w.on_permission(perms.SCREEN)
        self.assertEqual((opened, self.c.requested), (["url-screen"], []))  # opening settings requests nothing
        w.on_permission(perms.LISTEN, request=True)
        self.assertEqual((opened[-1], self.c.requested), ("url-listen", [perms.LISTEN]))
        w.relaunch()
        self.assertEqual(relaunched, [1])

    def test_missing_accessibility_turns_the_toggle_back_off(self):
        c = FakeUiController(effective=SimpleNamespace(allow_control=False, cast_only=False))
        w = self.build(c)
        w.vars["allow_control"].set(True)
        w.on_toggle("allow_control")
        self.assertFalse(w.vars["allow_control"].get())
        self.assertFalse(self.saved[-1].allow_control)
        c._events.append({"type": "permission_needed", "name": "Accessibility", "message": "Allow it first."})
        w.poll()
        self.assertEqual(w.permission_note.get(), "Allow it first.")


class AppWiringTests(unittest.TestCase):
    def test_selftest_passes_offline_and_leaves_state_alone(self):
        frozen_before = getattr(sys, "frozen", None)
        manifest_before = tools_fetch.MANIFEST
        with mock.patch("builtins.print"), mock.patch("urllib.request.urlopen",
                                                      side_effect=AssertionError("network used")):
            self.assertEqual(app.selftest(), 0)
        self.assertEqual(getattr(sys, "frozen", None), frozen_before)
        self.assertIs(tools_fetch.MANIFEST, manifest_before)
        self.assertFalse(paths.frozen())

    def test_selftest_flag_is_dispatched(self):
        with mock.patch.object(app, "selftest", return_value=7) as selftest:
            self.assertEqual(app.main(["--selftest"]), 7)
        selftest.assert_called_once()

    def test_relaunch_argv(self):
        bundle = app.relaunch_argv("/Applications/Spatial Launcher Agent.app/Contents/MacOS/agent", [])
        self.assertEqual(bundle[-1], "/Applications/Spatial Launcher Agent.app")
        plain = app.relaunch_argv("/usr/bin/agent", ["--background"])
        self.assertEqual(plain[-2:], ["/usr/bin/agent", "--background"])


if __name__ == "__main__":
    unittest.main()
