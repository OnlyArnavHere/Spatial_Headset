"""A Python stand-in for the headset: it listens for the agent's link handshake or pairing exchange, answers with
aiortc, then decodes and counts the video it receives. Used by test_session_e2e.py, and from the command line:

  python fake_headset.py --pairing --home DIR --port 0       pairing mode: prints a code, accepts `agent.py --pair`
                                                             (--decline answers the confirmation with no)
  python fake_headset.py --home DIR --port 0                 paired mode: accepts the laptops pinned in DIR/pairs.json
  python fake_headset.py --pairing --advertise --host 192.168.1.5     also announce itself over Bonjour (m=pair)

Its own identity and the laptops it trusts live in --home (use a scratch folder: the real keys are never touched).
Then run `python agent.py --pair --headset 127.0.0.1:PORT` and, once paired, `python agent.py --headset 127.0.0.1:PORT`.
The control channel accepts commands typed as JSON lines on stdin with --stdin, e.g. {"type":"open","target":"desktop"}.
"""
import argparse
import asyncio
import contextlib
import json
import logging
import socket
import sys
import time
from pathlib import Path
from unittest import mock

from aiortc import RTCConfiguration, RTCPeerConnection, RTCSessionDescription
from aiortc.mediastreams import MediaStreamError

import discovery
import handshake
import identity
import pair_protocol
import pairing
from session import sha256_only

log = logging.getLogger("fake_headset")


@contextlib.contextmanager
def localhost_only():
    """Makes aioice offer only 127.0.0.1 as a host candidate (it skips loopback by default), so a test is hermetic."""
    with mock.patch("aioice.ice.get_host_addresses", lambda use_ipv4, use_ipv6: ["127.0.0.1"]):
        yield


def _route_by_mid() -> None:
    """aiortc routes incoming RTP by SSRC and payload type only, and the agent's offer has no a=ssrc lines (the slots
    start without a track), so with several H.264 slots most packets would be dropped. A real headset (libwebrtc)
    routes by the MID header extension; teach aiortc's router to do that too."""
    from aiortc.rtcdtlstransport import RtpRouter
    if getattr(RtpRouter, "_mid_routing", False):
        return
    plain = RtpRouter.route_rtp

    def route_rtp(self, packet):
        receiver = self.mid_table.get(getattr(packet.extensions, "mid", None))
        if receiver is not None:
            self.ssrc_table[packet.ssrc] = receiver
            return receiver
        return plain(self, packet)

    RtpRouter.route_rtp = route_rtp
    RtpRouter._mid_routing = True


