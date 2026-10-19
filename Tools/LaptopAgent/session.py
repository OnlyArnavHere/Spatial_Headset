"""The headset link: one WebRTC session per headset, set up by the signed handshake (handshake.py) and carrying the
H.264 that ffmpeg already encoded, without decoding or re-encoding it.

  ffmpeg --RTSP--> MediaMTX (127.0.0.1) --RTSP--> MediaPlayer(decode=False) --av.Packet--> RTCRtpSender --SRTP--> headset

The agent is the offerer. One RTCPeerConnection has a fixed pool of MAX_WINDOWS send-only H.264 video transceivers
("slots") and negotiated, reliable data channels: `control` (id 0) and `input-k` (id 1+k, one per slot). Nothing is
ever renegotiated: opening a window attaches a stream to a free slot, closing it detaches the stream again. There are
no ICE servers and no trickle: the offer already holds every candidate when it is signed.

Control channel (JSON text, at most 16 KiB per message; anything bigger ends the session):
  agent -> headset  {"type":"welcome","version":5,"slots":[{"slot","mid"}],"input","keyboard","cast_only"}
  headset -> agent  {"type":"list"}                  agent -> headset {"type":"targets","targets":[...]}
  headset -> agent  {"type":"open","target":id}      agent -> headset {"type":"opened","id","path","slot","mid","target"}
                                                     or {"type":"error","reason":"gone"|"minimized"|"no slot"}
  headset -> agent  {"type":"close","slot":n}
  headset -> agent  {"type":"ping","id","t"}         agent -> headset {"type":"pong","id","t","agent_ms"}
  agent -> headset  {"type":"error","reason":"gone","slot":n} when a streamed window goes away
  agent -> headset  {"type":"closed","slot":n,"reason":"stream ended"} when a window's video feed stopped on its own
                                                     (the slot is already free again)
The input channels are a stub for now: they count messages and drop them.

Each slot's sender holds a SlotTrack, a small queue the window's packets are forwarded into. sender.replaceTrack
(slot.track) opens a window and replaceTrack(None) closes it. The extra queue is what keeps a slot reusable: aiortc's
sender loop stays blocked in recv() of the track it last read, and a MediaPlayer track that is stopped under it would
end that loop for good.

LinkRunner dials the headsets (given addresses, Bonjour, the USB hotspot), one session at a time, and redials with a
backoff after a session ends.
"""
import asyncio
import json
import logging
import re
import time
from dataclasses import dataclass
from fractions import Fraction
from typing import Callable

import av
from aiortc import RTCConfiguration, RTCPeerConnection, RTCRtpSender, RTCSessionDescription
from aiortc.contrib.media import MediaPlayer
from aiortc.mediastreams import MediaStreamError, MediaStreamTrack

import discovery
import handshake
import identity
import pairing
from streams import Stream, StreamManager

log = logging.getLogger("session")

PROTOCOL_VERSION = 5
MAX_CONTROL_MESSAGE = 16 * 1024
MAX_PENDING_REQUESTS = 8      # control requests handled at once; more are dropped
MAX_TARGETS = 100             # windows listed in one `targets` message
PLAYER_RETRY_S = 5.0          # how long to wait for ffmpeg to start publishing
DISCONNECTED_LIMIT_S = 10.0   # a session ends after this long in "disconnected"
CONNECT_LIMIT_S = 20.0        # ...or if the peer connection never gets to "connected"
SLOT_QUEUE = 120
CLOCK_HZ = 90000
BACKOFF_START_S = 1.0
BACKOFF_MAX_S = 30.0
BACKOFF_MAX_WIRED_S = 10.0
MAX_DISCOVERED = 16           # headsets found by discovery that are dialled at once
DROP_AFTER_FAILURES = 5       # a Bonjour-found headset is forgotten after this many failures in a row
_IDR = re.compile(rb"\x00\x00\x01[\x25\x45\x65]")  # start code + NAL header of an IDR slice (type 5)
_SPS = re.compile(rb"\x00\x00\x01[\x27\x47\x67]")  # ...of a sequence parameter set (type 7)


_OTHER_FINGERPRINTS = re.compile(r"a=fingerprint:(?!sha-256 )[^\r\n]*(\r\n|\n)")


