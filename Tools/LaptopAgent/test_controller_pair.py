"""Controller pairing with fakes: the typed code reaches pair_command, cancel and the 120 s limit end it, nothing is
added to the PairStore. No tkinter."""
import asyncio
import subprocess
import sys
import time
import unittest
from types import SimpleNamespace
from unittest import mock

import agent
import controller
from test_controller import FakeStore, wait_for


class RecordingStore(FakeStore):
    def __init__(self):
        super().__init__()
        self.added = []


def make(pair_command=None, timeout=5.0, **settings):
    store = RecordingStore()
    c = controller.AgentController(load_identity=lambda: "ident", store_factory=lambda: store,
                                   pair_command=pair_command, pair_timeout=timeout)
    if settings:
        c.apply(SimpleNamespace(**settings))
    return c, store


def collect(c, kind, seconds=3.0):
    seen = []

    def check():
        seen.extend(e for e in c.events() if e["type"] == kind)
        return bool(seen)
    wait_for(check, seconds)
    return seen


class PairTests(unittest.TestCase):
    c = None

    def tearDown(self):
        if self.c is not None:
            self.c.shutdown()

    def test_code_reaches_pair_command_and_pairing_succeeds(self):
        got = []

        async def fake_pair_once(host, port, ident, store, code, name, announce, kw):
            got.append((host, port, code))
            return SimpleNamespace(name="Quest", headset_spki=b"x")

        with mock.patch.object(agent, "_pair_once", fake_pair_once):
            self.c, store = make(headset_address="192.168.1.5:4000")
            self.c.begin_pair()
            self.assertTrue(wait_for(lambda: self.c._pair_tasks))
            time.sleep(0.05)
            self.c.submit_code("abcd-efgh-jkmn")
            done = collect(self.c, "pair_done")
        self.assertEqual(got, [("192.168.1.5", 4000, "ABCDEFGHJKMN")])
        self.assertTrue(done and done[0]["ok"])

    def test_cancel_ends_pairing_and_adds_nothing(self):
        self.c, store = make(pair_command=agent.pair_command, headset_address="192.168.1.5")
        self.c.begin_pair()
        self.assertTrue(wait_for(lambda: self.c._pair_tasks))
        self.c.cancel_pair()
        done = collect(self.c, "pair_done")
        self.assertTrue(done)
        self.assertFalse(done[0]["ok"])
        self.assertEqual(store.list(), ["Quest"])  # unchanged: the fake store only holds its original entry
        self.assertEqual(store.added, [])

    def test_times_out(self):
        self.c, store = make(timeout=0.2, headset_address="192.168.1.5")
        self.c.begin_pair()
        done = collect(self.c, "pair_done")
        self.assertTrue(done)
        self.assertFalse(done[0]["ok"])
        self.assertIn("timed out", done[0]["text"])
        self.assertEqual(store.added, [])

    def test_default_timeout_is_120_seconds(self):
        self.c = controller.AgentController()
        self.assertEqual(self.c._pair_timeout, 120.0)

    def test_prompt_is_forwarded_and_wrong_code_does_not_pair(self):
        self.c, store = make(headset_address="192.168.1.5")
        self.c.begin_pair()
        prompts = collect(self.c, "pair_prompt")
        self.assertIn("code", prompts[0]["text"])
        self.c.submit_code("nope")
        says = collect(self.c, "pair_say")
        self.assertTrue(says)
        self.c.cancel_pair()
        collect(self.c, "pair_done")

    def test_second_begin_is_ignored_while_running(self):
        calls = []

        async def slow(*a, **kw):
            calls.append(1)
            await asyncio.sleep(10)
            return 0

        self.c, _ = make(pair_command=slow)
        self.c.begin_pair()
        self.c.begin_pair()
        self.assertTrue(wait_for(lambda: calls))
        time.sleep(0.1)
        self.assertEqual(len(calls), 1)
        self.c.cancel_pair()

    def test_cancel_right_after_begin_means_pair_command_never_runs(self):
        calls = []

        async def spy(*a, **kw):
            calls.append(1)
            return 0

        self.c, store = make(pair_command=spy)
        for _ in range(20):
            self.c.begin_pair()
            self.c.cancel_pair()
        time.sleep(0.3)
        self.assertEqual(calls, [])
        self.assertTrue(wait_for(lambda: not self.c._pair_tasks))
        self.assertIsNone(self.c._pair_session)

    def test_events_are_tagged_and_reopen_gets_a_new_generation(self):
        async def never(*a, **kw):
            await asyncio.sleep(10)

        self.c, _ = make(pair_command=never)
        first = self.c.begin_pair()
        self.assertEqual(self.c.begin_pair(), first)  # still running: same one
        self.c.cancel_pair()
        second = self.c.begin_pair()
        self.assertNotEqual(first, second)
        self.c.cancel_pair()
        done = []
        wait_for(lambda: done.extend(e for e in self.c.events() if e["type"] == "pair_done") or len(done) >= 1)
        self.assertTrue(all(e["gen"] in (first, second) for e in done))
        self.assertTrue(all(not e["ok"] for e in done))

    def test_shutdown_waits_for_the_pair_task(self):
        async def never(*a, **kw):
            await asyncio.sleep(10)

        self.c, _ = make(pair_command=never)
        self.c.begin_pair()
        self.assertTrue(wait_for(lambda: self.c._pair_tasks))
        task = next(iter(self.c._pair_tasks))
        self.c.shutdown()
        self.assertTrue(task.done())

    def test_cli_hints_are_mapped_for_the_window_only(self):
        self.assertNotIn("--", controller.gui_hint(agent.PAIR_NOT_FOUND))
        self.assertIn("Ctrl+C", agent.PAIR_NOT_FOUND)  # the CLI text itself is untouched
        for text in ("No headset showed a pairing code. Open Laptop > Pair new laptop on the headset, or use --headset.",
                     "Run again with --headset IP[:PORT] to choose the headset.",
                     "Open Laptop > Pair new laptop on it and run --pair again.",
                     "Revoke it first (--revoke NAME)."):
            self.assertNotIn("--", controller.gui_hint(text))

    def test_no_pairing_without_begin(self):
        self.c, store = make()
        self.c.submit_code("ABCD-EFGH-JKMN")  # nobody is waiting: ignored
        self.c.cancel_pair()
        self.assertEqual(self.c.events(), [])

    def test_no_tkinter_in_controller_imports(self):
        code = "import controller, settings, sys; sys.exit(1 if 'tkinter' in sys.modules else 0)"
        self.assertEqual(subprocess.run([sys.executable, "-c", code]).returncode, 0)


if __name__ == "__main__":
    unittest.main()
