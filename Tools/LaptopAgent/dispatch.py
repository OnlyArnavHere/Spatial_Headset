"""Turns the headset's input messages into mouse and keyboard input on this laptop. The messages arrive on the per-slot
`input-k` data channels of the WebRTC session (session.py); `control-*` messages arrive on the control channel. There is
no other way in: the agent has no listening socket for input.

Control state, per session (agent -> headset messages are on the control channel):
  VIEW_ONLY (start) -> REQUESTED -> GRANTED;  GRANTED -> VIEW_ONLY when the headset releases;
  any state -> REVOKED by the kill switch (Ctrl+Alt+Shift+Q), by {"type":"stop"} from the headset (this session only)
  REVOKED is final: the kill switch stays in force for the rest of the agent's run.
  headset -> agent  {"type":"control-request","consent":"full control"}   the headset's own consent gate approved it
                    {"type":"control-release"}                            back to VIEW_ONLY, everything held is released
                    {"type":"stop"}                                        revoke, release and end the session
  agent -> headset  {"type":"control-granted","keyboard":bool}
                    {"type":"control-denied","reason":"cast only"|"input off"|"stopped"|"no consent"|"busy"|"denied"|"timeout"}
                    {"type":"control-revoked","reason":"kill switch"|...}
The approver (async, info -> bool; raises TimeoutError for "timeout") decides a request on this laptop: approval.py asks
with a Yes/No box (30 s, default No), the default grants when --input is set. After a deny, requests get "busy" for 10 s.

Input messages (JSON, at most 1 KiB, on input-k; they only ever reach slot k's window):
  {"type": "move"|"down"|"up", "x": 0..1, "y": 0..1, "button": "left"|"right"}
  {"type": "scroll", "x": 0..1, "y": 0..1, "delta": notches (positive = up)}
  {"type": "key", "code": "<name from keys.KEY_CODES>", "down": bool}
  {"type": "text", "text": "<up to 64 printable characters>"}
  {"type": "media", "action": "<name from keys.MEDIA_ACTIONS>"}
Everything else is dropped. Before GRANTED all but media are dropped. Dry run: each injected event is acknowledged on the
same channel ({"type":"ack","event",...,"path"}); {"type":"throttled"} says key, text or media events were rate limited
(at most once a second).

Cast only (agent.py --cast-only): only `media` is accepted, with no grant and no approver: play/pause, next, previous,
volume up/down, mute, seek back/forward. Each is one press and release of one whitelisted key, never with a modifier.
Everything else is dropped before anything reaches Windows and counted in `dropped`. Without --cast-only, media needs
GRANTED and --input --keyboard. After the kill switch nothing is injected, media included.

Keyboard input goes to the window of the slot: it is brought to the foreground first and the key is dropped if that
fails. Positions are normalized over the streamed area with the origin at the top-left.
"""
import asyncio
import enum
import json
import logging
import math
import time
from dataclasses import dataclass, field
from typing import Awaitable, Callable

import keys
import winapi
from streams import Stream, Target

log = logging.getLogger("input")

CAST_ALLOWED = ("media",)
INPUT_TYPES = ("move", "down", "up", "scroll", "key", "text", "media")
CONSENT = "full control"
MAX_INPUT_MESSAGE = 1024
# The laptop box times out at 30 s on its own; this only guards an approver that never answers.
APPROVAL_BACKSTOP_S = 35.0
# After a deny or a timeout the headset can't ask again (no prompt) for this long.
REASK_COOLDOWN_S = 10.0
# A held button with no message for this long is released: the link may be dead before it is noticed.
IDLE_RELEASE_S = 3.0
THROTTLE_NOTICE_INTERVAL_S = 1.0
MESSAGE_RATE = 240.0
MESSAGE_BURST = 480.0
MAX_DROP_KINDS = 64

Approver = Callable[[dict], Awaitable[bool]]


class ControlState(enum.Enum):
    VIEW_ONLY = "view-only"
    REQUESTED = "requested"
    GRANTED = "granted"
    REVOKED = "revoked"


async def approve_when_input_enabled(info: dict) -> bool:
    """The default approver: --input was given on the command line, which is the laptop owner's consent."""
    return True