def sha256_only(sdp: str) -> str:
    """aiortc lists sha-256, -384 and -512 DTLS fingerprints; the link signs exactly one, sha-256."""
    return _OTHER_FINGERPRINTS.sub("", sdp)


def contains_idr(data: bytes) -> bool:
    return _IDR.search(data) is not None


def annexb_parameter_sets(extradata: bytes) -> bytes:
    """SPS and PPS as Annex B from a stream's extradata (already Annex B, or an avcC record)."""
    extradata = bytes(extradata or b"")
    if extradata[:1] != b"\x01" or len(extradata) < 7:
        return extradata
    out, pos = b"", 6
    count = extradata[5] & 0x1F  # SPS entries, then a PPS count byte and the PPS entries
    for part in range(2):
        for _ in range(count):
            size = int.from_bytes(extradata[pos:pos + 2], "big")
            out += b"\x00\x00\x00\x01" + extradata[pos + 2:pos + 2 + size]
            pos += 2 + size
        if part == 0:
            count, pos = extradata[pos], pos + 1
    return out


def player_parameter_sets(player) -> bytes:
    """What the RTSP stream announced about its codec: an encoder that doesn't repeat SPS/PPS before every IDR would
    otherwise leave a window that joins mid-stream undecodable."""
    try:
        stream = player._MediaPlayer__container.streams.video[0]
        return annexb_parameter_sets(bytes(stream.codec_context.extradata or b""))
    except Exception:
        return b""


class SlotTrack(MediaStreamTrack):
    """A video track the sender reads; the window's pump pushes pre-encoded packets into it."""
    kind = "video"

    def __init__(self) -> None:
        super().__init__()
        self._queue: asyncio.Queue = asyncio.Queue(SLOT_QUEUE)

    async def recv(self):
        if self.readyState != "live":
            raise MediaStreamError
        return await self._queue.get()

    def push(self, packet) -> None:
        try:
            self._queue.put_nowait(packet)
        except asyncio.QueueFull:
            pass  # the sender is stuck; dropping is better than growing

    def clear(self) -> None:
        while not self._queue.empty():
            self._queue.get_nowait()


@dataclass(eq=False)
class Slot:
    index: int
    transceiver: object
    track: SlotTrack
    channel: object
    window: "Window | None" = None
    reserved: bool = False
    input_messages: int = 0
    clock: int = 0  # last RTP-clock tick (90 kHz) sent on this slot

    @property
    def free(self) -> bool:
        return self.window is None and not self.reserved

    @property
    def mid(self) -> str | None:
        return self.transceiver.mid


@dataclass(eq=False)
class Window:
    target_id: str
    stream: Stream
    player: MediaPlayer
    pump: asyncio.Task | None = None


def h264_preferences() -> list:
    return [c for c in RTCRtpSender.getCapabilities("video").codecs
            if c.mimeType.lower() == "video/h264" and c.parameters.get("packetization-mode") == "1"]


def open_player(url: str) -> MediaPlayer:
    return MediaPlayer(url, format="rtsp", options={"rtsp_transport": "tcp"}, decode=False)


