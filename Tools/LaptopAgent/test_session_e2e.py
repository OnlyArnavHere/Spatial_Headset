"""End-to-end test of the headset link: the real LinkRunner and LinkSession against fake_headset.py over loopback, with
the real MediaMTX and ffmpeg (a 640x360 test pattern, encoded with the project's encoder_args) from bin/.

  python -m unittest test_session_e2e -v        (from Tools/LaptopAgent; about a minute)

Runs once with x264 and once with NVENC (skipped only if ffmpeg can't encode with it here), and is skipped if
bin/ffmpeg.exe or bin/mediamtx.exe is missing. aioice is patched to offer only 127.0.0.1, so nothing leaves the machine.
"""
import asyncio
import contextlib
import json
import socket
import subprocess
import tempfile
import threading
import time
import unittest
from types import SimpleNamespace
from unittest import mock
from pathlib import Path

from aiortc.codecs.h264 import H264Encoder
from cryptography.hazmat.primitives.asymmetric import rsa

import agent
import fake_headset
import handshake
import identity
import pairing
import session
import winapi
from streams import StreamManager, Target

BIN = agent.BIN
SIZE = (640, 360)
FPS = 15
BITRATE = 1_000_000


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def new_identity() -> identity.Identity:
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    return identity.Identity(key, identity.public_der(key))


async def until(condition, timeout: float, what: str, step: float = 0.05):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = condition()
        if value:
            return value
        await asyncio.sleep(step)
    raise AssertionError(f"timed out after {timeout:g} s waiting for {what}")


class FakeStreams(StreamManager):
    """The real StreamManager over made-up windows, each captured by an ffmpeg test pattern."""

    def __init__(self, launch, titles):
        super().__init__(launch)
        rect = winapi.Rect(0, 0, *SIZE)
        self._targets = [Target(f"window:{i}", "window", i, title, f"window {title!r}", f"hwnd={i}",
                                rect=lambda: rect, usable=lambda: True) for i, title in enumerate(titles, 1)]

    def available(self):
        return list(self._targets)


