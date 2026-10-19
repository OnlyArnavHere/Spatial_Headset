"""Unit tests for the input dispatcher: routing per slot, the control state machine, cast-only, rate limits, malformed
input and releasing held input. No network and no real input: every Windows call is replaced by a recorder.

  python -m unittest test_dispatch -v        (from Tools/LaptopAgent)
"""
import asyncio
import json
import unittest
from unittest import mock

import dispatch
import keys
import session
import winapi
from dispatch import ControlState, InputDispatcher
from streams import Stream, Target

WINDOWS = {0: (42, winapi.Rect(100, 100, 900, 600)), 1: (43, winapi.Rect(1000, 200, 1400, 500))}


def window_stream(handle: int, rect: winapi.Rect, usable: bool = True) -> Stream:
    target = Target(f"window:{handle}", "window", handle, f"Window {handle}", f"window {handle}", f"hwnd={handle}",
                    rect=lambda: rect, usable=lambda: usable)
    return Stream(f"window-{handle}", target, (rect.width, rect.height), process=None)


class Recorder:
    """Stands in for every winapi call that could change anything on this machine."""

    def __init__(self) -> None:
        self.calls: list[tuple] = []
        self.focus_ok = True

    def install(self, case: unittest.TestCase) -> None:
        def record(name, result=True):
            def call(*args, **kwargs):
                self.calls.append((name,) + args)
                return result() if callable(result) else result
            return call

        for name in ("key", "mouse_move", "mouse_button", "mouse_wheel", "type_text", "raise_window"):
            case.enterContext(mock.patch.object(winapi, name, record(name)))
        case.enterContext(mock.patch.object(winapi, "focus_window", record("focus_window", lambda: self.focus_ok)))
        case.enterContext(mock.patch.object(winapi, "window_at", lambda x, y: 42 if x < 1000 else 43))

    def of(self, name: str) -> list[tuple]:
        return [c for c in self.calls if c[0] == name]


def key(code, down):
    return {"type": "key", "code": code, "down": down}


def down(x=0.5, y=0.5, button="left"):
    return {"type": "down", "x": x, "y": y, "button": button}


class Base(unittest.TestCase):
    def setUp(self):
        self.rec = Recorder()
        self.rec.install(self)
        self.now = [0.0]

    def make(self, granted: bool = True, **kw) -> InputDispatcher:
        kw.setdefault("input_enabled", True)
        kw.setdefault("keyboard_enabled", True)
        self.control, self.inputs = [], []
        d = InputDispatcher(2, self.control.append, lambda k, m: self.inputs.append((k, m)), clock=lambda: self.now[0], **kw)
        for k, (handle, rect) in WINDOWS.items():
            d.attach(k, window_stream(handle, rect))
        if granted:
            d.state = ControlState.GRANTED
        return d

    def send(self, d, slot, *messages):
        for message in messages:
            d.on_input(slot, message if isinstance(message, (str, bytes)) else json.dumps(message))

    def sent(self, kind):
        return [m for _, m in self.inputs if m["type"] == kind]