@dataclass(eq=False)
class SlotInput:
    """What one input channel holds: its window and the buttons and keys it has down."""
    index: int
    clock: Callable[[], float] = time.monotonic
    stream: Stream | None = None
    held: set = field(default_factory=set)
    held_keys: dict = field(default_factory=dict)  # code -> keys.KeyEvent of the press
    limiter: keys.RateLimiter = None
    bucket: keys.RateLimiter = None
    last_message: float = 0.0
    last_throttle_notice: float = -1e9
    last_drop_log: float = -1e9

    def __post_init__(self) -> None:
        self.limiter = self.limiter or keys.RateLimiter(clock=self.clock)
        self.bucket = self.bucket or keys.RateLimiter(rate=MESSAGE_RATE, burst=MESSAGE_BURST, clock=self.clock)
        self.last_message = self.clock()


class InputDispatcher:
    def __init__(self, slots: int, send_control: Callable[[dict], None], send_input: Callable[[int, dict], None], *,
                 input_enabled: bool = False, keyboard_enabled: bool = False, dry_run: bool = False,
                 cast_only: bool = False, approver: Approver | None = None, headset: dict | None = None,
                 revoked: bool = False, clock: Callable[[], float] = time.monotonic):
        self.send_control = send_control
        self.send_input = send_input
        self.input_enabled = input_enabled
        self.keyboard_enabled = keyboard_enabled
        self.dry_run = dry_run
        self.cast_only = cast_only
        self.approver = approver or approve_when_input_enabled
        self.headset = headset or {}
        self.clock = clock
        self.state = ControlState.REVOKED if revoked else ControlState.VIEW_ONLY
        self.slots = [SlotInput(k, clock) for k in range(slots)]
        # Messages dropped, by kind ("key", "move", "media:power", "oversize"...).
        self.dropped: dict = {}
        self._in_flight = False
        self._ask_again_at = -math.inf

    # --- State ---------------------------------------------------------------------------------------------------

    @property
    def media_enabled(self) -> bool:
        """Media actions can be accepted: always in cast-only mode, else only with --input --keyboard; never after a revoke."""
        return self.state is not ControlState.REVOKED and (self.cast_only or (self.input_enabled and self.keyboard_enabled))

    def _media_allowed(self) -> bool:
        if self.state is ControlState.REVOKED:
            return False
        return self.cast_only or (self.state is ControlState.GRANTED and self.input_enabled and self.keyboard_enabled)

    async def request_control(self, message: dict) -> None:
        if self.cast_only:
            return self._deny("cast only")
        if not self.input_enabled:
            return self._deny("input off")
        if self.state is ControlState.REVOKED:
            return self._deny("stopped")
        if message.get("consent") != CONSENT:
            return self._deny("no consent")
        if self.state is ControlState.GRANTED:
            self.send_control({"type": "control-granted", "keyboard": self.keyboard_enabled})
            return
        if self._in_flight or self.clock() < self._ask_again_at:
            return self._deny("busy")
        self._in_flight = True
        self.state = ControlState.REQUESTED
        reason = "denied"
        approved = False
        try:
            approved = bool(await asyncio.wait_for(self.approver(dict(self.headset)), APPROVAL_BACKSTOP_S))
        except asyncio.TimeoutError:
            reason = "timeout"
        except asyncio.CancelledError:
            if self.state is ControlState.REQUESTED:
                self.state = ControlState.VIEW_ONLY
            raise
        except Exception:
            log.exception("The approver failed; denying control")
        finally:
            self._in_flight = False
        if self.state is not ControlState.REQUESTED:
            return  # released or revoked while the question was open
        if approved:
            self.state = ControlState.GRANTED
            log.info("CONTROL GRANTED to %s", self.headset.get("name", "the headset"))
            self.send_control({"type": "control-granted", "keyboard": self.keyboard_enabled})
        else:
            self.state = ControlState.VIEW_ONLY
            self._ask_again_at = self.clock() + REASK_COOLDOWN_S
            self._deny(reason)

    def _deny(self, reason: str) -> None:
        log.info("Control denied: %s", reason)
        self.send_control({"type": "control-denied", "reason": reason})

    def release_control(self) -> None:
        """The headset gives control back: view only, everything held is let go."""
        if self.state in (ControlState.GRANTED, ControlState.REQUESTED):
            self.state = ControlState.VIEW_ONLY
        self.release_everything("control released")

    def revoke(self, reason: str) -> None:
        """Immediate and final: state change, then every held button and key released, before the next message."""
        if self.state is ControlState.REVOKED:
            return
        self.state = ControlState.REVOKED
        log.warning("CONTROL REVOKED (%s): the headset can no longer move the mouse, type or use media keys.", reason)
        self.release_everything(reason)
        self.send_control({"type": "control-revoked", "reason": reason})

    # --- Slots ---------------------------------------------------------------------------------------------------

    def attach(self, index: int, stream: Stream) -> None:
        slot = self.slots[index]
        self.release(slot, "switched stream")
        slot.stream = stream
        slot.last_message = self.clock()

    def detach(self, index: int, why: str = "window closed") -> None:
        slot = self.slots[index]
        self.release(slot, why)
        slot.stream = None

    def release_everything(self, why: str) -> None:
        for slot in self.slots:
            self.release(slot, why)

    def close(self) -> None:
        self.release_everything("session closed")

    def release_idle(self) -> None:
        now = self.clock()
        for slot in self.slots:
            if (slot.held or slot.held_keys) and now - slot.last_message > IDLE_RELEASE_S:
                self.release(slot, f"no input for {IDLE_RELEASE_S:g} s")

    def release(self, slot: SlotInput, why: str) -> None:
        if not slot.held and not slot.held_keys:
            return
        log.info("Releasing held buttons and keys (%s)", why)
        if slot.held:
            rect = slot.stream.target.rect() if slot.stream else None
            x, y = (rect.left + rect.width // 2, rect.top + rect.height // 2) if rect else (0, 0)
            for button in list(slot.held):
                self._inject(lambda b=button: winapi.mouse_button(x, y, b, False), f"{button} up (release)")
            slot.held.clear()
        for press in list(slot.held_keys.values()):
            self._inject(lambda p=press: winapi.key(p.vk, False, p.extended), f"key {press.code} up (release)")
        slot.held_keys.clear()

    # --- Input ---------------------------------------------------------------------------------------------------

    def on_input(self, index: int, raw) -> None:
        """One message from input channel `index`."""
        slot = self.slots[index]
        slot.last_message = self.clock()
        size = len(raw) if isinstance(raw, (bytes, bytearray)) else len(str(raw).encode("utf-8", "replace"))
        if size > MAX_INPUT_MESSAGE:
            self._drop(slot, "oversize")
            return
        try:
            message = json.loads(raw)
        except (ValueError, TypeError, RecursionError):
            return
        if not isinstance(message, dict) or not isinstance(message.get("type"), str):
            return
        kind = message["type"]
        if kind not in INPUT_TYPES:
            self._drop(slot, kind)
            return
        if self.cast_only and kind not in CAST_ALLOWED:
            self._drop(slot, kind)
            return
        if slot.stream is None:
            return
        # Releases are never limited; everything else shares the channel's message budget.
        # Only releases of things this slot is holding are exempt; stray ones count like any message.
        release = (kind == "up" and message.get("button", "left") in slot.held) or (
            kind == "key" and message.get("down") is False and message.get("code") in slot.held_keys)
        if not release and not slot.bucket.allow(1):
            self._throttled(slot)
            return
        try:
            if kind == "media":
                self._media(slot, message)
            elif self.state is ControlState.GRANTED and self.input_enabled:
                if kind in ("key", "text"):
                    self._keyboard(slot, kind, message)
                else:
                    self._mouse(slot, kind, message)
        except (TypeError, ValueError, OverflowError):
            log.debug("Dropped a malformed %s message", kind, exc_info=True)

    def _drop(self, slot: SlotInput, kind: str) -> None:
        """A message that isn't accepted: nothing is injected; it is only counted and logged."""
        if kind not in self.dropped and len(self.dropped) >= MAX_DROP_KINDS:
            kind = "other"
        self.dropped[kind] = self.dropped.get(kind, 0) + 1
        now = self.clock()
        if now - slot.last_drop_log >= THROTTLE_NOTICE_INTERVAL_S:
            slot.last_drop_log = now
            log.warning("Dropped %s on input-%d%s", kind[:40], slot.index, ": cast only (media controls only)" if self.cast_only else "")

    def _throttled(self, slot: SlotInput) -> None:
        now = self.clock()
        if now - slot.last_throttle_notice >= THROTTLE_NOTICE_INTERVAL_S:
            slot.last_throttle_notice = now
            log.warning("Dropping input on input-%d: rate limit", slot.index)
            self.send_input(slot.index, {"type": "throttled"})

    def _within_rate(self, slot: SlotInput, cost: int) -> bool:
        if slot.limiter.allow(cost):
            return True
        self._throttled(slot)
        return False

    def _ack(self, slot: SlotInput, fields: dict) -> None:
        if self.dry_run and slot.stream is not None:
            self.send_input(slot.index, {"type": "ack", **fields, "path": slot.stream.path})

    def _mouse(self, slot: SlotInput, kind: str, message: dict) -> None:
        if kind == "up" and message.get("button", "left") not in slot.held:
            return  # its press was dropped; no Win32 work
        target = slot.stream.target
        point = self._to_screen(target, message)
        if point is None:
            return
        x, y = point
        button = message.get("button", "left")
        if button not in ("left", "right"):
            return
        delta = 0.0
        if kind == "scroll":
            delta = float(message.get("delta", 0))
            if not math.isfinite(delta):
                return
            delta = max(-10.0, min(10.0, delta))
        path = slot.stream.path
        if kind == "move":
            self._inject(lambda: winapi.mouse_move(x, y), None)
        elif kind == "down":
            if not self._reachable(target, x, y):
                return
            if self._inject(lambda: winapi.mouse_button(x, y, button, True), f"{button} down at ({x}, {y}) in /{path}"):
                slot.held.add(button)
        elif kind == "up":
            if button not in slot.held:
                return  # its press was dropped
            self._inject(lambda: winapi.mouse_button(x, y, button, False), f"{button} up at ({x}, {y}) in /{path}")
            slot.held.discard(button)
        elif kind == "scroll":
            if not self._reachable(target, x, y):
                return
            self._inject(lambda: winapi.mouse_wheel(x, y, delta), f"scroll {delta:+g} at ({x}, {y}) in /{path}")
        if self.dry_run and kind != "move":
            self.send_input(slot.index, {"type": "ack", "event": kind, "x": x, "y": y, "button": button, "path": path})

    def _media(self, slot: SlotInput, message: dict) -> None:
        action = keys.parse_media(message)
        if action is None:
            if self.cast_only:
                self._drop(slot, f"media:{str(message.get('action'))[:40]}")
            return
        if not self._media_allowed():
            if self.cast_only:
                self._drop(slot, f"media:{action.action} (not allowed now)")
            return
        if not self._within_rate(slot, 1):
            return
        target = slot.stream.target
        if action.to_window and not self._focus(target):
            return

        def press() -> None:
            winapi.key(action.vk, True, action.extended)
            winapi.key(action.vk, False, action.extended)

        if self._inject(press, f"media {action.action} for /{slot.stream.path}"):
            self._ack(slot, {"event": "media", "action": action.action})

    def _keyboard(self, slot: SlotInput, kind: str, message: dict) -> None:
        if not self.keyboard_enabled:
            return
        target = slot.stream.target
        path = slot.stream.path

        if kind == "key":
            event = keys.parse_key(message)
            if event is None:
                return
            if not event.down:
                # A release always goes through (never rate-limited), but only for a key this channel pressed.
                press = slot.held_keys.pop(event.code, None)
                if press is not None:
                    self._inject(lambda: winapi.key(press.vk, False, press.extended), f"key {event.code} up in /{path}")
                    self._ack(slot, {"event": "key", "code": event.code, "down": False})
                return
            if event.code in slot.held_keys or len(slot.held_keys) >= keys.MAX_HELD_KEYS:
                return
            if not self._within_rate(slot, 1) or not self._focus(target):
                return
            if self._inject(lambda: winapi.key(event.vk, True, event.extended), f"key {event.code} down in /{path}"):
                slot.held_keys[event.code] = event
                self._ack(slot, {"event": "key", "code": event.code, "down": True})
            return

        text = keys.parse_text(message)
        if text is None or not self._within_rate(slot, len(text)) or not self._focus(target):
            return
        if self._inject(lambda: winapi.type_text(text), f"text, {len(text)} character(s), in /{path}"):
            self._ack(slot, {"event": "text", "text": text})

    def _focus(self, target: Target) -> bool:
        """Keyboard input goes to the foreground window: make that the streamed window, or drop the key.
        (Dry runs change nothing on screen.)"""
        if not target.usable():
            return False  # window closed or minimized: drop the key rather than type into whatever is in front
        if self.dry_run or target.kind != "window" or winapi.focus_window(target.handle):
            return True
        log.warning("Dropped a key: couldn't bring %s to the front", target.description)
        return False

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
            nx, ny = float(message["x"]), float(message["y"])
        except (KeyError, TypeError, ValueError):
            return None
        if not (math.isfinite(nx) and math.isfinite(ny)):
            return None
        nx, ny = min(1.0, max(0.0, nx)), min(1.0, max(0.0, ny))
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
