"""Checks a running agent's control server (start the agent with --input --keyboard --dry-run-input first).

  python selftest_input.py TOKEN [--host 127.0.0.1] [--port 8765] [--kill-switch]

Verifies: a wrong token is rejected, a key sent instead of the pairing hello is rejected, the right one is accepted,
ping/pong works, the window list starts with the whole desktop, unknown windows are refused, streams open on their own
paths, the same headset may open several connections while another address is refused, and (in dry-run mode) each
connection's clicks map to its own stream's pixels, keys and text reach its own stream, unknown keys are dropped and a
key flood is rate-limited without losing a release.
--kill-switch also presses Ctrl+Alt+Shift+Q (a real key press on this machine) and checks that all input stops; that
lasts for the rest of the agent's run, so restart the agent afterwards.
Opening streams starts captures; the agent stops them about 15 seconds after this test ends.
"""
import argparse
import asyncio
import json
import time

from websockets.asyncio.client import connect
from websockets.exceptions import ConnectionClosed

results: list[tuple[bool, str]] = []


def check(ok: bool, what: str) -> None:
    results.append((ok, what))
    print(f"{'PASS' if ok else 'FAIL'}: {what}", flush=True)


async def request(ws, message: dict, reply_type: str | None = None) -> dict:
    await ws.send(json.dumps(message))
    while True:
        reply = json.loads(await asyncio.wait_for(ws.recv(), 6))
        if reply_type is None or reply.get("type") in (reply_type, "error"):
            return reply


async def expect_rejected(url: str, token: str, what: str, **connect_args) -> None:
    try:
        async with connect(url, **connect_args) as ws:
            reply = await request(ws, {"type": "hello", "token": token})
            check(reply.get("type") == "error", f"{what} is rejected ({reply})")
    except ConnectionClosed as closed:
        check(True, f"{what} is rejected (closed: {closed.rcvd.reason if closed.rcvd else '?'})")
    except OSError as error:
        check(False, f"{what}: couldn't connect ({error})")


async def expect_key_without_hello_rejected(url: str) -> None:
    try:
        async with connect(url) as ws:
            reply = await request(ws, {"type": "key", "code": "A", "down": True})
            check(reply.get("type") == "error", f"a key sent instead of the pairing hello is rejected ({reply})")
    except ConnectionClosed as closed:
        check(True, f"a key sent instead of the pairing hello is rejected (closed: {closed.rcvd.reason if closed.rcvd else '?'})")


async def drain(ws, quiet: float = 0.4) -> list[dict]:
    """Everything the agent sends until it has been quiet for a moment."""
    messages = []
    try:
        while True:
            messages.append(json.loads(await asyncio.wait_for(ws.recv(), quiet)))
    except asyncio.TimeoutError:
        return messages


async def keyboard_checks(ws, path: str) -> None:
    ack = await request(ws, {"type": "key", "code": "A", "down": True}, "ack")
    check(ack.get("event") == "key" and ack.get("code") == "A" and ack.get("down") is True and ack.get("path") == path,
          f"a key press reaches /{path} ({ack})")
    ack = await request(ws, {"type": "key", "code": "A", "down": False}, "ack")
    check(ack.get("event") == "key" and ack.get("down") is False, "and its release too")
    ack = await request(ws, {"type": "text", "text": "Hello, headset!"}, "ack")
    check(ack.get("event") == "text" and ack.get("text") == "Hello, headset!", f"text reaches /{path} ({ack})")

    for message in [{"type": "key", "code": "LWin", "down": True}, {"type": "key", "code": "B", "down": False},
                    {"type": "text", "text": "x" * 65}]:
        await ws.send(json.dumps(message))
    replies = [await request(ws, {"type": "ping", "id": 7, "t": 0}, None)]
    # The agent answers in order: anything acknowledged for those three would arrive before the pong.
    check(replies[0].get("type") == "pong", f"the Windows key, a release without a press and over-long text are dropped ({replies[0]})")

    for _ in range(150):
        await ws.send(json.dumps({"type": "key", "code": "Z", "down": True}))
        await ws.send(json.dumps({"type": "key", "code": "Z", "down": False}))
    replies = await drain(ws)
    downs = sum(1 for r in replies if r.get("type") == "ack" and r.get("code") == "Z" and r.get("down") is True)
    ups = sum(1 for r in replies if r.get("type") == "ack" and r.get("code") == "Z" and r.get("down") is False)
    throttled = sum(1 for r in replies if r.get("type") == "throttled")
    check(0 < downs < 150 and ups == downs and throttled >= 1,
          f"a flood of 150 key presses is rate-limited: {downs} accepted, {ups} released, {throttled} throttle notice(s)")


def press_kill_switch() -> None:
    """Presses and releases Ctrl+Alt+Shift+Q on this machine (the agent's kill-switch hotkey)."""
    import ctypes
    send = ctypes.windll.user32.keybd_event
    for vk in (0x11, 0x12, 0x10, 0x51):  # Ctrl, Alt, Shift, Q
        send(vk, 0, 0, 0)
    for vk in (0x51, 0x10, 0x12, 0x11):
        send(vk, 0, 2, 0)  # KEYEVENTF_KEYUP


