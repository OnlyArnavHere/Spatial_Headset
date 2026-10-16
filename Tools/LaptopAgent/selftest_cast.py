"""Proves that cast-only mode (agent.py --cast-only) lets through the media controls and nothing else.

  python selftest_cast.py

Self-contained: it runs the agent's real control server (input_server.InputServer, cast_only=True, not a dry run) in
this process on a free local port, with a fake window to stream and with every Windows input call (key presses, mouse
moves, clicks, wheel, typing, focus) replaced by a recorder, so nothing is injected on this machine. A headset-like
client then sends each media action and a battery of everything else: letters, Escape, F4, Tab, Enter, arrows,
Delete, Win/Alt/Ctrl/Shift and their combinations (Alt+F4, Ctrl+W, Alt+Tab, Ctrl+Shift+Esc, Ctrl+L...), unknown key
names (LWin, Apps, PrintScreen, media key names as keys), text, mouse moves, clicks and scrolls, unknown media actions,
and an attempt to switch the stream to another window. The test passes when exactly the media actions reached the
recorder (one press and release of their whitelisted key each), every other event was counted as dropped, and the
switch was refused. It also checks the full-control agent (no --cast-only, no --keyboard) refuses media actions.
"""
import asyncio
import json
import socket
import sys

from websockets.asyncio.client import connect

import input_server
import keys
import winapi
from input_server import InputServer
from streams import Stream, Target

results: list[tuple[bool, str]] = []


def check(ok: bool, what: str) -> None:
    results.append((ok, what))
    print(f"{'PASS' if ok else 'FAIL'}: {what}", flush=True)


class Recorder:
    """Stands in for every winapi call that could change anything on this machine."""

    def __init__(self) -> None:
        self.calls: list[tuple] = []

    def install(self) -> None:
        def record(name):
            def call(*args, **kwargs):
                self.calls.append((name,) + args)
                return True
            return call

        for name in ("key", "mouse_move", "mouse_button", "mouse_wheel", "type_text", "focus_window", "raise_window"):
            setattr(winapi, name, record(name))
        winapi.window_at = lambda x, y: 4242
        winapi.watch_kill_switch = lambda on_pressed: True


class FakeRect:
    left, top, width, height = 100, 100, 1280, 720


def fake_target(handle: int, title: str) -> Target:
    return Target(f"window:{handle}", "window", handle, title, f"window {title!r}", f"hwnd={handle}",
                  rect=lambda: FakeRect(), usable=lambda: True)


class FakeProcess:
    def poll(self):
        return None

    def terminate(self):
        pass


class FakeStreams:
    """Two windows, 'Movie' and 'Mail'; opening one starts nothing."""

    def __init__(self) -> None:
        self.targets = {t.id: t for t in (fake_target(4242, "Movie"), fake_target(4343, "Mail"))}
        self.on_ended = None

    def listing(self) -> list[dict]:
        return [{"id": t.id, "path": t.title.lower(), "title": t.title, **t.describe()} for t in self.targets.values()]

    def acquire(self, target_id: str) -> Stream:
        target = self.targets.get(target_id)
        if target is None:
            raise LookupError("gone")
        return Stream(target.title.lower(), target, (1280, 720), FakeProcess(), users=1)

    def release(self, stream: Stream) -> None:
        pass


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


async def send(ws, message: dict) -> None:
    await ws.send(json.dumps(message))


async def drain(ws, quiet: float = 0.3) -> list[dict]:
    messages = []
    try:
        while True:
            messages.append(json.loads(await asyncio.wait_for(ws.recv(), quiet)))
    except asyncio.TimeoutError:
        return messages


def forbidden_messages() -> list[dict]:
    """Everything a cast window must never be able to do."""
    out: list[dict] = []
    for code in ["A", "W", "L", "R", "Q", "Escape", "F4", "F5", "F11", "Tab", "Enter", "Space", "Backspace", "Delete",
                 "Left", "Right", "Up", "Down", "Home", "End", "PageUp", "PageDown"]:
        out += [{"type": "key", "code": code, "down": True}, {"type": "key", "code": code, "down": False}]
    # Combinations, pressed in order and released in reverse.
    for combo in (["Alt", "F4"], ["Ctrl", "W"], ["Alt", "Tab"], ["Ctrl", "Shift", "Escape"], ["Ctrl", "L"],
                  ["Alt", "Left"], ["Ctrl", "Tab"], ["Ctrl", "Alt", "Delete"], ["Shift", "F10"]):
        out += [{"type": "key", "code": c, "down": True} for c in combo]
        out += [{"type": "key", "code": c, "down": False} for c in reversed(combo)]
    # Names that aren't keys in the protocol at all (the Windows key, menu key, print screen, raw media keys).
    for code in ["LWin", "RWin", "Win", "Apps", "PrintScreen", "MediaPlayPause", "VolumeUp", "Sleep"]:
        out += [{"type": "key", "code": code, "down": True}, {"type": "key", "code": code, "down": False}]
    out += [{"type": "text", "text": "hello"}, {"type": "text", "text": "rundll32"},
            {"type": "move", "x": 0.5, "y": 0.5},
            {"type": "down", "x": 0.1, "y": 0.1, "button": "left"}, {"type": "up", "x": 0.1, "y": 0.1, "button": "left"},
            {"type": "down", "x": 0.9, "y": 0.9, "button": "right"}, {"type": "up", "x": 0.9, "y": 0.9, "button": "right"},
            {"type": "scroll", "x": 0.5, "y": 0.5, "delta": 3},
            {"type": "media", "action": "power"}, {"type": "media", "action": "PLAY_PAUSE"},
            {"type": "media", "action": None}, {"type": "media"},
            {"type": "keys", "codes": ["Win", "R"]}, {"type": "launch", "app": "cmd"}]
    return out


