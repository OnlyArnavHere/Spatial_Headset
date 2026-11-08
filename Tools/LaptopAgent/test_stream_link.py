"""USB without a network: the usbmux client against a fake Apple USB service, and whole stream sessions (stream
handshake, welcome, windows, decoded H.264, input, the end) between the real LinkRunner and the fake headset, with the
USB tunnel stood in for by plain localhost TCP."""
import asyncio
import plistlib
import struct
import time
import unittest

import fake_headset
import handshake
import session
import stream_link
import usbmux
import test_session_e2e as e2e
from test_session_e2e import SIZE, until

LinkTests = None  # (not imported by name: unittest would run the WebRTC suite again here)


class FakeUsbmuxd:
    """Speaks the plist usbmux protocol on localhost: ListDevices, and Connect forwarded to a local TCP port."""

    def __init__(self, devices, ports):
        self.devices, self.ports = devices, ports  # ports: {(device_id, phone_port): local_port}
        self.requests = []
        self.server = None

    async def start(self):
        self.server = await asyncio.start_server(self._handle, "127.0.0.1", 0)
        return ("127.0.0.1", self.server.sockets[0].getsockname()[1])

    async def stop(self):
        self.server.close()
        await self.server.wait_closed()

    @staticmethod
    def _reply(writer, payload, tag):
        body = plistlib.dumps(payload)
        writer.write(struct.pack("<IIII", 16 + len(body), 1, 8, tag) + body)

    async def _handle(self, reader, writer):
        head = await reader.readexactly(16)
        length, _version, _kind, tag = struct.unpack("<IIII", head)
        request = plistlib.loads(await reader.readexactly(length - 16))
        self.requests.append(request)
        if request["MessageType"] == "ListDevices":
            self._reply(writer, {"DeviceList": self.devices}, tag)
            await writer.drain()
            writer.close()
            return
        port = struct.unpack(">H", struct.pack("<H", request["PortNumber"]))[0]
        local = self.ports.get((request["DeviceID"], port))
        if local is None:
            self._reply(writer, {"MessageType": "Result", "Number": 3}, tag)
            await writer.drain()
            writer.close()
            return
        self._reply(writer, {"MessageType": "Result", "Number": 0}, tag)
        await writer.drain()
        up_reader, up_writer = await asyncio.open_connection("127.0.0.1", local)

        async def pipe(src, dst):
            try:
                while data := await src.read(65536):
                    dst.write(data)
                    await dst.drain()
            except (ConnectionError, OSError):
                pass
            finally:
                dst.close()
        await asyncio.gather(pipe(reader, up_writer), pipe(up_reader, writer))


def device(device_id, serial, connection="USB"):
    return {"DeviceID": device_id, "MessageType": "Attached",
            "Properties": {"ConnectionType": connection, "DeviceID": device_id, "SerialNumber": serial,
                           "ProductID": 4776}}


class UsbmuxTests(unittest.IsolatedAsyncioTestCase):
    async def test_lists_only_phones_on_the_cable(self):
        mux = FakeUsbmuxd([device(3, "PHONE-USB"), device(4, "PHONE-WIFI", "Network")], {})
        address = await mux.start()
        self.addAsyncCleanup(mux.stop)
        devices = await usbmux.list_devices(address)
        self.assertEqual(devices, [usbmux.Device(3, "PHONE-USB", 4776)])
        self.assertEqual(devices[0].key, "usb:PHONE-USB")

    async def test_connect_reaches_the_phone_port_and_sends_it_in_network_order(self):
        async def echo(reader, writer):
            writer.write(await reader.readexactly(5))
            await writer.drain()
            writer.close()
        server = await asyncio.start_server(echo, "127.0.0.1", 0)
        self.addAsyncCleanup(server.wait_closed)
        self.addCleanup(server.close)
        mux = FakeUsbmuxd([device(3, "P")], {(3, 47810): server.sockets[0].getsockname()[1]})
        address = await mux.start()
        self.addAsyncCleanup(mux.stop)
        reader, writer = await usbmux.connect(3, 47810, address)
        writer.write(b"hello")
        self.assertEqual(await reader.readexactly(5), b"hello")
        writer.close()
        self.assertEqual(mux.requests[0]["PortNumber"], 0xC2BA)  # 47810 = 0xBAC2, byte-swapped

    async def test_nothing_listening_is_connection_refused(self):
        mux = FakeUsbmuxd([device(3, "P")], {})
        address = await mux.start()
        self.addAsyncCleanup(mux.stop)
        with self.assertRaises(ConnectionRefusedError):
            await usbmux.connect(3, 47810, address)

    async def test_missing_service_says_what_to_install(self):
        with self.assertRaises(usbmux.UsbmuxError) as caught:
            await usbmux.list_devices(("127.0.0.1", 1), timeout=1.0)
        self.assertTrue(str(caught.exception))


