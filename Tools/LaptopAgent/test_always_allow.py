"""Tests for "Always allow this headset for control": the mark in pairs.json, the approver skipping the box only for
the exact key that was marked, the third answer of every OS's box, and revoking from the app and the command line.
No box is ever shown (hostos.open_prompt is replaced) except the Windows smoke test, which only runs with
SL_UI_TESTS=1 and closes itself.

  python -m unittest test_always_allow -v        (from Tools/LaptopAgent)
"""
import asyncio
import contextlib
import io
import json
import os
import sys
import tempfile
import types
import unittest
from pathlib import Path
from unittest import mock

import _test_keychain  # noqa: F401  (a throwaway Keychain entry on macOS)
import agent
import approval
import dispatch
import hostos
import pairing
from approval import ALWAYS_TEXT, LaptopApprover
from hostos import _linux_pure, _macos_pure

KEY_A, KEY_B = b"\x30\x59" + b"A" * 89, b"\x30\x59" + b"B" * 89
CONSENT = {"consent": dispatch.CONSENT}


class StoreCase(unittest.TestCase):
    def setUp(self):
        self.home = Path(self.enterContext(tempfile.TemporaryDirectory()))
        self.store = pairing.PairStore(self.home)
        self.store.add("Phone", KEY_A)
        self.store.add("Other", KEY_B)


class Store(StoreCase):
    def test_off_for_new_pairs_and_saved_when_set(self):
        self.assertFalse(self.store.always_allowed(KEY_A))
        self.assertTrue(self.store.set_always_allow(KEY_A, True))
        self.assertTrue(pairing.PairStore(self.home).always_allowed(KEY_A), "persisted")
        self.assertFalse(self.store.always_allowed(KEY_B), "only that headset")
        doc = json.loads((self.home / pairing.PAIRS_FILE).read_text(encoding="utf-8"))
        self.assertEqual([p["always_allow"] for p in doc["pairs"]], [True, False])

    def test_by_name_and_unknown(self):
        self.assertTrue(self.store.set_always_allow("phone", True))
        self.assertTrue(self.store.always_allowed(KEY_A))
        self.assertTrue(self.store.set_always_allow(" PHONE ", False))
        self.assertFalse(self.store.always_allowed(KEY_A))
        self.assertFalse(self.store.set_always_allow("Nobody", True))
        self.assertFalse(self.store.set_always_allow(b"\x30" * 91, True))

    def test_only_the_full_key_counts(self):
        self.store.set_always_allow(KEY_A, True)
        self.assertFalse(self.store.always_allowed(KEY_A[:-1]))
        self.assertFalse(self.store.always_allowed(KEY_A + b"\x00"))
        self.assertFalse(self.store.always_allowed(b""))

    def test_revoke_and_re_pair_clear_it(self):
        self.store.set_always_allow(KEY_A, True)
        self.store.revoke("Phone")
        self.store.add("Phone", KEY_A)
        self.assertFalse(self.store.always_allowed(KEY_A), "a forgotten pair comes back without the mark")
        self.store.set_always_allow(KEY_A, True)
        self.store.add("Phone", KEY_A)  # re-paired in place
        self.assertFalse(self.store.always_allowed(KEY_A))

    def test_old_files_and_odd_values(self):
        path = self.home / pairing.PAIRS_FILE
        doc = json.loads(path.read_text(encoding="utf-8"))
        for p in doc["pairs"]:
            del p["always_allow"]
        path.write_text(json.dumps(doc), encoding="utf-8")
        self.assertFalse(self.store.always_allowed(KEY_A), "a file from before has no marks")
        for value in ("true", 1, "yes", [True], None):
            doc["pairs"][0]["always_allow"] = value
            path.write_text(json.dumps(doc), encoding="utf-8")
            self.assertFalse(self.store.always_allowed(KEY_A), f"only JSON true counts, not {value!r}")


class FakeBox:
    def __init__(self, answer):
        self.answer = answer
        self.shown = []

    def open_prompt(self, text, caption, timeout_ms, **kw):
        self.shown.append((text, caption, timeout_ms, kw))
        return types.SimpleNamespace(wait=lambda: self.answer, dismiss=lambda: True)