async def cast_checks(port: int, recorder: Recorder, server: InputServer) -> None:
    url = f"ws://127.0.0.1:{port}"
    async with connect(url) as ws:
        await send(ws, {"type": "hello", "token": server.token})
        welcome = json.loads(await asyncio.wait_for(ws.recv(), 5))
        check(welcome.get("type") == "welcome" and welcome.get("cast_only") is True and welcome.get("media") is True and
              welcome.get("input") is False and welcome.get("keyboard") is False,
              f"the welcome says cast only: media yes, input and keyboard no ({welcome})")
        await send(ws, {"type": "open", "target": "window:4242"})
        opened = [m for m in await drain(ws) if m.get("type") == "opened"]
        check(len(opened) == 1 and opened[0]["path"] == "movie", "the cast window opens its stream")

        recorder.calls.clear()
        forbidden = forbidden_messages()
        for message in forbidden:
            await send(ws, message)
        await drain(ws)
        check(recorder.calls == [], f"none of {len(forbidden)} non-media events reached Windows ({recorder.calls[:4]})")
        dropped = sum(server.dropped.values())
        check(dropped == len(forbidden), f"every one of them was counted as dropped ({dropped} of {len(forbidden)}: "
                                          f"{dict(sorted(server.dropped.items()))})")

        await send(ws, {"type": "open", "target": "window:4343"})
        replies = await drain(ws)
        check(any(r.get("reason") == "cast locked" for r in replies) and not any(r.get("type") == "opened" for r in replies)
              and server.dropped.get("open (switch)") == 1, "switching the cast to another window is refused")
        await send(ws, {"type": "open", "target": "window:4242"})
        check(any(r.get("type") == "opened" for r in await drain(ws)), "re-opening the same window is fine (reconnects)")

        recorder.calls.clear()
        for action in keys.MEDIA_ACTIONS:
            await send(ws, {"type": "media", "action": action})
            await asyncio.sleep(0.02)
        await drain(ws)
        pressed = [c for c in recorder.calls if c[0] == "key"]
        expected = []
        for action, (vk, extended) in keys.MEDIA_ACTIONS.items():
            expected += [("key", vk, True, extended), ("key", vk, False, extended)]
        check(pressed == expected, f"each of the {len(keys.MEDIA_ACTIONS)} media actions is one press and release of its "
                                   f"whitelisted key, in order ({len(pressed)} key calls)")
        others = [c for c in recorder.calls if c[0] not in ("key", "focus_window")]
        focused = [c for c in recorder.calls if c[0] == "focus_window"]
        check(not others and len(focused) == len(keys.WINDOW_MEDIA_ACTIONS) and all(c[1] == 4242 for c in focused),
              f"seeking first brings the cast window to the front; nothing else is touched ({others})")
        check(not any(vk in (0x5B, 0x5C, 0x12, 0x11, 0x10, 0x5D) for _, vk, *_ in pressed),
              "no Win, Alt, Ctrl, Shift or menu key is ever pressed")

        server.stop_input("self-test kill switch")
        recorder.calls.clear()
        await drain(ws)
        await send(ws, {"type": "media", "action": "play_pause"})
        await drain(ws)
        check(recorder.calls == [], "after the kill switch even media actions stop")


async def full_control_checks(port: int, recorder: Recorder, server: InputServer) -> None:
    async with connect(f"ws://127.0.0.1:{port}") as ws:
        await send(ws, {"type": "hello", "token": server.token})
        welcome = json.loads(await asyncio.wait_for(ws.recv(), 5))
        await send(ws, {"type": "open", "target": "window:4242"})
        await drain(ws)
        recorder.calls.clear()
        await send(ws, {"type": "media", "action": "play_pause"})
        await drain(ws)
        check(welcome.get("cast_only") is False and welcome.get("media") is False and recorder.calls == [],
              "an agent started without --cast-only or --keyboard refuses media actions")


async def run(cast_only: bool, body) -> None:
    recorder = Recorder()
    recorder.install()
    port = free_port()
    server = InputServer("selftest-token", FakeStreams(), host="127.0.0.1", port=port, cast_only=cast_only)
    task = asyncio.create_task(server.run())
    try:
        for _ in range(50):
            try:
                with socket.create_connection(("127.0.0.1", port), timeout=0.1):
                    break
            except OSError:
                await asyncio.sleep(0.05)
        await body(port, recorder, server)
    finally:
        task.cancel()
        try:
            await task
        except asyncio.CancelledError:
            pass


def main() -> int:
    import logging
    input_server.log.setLevel("ERROR")
    logging.getLogger("websockets").setLevel(logging.CRITICAL)  # the port probe's bare connect isn't a handshake
    asyncio.run(run(True, cast_checks))
    asyncio.run(run(False, full_control_checks))
    failed = [what for ok, what in results if not ok]
    print(f"\n{len(results) - len(failed)} passed, {len(failed)} failed", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