class LinkTests(unittest.IsolatedAsyncioTestCase):
    loop_factory = agent.LOOP_FACTORY  # see agent.LOOP_FACTORY
    encoder_choice = "x264"

    async def asyncSetUp(self):
        for exe in ("ffmpeg", "mediamtx"):
            if not (BIN / f"{exe}.exe").exists():
                self.skipTest(f"bin/{exe}.exe is missing (run setup.py)")
        self.ffmpeg = str(BIN / "ffmpeg.exe")
        try:
            self.encoder = agent.probe_encoder(self.ffmpeg, self.encoder_choice)
        except SystemExit:
            self.skipTest(f"ffmpeg can't encode with {self.encoder_choice} here")
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.tmp = Path(tmp.name)
        self.processes: list[subprocess.Popen] = []
        self.addCleanup(self.kill_processes)

        self.rtsp_port = free_port()
        config = agent.write_mediamtx_config(self.rtsp_port, self.tmp)
        self.start_process([str(BIN / "mediamtx.exe"), str(config)], "mediamtx")
        await until(lambda: self.port_open(self.rtsp_port), 10, "MediaMTX to listen")

        self.packs = self.encodes = 0
        real_pack, real_encode = H264Encoder.pack, H264Encoder.encode

        def pack(encoder, packet):
            self.packs += 1
            return real_pack(encoder, packet)

        def encode(encoder, *a, **k):
            self.encodes += 1
            return real_encode(encoder, *a, **k)

        H264Encoder.pack, H264Encoder.encode = pack, encode
        self.addCleanup(lambda: (setattr(H264Encoder, "pack", real_pack), setattr(H264Encoder, "encode", real_encode)))
        patch = fake_headset.localhost_only()
        patch.__enter__()
        self.addCleanup(patch.__exit__, None, None, None)

        self.streams = FakeStreams(self.launch, ["Test One", "Test Two"])
        self.addCleanup(self.streams.stop_all)
        self.agent_ident = new_identity()
        self.store = pairing.PairStore(self.tmp / "agent")
        self.headset = fake_headset.FakeHeadset(self.tmp / "headset", agent_spkis=[self.agent_ident.spki_der],
                                                gate=handshake.HeadsetGate(attempts=1000))
        self.store.add(self.headset.name, self.headset.ident.spki_der)
        self.port = await self.headset.start()
        self.addAsyncCleanup(self.headset.stop)

    def start_process(self, command, name):
        log_file = open(self.tmp / f"{name}.log", "ab")
        self.addCleanup(log_file.close)
        process = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=log_file, stderr=subprocess.STDOUT)
        self.processes.append(process)
        return process

    def kill_processes(self):
        for process in self.processes:
            if process.poll() is None:
                process.terminate()
        for process in self.processes:
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()

    @staticmethod
    def port_open(port: int) -> bool:
        try:
            socket.create_connection(("127.0.0.1", port), timeout=0.2).close()
            return True
        except OSError:
            return False

    def launch(self, target, path):
        name, codec, pix_fmt = self.encoder
        command = [self.ffmpeg, "-hide_banner", "-loglevel", "warning", "-re", "-f", "lavfi", "-i",
                   f"testsrc=size={SIZE[0]}x{SIZE[1]}:rate={FPS}", "-pix_fmt", pix_fmt]
        command += agent.encoder_args(name, codec, FPS, BITRATE)
        command += ["-f", "rtsp", "-rtsp_transport", "tcp", f"rtsp://127.0.0.1:{self.rtsp_port}/{path}"]
        return self.start_process(command, f"ffmpeg-{path}"), SIZE

    async def start_runner(self, ident=None, store=None, port=None, **options) -> session.LinkRunner:
        runner = session.LinkRunner(ident or self.agent_ident, store or self.store, self.streams, self.rtsp_port,
                                    max_windows=2, pinned=[("127.0.0.1", port or self.port)], **options)
        task = asyncio.ensure_future(runner.run())

        async def stop():
            task.cancel()
            await asyncio.gather(task, return_exceptions=True)

        self.addAsyncCleanup(stop)
        return runner

    async def welcomed(self, runner, headset=None) -> fake_headset.HeadsetSession:
        headset = headset or self.headset
        await until(lambda: headset.session is not None and runner.active is not None, 20, "a session")
        return headset.session

    async def test_frames_second_slot_and_redial(self):
        runner = await self.start_runner()
        headset = await self.welcomed(runner)

        welcome = await headset.wait_for("welcome", 10)
        self.assertEqual(welcome["version"], 5)
        self.assertEqual([s["slot"] for s in welcome["slots"]], [0, 1])
        self.assertFalse(welcome["input"])
        pong = await headset.request({"type": "ping", "id": 7, "t": 123}, "pong")
        self.assertEqual((pong["id"], pong["t"]), (7, 123))
        targets = await headset.request({"type": "list"}, "targets")
        self.assertEqual([t["id"] for t in targets["targets"]], ["window:1", "window:2"])

        # First window: at least 30 decoded 640x360 frames within 10 s.
        opened = await headset.request({"type": "open", "target": "window:1"}, ("opened", "error"), 15)
        self.assertEqual(opened["type"], "opened", opened)
        self.assertEqual((opened["slot"], opened["id"], opened["path"]), (0, "window:1", "test-one"))
        t0 = time.monotonic()
        mid0 = opened["mid"]
        await until(lambda: headset.frame_count(mid0, t0) >= 30, 10, "30 frames of the first window")
        sizes = {(w, h) for t, m, w, h in headset.frames if m == mid0}
        self.assertEqual(sizes, {SIZE})

        # Second window on the next slot: a picture within 2 s of the track being attached.
        opened2 = await headset.request({"type": "open", "target": "window:2"}, ("opened", "error"), 15)
        self.assertEqual(opened2["type"], "opened", opened2)
        self.assertEqual(opened2["slot"], 1)
        self.assertNotEqual(opened2["mid"], mid0)
        t1 = time.monotonic()
        await until(lambda: headset.frame_count(opened2["mid"], t1) >= 1, 2, "a picture on the second slot")

        # A third window has no slot; closing one frees it, and the freed slot streams again.
        refused = await headset.request({"type": "open", "target": "window:1"}, ("opened", "error"), 15)
        self.assertEqual(refused, {"type": "error", "reason": "no slot"})
        headset.send({"type": "close", "slot": 0})
        await until(lambda: runner.active.slots[0].window is None, 5, "slot 0 to close")
        gone = await headset.request({"type": "open", "target": "window:99"}, ("opened", "error"), 5)
        self.assertEqual(gone, {"type": "error", "reason": "gone"})
        t2 = time.monotonic()
        reopened = await headset.request({"type": "open", "target": "window:2"}, ("opened", "error"), 15)
        self.assertEqual((reopened["type"], reopened["slot"]), ("opened", 0))
        await until(lambda: headset.frame_count(mid0, t2) >= 1, 3, "a picture on the reused slot")

        # The packets went straight through the sender: packed, never encoded.
        self.assertGreater(self.packs, 30)
        self.assertEqual(self.encodes, 0)

        # The headset goes away (its WebRTC session closes) and comes back: the agent ends the session and redials.
        self.assertEqual(runner.session_count, 1)
        await self.headset.drop_session()
        await until(lambda: runner.session_count == 2, 20, "the agent to redial")
        self.assertTrue(runner.end_reasons, "the first session should have reported why it ended")
        headset = await until(lambda: self.headset.session, 5, "the second session")
        await headset.wait_for("welcome", 10)   # the control channel is open once the agent has said hello
        opened = await headset.request({"type": "open", "target": "window:1"}, ("opened", "error"), 15)
        t3 = time.monotonic()
        await until(lambda: headset.frame_count(opened["mid"], t3) >= 10, 10, "frames after the redial")

        # The headset disappears entirely: the session ends and the agent keeps trying.
        await self.headset.stop()
        await until(lambda: len(runner.end_reasons) == 2, 20, "the second session to end")
        attempts = runner.attempt_count
        await until(lambda: runner.attempt_count > attempts, 10, "another dial attempt")
        self.assertIsNone(runner.active)


    async def test_pair_by_typed_code_then_session_without_one(self):
        """agent.py --pair against a headset in pairing mode, with the code typed through an injected stdin."""
        ident, store = new_identity(), pairing.PairStore(self.tmp / "agent2")
        headset = fake_headset.FakeHeadset(self.tmp / "headset2", name="Pair Headset", pairing_mode=True,
                                           gate=handshake.HeadsetGate(attempts=1000), echo=lambda *a: None)
        port = await headset.start()
        self.addAsyncCleanup(headset.stop)
        typed = iter(["not a code", "AAAA-AAAA-AAAA", headset.code.lower()])  # bad format, wrong code, right code
        prompts, said = [], []

        def read_line(prompt):
            prompts.append(prompt)
            return next(typed)

        code = await agent.pair_command(("127.0.0.1", port), ident, store, "Test Laptop", read_line=read_line,
                                        say=said.append)
        self.assertEqual(code, 0, said)
        self.assertEqual(len(prompts), 3)
        self.assertTrue(all(p.startswith("Enter the code shown on the headset (XXXX-XXXX-XXXX)") for p in prompts))
        self.assertTrue(any("wrong" in s for s in said), said)
        self.assertIn("Waiting for you to confirm on the headset...", said)
        self.assertTrue(said[-1].startswith("Paired with Pair Headset ("), said)
        self.assertEqual(headset.paired, ["Test Laptop"])
        self.assertEqual([p.public_key for p in store.list()], [headset.ident.spki_der])
        self.assertEqual(headset.agent_spkis, [ident.spki_der])
        self.assertEqual(headset.issuer.strikes, 1)

        runner = await self.start_runner(ident, store, port)
        session_ = await self.welcomed(runner, headset)
        await session_.wait_for("welcome", 10)
        opened = await session_.request({"type": "open", "target": "window:1"}, ("opened", "error"), 15)
        self.assertEqual(opened["type"], "opened", opened)
        t0 = time.monotonic()
        await until(lambda: session_.frame_count(opened["mid"], t0) >= 10, 10, "frames after pairing")


