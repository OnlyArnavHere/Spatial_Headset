"""The status window: pure helpers, and a smoke test that builds it with a fake controller (skipped without Tk)."""
import gc
import logging
import sys
import unittest
from types import SimpleNamespace
from unittest import mock

import settings as settings_mod
from gui import main_window as mw
from settings import Settings


class FakeController:
    def __init__(self):
        self._events = []
        self.applied, self.revoked, self.calls = [], [], []
        self.pair_list = [SimpleNamespace(name="Quest", fingerprint="ABCD", added="2026-01-01")]

    def status(self):
        return {"state": "idle", "message": "", "encoder": None}

    def events(self):
        out, self._events = self._events, []
        return out

    def pairs(self):
        return list(self.pair_list)

    def revoke(self, name):
        self.revoked.append(name)
        return True

    def apply(self, s):
        self.applied.append(s)

    def start(self):
        self.calls.append("start")

    def stop_all(self):
        self.calls.append("stop_all")

    def begin_pair(self):
        self.calls.append("begin_pair")
        return 7

    def submit_code(self, text):
        self.calls.append(("code", text))

    def cancel_pair(self):
        self.calls.append("cancel_pair")


class HelperTests(unittest.TestCase):
    def test_importing_the_window_module_opens_nothing(self):
        self.assertTrue(callable(mw.MainWindow))

    def test_status_text(self):
        self.assertIn("Idle", mw.status_text({"state": "idle"}))
        self.assertIn("x264", mw.status_text({"state": "running", "encoder": "x264"}))
        self.assertIn("no ffmpeg", mw.status_text({"state": "error", "message": "no ffmpeg"}))
        self.assertIn("Idle", mw.status_text({}))

    def test_toggles_exclude_each_other(self):
        on = mw.next_toggles("allow_control", True, Settings(cast_only=True))
        self.assertTrue(on.allow_control)
        self.assertFalse(on.cast_only)
        cast = mw.next_toggles("cast_only", True, on)
        self.assertTrue(cast.cast_only)
        self.assertFalse(cast.allow_control)
        self.assertFalse(mw.next_toggles("allow_control", False, on).allow_control)

    def test_log_handler_collects_formatted_lines(self):
        h = mw.LogQueueHandler()
        h.emit(logging.LogRecord("agent", logging.INFO, "f", 1, "hello %s", ("you",), None))
        self.assertIn("agent: hello you", h.lines.get_nowait())


def make_root():
    import tkinter as tk
    try:
        root = tk.Tk()
    except tk.TclError as e:
        raise unittest.SkipTest(f"Tk is unavailable: {e}")
    root.withdraw()
    return root


class WindowSmokeTests(unittest.TestCase):
    def setUp(self):
        self.root = make_root()
        self.addCleanup(self._release)  # runs last: drop every Tk object on this thread
        self.addCleanup(self.root.destroy)
        self.c = FakeController()
        self.saved = []
        self.w = mw.MainWindow(self.root, self.c, Settings(), save=self.saved.append, kill_switch="Ctrl+Alt+Shift+Q",
                               wired_report=lambda: SimpleNamespace(lines=lambda: ["hotspot ok"]), poll_ms=10)
        self.addCleanup(self.w.close)

    def _release(self):
        self.w = self.root = None
        gc.collect()

    def pump(self, n=5):
        for _ in range(n):
            self.root.update()

    def test_builds_and_shows_idle_and_pairs(self):
        self.pump()
        self.assertIn("Idle", self.w.status.get())
        self.assertEqual(self.w.pair_list.size(), 1)

    def test_status_events_update_the_label(self):
        self.c._events.append({"type": "status", "state": "running", "message": "", "encoder": None})
        self.w.poll()
        self.assertIn("Running", self.w.status.get())

    def test_toggle_saves_and_applies(self):
        self.w.vars["allow_control"].set(True)
        self.w.on_toggle("allow_control")
        self.assertTrue(self.c.applied[-1].allow_control)
        self.assertTrue(self.saved[-1].allow_control)
        self.w.vars["cast_only"].set(True)
        self.w.on_toggle("cast_only")
        self.assertFalse(self.w.vars["allow_control"].get())

    def test_bad_address_is_refused_and_not_saved(self):
        self.w.address.set("not a host:::")
        with mock.patch.object(mw.messagebox, "showerror") as error:
            self.w.on_address()
        error.assert_called_once()
        self.assertEqual(self.saved, [])
        self.assertEqual(self.w.address.get(), "")

    def test_log_lines_reach_the_pane(self):
        logging.getLogger("smoke").warning("visible in the pane")
        self.pump()
        self.w.poll()
        self.assertIn("visible in the pane", self.w.log_text.get("1.0", "end"))

    def test_wired_report_is_shown(self):
        self.w.vars["wired"].set(True)
        self.w.on_toggle("wired")
        for _ in range(100):
            self.w.poll()
            if "hotspot ok" in self.w.wired_text.get():
                break
            import time
            time.sleep(0.01)
        self.assertIn("hotspot ok", self.w.wired_text.get())

    def test_pair_dialog_starts_and_cancels_pairing(self):
        self.w.open_pair_dialog()
        self.assertEqual(self.c.calls, ["begin_pair"])
        self.w.dialog.code.set("ABCD-EFGH-JKMN")
        self.w.dialog.submit()
        self.assertIn(("code", "ABCD-EFGH-JKMN"), self.c.calls)
        self.w.dialog.close()
        self.assertIn("cancel_pair", self.c.calls)
        self.assertIsNone(self.w.dialog)

    def test_pair_done_event_reaches_the_dialog(self):
        self.w.open_pair_dialog()
        self.c._events.append({"type": "pair_done", "gen": 7, "ok": True, "text": "Paired."})
        self.w.poll()
        self.assertEqual(self.w.dialog.status.get(), "Paired.")
        self.w.dialog.close()
        self.assertNotIn("cancel_pair", self.c.calls)

    def test_stale_pair_events_are_ignored(self):
        self.w.open_pair_dialog()
        self.c._events.append({"type": "pair_done", "gen": 6, "ok": False, "text": "old"})
        self.w.poll()
        self.assertEqual(self.w.dialog.status.get(), "")
        self.assertFalse(self.w.dialog.finished)

    def test_close_closes_an_open_pair_dialog(self):
        self.w.open_pair_dialog()
        self.w.close()
        self.assertIsNone(self.w.dialog)
        self.assertIn("cancel_pair", self.c.calls)

    def test_save_failure_keeps_the_applied_settings_and_says_so(self):
        def failing(_s):
            raise OSError("disk full")
        self.w._save = failing
        self.w.vars["wired"].set(False)
        self.w.vars["cast_only"].set(True)
        with mock.patch.object(mw.messagebox, "showerror") as error:
            self.w.on_toggle("cast_only")
        self.assertIn("Couldn't save settings", error.call_args.args[1])
        self.assertTrue(self.c.applied[-1].cast_only)
        self.assertTrue(self.w.settings.cast_only)

    def test_close_detaches_the_log_handler(self):
        self.w.close()
        self.assertNotIn(self.w.log_handler, logging.getLogger().handlers)


if __name__ == "__main__":
    unittest.main()