async def kill_switch_checks(ws) -> None:
    press_kill_switch()
    try:
        reply = await request(ws, {"type": "ping", "id": 9, "t": 0}, "input_stopped")
    except asyncio.TimeoutError:
        reply = {}
    check(reply.get("type") == "input_stopped", f"Ctrl+Alt+Shift+Q stops all input ({reply})")
    await ws.send(json.dumps({"type": "key", "code": "A", "down": True}))
    await ws.send(json.dumps({"type": "down", "x": 0.5, "y": 0.5, "button": "left"}))
    acks = [r for r in await drain(ws) if r.get("type") == "ack"]
    check(not acks, "after the kill switch, keys and clicks are dropped")


async def click(ws, nx: float, ny: float) -> dict:
    ack = await request(ws, {"type": "down", "x": nx, "y": ny, "button": "left"}, "ack")
    await request(ws, {"type": "up", "x": nx, "y": ny, "button": "left"}, "ack")
    return ack


async def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("token")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--kill-switch", action="store_true",
                        help="also press Ctrl+Alt+Shift+Q and check that input stops (restart the agent afterwards)")
    args = parser.parse_args()
    url = f"ws://{args.host}:{args.port}"

    await expect_rejected(url, "not-the-token", "a wrong token")
    await expect_key_without_hello_rejected(url)

    async with connect(url) as ws:
        welcome = await request(ws, {"type": "hello", "token": args.token})
        check(welcome.get("type") == "welcome" and welcome.get("version") == 3, f"the pairing token is accepted ({welcome})")
        dry_run = welcome.get("dry_run", False)
        keyboard = welcome.get("keyboard", False)

        sent = time.time() * 1000
        pong = await request(ws, {"type": "ping", "id": 1, "t": sent}, "pong")
        rtt = time.time() * 1000 - sent
        check(pong.get("type") == "pong" and pong.get("id") == 1 and "agent_ms" in pong, f"ping/pong works (RTT {rtt:.1f} ms)")

        listing = await request(ws, {"type": "list"}, "targets")
        targets = listing.get("targets", [])
        check(bool(targets) and targets[0].get("id") == "desktop" and targets[0].get("title") == "Whole desktop"
              and targets[0].get("path") == "desktop", f"the list starts with the whole desktop ({len(targets)} targets)")
        windows = [t for t in targets if t.get("kind") == "window"]
        paths = [t.get("path") for t in targets]
        check(len(set(paths)) == len(paths) and all(paths), f"every target has its own path ({', '.join(paths[:6])}...)")

        gone = await request(ws, {"type": "open", "target": "window:1"}, "opened")
        check(gone.get("type") == "error" and gone.get("reason") == "gone", f"an unknown window is refused ({gone})")

        opened = await request(ws, {"type": "open", "target": "desktop"}, "opened")
        check(opened.get("type") == "opened" and opened.get("path") == "desktop",
              f"opening the desktop streams it on /desktop ({opened.get('target')})")

        async with connect(url) as second:
            reply = await request(second, {"type": "hello", "token": args.token})
            check(reply.get("type") == "welcome", "the same headset may open a second connection (another window)")
            await expect_rejected(url, args.token, "another headset (127.0.0.2) while one is connected",
                                  local_addr=("127.0.0.2", 0))

            if windows:
                window = windows[0]
                opened2 = await request(second, {"type": "open", "target": window["id"]}, "opened")
                check(opened2.get("type") == "opened" and opened2.get("path") == window["path"],
                      f"a window streams on its own path /{opened2.get('path')} ({window['title']!r})")
            else:
                print("(no windows open: skipping the per-window checks)")

            if dry_run:
                for nx, ny in [(0.0, 0.0), (0.5, 0.5), (0.999, 0.999)]:
                    ack = await click(ws, nx, ny)
                    check(ack.get("type") == "ack" and ack.get("path") == "desktop",
                          f"desktop ({nx}, {ny}) maps to pixel ({ack.get('x')}, {ack.get('y')}) of a "
                          f"{opened['target']['width']}x{opened['target']['height']} desktop")
                if windows:
                    ack = await click(second, 0.5, 0.5)
                    check(ack.get("type") == "ack" and ack.get("path") == windows[0]["path"],
                          f"the second connection's click goes to its window /{ack.get('path')} ({ack.get('x')}, {ack.get('y')})")
                if keyboard:
                    await keyboard_checks(ws, "desktop")
                    if windows:
                        ack = await request(second, {"type": "text", "text": "hi"}, "ack")
                        check(ack.get("path") == windows[0]["path"],
                              f"the second connection's text goes to its window /{ack.get('path')}")
                else:
                    print("(agent started without --keyboard: skipping the keyboard checks)")
                if args.kill_switch:
                    await kill_switch_checks(ws)
            else:
                print("(agent is not in dry-run mode: skipping the mapping and keyboard checks so nothing is clicked or typed)")

    failed = sum(1 for ok, _ in results if not ok)
    print(f"Done: {len(results) - failed} passed, {failed} failed")


if __name__ == "__main__":
    asyncio.run(main())