class NvencLinkTests(LinkTests):
    encoder_choice = "nvenc"


class InputLinkTests(LinkTests):
    """Input over the data channels, with the Windows input calls replaced so nothing is ever injected."""
    test_frames_second_slot_and_redial = None
    test_pair_by_typed_code_then_session_without_one = None

    async def asyncSetUp(self):
        await super().asyncSetUp()
        self.keys: list[tuple] = []
        self.focused: list[int] = []
        self.enterContext(mock.patch.object(winapi, "key", lambda vk, down, extended: self.keys.append((vk, down, extended))))
        self.enterContext(mock.patch.object(winapi, "focus_window", lambda handle: self.focused.append(handle) or True))
        for name in ("mouse_move", "mouse_button", "mouse_wheel", "type_text", "raise_window"):
            self.enterContext(mock.patch.object(winapi, name, lambda *a, **k: None))

    async def opened_window(self, headset):
        opened = await headset.request({"type": "open", "target": "window:1"}, ("opened", "error"), 15)
        self.assertEqual(opened["type"], "opened", opened)
        return opened

    async def test_key_reaches_windows_only_after_a_grant(self):
        approver = mock.AsyncMock(return_value=True)
        runner = await self.start_runner(input_enabled=True, keyboard_enabled=True, approver=approver)
        headset = await self.welcomed(runner)
        welcome = await headset.wait_for("welcome", 10)
        self.assertEqual((welcome["control"], welcome["input"], welcome["keyboard"], welcome["media"], welcome["cast_only"]),
                         ("view-only", True, True, True, False))
        opened = await self.opened_window(headset)
        self.assertEqual(opened["slot"], 0)

        headset.send_input(0, {"type": "key", "code": "A", "down": True})
        headset.send_input(0, {"type": "media", "action": "mute"})
        await asyncio.sleep(0.7)
        self.assertEqual(self.keys, [], "nothing may reach Windows before a grant")
        approver.assert_not_called()

        denied = await headset.request({"type": "control-request"}, ("control-granted", "control-denied"))
        self.assertEqual(denied, {"type": "control-denied", "reason": "no consent"})
        approver.assert_not_called()
        granted = await headset.request({"type": "control-request", "consent": "full control"},
                                        ("control-granted", "control-denied"))
        self.assertEqual(granted, {"type": "control-granted", "keyboard": True})
        self.assertEqual(approver.await_args.args[0]["name"], self.headset.name)

        headset.send_input(0, {"type": "key", "code": "A", "down": True})
        headset.send_input(0, {"type": "key", "code": "A", "down": False})
        await until(lambda: len(self.keys) == 2, 5, "the key to reach winapi.key")
        self.assertEqual(self.keys, [(0x41, True, False), (0x41, False, False)])
        self.assertEqual(self.focused, [1])

        # input-1 has no window yet: dropped. A key held when the headset goes away is released.
        headset.send_input(1, {"type": "key", "code": "B", "down": True})
        headset.send_input(0, {"type": "key", "code": "Shift", "down": True})
        await until(lambda: (0x10, True, False) in self.keys, 5, "Shift to go down")
        self.assertNotIn((0x42, True, False), self.keys)
        await self.headset.drop_session()
        await until(lambda: (0x10, False, False) in self.keys, 10, "Shift to be released when the session ends")

    async def test_release_and_stop(self):
        runner = await self.start_runner(input_enabled=True, keyboard_enabled=True)
        headset = await self.welcomed(runner)
        await headset.wait_for("welcome", 10)
        await self.opened_window(headset)
        await headset.request({"type": "control-request", "consent": "full control"}, "control-granted")
        headset.send_input(0, {"type": "key", "code": "Ctrl", "down": True})
        await until(lambda: self.keys == [(0x11, True, False)], 5, "Ctrl to go down")
        headset.send({"type": "control-release"})
        await until(lambda: self.keys[-1] == (0x11, False, False), 5, "Ctrl to be released")
        headset.send_input(0, {"type": "key", "code": "A", "down": True})
        await asyncio.sleep(0.5)
        self.assertEqual(len(self.keys), 2)
        headset.send({"type": "stop"})
        await until(lambda: runner.end_reasons == ["headset stopped"], 10, "the session to end")

    async def test_kill_switch_ends_the_session_and_nothing_redials(self):
        self.enterContext(mock.patch.object(session, "BACKOFF_START_S", 0.2))
        runner = await self.start_runner(input_enabled=True, keyboard_enabled=True)
        headset = await self.welcomed(runner)
        await headset.wait_for("welcome", 10)
        await self.opened_window(headset)
        attempts, handshakes = runner.attempt_count, self.headset.handshakes
        runner.kill_switch()
        await runner._shutdown_task
        await until(lambda: headset.pc.connectionState in ("closed", "failed"), 10, "the headset to see the session end")
        self.assertIsNone(runner.active)
        await asyncio.sleep(1)  # five backoff periods
        self.assertEqual((runner.attempt_count, self.headset.handshakes), (attempts, handshakes))

    async def test_cast_only_media_arrives_and_keys_do_not(self):
        approver = mock.AsyncMock()
        runner = await self.start_runner(cast_only=True, approver=approver)
        headset = await self.welcomed(runner)
        welcome = await headset.wait_for("welcome", 10)
        self.assertEqual((welcome["cast_only"], welcome["media"], welcome["input"], welcome["keyboard"]),
                         (True, True, False, False))
        await self.opened_window(headset)
        headset.send_input(0, {"type": "key", "code": "A", "down": True})
        headset.send_input(0, {"type": "text", "text": "hi"})
        headset.send_input(0, {"type": "media", "action": "play_pause"})
        await until(lambda: self.keys == [(0xB3, True, True), (0xB3, False, True)], 5, "the media key to arrive")
        await asyncio.sleep(0.3)
        self.assertEqual(len(self.keys), 2)
        denied = await headset.request({"type": "control-request", "consent": "full control"}, "control-denied")
        self.assertEqual(denied["reason"], "cast only")
        approver.assert_not_called()