class Approver(StoreCase, unittest.IsolatedAsyncioTestCase):
    def info(self, key=KEY_A):
        return {"name": "Phone", "fingerprint": "AB:CD", "spki": key}

    async def ask(self, answer, info=None, store=True):
        box = FakeBox(answer)
        with mock.patch.object(hostos, "open_prompt", box.open_prompt):
            result = await LaptopApprover(store=self.store if store else None)(info or self.info())
        return result, box

    async def test_box_offers_always_and_remembers_it(self):
        result, box = await self.ask("always")
        self.assertTrue(result)
        self.assertEqual(box.shown[0][3], {"always": ALWAYS_TEXT})
        self.assertTrue(self.store.always_allowed(KEY_A))
        with self.assertLogs("approval", "WARNING") as logs:
            result, box = await self.ask("no")
        self.assertTrue(result, "granted without asking")
        self.assertEqual(box.shown, [], "no box at all")
        self.assertIn("CONTROL ALLOWED WITHOUT ASKING", logs.output[0])

    async def test_yes_is_only_this_time(self):
        self.assertTrue((await self.ask("yes"))[0])
        self.assertFalse(self.store.always_allowed(KEY_A))
        _, box = await self.ask("no")
        self.assertEqual(len(box.shown), 1, "asked again")

    async def test_no_and_timeout_mark_nothing(self):
        self.assertFalse((await self.ask("no"))[0])
        with self.assertRaises(asyncio.TimeoutError):
            await self.ask("timeout")
        self.assertFalse(self.store.always_allowed(KEY_A))

    async def test_another_headset_is_still_asked(self):
        self.store.set_always_allow(KEY_A, True)
        result, box = await self.ask("no", self.info(KEY_B))
        self.assertFalse(result)
        self.assertEqual(len(box.shown), 1)

    async def test_no_store_or_no_key_means_no_offer(self):
        _, box = await self.ask("yes", store=False)
        self.assertEqual(box.shown[0][3], {})
        result, box = await self.ask("always", {"name": "Phone", "fingerprint": "AB:CD"})
        self.assertEqual(box.shown[0][3], {})
        self.assertFalse(result, "'always' from a box that didn't offer it is not a yes")
        self.assertFalse(self.store.always_allowed(KEY_A))

    async def test_unreadable_store_asks(self):
        (self.home / pairing.PAIRS_FILE).write_text("{broken", encoding="utf-8")
        result, box = await self.ask("yes")
        self.assertTrue(result)
        self.assertEqual(len(box.shown), 1)

    async def test_revoked_mark_asks_again(self):
        self.store.set_always_allow(KEY_A, True)
        self.store.set_always_allow("Phone", False)
        _, box = await self.ask("no")
        self.assertEqual(len(box.shown), 1)

    async def test_the_gates_before_the_approver_still_hold(self):
        """Always allow skips only the laptop's box: no --input, cast only, the kill switch and a missing headset
        consent still refuse."""
        self.store.set_always_allow(KEY_A, True)
        approver = LaptopApprover(store=self.store)
        cases = [(dict(input_enabled=False), CONSENT, "input off"),
                 (dict(input_enabled=True, cast_only=True), CONSENT, "cast only"),
                 (dict(input_enabled=True, revoked=True), CONSENT, "stopped"),
                 (dict(input_enabled=True), {"consent": "nope"}, "no consent")]
        for kw, message, reason in cases:
            sent = []
            d = dispatch.InputDispatcher(1, sent.append, lambda k, m: None, approver=approver, headset=self.info(), **kw)
            await d.request_control(message)
            self.assertEqual(sent, [{"type": "control-denied", "reason": reason}])
        sent = []
        d = dispatch.InputDispatcher(1, sent.append, lambda k, m: None, input_enabled=True, approver=approver,
                                     headset=self.info())
        await d.request_control(CONSENT)
        self.assertEqual(sent[0]["type"], "control-granted")

    async def test_session_passes_the_handshake_key(self):
        import session
        from streams import StreamManager
        link = session.LinkSession(StreamManager(lambda t, p: (None, (0, 0))), 8554, max_windows=1)
        link.set_headset("Phone", "AB:CD", KEY_A)
        self.assertEqual(link.dispatcher.headset["spki"], KEY_A)
        self.assertNotIn(repr(KEY_A), approval.prompt_text(link.dispatcher.headset, False))
        await link.close()


