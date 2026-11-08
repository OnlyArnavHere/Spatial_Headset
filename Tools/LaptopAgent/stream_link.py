"""The headset session over one TCP stream instead of WebRTC: what USB without a network uses (usbmux.py), after the
signed handshake in stream mode (handshake.py) on that same connection.

Everything a WebRTC session carries goes over the stream as frames, with the same messages (session.py's control
protocol and dispatch.py's input messages are unchanged):

  frame = kind (u8) | slot (u8) | length (u32 big-endian) | payload
  kind 1 CONTROL  both ways, slot 0. Payload: one JSON control message (UTF-8), at most 16 KiB.
  kind 2 INPUT    both ways, slot k. Payload: one JSON input message for window slot k, at most 16 KiB.
  kind 3 VIDEO    agent -> headset, slot k. Payload: timestamp (u32 big-endian, 90 kHz, wraps) | flags (u8, bit 0 =
                  keyframe) | one H.264 access unit in Annex B (start codes), at most 4 MiB. The first frame of every
                  window on a slot is a keyframe that carries SPS and PPS.
  Anything else, a slot >= the welcome's slot count, a length of 0 or over the cap ends the session.

The control channel is "open" as soon as the handshake finished: the agent sends welcome at once, with "transport":
"stream" and slots without mids. The session ends when the stream closes, on a read error or on a bad frame, or for
the reasons a WebRTC session ends (session.LinkSession).

Backpressure: when the cable or the phone can't keep up (more than WRITE_HIGH bytes queued), a slot drops frames up to
its next keyframe; the agent forces one every second.
"""
import asyncio
import json
import logging
import struct

import session as session_mod
from session import LinkSession, contains_idr

log = logging.getLogger("stream")

HEADER = struct.Struct(">BBI")
CONTROL, INPUT, VIDEO = 1, 2, 3
MAX_MESSAGE = session_mod.MAX_CONTROL_MESSAGE
MAX_VIDEO = 4 * 1024 * 1024
WRITE_HIGH = 4 * 1024 * 1024
WRITE_LIMIT = 32 * 1024 * 1024  # the headset stopped reading: end the session rather than grow without bound
FLAG_KEY = 1


def frame(kind: int, slot: int, payload: bytes) -> bytes:
    return HEADER.pack(kind, slot, len(payload)) + payload


def video_payload(timestamp: int, key: bool, data: bytes) -> bytes:
    return struct.pack(">IB", timestamp & 0xFFFFFFFF, FLAG_KEY if key else 0) + data


class StreamClosed(Exception):
    pass


async def read_frame(reader, max_slots: int) -> tuple[int, int, bytes]:
    """One frame; raises StreamClosed on EOF and ValueError on a bad frame."""
    try:
        head = await reader.readexactly(HEADER.size)
    except (asyncio.IncompleteReadError, ConnectionError, OSError):
        raise StreamClosed from None
    kind, slot, length = HEADER.unpack(head)
    cap = MAX_VIDEO if kind == VIDEO else MAX_MESSAGE
    if kind not in (CONTROL, INPUT, VIDEO) or length == 0 or length > cap or slot >= max(1, max_slots):
        raise ValueError(f"bad frame (kind {kind}, slot {slot}, {length} bytes)")
    try:
        payload = await reader.readexactly(length)
    except (asyncio.IncompleteReadError, ConnectionError, OSError):
        raise StreamClosed from None
    return kind, slot, payload


class _Sender:
    """Stands in for an RTCRtpSender: replaceTrack switches the slot's sink on and off."""

    def __init__(self, sink: "StreamSlotSink") -> None:
        self._sink = sink

    def replaceTrack(self, track) -> None:  # noqa: N802 (aiortc's name)
        self._sink.active = track is not None
        self._sink.need_key = True


class _Transceiver:
    """Stands in for an RTCRtpTransceiver: no mid, a sender."""
    mid = None

    def __init__(self, sink: "StreamSlotSink") -> None:
        self.sender = _Sender(sink)


class StreamSlotSink:
    """What a slot's pump pushes packets into (session.SlotTrack's place): writes them as VIDEO frames."""

    def __init__(self, link: "StreamLinkSession", index: int) -> None:
        self._link, self.index = link, index
        self.active = False
        self.need_key = True
        self.sent = self.dropped = 0

    def push(self, packet) -> None:
        if not self.active or self._link.closed:
            return
        data = bytes(packet)
        key = bool(getattr(packet, "is_keyframe", False)) or contains_idr(data)
        if self.need_key and not key:
            self.dropped += 1
            return
        if not key and self._link.backlog() > WRITE_HIGH:
            self.need_key = True  # the cable can't keep up: skip to the next keyframe
            self.dropped += 1
            return
        self.need_key = False
        self.sent += 1
        self._link.write(VIDEO, self.index, video_payload(int(packet.pts or 0), key, data))

    def clear(self) -> None:
        self.need_key = True