class HeadsetSession:
    """The headset's side of one WebRTC session."""

    def __init__(self) -> None:
        _route_by_mid()
        self.pc: RTCPeerConnection | None = None
        self.control = None
        self.frames: list[tuple[float, str | None, int, int]] = []   # (time, mid, width, height)
        self.messages: list[dict] = []
        self.inbox: asyncio.Queue = asyncio.Queue()
        self.inputs: list = []                                       # the input-k channels, by slot
        self.input_messages: list[tuple[int, dict]] = []             # (slot, message) the agent sent on them
        self.input_inbox: asyncio.Queue = asyncio.Queue()
        self._readers: list[asyncio.Task] = []

    async def answer(self, offer_sdp: str) -> str:
        pc = self.pc = RTCPeerConnection(RTCConfiguration(iceServers=[]))
        slots = sum(1 for line in offer_sdp.splitlines() if line.startswith("m=video"))
        self.control = pc.createDataChannel("control", negotiated=True, id=0)
        self.control.on("message", self._on_message)
        for k in range(slots):
            channel = pc.createDataChannel(f"input-{k}", negotiated=True, id=1 + k)
            channel.on("message", lambda raw, k=k: self._on_input_message(k, raw))
            self.inputs.append(channel)

        @pc.on("track")
        def on_track(track):
            if track.kind == "video":
                self._readers.append(asyncio.ensure_future(self._read(track)))

        await pc.setRemoteDescription(RTCSessionDescription(sdp=offer_sdp, type="offer"))
        await pc.setLocalDescription(await pc.createAnswer())
        return sha256_only(pc.localDescription.sdp)

    def _on_message(self, raw) -> None:
        try:
            message = json.loads(raw)
        except ValueError:
            return
        self.messages.append(message)
        self.inbox.put_nowait(message)

    def _on_input_message(self, slot: int, raw) -> None:
        try:
            message = json.loads(raw)
        except ValueError:
            return
        self.input_messages.append((slot, message))
        self.input_inbox.put_nowait((slot, message))

    def send_input(self, slot: int, message) -> None:
        """Sends a message on input-`slot`; a str or bytes goes out as it is (to test malformed input)."""
        self.inputs[slot].send(message if isinstance(message, (str, bytes)) else json.dumps(message))

    async def wait_for_input(self, types: tuple[str, ...] | str, timeout: float = 10.0) -> tuple[int, dict]:
        types = (types,) if isinstance(types, str) else types
        async with asyncio.timeout(timeout):
            while True:
                slot, message = await self.input_inbox.get()
                if message.get("type") in types:
                    return slot, message

    async def _read(self, track) -> None:
        while True:
            try:
                frame = await track.recv()
            except MediaStreamError:
                return
            mid = next((t.mid for t in self.pc.getTransceivers() if t.receiver.track is track), None)
            self.frames.append((time.monotonic(), mid, frame.width, frame.height))

    def send(self, message: dict) -> None:
        self.control.send(json.dumps(message))

    async def request(self, message: dict, expect: str | tuple[str, ...], timeout: float = 10.0) -> dict:
        """Sends a control message and returns the next message of a type in `expect`."""
        expect = (expect,) if isinstance(expect, str) else expect
        self.send(message)
        return await self.wait_for(expect, timeout)

    async def wait_for(self, types: tuple[str, ...] | str, timeout: float = 10.0) -> dict:
        types = (types,) if isinstance(types, str) else types
        async with asyncio.timeout(timeout):
            while True:
                message = await self.inbox.get()
                if message.get("type") in types:
                    return message

    def frame_count(self, mid: str | None = None, since: float = 0.0) -> int:
        return sum(1 for t, m, _, _ in self.frames if t >= since and (mid is None or m == mid))

    async def close(self) -> None:
        for task in self._readers:
            task.cancel()
        await asyncio.gather(*self._readers, return_exceptions=True)
        if self.pc is not None:
            await self.pc.close()


class PrefixedReader:
    """Gives a StreamReader's already-consumed bytes back, then reads on from it (the handshake only uses readexactly)."""

    def __init__(self, prefix: bytes, reader) -> None:
        self._prefix = prefix
        self._reader = reader

    async def readexactly(self, n: int) -> bytes:
        take, self._prefix = self._prefix[:n], self._prefix[n:]
        if len(take) == n:
            return take
        return take + await self._reader.readexactly(n - len(take))


