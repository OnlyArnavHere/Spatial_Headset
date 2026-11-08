"""The web window's Bridge with a fake controller: what poll() reports, pairing feedback, settings and the log. No web
view is opened."""
import logging
import time
import unittest
from types import SimpleNamespace

from gui import web_app
from settings import Settings


class FakeController:
    def __init__(self):
        self.events_out, self.calls, self.applied = [], [], []
        self.pair_list = [SimpleNamespace(name="Quest", fingerprint="ABCD", added="2026-01-01", always_allow=True)]

    def status(self):
        return {"state": "idle", "message": "", "encoder": None}

    def events(self):
        out, self.events_out = self.events_out, []
        return out

    def pairs(self):
        return list(self.pair_list)

    def begin_pair(self):
        self.calls.append("begin_pair")
        return 3

    def submit_code(self, text):
        self.calls.append(("code", text))

    def cancel_pair(self):
        self.calls.append("cancel_pair")

    def revoke(self, name):
        self.calls.append(("revoke", name))
        return True

    def apply(self, s):
        self.applied.append(s)
        return s

    def start(self):
        self.calls.append("start")

    def stop_all(self):
        self.calls.append("stop_all")


LOGIN = SimpleNamespace(status=lambda: ("disabled", ""), enable=lambda: (True, ""), disable=lambda: (True, ""))


def make(settings=None):
    c = FakeController()
    saved = []
    b = web_app.Bridge(c, settings or Settings(), save=saved.append, login=LOGIN, show_permissions=False,
                       attach_log=False, wired_report=lambda: "cable ok")
    return b, c, saved


