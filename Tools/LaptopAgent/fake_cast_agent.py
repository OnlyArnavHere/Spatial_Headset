"""A stand-in laptop agent for the editor's Cast test: the real control server in cast-only, dry-run mode, with fake
windows instead of captures and every Windows input call replaced by a recorder (nothing is injected).

  python fake_cast_agent.py [--port 8765] [--token cast-test-token] [--seconds 300]

It writes what it received to .runtime/fake_cast_state.json every few tenths of a second: the media actions that
reached the (recorded) input layer and the messages it dropped. Unity's "Test Cast" reads that file. No video is served.
"""
import argparse
import asyncio
import json
import logging
from pathlib import Path

import input_server
from input_server import InputServer
from selftest_cast import FakeStreams, Recorder

STATE = Path(__file__).resolve().parent / ".runtime" / "fake_cast_state.json"


async def main(port: int, token: str, seconds: float) -> None:
    recorder = Recorder()
    recorder.install()
    server = InputServer(token, FakeStreams(), host="127.0.0.1", port=port, cast_only=True, dry_run=False)
    task = asyncio.create_task(server.run())
    STATE.parent.mkdir(exist_ok=True)
    print(f"Fake cast agent on 127.0.0.1:{port}, token {token!r}, for {seconds:g} s", flush=True)
    try:
        for _ in range(int(seconds / 0.3)):
            await asyncio.sleep(0.3)
            if task.done():
                task.result()
            keys = [c for c in recorder.calls if c[0] == "key"]
            state = {"dropped": server.dropped, "dropped_total": sum(server.dropped.values()),
                     "key_call_count": len(keys), "key_calls": [f"{c[1]:#04x}:{'down' if c[2] else 'up'}" for c in keys],
                     "other_calls": [c[0] for c in recorder.calls if c[0] not in ("key", "focus_window")]}
            STATE.write_text(json.dumps(state), encoding="utf-8")
    finally:
        task.cancel()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--token", default="cast-test-token")
    parser.add_argument("--seconds", type=float, default=300)
    args = parser.parse_args()
    input_server.log.setLevel(logging.WARNING)
    logging.basicConfig(level=logging.WARNING)
    try:
        asyncio.run(main(args.port, args.token, args.seconds))
    except KeyboardInterrupt:
        pass
