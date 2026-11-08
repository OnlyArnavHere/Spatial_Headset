"""The keyboard pop-up signal: which focused elements count as text fields, and the "text-focus" message the session
sends while the headset can type. No real UI Automation (except one read-only live check on Windows).

  python -m unittest test_textfocus -v        (from Tools/LaptopAgent)
"""
import asyncio
import json
import sys
import unittest
from unittest import mock

import dispatch
import session
import textfocus
from textfocus import CONTROL_COMBO_BOX, CONTROL_DOCUMENT, CONTROL_EDIT


class RuleTests(unittest.TestCase):
    def test_edit_boxes_are_text_fields_unless_read_only_or_disabled(self):
        self.assertTrue(textfocus.is_text_field(CONTROL_EDIT, True, False))
        self.assertTrue(textfocus.is_text_field(CONTROL_EDIT, False, None))  # an edit without a Value pattern
        self.assertFalse(textfocus.is_text_field(CONTROL_EDIT, True, True))
        self.assertFalse(textfocus.is_text_field(CONTROL_EDIT, True, False, enabled=False))

    def test_writable_values_count_and_a_read_only_page_does_not(self):
        self.assertTrue(textfocus.is_text_field(CONTROL_COMBO_BOX, True, False))   # a search box you type into
        self.assertTrue(textfocus.is_text_field(CONTROL_DOCUMENT, True, False))    # contenteditable
        self.assertFalse(textfocus.is_text_field(CONTROL_DOCUMENT, True, True))    # a web page
        self.assertFalse(textfocus.is_text_field(50000, False, None))              # a button
        self.assertFalse(textfocus.is_text_field(None, None, None))

    @unittest.skipUnless(sys.platform == "win32", "UI Automation is Windows only")
    def test_live_check_answers_without_raising(self):
        self.assertIn(textfocus.editable(), (True, False))  # this thread, whatever COM mode it already has
        result = session._TEXT_FOCUS_POOL.submit(textfocus.editable).result(10)  # the agent's own thread
        self.assertIn(result, (True, False))


def click(slot=0):
    return json.dumps({"type": "down", "x": 0.5, "y": 0.5, "button": "left"})


class SessionTests(unittest.IsolatedAsyncioTestCase):
    def make(self, answers, **kw):
        options = dict(input_enabled=True, keyboard_enabled=True)
        options.update(kw)
        s = session.LinkSession(mock.Mock(), 8554, **options)
        sent = []
        s._send = sent.append
        answers = list(answers)
        s.text_focus_probe = lambda: answers.pop(0) if len(answers) > 1 else answers[0]
        s.dispatcher.on_input = lambda *a: None
        s.dispatcher.state = dispatch.ControlState.GRANTED
        slot = mock.Mock(index=1, input_messages=0)
        self.addAsyncCleanup(s.close)
        return s, sent, slot

    async def wait_for_messages(self, sent, count, seconds=3.0):
        for _ in range(int(seconds / 0.02)):
            if len([m for m in sent if m["type"] == "text-focus"]) >= count:
                break
            await asyncio.sleep(0.02)
        return [m for m in sent if m["type"] == "text-focus"]

    async def test_focus_into_and_out_of_a_text_field_is_reported_with_the_clicked_slot(self):
        with mock.patch.object(session, "TEXT_FOCUS_POLL_S", 0.01):
            s, sent, slot = self.make([True, True, False])
            s._on_input(slot, click())
            got = await self.wait_for_messages(sent, 2)
        self.assertEqual(got[:2], [{"type": "text-focus", "editable": True, "slot": 1},
                                   {"type": "text-focus", "editable": False, "slot": 1}])

    async def test_nothing_before_the_first_click(self):
        with mock.patch.object(session, "TEXT_FOCUS_POLL_S", 0.01):
            s, sent, slot = self.make([True])
            s._on_input(slot, json.dumps({"type": "move", "x": 0.1, "y": 0.1}))
            await asyncio.sleep(0.1)
        self.assertEqual(sent, [])

    async def test_nothing_without_keyboard_control(self):
        with mock.patch.object(session, "TEXT_FOCUS_POLL_S", 0.01):
            s, sent, slot = self.make([True])
            s.dispatcher.state = dispatch.ControlState.VIEW_ONLY
            s._on_input(slot, click())
            await asyncio.sleep(0.1)
        self.assertEqual(sent, [])

    async def test_no_watcher_without_keyboard_input(self):
        s, sent, slot = self.make([True], keyboard_enabled=False)
        s._on_input(slot, click())
        self.assertIsNone(s._text_focus_task)

    async def test_an_os_that_cannot_tell_stops_the_watcher(self):
        with mock.patch.object(session, "TEXT_FOCUS_POLL_S", 0.01):
            s, sent, slot = self.make([None])
            s._on_input(slot, click())
            await asyncio.wait_for(s._text_focus_task, 2)
        self.assertEqual(sent, [])


if __name__ == "__main__":
    unittest.main()