class RoutingTests(Base):
    def test_input_reaches_its_own_window(self):
        d = self.make()
        self.send(d, 0, down(), {"type": "up", "x": 0.5, "y": 0.5, "button": "left"}, key("A", True), key("A", False),
                  {"type": "text", "text": "zero"})
        self.send(d, 1, down(), {"type": "up", "x": 0.5, "y": 0.5, "button": "left"}, key("B", True), key("B", False),
                  {"type": "text", "text": "one"})
        self.assertEqual(self.rec.of("mouse_button"), [("mouse_button", 500, 350, "left", True),
                                                       ("mouse_button", 500, 350, "left", False),
                                                       ("mouse_button", 1200, 350, "left", True),
                                                       ("mouse_button", 1200, 350, "left", False)])
        self.assertEqual([c[1] for c in self.rec.of("focus_window")], [42, 42, 43, 43])
        self.assertEqual(self.rec.of("key"), [("key", 0x41, True, False), ("key", 0x41, False, False),
                                              ("key", 0x42, True, False), ("key", 0x42, False, False)])
        self.assertEqual(self.rec.of("type_text"), [("type_text", "zero"), ("type_text", "one")])

    def test_no_window_attached_drops_everything(self):
        d = self.make()
        d.detach(0)
        self.send(d, 0, down(), key("A", True), {"type": "text", "text": "x"}, {"type": "move", "x": 0, "y": 0})
        self.assertEqual(self.rec.calls, [])
        self.send(d, 1, {"type": "move", "x": 0, "y": 0})
        self.assertEqual(self.rec.of("mouse_move"), [("mouse_move", 1000, 200)])

    def test_scroll_and_covered_click(self):
        d = self.make()
        self.send(d, 0, {"type": "scroll", "x": 0.5, "y": 0.5, "delta": 99})
        self.assertEqual(self.rec.of("mouse_wheel"), [("mouse_wheel", 500, 350, 10.0)])
        with mock.patch.object(winapi, "window_at", lambda x, y: 999), self.assertLogs("input", "WARNING"):
            self.send(d, 0, down())
        self.assertEqual(len(self.rec.of("mouse_button")), 0)

    def test_dry_run_acks_go_back_on_the_slots_own_channel(self):
        d = self.make(dry_run=True)
        self.send(d, 1, key("A", True), key("A", False), {"type": "text", "text": "Hi"}, down())
        self.assertEqual({k for k, _ in self.inputs}, {1})
        self.assertEqual([(m["event"], m.get("code"), m.get("down"), m["path"]) for m in self.sent("ack")],
                         [("key", "A", True, "window-43"), ("key", "A", False, "window-43"),
                          ("text", None, None, "window-43"), ("down", None, None, "window-43")])
        self.assertEqual(self.rec.calls, [])


class ViewOnlyTests(Base):
    def test_everything_but_media_is_dropped_before_a_grant(self):
        d = self.make(granted=False)
        self.send(d, 0, down(), {"type": "up", "x": 0, "y": 0}, {"type": "move", "x": 0, "y": 0},
                  {"type": "scroll", "x": 0, "y": 0, "delta": 1}, key("A", True), {"type": "text", "text": "x"},
                  {"type": "media", "action": "play_pause"})
        self.assertEqual(self.rec.calls, [])  # media too: full mode needs a grant

    def test_keyboard_needs_the_keyboard_flag(self):
        d = self.make(keyboard_enabled=False)
        self.send(d, 0, key("A", True), {"type": "text", "text": "x"}, {"type": "media", "action": "mute"}, down())
        self.assertEqual(len(self.rec.of("key")) + len(self.rec.of("type_text")), 0)
        self.assertEqual(len(self.rec.of("mouse_button")), 1)


def battery() -> list[dict]:
    """Everything a cast window must never be able to do."""
    out: list[dict] = []
    for code in ["A", "W", "L", "R", "Q", "Escape", "F4", "F5", "F11", "Tab", "Enter", "Space", "Backspace", "Delete",
                 "Left", "Right", "Up", "Down", "Home", "End", "PageUp", "PageDown"]:
        out += [key(code, True), key(code, False)]
    for combo in (["Alt", "F4"], ["Ctrl", "W"], ["Alt", "Tab"], ["Ctrl", "Shift", "Escape"], ["Ctrl", "L"],
                  ["Alt", "Left"], ["Ctrl", "Tab"], ["Ctrl", "Alt", "Delete"], ["Shift", "F10"]):
        out += [key(c, True) for c in combo] + [key(c, False) for c in reversed(combo)]
    for code in ["LWin", "RWin", "Win", "Apps", "PrintScreen", "MediaPlayPause", "VolumeUp", "Sleep"]:
        out += [key(code, True), key(code, False)]
    out += [{"type": "text", "text": "hello"}, {"type": "text", "text": "rundll32"}, {"type": "move", "x": 0.5, "y": 0.5},
            down(0.1, 0.1), {"type": "up", "x": 0.1, "y": 0.1, "button": "left"}, down(0.9, 0.9, "right"),
            {"type": "up", "x": 0.9, "y": 0.9, "button": "right"}, {"type": "scroll", "x": 0.5, "y": 0.5, "delta": 3},
            {"type": "media", "action": "power"}, {"type": "media", "action": "PLAY_PAUSE"},
            {"type": "media", "action": None}, {"type": "media"},
            {"type": "keys", "codes": ["Win", "R"]}, {"type": "launch", "app": "cmd"}]
    return out


