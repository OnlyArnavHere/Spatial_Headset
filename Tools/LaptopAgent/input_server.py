"""WebSocket server the headset talks to: it lists what can be streamed, starts streams, and turns normalized pointer
events into mouse input on this laptop (only when the agent runs with --input) and key events into keyboard input
(only with --input --keyboard).

Protocol (JSON text messages), version 4:
  client -> agent  {"type": "hello", "token": "<pairing token>"}           must be the first message
  agent  -> client {"type": "welcome", "version": 4, "input": bool, "keyboard": bool, "dry_run": bool,
                   "cast_only": bool, "media": bool}
                   or {"type": "error", "reason": "bad token"|"busy"|...} then close
  client -> agent  {"type": "list"}
  agent  -> client {"type": "targets", "targets": [{"id", "path", "title", "kind", "description", "width", "height"}]}
                   the whole desktop first, then the open windows
  client -> agent  {"type": "open", "target": "<id>"}                    starts that stream (if needed) and routes this
  agent  -> client {"type": "opened", "id", "path", "target": {...}}       connection's pointer input to it; WHEP at
                   or {"type": "error", "reason": "gone"|"minimized"}      http://<laptop>:8889/<path>/whep
                   {"type": "error", "reason": "gone"} also arrives later if the window closes while streamed
  client -> agent  {"type": "move"|"down"|"up", "x": 0..1, "y": 0..1, "button": "left"|"right"}
                   {"type": "scroll", "x": 0..1, "y": 0..1, "delta": notches (positive = up)}
                   {"type": "key", "code": "<name from keys.KEY_CODES>", "down": bool}
                   {"type": "text", "text": "<up to 64 printable characters>"}
                   {"type": "media", "action": "<name from keys.MEDIA_ACTIONS>"}  (cast controls; see below)
                   {"type": "ping", "id": n, "t": client_ms}
  agent  -> client {"type": "pong", "id": n, "t": client_ms, "agent_ms": unix_ms}
                   {"type": "ack", "event", "x", "y", "button", "path"} after each down/up/scroll in dry-run mode
                   {"type": "ack", "event": "key", "code", "down", "path"} / {"type": "ack", "event": "text", "text",
                   "path"} after each injected key or text in dry-run mode
                   {"type": "throttled"} when key or text events were dropped by the rate limit (at most once a second)
                   {"type": "input_stopped", "reason"} when the kill switch (Ctrl+Alt+Shift+Q on the laptop) turned
                   all input off for the rest of the run; the connection stays open for video

Cast only (agent.py --cast-only): the server accepts hello, ping, list, one open per connection (it can't switch to
another target afterwards) and media actions: play/pause, next, previous, volume up/down, mute, seek back/forward.
Every other message (keys, text, mouse moves, clicks, scrolls, unknown media actions) is dropped here, before anything
reaches Windows, and counted in `dropped`. Media actions are a single press and release of one whitelisted key, never
with a modifier. Without --cast-only, media actions are accepted only with --input --keyboard.

Keyboard input goes to the window the connection opened: the agent brings it to the foreground first and drops the key
if it can't (so keys never land in another window). For the whole desktop it goes to whatever window is in front.

Each connection opens at most one stream; a headset with several windows open uses one connection per window, so
every window's input goes to its own target. Only one headset (one address) may be connected at a time.
Positions are normalized over the streamed area with the origin at the top-left (screen convention).
"""
import asyncio
import hmac
import ipaddress
import json
import logging
import time
from dataclasses import dataclass, field
from typing import Callable

from websockets.asyncio.server import ServerConnection, serve
from websockets.exceptions import ConnectionClosed

import keys
import winapi
from streams import Stream, StreamManager, Target

log = logging.getLogger("input")

PROTOCOL_VERSION = 4
# What a cast-only connection may send; everything else is dropped.
CAST_ALLOWED = ("ping", "list", "open", "media")
HELLO_TIMEOUT_S = 5.0
# A held button with no message for this long is released: the link may be dead before TCP notices.
IDLE_RELEASE_S = 3.0
MAX_FAILED_ATTEMPTS = 5
FAILED_ATTEMPT_DELAY_S = 2.0
THROTTLE_NOTICE_INTERVAL_S = 1.0