class LinkSession:
    def __init__(self, streams: StreamManager, rtsp_port: int, *, max_windows: int = 4, input_enabled: bool = False,
                 keyboard_enabled: bool = False, cast_only: bool = False):
        self.streams = streams
        self.rtsp_port = rtsp_port
        self.max_windows = max_windows
        self.input_enabled = input_enabled
        self.keyboard_enabled = keyboard_enabled
        self.cast_only = cast_only
        self.pc: RTCPeerConnection | None = None
        self.control = None
        self.slots: list[Slot] = []
        self.control_closed = False
        self.closed = False
        self._wake = asyncio.Event()
        self._requests: set[asyncio.Task] = set()
        self._welcomed = False
        self._oversize = False

    # --- Setup ---------------------------------------------------------------------------------------------------

    async def build_offer(self) -> str:
        """Creates the peer connection and returns the offer SDP once ICE gathering is complete."""
        pc = self.pc = RTCPeerConnection(RTCConfiguration(iceServers=[]))
        preferences = h264_preferences()
        self.control = pc.createDataChannel("control", negotiated=True, id=0)
        self.control.on("open", self._on_control_open)
        self.control.on("close", self._on_control_close)
        self.control.on("message", self._on_control_message)
        for k in range(self.max_windows):
            transceiver = pc.addTransceiver("video", direction="sendonly")
            transceiver.setCodecPreferences(preferences)
            channel = pc.createDataChannel(f"input-{k}", negotiated=True, id=1 + k)
            slot = Slot(k, transceiver, SlotTrack(), channel)
            channel.on("message", lambda message, s=slot: self._on_input(s, message))
            self.slots.append(slot)
        pc.on("connectionstatechange", self._wake.set)
        await pc.setLocalDescription(await pc.createOffer())  # aiortc finishes gathering before this returns
        return sha256_only(pc.localDescription.sdp)

    async def apply_answer(self, sdp: str) -> None:
        await self.pc.setRemoteDescription(RTCSessionDescription(sdp=sdp, type="answer"))

    # --- Running -------------------------------------------------------------------------------------------------

    async def run(self) -> str:
        """Returns why the session ended."""
        loop = asyncio.get_running_loop()
        started = loop.time()
        disconnected_since = None
        while True:
            state = self.pc.connectionState
            now = loop.time()
            if self._oversize:
                return "control message too large"
            if state in ("failed", "closed"):
                return f"connection {state}"
            if self.control_closed:
                return "control channel closed"
            if state == "disconnected":
                disconnected_since = disconnected_since if disconnected_since is not None else now
                if now - disconnected_since > DISCONNECTED_LIMIT_S:
                    return f"disconnected for more than {DISCONNECTED_LIMIT_S:g} s"
            else:
                disconnected_since = None
            if state != "connected" and now - started > CONNECT_LIMIT_S:
                return "never connected"
            for slot in self.slots:
                window = slot.window
                if window is not None and not window.stream.running:
                    await self._end_window(slot, "gone")
                elif window is not None and window.pump is not None and window.pump.done():
                    await self._end_window(slot, "stream ended", closed=True)
            self._wake.clear()
            try:
                await asyncio.wait_for(self._wake.wait(), 0.5)
            except asyncio.TimeoutError:
                pass

    async def close(self) -> None:
        """Releases every stream and player and closes the peer connection. Safe to call twice."""
        if self.closed:
            return
        self.closed = True
        for task in list(self._requests):
            task.cancel()
        if self._requests:
            await asyncio.gather(*self._requests, return_exceptions=True)
        for slot in self.slots:
            await self._close_window(slot)
        if self.pc is not None:
            try:
                await asyncio.wait_for(self.pc.close(), 5.0)  # aioice can wait forever on a busy UDP socket
            except (asyncio.TimeoutError, Exception):
                log.debug("Peer connection did not close cleanly", exc_info=True)

    # --- Control channel -----------------------------------------------------------------------------------------

    def _send(self, message: dict) -> None:
        if self.control is not None and self.control.readyState == "open":
            self.control.send(json.dumps(message, separators=(",", ":")))

    def _on_control_open(self) -> None:
        if self._welcomed:
            return
        self._welcomed = True
        self._send({"type": "welcome", "version": PROTOCOL_VERSION,
                    "slots": [{"slot": s.index, "mid": s.mid} for s in self.slots],
                    "input": self.input_enabled, "keyboard": self.input_enabled and self.keyboard_enabled,
                    "cast_only": self.cast_only})

    def _on_control_close(self) -> None:
        self.control_closed = True
        self._wake.set()

    def _on_input(self, slot: Slot, message) -> None:
        slot.input_messages += 1  # the dispatcher comes later; for now the message is counted and dropped

    def _on_control_message(self, raw) -> None:
        size = len(raw) if isinstance(raw, (bytes, bytearray)) else len(raw.encode("utf-8", "replace"))
        if size > MAX_CONTROL_MESSAGE:
            log.warning("Control message of %d bytes refused: ending the session", size)
            self._oversize = True
            self._wake.set()
            return
        try:
            message = json.loads(raw)
            kind = message["type"]
        except (ValueError, KeyError, TypeError):
            return
        if not isinstance(kind, str):
            return
        if kind == "ping":
            self._send({"type": "pong", "id": message.get("id"), "t": message.get("t"),
                        "agent_ms": time.time() * 1000.0})
            return
        if len(self._requests) >= MAX_PENDING_REQUESTS or self.closed:
            return
        task = asyncio.ensure_future(self._request(kind, message))
        self._requests.add(task)
        task.add_done_callback(self._requests.discard)

    async def _request(self, kind: str, message: dict) -> None:
        try:
            if kind == "list":
                self._send({"type": "targets", "targets": self.streams.listing()[:MAX_TARGETS]})
            elif kind == "open":
                target = message.get("target")
                if isinstance(target, str) and 0 < len(target) <= 200:
                    await self._open(target)
            elif kind == "close":
                slot = message.get("slot")
                if isinstance(slot, int) and not isinstance(slot, bool) and 0 <= slot < len(self.slots):
                    await self._close_window(self.slots[slot])
        except asyncio.CancelledError:
            raise
        except Exception:
            log.exception("Control request %r failed", kind)

    # --- Windows -------------------------------------------------------------------------------------------------

    async def _open(self, target_id: str) -> None:
        slot = next((s for s in self.slots if s.free), None)
        if slot is None:
            self._send({"type": "error", "reason": "no slot"})
            return
        slot.reserved = True
        stream = None
        attached = False
        try:
            try:
                stream = self.streams.acquire(target_id)
            except LookupError as e:
                self._send({"type": "error", "reason": "minimized" if str(e) == "minimized" else "gone"})
                return
            except Exception:
                log.exception("Couldn't start a stream for %s", target_id)
                self._send({"type": "error", "reason": "gone"})
                return
            try:
                player = await self._start_player(stream.path)
            except Exception as e:
                log.warning("Couldn't read /%s from MediaMTX: %s", stream.path, e)
                self._send({"type": "error", "reason": "gone"})
                return
            if self.closed:
                await self._stop_player(player)
                return
            window = Window(target_id, stream, player)
            window.pump = asyncio.ensure_future(self._pump(slot, window))
            slot.track.clear()
            slot.window = window
            attached = True
            slot.transceiver.sender.replaceTrack(slot.track)
            self._send({"type": "opened", "id": target_id, "path": stream.path, "slot": slot.index, "mid": slot.mid,
                        "target": stream.target.describe()})
            log.info("Window %s on slot %d (mid %s)", stream.target.description, slot.index, slot.mid)
        finally:
            slot.reserved = False
            if stream is not None and not attached:
                self.streams.release(stream)  # also when cancelled: otherwise ffmpeg would keep running

    async def _start_player(self, path: str) -> MediaPlayer:
        loop = asyncio.get_running_loop()
        url = f"rtsp://127.0.0.1:{self.rtsp_port}/{path}"
        deadline = loop.time() + PLAYER_RETRY_S
        while True:
            future = loop.run_in_executor(None, open_player, url)
            try:
                done, _ = await asyncio.wait({future}, timeout=max(0.5, deadline - loop.time()))
            except BaseException:
                future.add_done_callback(self._stop_late_player)  # cancelled: nobody will pick the player up
                raise
            try:
                if not done:
                    future.add_done_callback(self._stop_late_player)
                    raise asyncio.TimeoutError()
                return future.result()
            except Exception:
                if loop.time() >= deadline:
                    raise
            await asyncio.sleep(0.25)

    def _stop_late_player(self, future) -> None:
        """Done-callback for an open_player call nobody waits for any more: its MediaPlayer must still be stopped."""
        if future.cancelled() or future.exception() is not None:
            return
        asyncio.ensure_future(self._stop_player(future.result()))

    async def _pump(self, slot: Slot, window: Window) -> None:
        """Forwards the window's packets to its slot, starting at a keyframe so the headset never decodes garbage.
        Timestamps are rewritten onto the slot's own clock, which keeps running across windows: a receiver drops
        frames whose RTP timestamp goes backwards, which is what the next window's own clock would do."""
        track = window.player.video
        headers = player_parameter_sets(window.player)
        started = False
        origin = shift = 0
        try:
            while True:
                packet = await track.recv()
                data = None
                if not started or packet.is_keyframe:
                    data = bytes(packet)
                    if not (packet.is_keyframe or contains_idr(data)):
                        if started:
                            data = None
                        else:
                            continue
                if data is not None and headers and contains_idr(data) and _SPS.search(data) is None:
                    fixed = av.Packet(headers + data)
                    fixed.is_keyframe = True
                    fixed.pts, fixed.dts, fixed.time_base = packet.pts, packet.dts, packet.time_base
                    packet = fixed
                ticks = round(float(packet.pts * packet.time_base) * CLOCK_HZ)
                if not started:
                    started = True
                    origin = ticks
                    shift = slot.clock + CLOCK_HZ // 30  # one frame after whatever this slot sent last
                out = shift + ticks - origin
                slot.clock = max(slot.clock, out)
                packet.time_base = Fraction(1, CLOCK_HZ)
                packet.pts = packet.dts = out
                slot.track.push(packet)
        except MediaStreamError:
            pass
        if slot.window is window:
            self._wake.set()  # the stream ended on its own; run() reports it

    async def _end_window(self, slot: Slot, reason: str, closed: bool = False) -> None:
        index = slot.index
        if slot.window is None:
            return
        await self._close_window(slot)
        self._send({"type": "closed" if closed else "error", "reason": reason, "slot": index})

    async def _close_window(self, slot: Slot) -> None:
        window, slot.window = slot.window, None
        if window is None:
            return
        slot.transceiver.sender.replaceTrack(None)
        if window.pump is not None:
            window.pump.cancel()
            await asyncio.gather(window.pump, return_exceptions=True)
        slot.track.clear()
        self.streams.release(window.stream)
        await self._stop_player(window.player)

    @staticmethod
    async def _stop_player(player: MediaPlayer) -> None:
        # Stopping joins the player's reader thread, which can wait for the next packet.
        try:
            await asyncio.wait_for(asyncio.get_running_loop().run_in_executor(None, player.video.stop), 5.0)
        except Exception:
            log.debug("Player did not stop cleanly", exc_info=True)


