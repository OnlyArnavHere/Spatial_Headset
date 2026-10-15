"""WebSocket server that turns normalized pointer events from the headset into mouse input on this laptop.

Protocol (JSON text messages):
  client -> agent  {"type": "hello", "token": "<pairing token>"}           must be the first message
  agent  -> client {"type": "welcome", "target": {...}} or {"type": "error", "reason": "..."} then close
  client -> agent  {"type": "move"|"down"|"up", "x": 0..1, "y": 0..1, "button": "left"|"right"}
                   {"type": "scroll", "x": 0..1, "y": 0..1, "delta": notches (positive = up)}
                   {"type": "ping", "id": n, "t": client_ms}
  agent  -> client {"type": "pong", "id": n, "t": client_ms, "agent_ms": unix_ms}
                   {"type": "ack", ...} after each injected down/up/scroll in dry-run mode (for tests)

Positions are normalized over the captured area with the origin at the top-left (screen convention).
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

import winapi

log = logging.getLogger("input")

HELLO_TIMEOUT_S = 5.0
# A held button with no message for this long is released: the link may be dead before TCP notices.
IDLE_RELEASE_S = 3.0
MAX_FAILED_ATTEMPTS = 5
FAILED_ATTEMPT_DELAY_S = 2.0


def is_local_network(host: str) -> bool:
    """Loopback, private (RFC 1918 / ULA) or link-local addresses only."""
    try:
        address = ipaddress.ip_address(host.split("%")[0])
    except ValueError:
        return False
    if address.version == 6 and address.ipv4_mapped:
        address = address.ipv4_mapped
    return address.is_loopback or address.is_private or address.is_link_local


@dataclass
class Target:
    """What the stream shows, so normalized positions can be mapped to screen pixels."""
    kind: str                      # "window" or "monitor"
    handle: int
    description: str
    rect: Callable[[], "winapi.Rect | None"]
    usable: Callable[[], bool]

    def describe(self) -> dict:
        r = self.rect()
        return {"kind": self.kind, "description": self.description,
                "width": r.width if r else 0, "height": r.height if r else 0}


@dataclass
class InputServer:
    token: str
    target: Target
    host: str = "0.0.0.0"
    port: int = 8765
    dry_run: bool = False
    _active: ServerConnection | None = None
    _held: set = field(default_factory=set)
    _failed: dict = field(default_factory=dict)
    _last_message: float = 0.0

    async def run(self) -> None:
        async with serve(self._handle, self.host, self.port, ping_interval=2, ping_timeout=4,
                         max_size=4096, max_queue=64) as server:
            log.info("Input server listening on %s:%d (%s)", self.host, self.port,
                     "DRY RUN: logging, not injecting" if self.dry_run else "injecting mouse input")
            watchdog = asyncio.create_task(self._release_when_idle())
            try:
                await server.serve_forever()
            finally:
                watchdog.cancel()
                self._release_all("server stopping")

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
        if self._active is not None:
            log.warning("Rejected %s: another headset is already in control", peer)
            await self._send(connection, {"type": "error", "reason": "busy"})
            await connection.close(code=1013, reason="busy")
            return

        self._active = connection
        log.info("Headset %s paired; it now controls %s", peer, self.target.description)
        try:
            await self._send(connection, {"type": "welcome", "target": self.target.describe(), "dry_run": self.dry_run})
            async for raw in connection:
                self._last_message = time.monotonic()
                await self._dispatch(connection, raw)
        except ConnectionClosed:
            pass
        finally:
            # Never leave a button held down after the headset goes away.
            self._release_all(f"headset {peer} disconnected")
            self._active = None

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

    async def _dispatch(self, connection: ServerConnection, raw) -> None:
        try:
            message = json.loads(raw)
            kind = message["type"]
        except (ValueError, KeyError, TypeError):
            return
        if kind == "ping":
            await self._send(connection, {"type": "pong", "id": message.get("id"), "t": message.get("t"),
                                          "agent_ms": time.time() * 1000.0})
            return
        if kind not in ("move", "down", "up", "scroll"):
            return

        point = self._to_screen(message)
        if point is None:
            return
        x, y = point
        button = message.get("button", "left")
        if button not in ("left", "right"):
            return

        if kind == "move":
            self._inject(lambda: winapi.mouse_move(x, y), None)
        elif kind == "down":
            if self._inject(lambda: winapi.mouse_button(x, y, button, True), f"{button} down at ({x}, {y})"):
                self._held.add(button)
        elif kind == "up":
            self._inject(lambda: winapi.mouse_button(x, y, button, False), f"{button} up at ({x}, {y})")
            self._held.discard(button)
        elif kind == "scroll":
            delta = max(-10.0, min(10.0, float(message.get("delta", 0))))
            self._inject(lambda: winapi.mouse_wheel(x, y, delta), f"scroll {delta:+g} at ({x}, {y})")

        if self.dry_run and kind != "move":
            await self._send(connection, {"type": "ack", "event": kind, "x": x, "y": y, "button": button})

    def _to_screen(self, message: dict) -> tuple[int, int] | None:
        if not self.target.usable():
            return None  # window closed or minimized: drop input rather than click whatever is behind it
        rect = self.target.rect()
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

    def _release_all(self, why: str) -> None:
        if not self._held:
            return
        log.info("Releasing held buttons (%s)", why)
        rect = self.target.rect()
        x, y = (rect.left + rect.width // 2, rect.top + rect.height // 2) if rect else (0, 0)
        for button in list(self._held):
            self._inject(lambda b=button: winapi.mouse_button(x, y, b, False), f"{button} up (release)")
        self._held.clear()

    async def _release_when_idle(self) -> None:
        while True:
            await asyncio.sleep(0.5)
            if self._held and time.monotonic() - self._last_message > IDLE_RELEASE_S:
                self._release_all(f"no input for {IDLE_RELEASE_S:g} s")

    @staticmethod
    async def _send(connection: ServerConnection, message: dict) -> None:
        await connection.send(json.dumps(message))