class StreamLinkSession(LinkSession):
    """A LinkSession (windows, input, the shell, approval) whose transport is the handshake's own TCP stream."""

    def __init__(self, streams, rtsp_port: int, reader, writer, **options) -> None:
        super().__init__(streams, rtsp_port, **options)
        self.reader, self.writer = reader, writer
        self._reader_task: asyncio.Task | None = None
        self._stream_error: str | None = None
        for k in range(self.max_windows):
            sink = StreamSlotSink(self, k)
            self.slots.append(session_mod.Slot(k, _Transceiver(sink), sink, None))

    # --- transport -----------------------------------------------------------------------------------------------

    def backlog(self) -> int:
        transport = getattr(self.writer, "transport", None)
        try:
            return transport.get_write_buffer_size() if transport is not None else 0
        except Exception:
            return 0

    def write(self, kind: int, slot: int, payload: bytes) -> None:
        if self.closed or self._stream_error is not None:
            return
        if self.backlog() > WRITE_LIMIT:
            self._fail("the headset stopped reading")
            return
        try:
            self.writer.write(frame(kind, slot, payload))
        except (ConnectionError, OSError, RuntimeError) as e:
            self._fail(f"write failed: {e}")

    def _send(self, message: dict) -> None:
        self.write(CONTROL, 0, json.dumps(message, separators=(",", ":")).encode("utf-8"))

    def _send_input(self, index: int, message: dict) -> None:
        if 0 <= index < len(self.slots):
            self.write(INPUT, index, json.dumps(message, separators=(",", ":")).encode("utf-8"))

    def _fail(self, reason: str) -> None:
        if self._stream_error is None:
            self._stream_error = reason
            self._wake.set()

    async def _read_loop(self) -> None:
        try:
            while True:
                kind, slot, payload = await read_frame(self.reader, len(self.slots))
                if kind == CONTROL:
                    self._on_control_message(payload.decode("utf-8", "replace"))
                elif kind == INPUT:
                    self._on_input(self.slots[slot], payload.decode("utf-8", "replace"))
                else:
                    raise ValueError("the headset sent video")
        except StreamClosed:
            self._fail("the headset closed the connection")
        except ValueError as e:
            log.warning("Stream session: %s", e)
            self._fail("bad frame from the headset")
        except asyncio.CancelledError:
            raise
        except Exception as e:
            log.exception("Stream session reader failed")
            self._fail(f"error: {e}")

    # --- session lifecycle (the parts LinkSession does with WebRTC) ----------------------------------------------

    async def build_offer(self) -> str:  # never used: the stream handshake has no SDP
        raise RuntimeError("a stream session has no offer")

    def start(self) -> None:
        """The handshake finished: start reading and say welcome (the control channel is open right away)."""
        self._reader_task = asyncio.ensure_future(self._read_loop())
        self._on_control_open()

    def _on_control_open(self) -> None:
        if self._welcomed:
            return
        self._welcomed = True
        self._send({"type": "welcome", "version": session_mod.PROTOCOL_VERSION, "transport": "stream",
                    "slots": [{"slot": s.index, "mid": None} for s in self.slots],
                    "input": self.input_enabled, "keyboard": self.input_enabled and self.keyboard_enabled,
                    "cast_only": self.cast_only, "media": self.dispatcher.media_enabled, "dry_run": self.dry_run,
                    "control": "revoked" if self.dispatcher.state is session_mod.dispatch.ControlState.REVOKED
                    else "view-only",
                    "shell": self.shell.info})

    async def run(self) -> str:
        if self._reader_task is None:
            self.start()
        while True:
            if self._oversize:
                return "control message too large"
            if self._end_reason is not None:
                return self._end_reason
            if self._stream_error is not None:
                return self._stream_error
            for slot in self.slots:
                window = slot.window
                if window is not None and not window.stream.running:
                    await self._end_window(slot, "gone")
                elif window is not None and window.pump is not None and window.pump.done():
                    await self._end_window(slot, "stream ended", closed=True)
            self.dispatcher.release_idle()
            self._wake.clear()
            try:
                await asyncio.wait_for(self._wake.wait(), 0.5)
            except asyncio.TimeoutError:
                pass

    async def close(self) -> None:
        if self.closed:
            return
        await super().close()  # windows, players, input; pc is None so nothing WebRTC is touched
        if self._reader_task is not None:
            self._reader_task.cancel()
            await asyncio.gather(self._reader_task, return_exceptions=True)
        try:
            await asyncio.wait_for(self.writer.drain(), 1.0)  # let a last "stop" or "error" leave
        except Exception:
            pass
        try:
            self.writer.close()
            await asyncio.wait_for(self.writer.wait_closed(), 1.0)
        except Exception:
            pass