class Answers(unittest.TestCase):
    def test_windows_task_dialog(self):
        if sys.platform != "win32":
            self.skipTest("Windows only")
        import winapi
        self.assertEqual(winapi.task_dialog_answer(winapi._IDYES, True, False), "always")
        self.assertEqual(winapi.task_dialog_answer(winapi._IDYES, False, False), "yes")
        self.assertEqual(winapi.task_dialog_answer(winapi._IDNO, True, False), "no", "ticked and No is still No")
        self.assertEqual(winapi.task_dialog_answer(2, True, False), "no", "closed (Esc) is No")
        self.assertEqual(winapi.task_dialog_answer(winapi._IDYES, True, True), "timeout")

    def test_windows_falls_back_to_the_plain_box(self):
        if sys.platform != "win32":
            self.skipTest("Windows only")
        import winapi
        with mock.patch.object(winapi, "ask_with_always", return_value=None), \
                mock.patch.object(winapi.user32, "MessageBoxTimeoutW", create=True,
                                  return_value=winapi._IDYES) as box:
            self.assertEqual(winapi.ask_yes_no("t", "c", 1000, always="x"), "yes")
            box.assert_called_once()

    def test_macos_dialog(self):
        argv = _macos_pure.osascript_argv("text", "title", 30, always=True)
        self.assertEqual(argv[:3], ["osascript", "-e", _macos_pure.SCRIPT_ALWAYS])
        self.assertEqual(argv[3:], ["--", "text", "title", "30", "Always allow"])
        self.assertIn('default button "No" cancel button "No"', _macos_pure.SCRIPT_ALWAYS)
        self.assertNotIn("text", _macos_pure.SCRIPT_ALWAYS.replace("(item", ""), "no text inside the script")
        self.assertEqual(_macos_pure.osascript_argv("t", "c", 5)[2], _macos_pure.SCRIPT)
        parse = _macos_pure.parse_dialog
        self.assertEqual(parse(0, "button returned:Always allow, gave up:false\n", ""), "always")
        self.assertEqual(parse(0, "button returned:Always allow", ""), "always")
        self.assertEqual(parse(0, "button returned:Yes, gave up:false", ""), "yes")
        self.assertEqual(parse(0, "button returned:Always allowed, gave up:false", ""), "no")
        self.assertEqual(parse(0, "button returned:, gave up:true", ""), "timeout")
        self.assertEqual(parse(1, "", "User canceled. (-128)"), "no")

    def test_linux_dialog(self):
        argv = _linux_pure.zenity_argv("text", "title", 30, always=True)
        self.assertIn("--extra-button=Always allow", argv)
        self.assertIn("--default-cancel", argv)
        self.assertNotIn("--extra-button=Always allow", _linux_pure.zenity_argv("t", "c", 30))
        self.assertEqual(_linux_pure.tk_argv("d.py", "t", "c", 30, always=True)[-1], "always")
        answer = _linux_pure.answer_for
        self.assertEqual(answer(1, "Always allow\n", True), "always")
        self.assertEqual(answer(2, "", True), "always")
        self.assertEqual(answer(1, "Always allow\n", False), "no", "not offered: not accepted")
        self.assertEqual(answer(2, "", False), "no")
        self.assertEqual(answer(1, "", True), "no")
        self.assertEqual(answer(0, "", True), "yes")
        self.assertEqual(answer(5, "", True), "timeout")


class Revoking(StoreCase):
    def run_cli(self, *argv):
        out = io.StringIO()
        with mock.patch.object(agent.identity, "default_home", return_value=self.home), contextlib.redirect_stdout(out):
            code = agent._pairs_command(agent.parse_args(list(argv)))
        return code, out.getvalue()

    def test_pairs_shows_it_and_revoke_always_clears_it(self):
        self.store.set_always_allow(KEY_A, True)
        code, out = self.run_cli("--pairs")
        self.assertEqual(code, 0)
        phone = next(line for line in out.splitlines() if line.startswith("Phone"))
        other = next(line for line in out.splitlines() if line.startswith("Other"))
        self.assertIn("ALWAYS ALLOWED", phone)
        self.assertNotIn("ALWAYS", other)
        code, out = self.run_cli("--revoke-always", "phone")
        self.assertEqual(code, 0)
        self.assertFalse(self.store.always_allowed(KEY_A))
        self.assertIsNotNone(pairing.pinned(self.store, KEY_A), "the pair itself stays")
        self.assertEqual(self.run_cli("--revoke-always", "nobody")[0], 1)

    def test_revoke_forgets_the_pair_and_the_mark(self):
        self.store.set_always_allow(KEY_A, True)
        self.assertEqual(self.run_cli("--revoke", "Phone")[0], 0)
        self.assertIsNone(pairing.pinned(self.store, KEY_A))
        self.assertFalse(self.store.always_allowed(KEY_A))

    def test_controller_and_window_text(self):
        import controller
        self.store.set_always_allow(KEY_A, True)
        c = controller.AgentController.__new__(controller.AgentController)
        c._store, c._store_factory = self.store, lambda: self.store
        self.assertTrue(c.stop_always_allowing("Phone"))
        self.assertFalse(self.store.always_allowed(KEY_A))
        self.assertFalse(c.stop_always_allowing("Nobody"))
        try:
            from gui.main_window import pair_text
        except ImportError as e:  # no Tk on this Python
            self.skipTest(f"no tkinter: {e}")
        self.store.set_always_allow(KEY_A, True)
        texts = {p.name: pair_text(p) for p in self.store.list()}
        self.assertIn("ALWAYS ALLOWED", texts["Phone"])
        self.assertNotIn("ALWAYS", texts["Other"])

    def test_controller_default_approver_uses_the_store(self):
        import controller
        c = controller.AgentController(store_factory=lambda: self.store)
        approver = c._approver_factory(True)
        self.assertIsInstance(approver, LaptopApprover)
        self.assertIs(approver.store, self.store)
        self.assertTrue(approver.keyboard)


@unittest.skipUnless(sys.platform == "win32" and os.environ.get("SL_UI_TESTS") == "1",
                     "set SL_UI_TESTS=1 on Windows to show a real task dialog")
class RealTaskDialog(unittest.TestCase):
    def test_closes_itself(self):
        prompt = hostos.open_prompt("Spatial Launcher test (closes itself)", "Spatial Launcher", 1000, always=ALWAYS_TEXT)
        self.assertEqual(prompt.wait(), "timeout")


if __name__ == "__main__":
    unittest.main()
