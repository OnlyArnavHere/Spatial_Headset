"""Unit tests for keyboard input: the key protocol, the rate limit and the control server's injection, in dry-run mode
and with the Windows calls replaced. No agent needs to be running and nothing is typed.

  python -m unittest test_keyboard -v        (from Tools/LaptopAgent)
"""
import asyncio
import json
import unittest
from unittest import mock

import keys
import winapi
from input_server import InputServer, Session
from streams import Stream, Target


class FakeConnection:
    """Collects what the server sends."""

    def __init__(self):
        self.sent: list[dict] = []

    async def send(self, text: str) -> None:
        self.sent.append(json.loads(text))

    def of_type(self, kind: str) -> list[dict]:
        return [m for m in self.sent if m.get("type") == kind]


def window_stream(usable: bool = True) -> Stream:
    target = Target("window:42", "window", 42, "Untitled - Notepad", "window 'Untitled - Notepad'", "hwnd=42",
                    rect=lambda: winapi.Rect(100, 100, 900, 600), usable=lambda: usable)
    return Stream("untitled-notepad", target, (800, 500), process=None)


def server(dry_run: bool = True, keyboard: bool = True) -> InputServer:
    return InputServer("token", streams=None, input_enabled=True, keyboard_enabled=keyboard, dry_run=dry_run)


def session(paired: bool = True, stream: Stream | None = None) -> Session:
    return Session(FakeConnection(), "127.0.0.1", paired=paired, stream=stream or window_stream())


def run(server_: InputServer, session_: Session, *messages: dict) -> None:
    async def go():
        for message in messages:
            await server_._dispatch(session_, json.dumps(message))
        await asyncio.sleep(0)  # let notices scheduled with create_task go out
    asyncio.run(go())


def key(code, down):
    return {"type": "key", "code": code, "down": down}


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


class RateLimiterTests(unittest.TestCase):
    def test_burst_then_rate(self):
        now = [0.0]
        limiter = keys.RateLimiter(rate=10, burst=20, clock=lambda: now[0])
        self.assertEqual(sum(limiter.allow() for _ in range(50)), 20)
        now[0] += 0.5  # five more
        self.assertEqual(sum(limiter.allow() for _ in range(50)), 5)
        now[0] += 100  # refills to the burst, no more
        self.assertEqual(sum(limiter.allow() for _ in range(50)), 20)

    def test_text_costs_per_character(self):
        now = [0.0]
        limiter = keys.RateLimiter(rate=10, burst=20, clock=lambda: now[0])
        self.assertTrue(limiter.allow(15))
        self.assertFalse(limiter.allow(10))
        self.assertTrue(limiter.allow(5))


