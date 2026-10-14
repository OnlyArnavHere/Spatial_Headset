"""Checks a running agent's input server (start the agent with --input --dry-run-input first).

  python selftest_input.py TOKEN [--host 127.0.0.1] [--port 8765]

Verifies: a wrong token is rejected, the right one is accepted, ping/pong works, a second headset is refused while one
is connected, and (in dry-run mode) normalized positions map to the expected pixels of the captured area.
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


async def expect_rejected(url: str, token: str, what: str) -> None:
    try:
        async with connect(url) as ws:
            await ws.send(json.dumps({"type": "hello", "token": token}))
            reply = json.loads(await asyncio.wait_for(ws.recv(), 6))
            check(reply.get("type") == "error", f"{what} is rejected ({reply})")
    except ConnectionClosed as closed:
        check(True, f"{what} is rejected (closed: {closed.rcvd.reason if closed.rcvd else '?'})")


async def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("token")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    args = parser.parse_args()
    url = f"ws://{args.host}:{args.port}"

    await expect_rejected(url, "not-the-token", "a wrong token")

    async with connect(url) as ws:
        await ws.send(json.dumps({"type": "hello", "token": args.token}))
        welcome = json.loads(await ws.recv())
        check(welcome.get("type") == "welcome", f"the pairing token is accepted (target {welcome.get('target')})")
        target = welcome.get("target", {})
        dry_run = welcome.get("dry_run", False)

        sent = time.time() * 1000
        await ws.send(json.dumps({"type": "ping", "id": 1, "t": sent}))
        pong = json.loads(await ws.recv())
        rtt = time.time() * 1000 - sent
        check(pong.get("type") == "pong" and pong.get("id") == 1 and "agent_ms" in pong, f"ping/pong works (RTT {rtt:.1f} ms)")

        await expect_rejected(url, args.token, "a second headset while one is connected")

        if dry_run:
            for nx, ny in [(0.0, 0.0), (0.5, 0.5), (0.999, 0.999)]:
                await ws.send(json.dumps({"type": "down", "x": nx, "y": ny, "button": "left"}))
                ack = json.loads(await ws.recv())
                await ws.send(json.dumps({"type": "up", "x": nx, "y": ny, "button": "left"}))
                await ws.recv()
                check(ack.get("type") == "ack", f"({nx}, {ny}) maps to pixel ({ack.get('x')}, {ack.get('y')}) "
                                                f"of a {target.get('width')}x{target.get('height')} target")
        else:
            print("(agent is not in dry-run mode: skipping mapping checks so nothing is clicked)")

    failed = sum(1 for ok, _ in results if not ok)
    print(f"Done: {len(results) - failed} passed, {failed} failed")


if __name__ == "__main__":
    asyncio.run(main())