class FakeHeadset:
    """Listens like the real headset: the first frame says whether this is a paired link (wire v1) or a pairing
    exchange (wire v2, only while a code is active). Agents it trusts live in its own PairStore (in `home`)."""

    def __init__(self, home: Path, *, name: str = "Fake Headset", agent_spkis=(), pairing_mode: bool = False,
                 confirm: bool = True, host: str = "127.0.0.1", port: int = 0, advertise: bool = False,
                 gate: handshake.HeadsetGate | None = None, pair_iterations: int = pair_protocol.PAIR_ITERATIONS,
                 echo=print):
        self.home = Path(home)
        self.ident = identity.load_or_create(self.home)
        self.name = name
        self.agents = pairing.PairStore(self.home)
        for i, spki in enumerate(agent_spkis):
            self.agents.add(f"agent-{i}", bytes(spki))
        self.issuer = pairing.CodeIssuer()
        self.confirm = confirm
        self.pair_iterations = pair_iterations
        self.host = host
        self.port = port
        self.advertise = advertise
        self.gate = gate or handshake.HeadsetGate()
        self.session: HeadsetSession | None = None
        self.handshakes = 0
        self.refusals: list[str] = []
        self.paired: list[str] = []
        self.prompts: list[str] = []
        self._echo = echo
        self._server: asyncio.AbstractServer | None = None
        self._azc = None
        self._info = None
        self._want_pairing = pairing_mode

    @property
    def agent_spkis(self) -> list[bytes]:
        return [p.public_key for p in self.agents.list()]

    @property
    def code(self) -> str | None:
        return self.issuer.code

    async def start(self) -> int:
        self._server = await asyncio.start_server(self._handle, self.host, self.port)
        self.port = self._server.sockets[0].getsockname()[1]
        if self.advertise:
            from zeroconf.asyncio import AsyncZeroconf
            self._azc = AsyncZeroconf(interfaces=[self.host])
            await self._register()
        if self._want_pairing:
            await self.start_pairing()
        return self.port

    async def _register(self) -> None:
        from zeroconf import ServiceInfo
        instance = discovery.random_instance_name()
        properties = {"v": "1"}
        if self.issuer.active:
            properties["m"] = "pair"
        self._info = ServiceInfo(handshake.SERVICE_TYPE, f"{instance}.{handshake.SERVICE_TYPE}", port=self.port,
                                 properties=properties, addresses=[socket.inet_aton(self.host)],
                                 server=f"{instance}.local.")
        await self._azc.async_register_service(self._info)

    async def start_pairing(self) -> str:
        """Issues a code (printed, as the real headset would show it) and, if advertising, announces m=pair."""
        code = self.issuer.issue()
        self._echo(f"PAIRING CODE: {pairing.format_code(code)}")
        if self._azc is not None:
            with contextlib.suppress(Exception):
                await self._azc.async_unregister_service(self._info)
            await self._register()
        return code

    async def stop(self) -> None:
        if self._azc is not None:
            with contextlib.suppress(Exception):
                await self._azc.async_unregister_service(self._info)
            await self._azc.async_close()
            self._azc = None
        if self._server is not None:
            self._server.close()
            await self._server.wait_closed()
            self._server = None
        await self.drop_session()

    async def drop_session(self) -> None:
        """Closes the current WebRTC session but keeps listening, as a headset that went away and came back would."""
        session, self.session = self.session, None
        if session is not None:
            await session.close()

    async def _first_frame(self, reader) -> tuple[dict | None, bytes]:
        try:
            async with asyncio.timeout(5):
                head = await reader.readexactly(4)
                length = int.from_bytes(head, "big")
                if not 0 < length <= handshake.MAX_FRAME:
                    return None, head
                body = await reader.readexactly(length)
        except (asyncio.TimeoutError, asyncio.IncompleteReadError, OSError):
            return None, b""
        try:
            message = json.loads(body)
        except ValueError:
            message = None
        return message if isinstance(message, dict) else None, head + body

    async def _handle(self, reader, writer) -> None:
        message, raw = await self._first_frame(reader)
        reader = PrefixedReader(raw, reader)
        if message is not None and message.get("v") == pair_protocol.VERSION and message.get("type") == "pair":
            await self._handle_pair(reader, writer)
        else:
            await self._handle_link(reader, writer)

    async def _handle_pair(self, reader, writer) -> None:
        async def confirm_cb(prompt: str) -> bool:
            self.prompts.append(prompt)
            self._echo(f"Confirm: {prompt} -> {'yes' if self.confirm else 'no'}")
            return self.confirm

        def add_cb(spki_a: bytes, name_a: str) -> None:
            self.agents.add(name_a, spki_a)
            self.paired.append(name_a)

        try:
            await pair_protocol.serve_pair_connection(reader, writer, self.gate, self.ident, self.issuer, add_cb,
                                                      confirm_cb, self.name, iterations=self.pair_iterations)
        except handshake.HandshakeError as e:
            self.refusals.append(e.reason)
            log.info("Pairing refused: %s", e.reason)
            return
        except Exception:
            log.exception("Pairing crashed")
            return
        log.info("Paired")

    async def _handle_link(self, reader, writer) -> None:
        pending = HeadsetSession()

        async def make_answer(offer_sdp: str) -> str:
            return await pending.answer(offer_sdp)

        try:
            await handshake.serve_headset_connection(reader, writer, self.gate, self.ident, self.name,
                                                     self.agent_spkis, make_answer)
        except handshake.HandshakeError as e:
            self.refusals.append(e.reason)
            log.info("Handshake refused: %s", e.reason)
            await pending.close()
            return
        except Exception:
            log.exception("Handshake crashed")
            await pending.close()
            return
        await self.drop_session()
        self.session = pending
        self.handshakes += 1
        log.info("Session up")