class CastOnlyTests(Base):
    def make_cast(self):
        approver = mock.AsyncMock(side_effect=AssertionError("the approver must not be called in cast-only mode"))
        return self.make(granted=False, input_enabled=False, keyboard_enabled=False, cast_only=True, approver=approver), approver

    def test_the_whole_battery_is_dropped_and_counted(self):
        d, approver = self.make_cast()
        messages = battery()
        for slot in (0, 1):
            self.send(d, slot, *messages)
        self.assertEqual(self.rec.calls, [])
        self.assertEqual(sum(d.dropped.values()), 2 * len(messages), dict(d.dropped))
        self.assertEqual(d.dropped["key"], 2 * sum(1 for m in messages if m["type"] == "key"))
        self.assertIn("media:power", d.dropped)
        approver.assert_not_called()

    def test_each_media_action_is_one_press_and_release(self):
        d, approver = self.make_cast()
        for action in keys.MEDIA_ACTIONS:
            self.send(d, 0, {"type": "media", "action": action})
        expected = []
        for vk, extended in keys.MEDIA_ACTIONS.values():
            expected += [("key", vk, True, extended), ("key", vk, False, extended)]
        self.assertEqual(self.rec.of("key"), expected)
        self.assertFalse({c[1] for c in self.rec.of("key")} & {0x5B, 0x5C, 0x5D, 0x10, 0x11, 0x12})
        self.assertEqual([c[0] for c in self.rec.calls if c[0] != "key"], ["focus_window"] * len(keys.WINDOW_MEDIA_ACTIONS))
        approver.assert_not_called()

    def test_seek_focuses_the_slots_window(self):
        d, _ = self.make_cast()
        self.send(d, 1, {"type": "media", "action": "seek_forward"})
        self.send(d, 0, {"type": "media", "action": "seek_back"}, {"type": "media", "action": "mute"})
        self.assertEqual(self.rec.calls, [("focus_window", 43), ("key", 0x27, True, True), ("key", 0x27, False, True),
                                          ("focus_window", 42), ("key", 0x25, True, True), ("key", 0x25, False, True),
                                          ("key", 0xAD, True, True), ("key", 0xAD, False, True)])

    def test_media_is_acknowledged_in_dry_run_and_stops_after_the_kill_switch(self):
        d, _ = self.make_cast()
        d.dry_run = True
        self.send(d, 0, {"type": "media", "action": "next"})
        self.assertEqual(self.sent("ack"), [{"type": "ack", "event": "media", "action": "next", "path": "window-42"}])
        d.dry_run = False
        d.revoke("kill switch")
        self.send(d, 0, {"type": "media", "action": "next"})
        self.assertEqual(self.rec.calls, [])

    def test_full_mode_media_needs_a_grant_and_the_keyboard(self):
        d = self.make(granted=False)
        self.send(d, 0, {"type": "media", "action": "mute"})
        d.state = ControlState.GRANTED
        self.send(d, 0, {"type": "media", "action": "mute"})
        self.assertEqual(self.rec.of("key"), [("key", 0xAD, True, True), ("key", 0xAD, False, True)])