class WelcomeControlTests(unittest.TestCase):
    def welcome(self, revoked):
        link = session.LinkSession(mock.Mock(), 8554, max_windows=1, revoked=revoked)
        sent = []
        link.control = SimpleNamespace(readyState="open", send=sent.append)
        link._on_control_open()
        return json.loads(sent[0])["control"]

    def test_welcome_says_revoked_when_the_session_starts_revoked(self):
        self.assertEqual(self.welcome(True), "revoked")
        self.assertEqual(self.welcome(False), "view-only")


class CancelledOpenTests(unittest.IsolatedAsyncioTestCase):
    async def test_cancel_during_open_releases_stream_and_stops_late_player(self):
        gate, called = threading.Event(), threading.Event()
        player = SimpleNamespace(video=SimpleNamespace(stop=lambda: stopped.set()))
        stopped = threading.Event()

        def open_player(url):
            called.set()
            gate.wait(5)
            return player

        released = []
        stream = SimpleNamespace(path="p", target=None)
        streams = SimpleNamespace(acquire=lambda target: stream, release=released.append)
        link = session.LinkSession(streams, 1)
        link.slots = [SimpleNamespace(index=0, free=True, reserved=False)]
        with mock.patch.object(session, "open_player", open_player):
            task = asyncio.ensure_future(link._open("window:1"))
            await until(called.is_set, 2, "open_player to start")
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await task
            self.assertEqual(released, [stream])
            self.assertFalse(link.slots[0].reserved)
            gate.set()  # the executor call finishes after the cancel
            await until(stopped.is_set, 3, "the late player to be stopped")

    async def test_stdin_is_read_on_a_daemon_thread(self):
        seen = []

        def read_line(prompt):
            seen.append((prompt, threading.current_thread().daemon))
            return "abc"

        self.assertEqual(await agent.read_on_daemon_thread(read_line, "? "), "abc")
        self.assertEqual(seen, [("? ", True)])