class StreamFramingTests(unittest.IsolatedAsyncioTestCase):
    async def read(self, data: bytes, slots=4):
        reader = asyncio.StreamReader()
        reader.feed_data(data)
        reader.feed_eof()
        return await stream_link.read_frame(reader, slots)

    async def test_round_trip(self):
        self.assertEqual(await self.read(stream_link.frame(stream_link.INPUT, 2, b"{}")), (stream_link.INPUT, 2, b"{}"))

    async def test_bad_frames_are_refused(self):
        for bad in (struct.pack(">BBI", 9, 0, 1) + b"x",                      # unknown kind
                    struct.pack(">BBI", stream_link.CONTROL, 0, 0),             # empty
                    struct.pack(">BBI", stream_link.CONTROL, 0, 16 * 1024 + 1),  # too big for a message
                    struct.pack(">BBI", stream_link.INPUT, 4, 1) + b"x"):        # slot out of range
            with self.assertRaises(ValueError):
                await self.read(bad)

    async def test_eof_is_closed(self):
        with self.assertRaises(stream_link.StreamClosed):
            await self.read(b"\x01\x00")


class StreamSessionTests(unittest.IsolatedAsyncioTestCase):
    """The real runner, MediaMTX and ffmpeg (as in test_session_e2e) with the fake headset in stream mode."""
    loop_factory = e2e.LinkTests.loop_factory
    encoder_choice = e2e.LinkTests.encoder_choice
    asyncSetUp = e2e.LinkTests.asyncSetUp
    start_process = e2e.LinkTests.start_process
    kill_processes = e2e.LinkTests.kill_processes
    port_open = staticmethod(e2e.LinkTests.port_open)
    launch = e2e.LinkTests.launch

    async def start_usb_runner(self, **options) -> session.LinkRunner:
        self.headset.stream_allowed = options.pop("stream_allowed", True)

        async def usb_list():
            return [usbmux.Device(5, "FAKE-IPHONE")]

        async def usb_connect(device_id, port):
            self.assertEqual((device_id, port), (5, handshake.LINK_PORT))
            return await asyncio.open_connection("127.0.0.1", self.port)

        runner = session.LinkRunner(self.agent_ident, self.store, self.streams, self.rtsp_port, max_windows=2,
                                    usb=True, usb_list=usb_list, usb_connect=usb_connect, **options)
        task = asyncio.ensure_future(runner.run())

        async def stop():
            task.cancel()
            await asyncio.gather(task, return_exceptions=True)
        self.addAsyncCleanup(stop)
        return runner

    async def test_windows_video_and_input_over_the_stream(self):
        runner = await self.start_usb_runner(input_enabled=True)
        await until(lambda: self.headset.session is not None and runner.active is not None, 20, "a session")
        headset = self.headset.session
        self.assertIsInstance(headset, fake_headset.StreamHeadsetSession)
        self.assertIsInstance(runner.active, stream_link.StreamLinkSession)

        welcome = await headset.wait_for("welcome", 10)
        self.assertEqual(welcome["transport"], "stream")
        self.assertEqual(welcome["slots"], [{"slot": 0, "mid": None}, {"slot": 1, "mid": None}])
        pong = await headset.request({"type": "ping", "id": 3, "t": 9}, "pong")
        self.assertEqual((pong["id"], pong["t"]), (3, 9))
        targets = await headset.request({"type": "list"}, "targets")
        self.assertEqual([t["id"] for t in targets["targets"]], ["window:1", "window:2"])

        opened = await headset.request({"type": "open", "target": "window:1"}, ("opened", "error"), 15)
        self.assertEqual(opened["type"], "opened", opened)
        self.assertEqual(opened["slot"], 0)
        t0 = time.monotonic()
        await until(lambda: headset.frame_count("slot0", t0) >= 30, 10, "30 decoded frames on slot 0")
        self.assertEqual({(w, h) for _, m, w, h in headset.frames if m == "slot0"}, {SIZE})
        self.assertTrue(headset.video_frames[0][2], "the first video frame of a window is a keyframe")

        opened2 = await headset.request({"type": "open", "target": "window:2"}, ("opened", "error"), 15)
        self.assertEqual(opened2["slot"], 1)
        t1 = time.monotonic()
        await until(lambda: headset.frame_count("slot1", t1) >= 1, 3, "a picture on slot 1")

        # Input reaches the dispatcher on the slot it was sent on (view only: refused, but counted).
        headset.send_input(1, {"type": "move", "x": 0.5, "y": 0.5})
        await until(lambda: runner.active.slots[1].input_messages >= 1, 5, "the input message")

        headset.send({"type": "close", "slot": 0})
        await until(lambda: runner.active.slots[0].window is None, 5, "slot 0 to close")
        self.assertEqual(runner.active.slots[0].track.active, False)

        # The headset going away ends the session; the runner dials the cable again.
        await self.headset.drop_session()
        await until(lambda: runner.session_count >= 2 and runner.active is not None, 20, "the redial")
        self.assertTrue(any("closed the connection" in r for r in runner.end_reasons), runner.end_reasons)

    async def test_an_older_headset_app_gets_a_clear_reason(self):
        runner = await self.start_usb_runner(stream_allowed=False)
        await until(lambda: runner.attempt_count >= 1 and self.headset.refusals, 20, "a refused attempt")
        self.assertIsNone(runner.active)
        self.assertIsNone(self.headset.session)
        self.assertTrue(any("unsupported mode" in r for r in self.headset.refusals), self.headset.refusals)


if __name__ == "__main__":
    unittest.main()