class ControlTests(Base, unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        Base.setUp(self)

    async def test_cast_only_request_is_denied_without_asking(self):
        approver = mock.AsyncMock()
        d = self.make(granted=False, input_enabled=False, cast_only=True, approver=approver)
        await d.request_control({"type": "control-request", "consent": dispatch.CONSENT})
        self.assertEqual(self.control, [{"type": "control-denied", "reason": "cast only"}])
        approver.assert_not_called()

    async def test_input_off(self):
        approver = mock.AsyncMock()
        d = self.make(granted=False, input_enabled=False, approver=approver)
        await d.request_control({"consent": dispatch.CONSENT})
        self.assertEqual(self.control, [{"type": "control-denied", "reason": "input off"}])
        approver.assert_not_called()

    async def test_missing_or_wrong_consent(self):
        approver = mock.AsyncMock(return_value=True)
        d = self.make(granted=False, approver=approver)
        for message in ({}, {"consent": "yes"}, {"consent": None}, {"consent": ["full control"]}):
            await d.request_control(message)
        self.assertEqual(self.control, [{"type": "control-denied", "reason": "no consent"}] * 4)
        approver.assert_not_called()
        self.assertIs(d.state, ControlState.VIEW_ONLY)

    async def test_grant_passes_the_headset_info_to_the_approver(self):
        approver = mock.AsyncMock(return_value=True)
        d = self.make(granted=False, approver=approver, headset={"name": "Phone", "fingerprint": "AB:CD"})
        await d.request_control({"consent": dispatch.CONSENT})
        approver.assert_awaited_once_with({"name": "Phone", "fingerprint": "AB:CD"})
        self.assertEqual(self.control, [{"type": "control-granted", "keyboard": True}])
        self.assertIs(d.state, ControlState.GRANTED)

    async def test_default_approver_grants_and_a_no_denies(self):
        d = self.make(granted=False)
        await d.request_control({"consent": dispatch.CONSENT})
        self.assertIs(d.state, ControlState.GRANTED)
        d = self.make(granted=False, approver=mock.AsyncMock(return_value=False))
        await d.request_control({"consent": dispatch.CONSENT})
        self.assertEqual(self.control, [{"type": "control-denied", "reason": "denied"}])
        self.assertIs(d.state, ControlState.VIEW_ONLY)

    async def test_approver_timeout(self):
        async def never(info):
            await asyncio.sleep(60)

        d = self.make(granted=False, approver=never)
        with mock.patch.object(dispatch, "APPROVAL_BACKSTOP_S", 0.05):
            await d.request_control({"consent": dispatch.CONSENT})
        self.assertEqual(self.control, [{"type": "control-denied", "reason": "timeout"}])
        self.assertIs(d.state, ControlState.VIEW_ONLY)

    async def test_a_second_request_in_flight_is_busy(self):
        gate = asyncio.Event()

        async def approve(info):
            await gate.wait()
            return True

        d = self.make(granted=False, approver=approve)
        first = asyncio.ensure_future(d.request_control({"consent": dispatch.CONSENT}))
        await asyncio.sleep(0)
        self.assertIs(d.state, ControlState.REQUESTED)
        await d.request_control({"consent": dispatch.CONSENT})
        self.assertEqual(self.control, [{"type": "control-denied", "reason": "busy"}])
        gate.set()
        await first
        self.assertEqual(self.control[-1], {"type": "control-granted", "keyboard": True})

    async def test_release_while_asking_ignores_the_answer(self):
        gate = asyncio.Event()

        async def approve(info):
            await gate.wait()
            return True

        d = self.make(granted=False, approver=approve)
        task = asyncio.ensure_future(d.request_control({"consent": dispatch.CONSENT}))
        await asyncio.sleep(0)
        d.release_control()
        gate.set()
        await task
        self.assertIs(d.state, ControlState.VIEW_ONLY)
        self.assertEqual(self.control, [])

    async def test_release_and_kill_switch_release_held_input_and_nothing_follows(self):
        for how in ("release", "revoke"):
            self.rec.calls.clear()
            d = self.make()
            self.send(d, 0, key("Ctrl", True), down())
            self.send(d, 1, key("Shift", True))
            if how == "release":
                d.release_control()
                self.assertIs(d.state, ControlState.VIEW_ONLY)
            else:
                d.revoke("kill switch")
                self.assertIs(d.state, ControlState.REVOKED)
                self.assertEqual(self.control, [{"type": "control-revoked", "reason": "kill switch"}])
            self.assertEqual(self.rec.of("key")[2:], [("key", 0x11, False, False), ("key", 0x10, False, False)])
            self.assertEqual(self.rec.of("mouse_button")[-1], ("mouse_button", 500, 350, "left", False))
            before = len(self.rec.calls)
            self.send(d, 0, key("A", True), {"type": "text", "text": "x"}, down(), {"type": "media", "action": "mute"})
            self.assertEqual(len(self.rec.calls), before)
            for slot in d.slots:
                self.assertEqual((slot.held, slot.held_keys), (set(), {}))
        await d.request_control({"consent": dispatch.CONSENT})
        self.assertEqual(self.control[-1], {"type": "control-denied", "reason": "stopped"})
        d.revoke("again")
        self.assertEqual(len([m for m in self.control if m["type"] == "control-revoked"]), 1)

    async def test_a_new_session_after_the_kill_switch_starts_revoked(self):
        d = self.make(granted=False, revoked=True)
        await d.request_control({"consent": dispatch.CONSENT})
        self.assertEqual(self.control, [{"type": "control-denied", "reason": "stopped"}])


class LimitTests(Base):
    def test_flood_drops_presses_but_never_releases(self):
        d = self.make(dry_run=True)
        d.slots[0].limiter = keys.RateLimiter(rate=0.001, burst=10, clock=lambda: 0.0)
        self.send(d, 0, *[m for _ in range(30) for m in (key("A", True), key("A", False))])
        acks = self.sent("ack")
        self.assertEqual(len([a for a in acks if a["down"]]), 10)
        self.assertEqual(len([a for a in acks if not a["down"]]), 10)
        self.assertEqual(len(self.sent("throttled")), 1)
        self.assertEqual(d.slots[0].held_keys, {})
        self.now[0] += 1.5  # a notice again after a second
        self.send(d, 0, key("A", True))
        self.assertEqual(len(self.sent("throttled")), 2)

    def test_message_bucket_caps_move_floods_but_not_releases(self):
        d = self.make()
        self.send(d, 0, down())
        self.send(d, 0, *[{"type": "move", "x": 0.5, "y": 0.5}] * 2000)
        self.assertEqual(len(self.rec.of("mouse_move")), int(dispatch.MESSAGE_BURST) - 1)
        self.send(d, 0, {"type": "up", "x": 0.5, "y": 0.5, "button": "left"})
        self.assertEqual(self.rec.of("mouse_button")[-1][-1], False)
        self.assertEqual(len(self.sent("throttled")), 1)

    def test_unmatched_releases_are_throttled_but_a_held_release_still_goes_through(self):
        d = self.make()
        self.send(d, 0, down())
        up = {"type": "up", "x": 0.5, "y": 0.5, "button": "right"}
        self.send(d, 0, *[up, key("Z", False)] * 2000)
        self.assertEqual(len(self.sent("throttled")), 1)
        self.assertEqual(len(self.rec.of("mouse_button")), 1)  # only the press
        self.send(d, 0, {"type": "up", "x": 0.5, "y": 0.5, "button": "left"})
        self.assertEqual(self.rec.of("mouse_button")[-1][-1], False)
        self.assertEqual(d.slots[0].held, set())

    def test_held_key_cap(self):
        d = self.make(dry_run=True)
        self.send(d, 0, *[key(c, True) for c in "QWERTYUIOP"])
        self.assertEqual(len(d.slots[0].held_keys), keys.MAX_HELD_KEYS)

    def test_release_of_a_key_this_slot_did_not_press_is_ignored(self):
        d = self.make()
        self.send(d, 0, key("A", True))
        self.send(d, 1, key("A", False))
        self.assertEqual(self.rec.of("key"), [("key", 0x41, True, False)])

    def test_repeated_press_is_ignored(self):
        d = self.make()
        self.send(d, 0, key("A", True), key("A", True))
        self.assertEqual(len(self.rec.of("key")), 1)


class MalformedTests(Base):
    def test_garbage_is_dropped(self):
        d = self.make()
        self.send(d, 0, "not json", b"\xff\xfe", "[]", "5", '"x"', "null", "[" * 1000, '{"type": 5}', '{"type": ["key"]}',
                  "{}", {"type": "key", "code": "A"}, {"type": "key", "code": "A", "down": "yes"},
                  {"type": "down", "x": "a", "y": 0}, {"type": "down", "x": None, "y": 0}, {"type": "down", "y": 0},
                  {"type": "down", "x": 0, "y": 0, "button": "middle"}, {"type": "down", "x": 0, "y": 0, "button": []},
                  {"type": "scroll", "x": 0, "y": 0, "delta": "x"}, {"type": "scroll", "x": 0, "y": 0, "delta": [1]},
                  {"type": "text", "text": 5}, {"type": "text", "text": "x" * 65}, {"type": "media", "action": 3})
        self.send(d, 0, '{"type": "move", "x": NaN, "y": 0}', '{"type": "scroll", "x": 0, "y": 0, "delta": Infinity}')
        self.assertEqual(self.rec.calls, [])

    def test_unknown_types_are_counted(self):
        d = self.make()
        self.send(d, 0, {"type": "launch"}, {"type": "launch"}, {"type": "ping", "id": 1})
        self.assertEqual(d.dropped, {"launch": 2, "ping": 1})
        self.send(d, 0, *[{"type": f"t{i}"} for i in range(200)])
        self.assertLessEqual(len(d.dropped), dispatch.MAX_DROP_KINDS + 1)

    def test_oversized_input_is_dropped_and_counted(self):
        d = self.make()
        big = json.dumps({"type": "text", "text": "x", "pad": "p" * dispatch.MAX_INPUT_MESSAGE})
        self.send(d, 0, big, big.encode())
        self.assertEqual(d.dropped, {"oversize": 2})
        self.send(d, 0, {"type": "text", "text": "ok"})
        self.assertEqual(self.rec.of("type_text"), [("type_text", "ok")])

    def test_oversized_control_message_ends_the_session(self):
        link = session.LinkSession(None, 1)
        link._on_control_message("x" * (session.MAX_CONTROL_MESSAGE + 1))
        self.assertTrue(link._oversize)

    def test_closed_or_unfocusable_window_types_nothing(self):
        d = self.make()
        d.attach(0, window_stream(42, WINDOWS[0][1], usable=False))
        self.send(d, 0, key("A", True), {"type": "text", "text": "x"}, down())
        self.assertEqual(self.rec.calls, [])
        self.rec.focus_ok = False
        with self.assertLogs("input", "WARNING"):
            self.send(d, 1, key("A", True), {"type": "text", "text": "Hi"})
        self.assertEqual(self.rec.of("key") + self.rec.of("type_text"), [])


class ReleaseTests(Base):
    def test_slot_close_and_session_close_release_held_input(self):
        d = self.make()
        self.send(d, 0, key("Shift", True), down())
        self.send(d, 1, key("Ctrl", True))
        d.detach(0)
        self.assertEqual(self.rec.of("key")[-1], ("key", 0x10, False, False))
        self.assertEqual(self.rec.of("mouse_button")[-1], ("mouse_button", 500, 350, "left", False))
        self.assertEqual(len(self.rec.of("key")), 3)
        d.close()
        self.assertEqual(self.rec.of("key")[-1], ("key", 0x11, False, False))
        self.assertEqual(len(self.rec.of("key")), 4)
        d.close()
        self.assertEqual(len(self.rec.of("key")), 4)  # nothing twice

    def test_idle_release_after_three_seconds(self):
        d = self.make()
        self.send(d, 0, key("Shift", True))
        self.now[0] = 2.9
        d.release_idle()
        self.assertEqual(len(self.rec.of("key")), 1)
        self.now[0] = 3.1
        d.release_idle()
        self.assertEqual(self.rec.of("key")[-1], ("key", 0x10, False, False))
        self.assertEqual(d.slots[0].held_keys, {})

    def test_messages_keep_a_held_key_alive(self):
        d = self.make()
        self.send(d, 0, key("Shift", True))
        self.now[0] = 2.5
        self.send(d, 0, {"type": "move", "x": 0, "y": 0})
        self.now[0] = 5.0
        d.release_idle()
        self.assertEqual(len(self.rec.of("key")), 1)

    def test_keys_and_text_reach_the_window_with_modifiers(self):
        d = self.make()
        self.send(d, 0, key("Ctrl", True), key("Left", True), key("Left", False), key("Ctrl", False),
                  {"type": "text", "text": "Hi"})
        self.assertEqual(self.rec.of("key"), [("key", 0x11, True, False), ("key", 0x25, True, True),
                                              ("key", 0x25, False, True), ("key", 0x11, False, False)])
        self.assertEqual(self.rec.of("type_text"), [("type_text", "Hi")])
        self.assertEqual(self.sent("ack"), [])  # acks are for dry runs only


class ProtocolTests(unittest.TestCase):
    def test_known_keys_decode(self):
        event = keys.parse_key(key("A", True))
        self.assertEqual((event.code, event.vk, event.extended, event.down), ("A", 0x41, False, True))
        self.assertEqual(keys.parse_key(key("Left", False)).vk, 0x25)
        self.assertTrue(keys.parse_key(key("Left", False)).extended)
        self.assertEqual(keys.parse_key(key("Enter", True)).vk, 0x0D)
        self.assertEqual(keys.parse_key(key("F12", True)).vk, 0x7B)

    def test_bad_keys_are_refused(self):
        for message in [key("LWin", True), key("Win", True), key("PrintScreen", True), key("a", True),
                        key(0x41, True), key("A", "yes"), key("A", 1), {"type": "key", "code": "A"}, {"type": "key"}]:
            self.assertIsNone(keys.parse_key(message), message)

    def test_no_system_keys_exist(self):
        vks = {vk for vk, _ in keys.KEY_CODES.values()}
        for vk in (0x5B, 0x5C, 0x5D, 0x2C, 0x5F):  # left/right Windows, Apps, Print Screen, Sleep
            self.assertNotIn(vk, vks)

    def test_text(self):
        self.assertEqual(keys.parse_text({"text": "Hello, world"}), "Hello, world")
        self.assertEqual(keys.parse_text({"text": "héllo ✓"}), "héllo ✓")
        self.assertEqual(keys.parse_text({"text": "a\nb\tc\x1b"}), "abc")
        self.assertIsNone(keys.parse_text({"text": ""}))
        self.assertIsNone(keys.parse_text({"text": "\n"}))
        self.assertIsNone(keys.parse_text({"text": 5}))
        self.assertIsNone(keys.parse_text({"text": "x" * (keys.MAX_TEXT + 1)}))
        self.assertEqual(keys.parse_text({"text": "x" * keys.MAX_TEXT}), "x" * keys.MAX_TEXT)

    def test_rate_limiter_burst_then_rate(self):
        now = [0.0]
        limiter = keys.RateLimiter(rate=10, burst=20, clock=lambda: now[0])
        self.assertEqual(sum(limiter.allow() for _ in range(50)), 20)
        now[0] += 0.5  # five more
        self.assertEqual(sum(limiter.allow() for _ in range(50)), 5)
        now[0] += 100  # refills to the burst, no more
        self.assertEqual(sum(limiter.allow() for _ in range(50)), 20)

    def test_rate_limiter_text_costs_per_character(self):
        now = [0.0]
        limiter = keys.RateLimiter(rate=10, burst=20, clock=lambda: now[0])
        self.assertTrue(limiter.allow(15))
        self.assertFalse(limiter.allow(10))
        self.assertTrue(limiter.allow(5))


if __name__ == "__main__":
    unittest.main()