async def amain(args) -> None:
    home = Path(args.home) if args.home else identity.default_home() / "fake_headset"
    headset = FakeHeadset(home, name=args.name, pairing_mode=args.pairing, confirm=not args.decline, host=args.host,
                          port=args.port, advertise=args.advertise)
    port = await headset.start()
    print(f"fake headset {headset.name!r} (fingerprint {identity.format_fp(headset.ident.fingerprint)}) listening on "
          f"{args.host}:{port}, {'pairing' if args.pairing else 'paired'} mode, "
          f"{len(headset.agents.list())} paired laptop(s){', advertised over Bonjour' if args.advertise else ''}",
          flush=True)

    async def report() -> None:
        last = 0
        while True:
            await asyncio.sleep(2)
            session = headset.session
            if session is not None:
                counts = {}
                for _, mid, w, h in session.frames:
                    counts.setdefault((mid, w, h), 0)
                    counts[(mid, w, h)] += 1
                print(f"frames: {len(session.frames)} total {counts}", flush=True)
                for message in session.messages[last:]:
                    print("control:", json.dumps(message)[:300], flush=True)
                last = len(session.messages)

    async def commands() -> None:
        loop = asyncio.get_running_loop()
        while True:
            line = await loop.run_in_executor(None, sys.stdin.readline)
            if not line:
                return
            try:
                message = json.loads(line)
            except ValueError:
                continue
            if headset.session is not None and headset.session.control is not None:
                headset.session.send(message)

    tasks = [asyncio.ensure_future(report())]
    if args.stdin:
        tasks.append(asyncio.ensure_future(commands()))
    try:
        await asyncio.Event().wait()
    finally:
        for task in tasks:
            task.cancel()
        await headset.stop()


def main() -> None:
    p = argparse.ArgumentParser(description="A fake Spatial Launcher headset for testing the laptop agent.")
    p.add_argument("--host", default="127.0.0.1", help="address to listen on (default 127.0.0.1)")
    p.add_argument("--port", type=int, default=handshake.LINK_PORT, help=f"TCP port (default {handshake.LINK_PORT}; 0 = any)")
    p.add_argument("--name", default="Fake Headset")
    p.add_argument("--home", help="folder for this fake headset's identity key and the laptops it trusts")
    p.add_argument("--pairing", action="store_true", help="show a pairing code and accept a pairing exchange")
    p.add_argument("--decline", action="store_true", help="with --pairing: answer the confirmation with no")
    p.add_argument("--advertise", action="store_true", help="announce _spatiallauncher._tcp over Bonjour on --host")
    p.add_argument("--stdin", action="store_true", help="send JSON lines from stdin on the control channel")
    p.add_argument("--aioice-localhost", action="store_true", help="offer only 127.0.0.1 for media (hermetic runs)")
    args = p.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(name)s: %(message)s", datefmt="%H:%M:%S")
    with localhost_only() if args.aioice_localhost else contextlib.nullcontext():
        try:
            asyncio.run(amain(args))
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    main()
