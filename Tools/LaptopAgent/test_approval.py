"""Tests for the laptop approval prompt and the kill hotkey. winapi is mocked: no message box is ever shown, except
the smoke test, which only runs with SL_UI_TESTS=1 and dismisses itself after one second.

  python -m unittest test_approval -v        (from Tools/LaptopAgent)
"""
import asyncio
import os
import threading
import time
import unittest
from unittest import mock

import approval
import dispatch
import session
import winapi
from approval import LaptopApprover, sanitize_name

INFO = {"name": "Phone", "fingerprint": "AB:CD"}
CONSENT = {"consent": dispatch.CONSENT}


class FakeBox:
    """Replaces winapi.ask_yes_no / dismiss_prompt: a box that blocks its thread until answered or dismissed."""

    def __init__(self, answer=None, block=0.0):
        self.answer, self.block = answer, block
        self.shown, self.dismissed = [], []
        self.release = threading.Event()
        self.open = threading.Event()

    def ask(self, text, caption, timeout_ms):
        self.shown.append((text, caption, timeout_ms))
        self.open.set()
        if self.answer is None:
            self.release.wait(20)
            return "no"
        time.sleep(self.block)
        return self.answer

    def dismiss(self, thread_id):
        self.dismissed.append(thread_id)
        self.release.set()
        return True

    def install(self, case):
        case.enterContext(mock.patch.object(winapi, "ask_yes_no", self.ask))
        case.enterContext(mock.patch.object(winapi, "dismiss_prompt", self.dismiss))


async def box_opened(box):
    await asyncio.get_running_loop().run_in_executor(None, box.open.wait, 5)


class ApproverTests(unittest.IsolatedAsyncioTestCase):
    async def test_yes_no_and_timeout(self):
        for answer in ("yes", "no", "timeout"):
            box = FakeBox(answer)
            with mock.patch.object(winapi, "ask_yes_no", box.ask):
                call = LaptopApprover(keyboard=True)(INFO)
                if answer == "timeout":
                    with self.assertRaises(asyncio.TimeoutError):
                        await call
                else:
                    self.assertIs(await call, answer == "yes")
            self.assertEqual(box.shown[0][1:], ("Spatial Launcher", 30000))
            self.assertIn("mouse and keyboard", box.shown[0][0])
            self.assertIn("(AB:CD)", box.shown[0][0])

    async def test_dispatch_reasons(self):
        for answer, expected in (("yes", "control-granted"), ("no", "denied"), ("timeout", "timeout")):
            sent = []
            d = dispatch.InputDispatcher(1, sent.append, lambda k, m: None, input_enabled=True,
                                         approver=LaptopApprover(), headset=INFO)
            with mock.patch.object(winapi, "ask_yes_no", FakeBox(answer).ask):
                await d.request_control(CONSENT)
            self.assertEqual(sent[0].get("reason", sent[0]["type"]), expected)

    async def test_session_close_and_kill_switch_dismiss_the_box(self):
        for how in ("close", "kill"):
            box = FakeBox()
            box.install(self)
            sent = []
            d = dispatch.InputDispatcher(1, sent.append, lambda k, m: None, input_enabled=True,
                                         approver=LaptopApprover(), headset=INFO)
            task = asyncio.ensure_future(d.request_control(CONSENT))
            await box_opened(box)
            if how == "kill":
                d.revoke("kill switch")
            task.cancel()  # LinkSession.close() cancels the pending request
            with self.assertRaises(asyncio.CancelledError):
                await task
            self.assertTrue(box.dismissed, how)
            self.assertFalse([m for m in sent if m["type"] == "control-granted"])
            self.assertFalse(approval._one_box.locked())

    async def test_thread_start_failure_releases_the_lock(self):
        with mock.patch.object(approval.threading.Thread, "start", side_effect=RuntimeError("no threads")):
            with self.assertRaises(RuntimeError):
                await LaptopApprover()(INFO)
        self.assertFalse(approval._one_box.locked())

    async def test_box_is_off_the_loop_and_only_one_at_a_time(self):
        box = FakeBox("yes", block=0.5)
        ticks = 0

        async def heartbeat():
            nonlocal ticks
            while True:
                await asyncio.sleep(0.05)
                ticks += 1

        beat = asyncio.ensure_future(heartbeat())
        with mock.patch.object(winapi, "ask_yes_no", box.ask):
            first = asyncio.ensure_future(LaptopApprover()(INFO))
            await box_opened(box)
            self.assertFalse(await LaptopApprover()(INFO))  # denied at once, no second box
            self.assertIs(await first, True)
        beat.cancel()
        self.assertEqual(len(box.shown), 1)
        self.assertGreaterEqual(ticks, 5)


class SanitizeTests(unittest.TestCase):
    def test_cleaned_and_capped(self):
        self.assertEqual(sanitize_name("a\nb\rc\td"), "a b c d")
        self.assertEqual(sanitize_name("evil‮txt⁦x⁩"), "eviltxtx")
        self.assertEqual(sanitize_name("zero​width"), "zerowidth")
        self.assertEqual(sanitize_name('say "hi"'), "say hi")
        self.assertEqual(sanitize_name("“quoted”"), "quoted")
        self.assertEqual(sanitize_name("«name»"), "name")
        self.assertEqual(sanitize_name("x" + "́" * 20 + "b"), "xb")
        self.assertEqual(sanitize_name("” (AB:CD) wants to view only"), "(AB:CD) wants to view only")
        self.assertEqual(sanitize_name("my-box_1.0 (a)"), "my-box_1.0 (a)")
        self.assertEqual(sanitize_name("x" * 100), "x" * 32 + "…")
        for empty in ("", "  \n\t ", "​‮", None):
            self.assertEqual(sanitize_name(empty), "Unnamed headset")
        text = approval.prompt_text({"name": "Bad\nName", "fingerprint": "AB"}, False)
        self.assertEqual(text.count("\n"), 2)
        self.assertNotIn("keyboard", text)


class ProtocolFlowTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.calls = []
        for name in ("key", "mouse_move", "mouse_button", "mouse_wheel", "type_text", "raise_window"):
            self.enterContext(mock.patch.object(winapi, name, lambda *a, _n=name, **k: self.calls.append((_n,) + a)))
        self.enterContext(mock.patch.object(winapi, "window_at", lambda x, y: 42))
        self.enterContext(mock.patch.object(winapi, "focus_window", lambda h: True))

    def make(self, answer, **kw):
        from test_dispatch import window_stream
        self.control, self.now = [], [0.0]
        self.box = FakeBox(answer)
        self.enterContext(mock.patch.object(winapi, "ask_yes_no", self.box.ask))
        kw.setdefault("input_enabled", True)
        d = dispatch.InputDispatcher(1, self.control.append, lambda k, m: None, approver=LaptopApprover(),
                                     headset=INFO, clock=lambda: self.now[0], **kw)
        d.attach(0, window_stream(42, winapi.Rect(0, 0, 100, 100)))
        return d

    def buttons(self):
        return [c for c in self.calls if c[0] == "mouse_button"]

    async def test_yes_grants_then_input_is_injected(self):
        d = self.make("yes", keyboard_enabled=True)
        await d.request_control(CONSENT)
        self.assertEqual(self.control, [{"type": "control-granted", "keyboard": True}])
        d.on_input(0, '{"type":"down","x":0.5,"y":0.5,"button":"left"}')
        self.assertEqual(len(self.buttons()), 1)

    async def test_no_denies_with_nothing_injected_and_reask_is_busy(self):
        d = self.make("no")
        await d.request_control(CONSENT)
        self.assertEqual(self.control, [{"type": "control-denied", "reason": "denied"}])
        d.on_input(0, '{"type":"down","x":0.5,"y":0.5,"button":"left"}')
        self.assertEqual(self.calls, [])
        self.now[0] = 9.0
        await d.request_control(CONSENT)
        self.assertEqual(self.control[-1], {"type": "control-denied", "reason": "busy"})
        self.assertEqual(len(self.box.shown), 1)
        self.now[0] = 10.5
        await d.request_control(CONSENT)
        self.assertEqual(len(self.box.shown), 2)

    async def test_cast_only_never_prompts_and_media_works(self):
        d = self.make("yes", input_enabled=False, cast_only=True)
        await d.request_control(CONSENT)
        self.assertEqual(self.control, [{"type": "control-denied", "reason": "cast only"}])
        d.on_input(0, '{"type":"media","action":"mute"}')
        self.assertTrue([c for c in self.calls if c[0] == "key"])
        self.assertEqual(self.box.shown, [])


class KillSwitchTests(unittest.IsolatedAsyncioTestCase):
    def runner(self, **kw):
        streams = mock.Mock()
        return session.LinkRunner(mock.Mock(), mock.Mock(), streams, 8554, **kw), streams

    async def test_kill_closes_sessions_stops_video_and_blocks_redialling(self):
        attempts = []
        r, streams = self.runner(pinned=[("h", 1)])
        live = mock.Mock(close=mock.AsyncMock())
        r.active = live

        async def attempt(key):
            attempts.append(key)
            return "failed", "x"

        r._attempt = attempt
        run = asyncio.ensure_future(r.run())
        await asyncio.sleep(0.05)
        r.kill_switch()
        r.kill_switch()  # a second press changes nothing
        await r._shutdown_task
        live.dispatcher.revoke.assert_called_once_with("kill switch")
        live.close.assert_awaited_once()
        streams.stop_all.assert_called_once()
        count = len(attempts)
        r._found("2.2.2.2", 5, 30.0, "test")
        r._add_target("3.3.3.3", 6, 30.0)
        await asyncio.sleep(0.1)
        self.assertEqual(len(attempts), count)
        self.assertEqual(r._workers, {})
        self.assertFalse(run.done())  # stays parked, idle
        run.cancel()
        await asyncio.gather(run, return_exceptions=True)

    async def test_kill_dismisses_an_open_box(self):
        box = FakeBox()
        box.install(self)
        r, _ = self.runner()
        live = session.LinkSession(mock.Mock(), 8554, input_enabled=True, approver=LaptopApprover())
        live.set_headset("Phone", "AB")
        r.active = live
        live._on_control_message('{"type":"control-request","consent":"full control"}')
        await box_opened(box)
        r.kill_switch()
        await r._shutdown_task
        self.assertTrue(box.dismissed)
        self.assertTrue(live.closed)


@unittest.skipUnless(os.environ.get("SL_UI_TESTS") == "1", "set SL_UI_TESTS=1 to show a real message box")
class RealBoxTests(unittest.TestCase):
    def test_box_times_out_on_its_own(self):
        start = time.monotonic()
        self.assertEqual(winapi.ask_yes_no("Spatial Launcher test prompt (closes itself)", "Spatial Launcher", 1000),
                         "timeout")
        self.assertLess(time.monotonic() - start, 3)


if __name__ == "__main__":
    unittest.main()