class DryRunTests(unittest.TestCase):
    def test_key_press_and_release_are_acknowledged(self):
        srv, s = server(), session()
        run(srv, s, key("A", True), key("A", False))
        acks = s.connection.of_type("ack")
        self.assertEqual([(a["event"], a["code"], a["down"], a["path"]) for a in acks],
                         [("key", "A", True, "untitled-notepad"), ("key", "A", False, "untitled-notepad")])
        self.assertEqual(s.held_keys, {})

    def test_text_is_acknowledged(self):
        srv, s = server(), session()
        run(srv, s, {"type": "text", "text": "Hello"})
        self.assertEqual(s.connection.of_type("ack"), [{"type": "ack", "event": "text", "text": "Hello", "path": "untitled-notepad"}])

    def test_unpaired_connection_is_refused(self):
        srv, s = server(), session(paired=False)
        with self.assertLogs("input", "WARNING") as logs:
            run(srv, s, key("A", True), {"type": "text", "text": "x"})
        self.assertEqual(s.connection.sent, [])
        self.assertTrue(any("not paired" in line for line in logs.output))

    def test_keyboard_off_or_no_stream_drops_keys(self):
        for srv, s in [(server(keyboard=False), session()), (server(), Session(FakeConnection(), "127.0.0.1", paired=True))]:
            run(srv, s, key("A", True), {"type": "text", "text": "x"})
            self.assertEqual(s.connection.sent, [])

    def test_closed_window_drops_keys(self):
        srv, s = server(), session(stream=window_stream(usable=False))
        run(srv, s, key("A", True), {"type": "text", "text": "x"})
        self.assertEqual(s.connection.sent, [])

    def test_release_without_press_and_unknown_keys_are_ignored(self):
        srv, s = server(), session()
        run(srv, s, key("B", False), key("LWin", True), key("A", True), key("A", True))
        self.assertEqual([(a["code"], a["down"]) for a in s.connection.of_type("ack")], [("A", True)])

    def test_held_key_limit(self):
        srv, s = server(), session()
        codes = list("QWERTYUIOP")
        run(srv, s, *[key(c, True) for c in codes])
        self.assertEqual(len(s.held_keys), keys.MAX_HELD_KEYS)

    def test_rate_limit_drops_presses_but_never_releases(self):
        srv, s = server(), session()
        s.limiter = keys.RateLimiter(rate=0.001, burst=10)
        run(srv, s, *[m for _ in range(30) for m in (key("A", True), key("A", False))])
        acks = s.connection.of_type("ack")
        downs = [a for a in acks if a["down"]]
        ups = [a for a in acks if not a["down"]]
        self.assertEqual(len(downs), 10)
        self.assertEqual(len(ups), 10)  # every accepted press was released
        self.assertEqual(len(s.connection.of_type("throttled")), 1)  # one notice, not one per dropped key
        self.assertEqual(s.held_keys, {})

    def test_kill_switch_releases_and_stops_everything(self):
        srv, s = server(), session()
        srv._sessions.append(s)

        async def go():
            await srv._dispatch(s, json.dumps(key("Ctrl", True)))
            await srv._dispatch(s, json.dumps({"type": "down", "x": 0.5, "y": 0.5, "button": "left"}))
            srv.stop_input("test")
            await asyncio.sleep(0)
            before = len(s.connection.sent)
            for message in (key("A", True), {"type": "text", "text": "x"}, {"type": "down", "x": 0.5, "y": 0.5}):
                await srv._dispatch(s, json.dumps(message))
            return before

        before = asyncio.run(go())
        self.assertEqual(s.held_keys, {})
        self.assertEqual(s.held, set())
        self.assertEqual(s.connection.of_type("input_stopped"), [{"type": "input_stopped", "reason": "test"}])
        self.assertEqual(len(s.connection.sent), before)  # nothing acknowledged after the kill switch
        self.assertFalse(srv.input_enabled or srv.keyboard_enabled)


class InjectionTests(unittest.TestCase):
    """Not dry run: the Windows calls are replaced, so this checks what would be sent to SendInput."""

    def test_keys_and_text_reach_the_streamed_window(self):
        srv, s = server(dry_run=False), session()
        with mock.patch.object(winapi, "focus_window", return_value=True) as focus, \
                mock.patch.object(winapi, "key") as press, mock.patch.object(winapi, "type_text") as type_text:
            run(srv, s, key("Ctrl", True), key("Left", True), key("Left", False), key("Ctrl", False),
                {"type": "text", "text": "Hi"})
        focus.assert_called_with(42)
        self.assertEqual(press.call_args_list, [mock.call(0x11, True, False), mock.call(0x25, True, True),
                                                mock.call(0x25, False, True), mock.call(0x11, False, False)])
        type_text.assert_called_once_with("Hi")
        self.assertEqual(s.connection.of_type("ack"), [])  # acks are for dry runs only

    def test_nothing_is_typed_when_the_window_cant_be_focused(self):
        srv, s = server(dry_run=False), session()
        with mock.patch.object(winapi, "focus_window", return_value=False), \
                mock.patch.object(winapi, "key") as press, mock.patch.object(winapi, "type_text") as type_text:
            with self.assertLogs("input", "WARNING"):
                run(srv, s, key("A", True), {"type": "text", "text": "Hi"})
        press.assert_not_called()
        type_text.assert_not_called()

    def test_disconnect_releases_held_keys(self):
        srv, s = server(dry_run=False), session()
        with mock.patch.object(winapi, "focus_window", return_value=True), mock.patch.object(winapi, "key") as press:
            run(srv, s, key("Shift", True))
            srv._release_all(s, "test")
        self.assertEqual(press.call_args_list, [mock.call(0x10, True, False), mock.call(0x10, False, False)])


if __name__ == "__main__":
    unittest.main()