class OfferTests(unittest.IsolatedAsyncioTestCase):
    async def test_offer_has_h264_only_slots_and_negotiated_channels(self):
        link = session.LinkSession(StreamManager(lambda t, p: None), 8554, max_windows=3)
        with fake_headset.localhost_only():
            sdp = await link.build_offer()
        try:
            self.assertEqual(sdp.count("m=video"), 3)
            self.assertEqual(sdp.count("a=sendonly"), 3)
            self.assertIn("m=application", sdp)
            self.assertIn("H264/90000", sdp)
            for other in ("VP8", "VP9", "AV1", "rtx"):
                self.assertNotIn(other, sdp)
            self.assertIn("packetization-mode=1", sdp)
            self.assertEqual([s.mid for s in link.slots], ["0", "1", "2"])
            self.assertEqual(link.control.id, 0)
            self.assertEqual([s.channel.id for s in link.slots], [1, 2, 3])
            self.assertIn("a=candidate:", sdp)  # gathering was complete before the offer was taken
            self.assertIn("a=end-of-candidates", sdp)
        finally:
            await link.close()

    def test_mediamtx_config_is_loopback_only(self):
        with tempfile.TemporaryDirectory() as tmp:
            text = agent.write_mediamtx_config(9554, Path(tmp)).read_text(encoding="utf-8")
        self.assertIn("rtspAddress: 127.0.0.1:9554", text)
        for off in ("webrtc", "hls", "rtmp", "srt", "api", "metrics", "pprof", "playback"):
            self.assertIn(f"{off}: no", text)
        self.assertNotIn("viewer", text)

    def test_idr_detection(self):
        self.assertTrue(session.contains_idr(b"\x00\x00\x00\x01\x67abc\x00\x00\x00\x01\x65xyz"))
        self.assertFalse(session.contains_idr(b"\x00\x00\x00\x01\x41abc"))


