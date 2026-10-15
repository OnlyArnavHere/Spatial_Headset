"""Checks a running agent's control server (start the agent with --input --dry-run-input first).

  python selftest_input.py TOKEN [--host 127.0.0.1] [--port 8765]

Verifies: a wrong token is rejected, the right one is accepted, ping/pong works, the window list starts with the whole
desktop, unknown windows are refused, streams open on their own paths, the same headset may open several connections
while another address is refused, and (in dry-run mode) each connection's clicks map to its own stream's pixels.
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


async def click(ws, nx: float, ny: float) -> dict:
    ack = await request(ws, {"type": "down", "x": nx, "y": ny, "button": "left"}, "ack")
    await request(ws, {"type": "up", "x": nx, "y": ny, "button": "left"}, "ack")
    return ack


async def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("token")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    args = parser.parse_args()
    url = f"ws://{args.host}:{args.port}"

    await expect_rejected(url, "not-the-token", "a wrong token")

    async with connect(url) as ws:
        welcome = await request(ws, {"type": "hello", "token": args.token})
        check(welcome.get("type") == "welcome" and welcome.get("version") == 2, f"the pairing token is accepted ({welcome})")
        dry_run = welcome.get("dry_run", False)

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
            else:
                print("(agent is not in dry-run mode: skipping mapping checks so nothing is clicked)")

    failed = sum(1 for ok, _ in results if not ok)
    print(f"Done: {len(results) - failed} passed, {failed} failed")


if __name__ == "__main__":
    asyncio.run(main())