class LinkRunner:
    """Finds headsets and keeps one session going with the first one that completes the handshake."""

    def __init__(self, ident: identity.Identity, store: pairing.PairStore,
                 streams: StreamManager, rtsp_port: int, *, max_windows: int = 4, input_enabled: bool = False,
                 keyboard_enabled: bool = False, cast_only: bool = False,
                 pinned: list[tuple[str, int]] | None = None, discover: bool = False,
                 browse: Callable = discovery.browse, wired: Callable = discovery.wired_candidates):
        self.ident = ident
        self.store = store
        self.streams = streams
        self.rtsp_port = rtsp_port
        self.options = dict(max_windows=max_windows, input_enabled=input_enabled,
                            keyboard_enabled=keyboard_enabled, cast_only=cast_only)
        self.pinned = list(pinned or [])
        self.discover = discover
        self._browse = browse
        self._wired = wired
        self.limiter = handshake.HandshakeLimiter()
        self.blocklist = handshake.AuthBlocklist()
        self.active: LinkSession | None = None
        self.active_spki: bytes | None = None
        self.session_count = 0
        self.attempt_count = 0
        self.end_reasons: list[str] = []
        self._idle = asyncio.Event()
        self._idle.set()
        self._workers: dict[tuple[str, int], tuple[asyncio.Task, asyncio.Event]] = {}
        self._discovered: set[tuple[str, int]] = set()  # workers started by discovery rather than by hand

    async def run(self) -> None:
        tasks = []
        try:
            for host, port in self.pinned:
                self._add_target(host, port, BACKOFF_MAX_S)  # given by hand: retried forever
            if self.discover:
                tasks = [asyncio.ensure_future(self._browse_loop()), asyncio.ensure_future(self._wired_loop())]
            await asyncio.Event().wait()
        finally:
            for task in tasks + [w[0] for w in self._workers.values()]:
                task.cancel()
            await asyncio.gather(*tasks, *[w[0] for w in self._workers.values()], return_exceptions=True)
            if self.active is not None:
                await self.active.close()

    # --- Finding headsets ----------------------------------------------------------------------------------------

    def _add_target(self, host: str, port: int, cap: float, drop_after: int = 0) -> bool:
        key = (host, port)
        if key in self._workers:
            return False
        wake = asyncio.Event()
        self._workers[key] = (asyncio.ensure_future(self._worker(key, cap, wake, drop_after)), wake)
        return True

    def _found(self, host: str, port: int, cap: float, source: str, drop_after: int = 0) -> None:
        if (host, port) not in self._workers and len(self._discovered) >= MAX_DISCOVERED:
            return
        if self._add_target(host, port, cap, drop_after):
            self._discovered.add((host, port))
            log.info("Found a headset at %s:%d (%s)", host, port, source)
            for _, wake in self._workers.values():
                wake.set()  # redial at once

    async def _browse_loop(self) -> None:
        while True:
            try:
                async for host, port in self._browse():
                    self._found(host, port, BACKOFF_MAX_S, "Bonjour", DROP_AFTER_FAILURES)
            except asyncio.CancelledError:
                raise
            except Exception as e:
                log.warning("Bonjour discovery stopped (%s); retrying in 10 s", e)
            await asyncio.sleep(10)

    async def _wired_loop(self) -> None:
        loop = asyncio.get_running_loop()
        while True:
            for host, port in await loop.run_in_executor(None, self._wired):
                self._found(host, port, BACKOFF_MAX_WIRED_S, "USB hotspot")
            await asyncio.sleep(30)

    # --- Dialling ------------------------------------------------------------------------------------------------

    async def _worker(self, key: tuple[str, int], cap: float, wake: asyncio.Event, drop_after: int = 0) -> None:
        delay = BACKOFF_START_S
        last_failure = None
        failures = 0
        while True:
            await self._idle.wait()
            outcome, reason = await self._attempt(key)
            if outcome == "failed":
                if reason != last_failure:
                    log.info("Headset %s:%d: %s", *key, reason)
                else:
                    log.debug("Headset %s:%d: %s", *key, reason)
                last_failure = reason
                failures += 1
                if drop_after and failures >= drop_after:
                    log.info("Headset %s:%d: giving up after %d failed attempts (it is re-added if Bonjour sees it again)",
                             *key, failures)
                    self._workers.pop(key, None)
                    self._discovered.discard(key)
                    return
                wait = delay
                delay = min(cap, delay * 2)
            else:
                delay, last_failure, wait, failures = BACKOFF_START_S, None, BACKOFF_START_S, 0
            wake.clear()
            try:
                await asyncio.wait_for(wake.wait(), wait)
                delay = BACKOFF_START_S
            except asyncio.TimeoutError:
                pass

    async def _attempt(self, key: tuple[str, int]) -> tuple[str, str]:
        host, port = key
        session = None
        self.attempt_count += 1

        async def make_offer() -> str:
            nonlocal session
            session = LinkSession(self.streams, self.rtsp_port, **self.options)
            return await session.build_offer()

        try:
            result = await handshake.agent_connect(host, port, self.ident, self.store, make_offer,
                                                   limiter=self.limiter, blocklist=self.blocklist)
        except handshake.HandshakeError as e:
            if session is not None:
                await session.close()
            return "failed", e.reason if not e.remote else f"the headset refused: {e.reason}"
        except asyncio.CancelledError:
            if session is not None:
                await session.close()
            raise
        except Exception as e:
            if session is not None:
                await session.close()
            log.debug("Handshake with %s:%d crashed", host, port, exc_info=True)
            return "failed", f"{type(e).__name__}: {e}"

        if self.active is not None:
            same = self.active_spki == result.headset_spki
            log.info("Ignoring %s (%s:%d): %s", result.name, host, port,
                     "that headset is already connected" if same else "another headset is connected first")
            await session.close()
            return "ignored", ""
        self.active, self.active_spki = session, result.headset_spki
        self._idle.clear()
        reason = "?"
        try:
            await session.apply_answer(result.answer_sdp)
            log.info("Linked with %s (%s:%d, %s)", result.name, host, port, result.mode)
            self.session_count += 1
            reason = await session.run()
            log.info("Session with %s ended: %s", result.name, reason)
        except asyncio.CancelledError:
            raise
        except Exception as e:
            log.warning("Session with %s failed: %s", result.name, e)
            reason = f"error: {e}"
            self.end_reasons.append(reason)
            return "failed", reason
        finally:
            await session.close()
            self.active, self.active_spki = None, None
            self._idle.set()
        self.end_reasons.append(reason)
        return "session", reason