def is_local_network(host: str) -> bool:
    """Loopback, private (RFC 1918 / ULA) or link-local addresses only."""
    try:
        address = ipaddress.ip_address(host.split("%")[0])
    except ValueError:
        return False
    if address.version == 6 and address.ipv4_mapped:
        address = address.ipv4_mapped
    return address.is_loopback or address.is_private or address.is_link_local


@dataclass(eq=False)
class Session:
    """One headset connection: the stream it opened and the buttons and keys it holds down."""
    connection: ServerConnection
    peer: str
    # Set only after the pairing token checked out; nothing reaches the keyboard without it.
    paired: bool = False
    stream: Stream | None = None
    held: set = field(default_factory=set)
    held_keys: dict = field(default_factory=dict)  # code -> keys.KeyEvent of the press
    limiter: keys.RateLimiter = field(default_factory=keys.RateLimiter)
    last_message: float = field(default_factory=time.monotonic)
    last_throttle_notice: float = 0.0


@dataclass
class InputServer:
    token: str
    streams: StreamManager
    host: str = "0.0.0.0"
    port: int = 8765
    input_enabled: bool = False
    keyboard_enabled: bool = False
    dry_run: bool = False
    cast_only: bool = False
    input_stopped: bool = False
    # Messages dropped in cast-only mode, by kind ("key", "text", "move", "down", "media:power"...).
    dropped: dict = field(default_factory=dict)
    _sessions: list = field(default_factory=list)
    _failed: dict = field(default_factory=dict)

    @property
    def media_enabled(self) -> bool:
        """Media actions are accepted: always in cast-only mode, else only with --input --keyboard; never after the kill switch."""
        return not self.input_stopped and (self.cast_only or (self.input_enabled and self.keyboard_enabled))

    async def run(self) -> None:
        self.streams.on_ended = self._stream_ended
        async with serve(self._handle, self.host, self.port, ping_interval=2, ping_timeout=4,
                         max_size=65536, max_queue=64) as server:
            what = "mouse and keyboard input" if self.keyboard_enabled else "mouse input"
            mode = (f"DRY RUN: logging {what}, not injecting" if self.dry_run else f"injecting {what}") \
                if self.input_enabled else "view only: no input"
            if self.cast_only:
                mode = "CAST ONLY: media controls only" + (" (dry run)" if self.dry_run else "")
            log.info("Control server listening on %s:%d (%s)", self.host, self.port, mode)
            if self.input_enabled or self.cast_only:
                loop = asyncio.get_running_loop()
                if winapi.watch_kill_switch(lambda: loop.call_soon_threadsafe(self.stop_input, "kill switch pressed")):
                    log.info("Kill switch: press %s on this laptop to stop all input at once", winapi.KILL_SWITCH)
                else:
                    log.warning("Kill switch %s is taken by another program; stop the agent with Ctrl+C instead",
                                winapi.KILL_SWITCH)
            watchdog = asyncio.create_task(self._release_when_idle())
            try:
                await server.serve_forever()
            finally:
                watchdog.cancel()
                for session in self._sessions:
                    self._release_all(session, "server stopping")

    async def _handle(self, connection: ServerConnection) -> None:
        peer = connection.remote_address[0] if connection.remote_address else "?"
        if not is_local_network(peer):
            log.warning("Rejected %s: not on the local network", peer)
            await connection.close(code=1008, reason="local network only")
            return
        if self._failed.get(peer, 0) >= MAX_FAILED_ATTEMPTS:
            log.warning("Rejected %s: too many failed pairing attempts (restart the agent to reset)", peer)
            await connection.close(code=1008, reason="locked out")
            return
        if not await self._authenticate(connection, peer):
            return
        owner = next((s.peer for s in self._sessions), None)
        if owner is not None and owner != peer:
            log.warning("Rejected %s: headset %s is already connected", peer, owner)
            await self._send(connection, {"type": "error", "reason": "busy"})
            await connection.close(code=1013, reason="busy")
            return

        session = Session(connection, peer, paired=True)
        self._sessions.append(session)
        log.info("Headset %s connected (%d connection%s)", peer, len(self._sessions), "" if len(self._sessions) == 1 else "s")
        try:
            await self._send(connection, {"type": "welcome", "version": PROTOCOL_VERSION,
                                          "input": self.input_enabled, "dry_run": self.dry_run,
                                          "keyboard": self.input_enabled and self.keyboard_enabled,
                                          "cast_only": self.cast_only, "media": self.media_enabled})
            async for raw in connection:
                session.last_message = time.monotonic()
                await self._dispatch(session, raw)
        except ConnectionClosed:
            pass
        finally:
            # Never leave a button held down after the headset goes away.
            self._release_all(session, f"headset {peer} disconnected")
            if session.stream is not None:
                self.streams.release(session.stream)
            self._sessions.remove(session)

    async def _authenticate(self, connection: ServerConnection, peer: str) -> bool:
        try:
            raw = await asyncio.wait_for(connection.recv(), HELLO_TIMEOUT_S)
            message = json.loads(raw)
            supplied = str(message.get("token", "")) if message.get("type") == "hello" else ""
        except (asyncio.TimeoutError, ConnectionClosed, ValueError, AttributeError):
            supplied = ""
        if supplied and hmac.compare_digest(supplied.encode(), self.token.encode()):
            self._failed.pop(peer, None)
            return True
        self._failed[peer] = self._failed.get(peer, 0) + 1
        log.warning("Rejected %s: wrong or missing pairing token (attempt %d of %d)",
                    peer, self._failed[peer], MAX_FAILED_ATTEMPTS)
        await asyncio.sleep(FAILED_ATTEMPT_DELAY_S)  # slows down guessing
        try:
            await self._send(connection, {"type": "error", "reason": "bad token"})
            await connection.close(code=1008, reason="bad token")
        except ConnectionClosed:
            pass
        return False

    async def _dispatch(self, session: Session, raw) -> None:
        try:
            message = json.loads(raw)
            kind = message["type"]
        except (ValueError, KeyError, TypeError):
            return
        connection = session.connection
        if self.cast_only and kind not in CAST_ALLOWED:
            self._drop(session, str(kind))
            return
        if kind == "ping":
            await self._send(connection, {"type": "pong", "id": message.get("id"), "t": message.get("t"),
                                          "agent_ms": time.time() * 1000.0})
            return
        if kind == "list":
            await self._send(connection, {"type": "targets", "targets": self.streams.listing()})
            return
        if kind == "open":
            target_id = str(message.get("target", ""))
            if self.cast_only and session.stream is not None and session.stream.target.id != target_id:
                self._drop(session, "open (switch)")
                await self._send(connection, {"type": "error", "reason": "cast locked"})
                return
            await self._open(session, target_id)
            return
        if kind == "media":
            await self._media(session, message)
            return
        if kind in ("key", "text"):
            await self._keyboard(session, kind, message)
            return
        if kind not in ("move", "down", "up", "scroll") or not self.input_enabled or session.stream is None:
            return

        target = session.stream.target
        point = self._to_screen(target, message)
        if point is None:
            return
        x, y = point
        button = message.get("button", "left")
        if button not in ("left", "right"):
            return

        if kind == "move":
            self._inject(lambda: winapi.mouse_move(x, y), None)
        elif kind == "down":
            if not self._reachable(target, x, y):
                return
            if self._inject(lambda: winapi.mouse_button(x, y, button, True), f"{button} down at ({x}, {y}) in /{session.stream.path}"):
                session.held.add(button)
        elif kind == "up":
            if button not in session.held:
                return  # its press was dropped
            self._inject(lambda: winapi.mouse_button(x, y, button, False), f"{button} up at ({x}, {y}) in /{session.stream.path}")
            session.held.discard(button)
        elif kind == "scroll":
            if not self._reachable(target, x, y):
                return
            delta = max(-10.0, min(10.0, float(message.get("delta", 0))))
            self._inject(lambda: winapi.mouse_wheel(x, y, delta), f"scroll {delta:+g} at ({x}, {y}) in /{session.stream.path}")

        if self.dry_run and kind != "move":
            await self._send(connection, {"type": "ack", "event": kind, "x": x, "y": y, "button": button,
                                          "path": session.stream.path})

    def _drop(self, session: Session, kind: str) -> None:
        """Cast-only mode: a message that isn't a media control. Nothing is injected; it is only counted and logged."""
        self.dropped[kind] = self.dropped.get(kind, 0) + 1
        now = time.monotonic()
        if now - session.last_throttle_notice >= THROTTLE_NOTICE_INTERVAL_S:
            session.last_throttle_notice = now
            log.warning("Dropped %s from %s: cast only (media controls only)", kind, session.peer)

    async def _media(self, session: Session, message: dict) -> None:
        action = keys.parse_media(message)
        if action is None:
            if self.cast_only:
                self._drop(session, f"media:{message.get('action')}")
            return
        if not self.media_enabled or session.stream is None:
            if self.cast_only:
                self._drop(session, f"media:{action.action} (not allowed now)")
            return
        if not await self._within_rate(session, 1):
            return
        target = session.stream.target
        if action.to_window and not self._focus(target):
            return

        def press() -> None:
            winapi.key(action.vk, True, action.extended)
            winapi.key(action.vk, False, action.extended)

        if self._inject(press, f"media {action.action} for /{session.stream.path}"):
            await self._ack(session, {"event": "media", "action": action.action})

    async def _keyboard(self, session: Session, kind: str, message: dict) -> None:
        if not session.paired:
            log.warning("Dropped a %s event from %s: not paired", kind, session.peer)
            return
        if not (self.input_enabled and self.keyboard_enabled) or session.stream is None:
            return
        target = session.stream.target
        path = session.stream.path

        if kind == "key":
            event = keys.parse_key(message)
            if event is None:
                return
            if not event.down:
                # A release always goes through (never rate-limited), but only for a key this connection pressed.
                press = session.held_keys.pop(event.code, None)
                if press is not None:
                    self._inject(lambda: winapi.key(press.vk, False, press.extended), f"key {event.code} up in /{path}")
                    await self._ack(session, {"event": "key", "code": event.code, "down": False})
                return
            if event.code in session.held_keys or len(session.held_keys) >= keys.MAX_HELD_KEYS:
                return
            if not await self._within_rate(session, 1) or not self._focus(target):
                return
            if self._inject(lambda: winapi.key(event.vk, True, event.extended), f"key {event.code} down in /{path}"):
                session.held_keys[event.code] = event
                await self._ack(session, {"event": "key", "code": event.code, "down": True})
            return

        text = keys.parse_text(message)
        if text is None or not await self._within_rate(session, len(text)) or not self._focus(target):
            return
        if self._inject(lambda: winapi.type_text(text), f"text, {len(text)} character(s), in /{path}"):
            await self._ack(session, {"event": "text", "text": text})

    async def _within_rate(self, session: Session, cost: int) -> bool:
        if session.limiter.allow(cost):
            return True
        now = time.monotonic()
        if now - session.last_throttle_notice >= THROTTLE_NOTICE_INTERVAL_S:
            session.last_throttle_notice = now
            log.warning("Dropping key events from %s: more than %g a second", session.peer, session.limiter.rate)
            await self._send_quietly(session.connection, {"type": "throttled"})
        return False

    def _focus(self, target: Target) -> bool:
        """Keyboard input goes to the foreground window: make that the streamed window, or drop the key.
        (Dry runs change nothing on screen.)"""
        if not target.usable():
            return False  # window closed or minimized: drop the key rather than type into whatever is in front
        if self.dry_run or target.kind != "window" or winapi.focus_window(target.handle):
            return True
        log.warning("Dropped a key: couldn't bring %s to the front", target.description)
        return False

    async def _ack(self, session: Session, fields: dict) -> None:
        if self.dry_run and session.stream is not None:
            await self._send_quietly(session.connection, {"type": "ack", **fields, "path": session.stream.path})

    def stop_input(self, why: str) -> None:
        """The kill switch: no more mouse or keyboard input for the rest of this run. Held buttons and keys are
        released and every headset is told; video keeps streaming. Restart the agent to allow input again."""
        if self.input_stopped:
            return
        self.input_stopped = True
        self.input_enabled = False
        self.keyboard_enabled = False
        log.warning("INPUT STOPPED (%s): the headset can no longer move the mouse or type. Restart the agent to allow it.", why)
        for session in self._sessions:
            self._release_all(session, why)
            asyncio.get_running_loop().create_task(
                self._send_quietly(session.connection, {"type": "input_stopped", "reason": why}))

    async def _open(self, session: Session, target_id: str) -> None:
        try:
            stream = self.streams.acquire(target_id)
        except LookupError as error:
            await self._send(session.connection, {"type": "error", "reason": str(error.args[0])})
            return
        if session.stream is not None:
            self._release_all(session, "switched stream")
            self.streams.release(session.stream)
        session.stream = stream
        log.info("Headset %s opened %s (/%s)", session.peer, stream.target.description, stream.path)
        await self._send(session.connection, {"type": "opened", "id": target_id, "path": stream.path,
                                              "target": stream.target.describe()})

    def _stream_ended(self, stream: Stream) -> None:
        """A capture stopped on its own (window closed): tell the headset windows showing it."""
        for session in self._sessions:
            if session.stream is stream:
                self._release_all(session, "stream ended")
                session.stream = None
                asyncio.get_running_loop().create_task(self._send_quietly(session.connection, {"type": "error", "reason": "gone"}))

    def _reachable(self, target: Target, x: int, y: int) -> bool:
        """For a window, bring it to the top first and refuse the click if another window still covers the point,
        so a click meant for one window never lands on another. (Dry runs change nothing on screen.)"""
        if self.dry_run or target.kind != "window":
            return True
        winapi.raise_window(target.handle)
        if winapi.window_at(x, y) == target.handle:
            return True
        log.warning("Dropped a click at (%d, %d): %s is covered there", x, y, target.description)
        return False

    @staticmethod
    def _to_screen(target: Target, message: dict) -> tuple[int, int] | None:
        if not target.usable():
            return None  # window closed or minimized: drop input rather than click whatever is behind it
        rect = target.rect()
        if rect is None or rect.width <= 0 or rect.height <= 0:
            return None
        try:
            nx = min(1.0, max(0.0, float(message["x"])))
            ny = min(1.0, max(0.0, float(message["y"])))
        except (KeyError, TypeError, ValueError):
            return None
        return (rect.left + min(rect.width - 1, int(nx * rect.width)),
                rect.top + min(rect.height - 1, int(ny * rect.height)))

    def _inject(self, action: Callable[[], None], description: str | None) -> bool:
        if self.dry_run:
            if description:
                log.info("[dry run] %s", description)
            return True
        try:
            action()
            if description:
                log.debug("%s", description)
            return True
        except OSError as error:
            log.warning("Input not injected: %s", error)
            return False

    def _release_all(self, session: Session, why: str) -> None:
        if not session.held and not session.held_keys:
            return
        log.info("Releasing held buttons and keys (%s)", why)
        if session.held:
            rect = session.stream.target.rect() if session.stream else None
            x, y = (rect.left + rect.width // 2, rect.top + rect.height // 2) if rect else (0, 0)
            for button in list(session.held):
                self._inject(lambda b=button: winapi.mouse_button(x, y, b, False), f"{button} up (release)")
            session.held.clear()
        for press in list(session.held_keys.values()):
            self._inject(lambda p=press: winapi.key(p.vk, False, p.extended), f"key {press.code} up (release)")
        session.held_keys.clear()

    async def _release_when_idle(self) -> None:
        while True:
            await asyncio.sleep(0.5)
            for session in self._sessions:
                if (session.held or session.held_keys) and time.monotonic() - session.last_message > IDLE_RELEASE_S:
                    self._release_all(session, f"no input for {IDLE_RELEASE_S:g} s")

    @staticmethod
    async def _send(connection: ServerConnection, message: dict) -> None:
        await connection.send(json.dumps(message))

    @classmethod
    async def _send_quietly(cls, connection: ServerConnection, message: dict) -> None:
        try:
            await cls._send(connection, message)
        except ConnectionClosed:
            pass