class PairDiscoveryTests(unittest.IsolatedAsyncioTestCase):
    @staticmethod
    def browse_of(*found, delay=0.0):
        async def browse(pairing=False):
            assert pairing is True
            await asyncio.sleep(delay)
            for item in found:
                yield item
            await asyncio.sleep(3600)
        return browse

    async def find(self, browse, answer="", **kw):
        said, asked = [], []

        async def ask(prompt):
            asked.append(prompt)
            return answer

        result = await agent.find_pairing_headset(ask, browse=browse, say=said.append, **kw)
        return result, said, asked

    async def test_exactly_one_is_used_without_asking(self):
        result, said, asked = await self.find(self.browse_of(("10.0.0.5", 47810)), seconds=0.3)
        self.assertEqual(result, ("10.0.0.5", 47810))
        self.assertEqual(asked, [])

    async def test_several_need_a_pick_and_a_bad_pick_connects_to_none(self):
        found = self.browse_of(("10.0.0.5", 47810), ("10.0.0.6", 47810))
        result, said, asked = await self.find(found, "2", seconds=0.3)
        self.assertEqual(result, ("10.0.0.6", 47810))
        result, said, asked = await self.find(found, "9", seconds=0.3)
        self.assertIsNone(result)
        self.assertTrue(any("--headset" in s for s in said))

    async def test_none_found_prints_a_hint_and_gives_up(self):
        result, said, _ = await self.find(self.browse_of(), seconds=0.2, give_up=0.5)
        self.assertIsNone(result)
        self.assertTrue(any("Pair new laptop" in s and "--headset" in s for s in said))

    async def test_a_late_headset_is_found_while_waiting(self):
        result, said, _ = await self.find(self.browse_of(("10.0.0.7", 1234), delay=0.5), seconds=0.2, give_up=3)
        self.assertEqual(result, ("10.0.0.7", 1234))

    async def test_pair_with_a_decline_exits_one(self):
        with tempfile.TemporaryDirectory() as tmp:
            tmp = Path(tmp)
            headset = fake_headset.FakeHeadset(tmp / "h", name="H", pairing_mode=True, confirm=False,
                                               gate=handshake.HeadsetGate(attempts=100), echo=lambda *a: None)
            port = await headset.start()
            try:
                store, said = pairing.PairStore(tmp / "a"), []
                code = await agent.pair_command(("127.0.0.1", port), new_identity(), store, "Laptop",
                                                read_line=lambda prompt: headset.code, say=said.append)
            finally:
                await headset.stop()
            self.assertEqual(code, 1)
            self.assertEqual(store.list(), [])
            self.assertEqual(headset.agent_spkis, [])
            self.assertTrue(any("declined" in s for s in said), said)


if __name__ == "__main__":
    unittest.main()