class BridgeTests(unittest.TestCase):
    def test_poll_reports_status_pairs_and_settings(self):
        b, c, _ = make()
        s = b.poll(0)
        self.assertEqual(s["status"]["state"], "idle")
        self.assertIn("Idle", s["status"]["text"])
        self.assertEqual(s["pairs"], [{"name": "Quest", "fingerprint": "ABCD", "added": "2026-01-01", "always": True}])
        self.assertFalse(s["settings"]["wired"])
        self.assertIsNone(s["pair"])

    def test_status_events_update_the_state(self):
        b, c, _ = make()
        c.events_out.append({"type": "status", "state": "running", "encoder": "x264", "message": ""})
        s = b.poll(0)
        self.assertEqual(s["status"]["state"], "running")
        self.assertEqual(s["status"]["encoder"], "x264")

    def test_code_typed_before_the_headset_is_found_is_acknowledged_then_sent(self):
        b, c, _ = make()
        p = b.pair_submit("abcd-efgh-jkmn")
        self.assertEqual(c.calls[:2], ["begin_pair", ("code", "abcd-efgh-jkmn")])
        self.assertIn("as soon as the headset is found", p["status"])
        c.events_out.append({"type": "pair_prompt", "gen": 3, "text": "Enter the code "})
        s = b.poll(0)
        self.assertEqual(s["pair"]["status"], "Sending...")
        self.assertFalse(s["pair"]["searching"])

    def test_qr_event_draws_the_qr_code(self):
        b, c, _ = make()
        b.pair_start()
        self.assertEqual(b.poll(0)["pair"]["qr"], "")
        c.events_out.append({"type": "pair_qr", "gen": 3, "payload": "SLPAIR:0123456789ABCDEFGHJK:ABCDEFGHIJKLMNOP"})
        p = b.poll(0)["pair"]
        self.assertTrue(p["qr"].startswith("<svg") and p["qr"].endswith("</svg>"))
        self.assertEqual(p["qr_key"], "3")
        c.events_out.append({"type": "pair_qr", "gen": 2, "payload": "SLPAIR:ZZZZZZZZZZZZZZZZZZZZ:ABCDEFGHIJKLMNOP"})
        self.assertEqual(b.poll(0)["pair"]["qr"], p["qr"])  # another run's QR is ignored

    def test_pair_events_of_another_run_are_ignored_and_done_is_shown(self):
        b, c, _ = make()
        b.pair_start()
        c.events_out.append({"type": "pair_done", "gen": 2, "ok": False, "text": "old"})
        self.assertFalse(b.poll(0)["pair"]["finished"])
        c.events_out.append({"type": "pair_say", "gen": 3, "text": "That code is wrong."})
        self.assertEqual(b.poll(0)["pair"]["tone"], "error")
        c.events_out.append({"type": "pair_done", "gen": 3, "ok": True, "text": "Paired."})
        p = b.poll(0)["pair"]
        self.assertTrue(p["finished"] and p["ok"])
        self.assertEqual(p["tone"], "ok")

    def test_forget_removes_the_headset_or_says_why_not(self):
        b, c, _ = make()
        c.revoke = lambda name: name == "Quest"
        self.assertEqual(b.forget("Quest"), "")
        self.assertIn("isn't paired", b.forget("Other"))

        def broken(name):
            raise OSError("disk full")
        c.revoke = broken
        self.assertIn("disk full", b.forget("Quest"))

    def test_connected_headsets_follow_the_session_log(self):
        b, c, _ = make()
        watch = b._sessions
        log = logging.getLogger("session")

        def emit(msg, *args):
            watch.emit(log.makeRecord("session", logging.INFO, "", 0, msg, args, None))
        emit(web_app.SessionWatch.LINKED, "Quest", "usb:0000-1111", 47810, "paired")
        emit(web_app.SessionWatch.LINKED, "Phone", "192.168.1.5", 47810, "paired")
        self.assertEqual(b.poll(0)["connected"], [])  # not running: nothing is shown as connected
        c.events_out.append({"type": "status", "state": "running", "encoder": "", "message": ""})
        self.assertEqual(b.poll(0)["connected"], [{"name": "Quest", "link": "USB cable"},
                                                  {"name": "Phone", "link": "Wi-Fi"}])
        emit(web_app.SessionWatch.ENDED, "Quest", "connection closed")
        self.assertEqual(b.poll(0)["connected"], [{"name": "Phone", "link": "Wi-Fi"}])

    def test_cancel_cancels_only_a_running_pairing(self):
        b, c, _ = make()
        b.pair_start()
        b.pair_cancel()
        self.assertIn("cancel_pair", c.calls)
        self.assertIsNone(b.poll(0)["pair"])

    def test_control_and_cast_only_exclude_each_other_and_are_saved(self):
        b, c, saved = make(Settings(cast_only=True))
        self.assertEqual(b.set_toggle("allow_control", True), "")
        s = b.poll(0)["settings"]
        self.assertTrue(s["allow_control"])
        self.assertFalse(s["cast_only"])
        self.assertTrue(saved[-1].allow_control)

    def test_wired_on_runs_the_cable_check(self):
        b, c, _ = make()
        b.set_toggle("wired", True)
        deadline = time.time() + 3
        while time.time() < deadline and b.poll(0)["wired_text"] != "cable ok":
            time.sleep(0.02)
        self.assertEqual(b.poll(0)["wired_text"], "cable ok")

    def test_log_lines_arrive_once(self):
        b, c, _ = make()
        b._log_handler.emit(logging.LogRecord("x", logging.INFO, "", 0, "first", None, None))
        s = b.poll(0)
        self.assertEqual(len(s["log"]), 1)
        self.assertEqual(b.poll(s["log_seq"])["log"], [])
        b._log_handler.emit(logging.LogRecord("x", logging.INFO, "", 0, "second", None, None))
        self.assertTrue(b.poll(s["log_seq"])["log"][0].endswith("second"))

    def test_the_page_ships_with_the_app(self):
        self.assertTrue(all(__import__("os").path.isfile(__import__("os").path.join(web_app.WEB_DIR, f))
                            for f in ("index.html", "app.css", "app.js")))


class LiftTests(unittest.TestCase):
    class Window:
        def __init__(self, native):
            self.native = native
            self.on_top_sets = []

        @property
        def on_top(self):
            return False

        @on_top.setter
        def on_top(self, value):
            self.on_top_sets.append(value)

    def test_windows_lift_never_sets_on_top_from_another_thread(self):
        # Setting pywebview's on_top off the GUI thread deadlocked the window on Windows (a second launch froze it).
        lifted = []
        original, platform = web_app._lift_windows, web_app.sys.platform
        web_app._lift_windows = lambda window: lifted.append(window) or True
        web_app.sys.platform = "win32"
        try:
            window = self.Window(native=object())
            web_app.WebRoot(window).lift()
        finally:
            web_app._lift_windows, web_app.sys.platform = original, platform
        self.assertEqual(lifted, [window])
        self.assertEqual(window.on_top_sets, [])

    def test_lift_without_a_handle_is_refused(self):
        self.assertFalse(web_app._lift_windows(self.Window(native=None)))


if __name__ == "__main__":
    unittest.main()
