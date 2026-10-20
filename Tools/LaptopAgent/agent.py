"""Laptop agent: streams this laptop's windows or whole desktop to the headset and, if asked, accepts its mouse input.

  python agent.py --list                          show monitors and windows
  python agent.py                                 let the headset pick what to stream (Laptop icon), view only
  python agent.py --input                         ...and let it move and click the mouse in the streamed windows
  python agent.py --input --keyboard              ...and type into them (each grant is asked on this laptop; Ctrl+Alt+Shift+Q ends everything)
  python agent.py --pair                          pair a new headset: type the code it shows (Laptop > Pair new laptop), then exit
  python agent.py --pair --headset 192.168.1.5    ...at this address instead of finding it over Bonjour
  python agent.py --headset 192.168.1.5           dial this paired headset directly instead of finding it over Bonjour
  python agent.py --pairs                         list paired headsets (name, fingerprint, date), then exit
  python agent.py --revoke NAME                   forget a paired headset, then exit
  python agent.py --window "Notepad"              also start streaming one window right away (e.g. for the browser)
  python agent.py --cast-only                     view only, plus play/pause, seek, next/previous and volume (Cast)
  python agent.py --wired                         print the USB Personal Hotspot address and dial the headset over it

Run `python agent.py --help` for every option. See README.md, especially the risks section, before using --input.
"""
import argparse
import asyncio
import logging
import shutil
import socket
import subprocess
import sys
import threading
import time
from pathlib import Path

import approval
import discovery
import hostos
import identity
import network
import pairing
from session import LinkRunner
from streams import StreamManager, Target, monitor_target, window_target

HERE = Path(__file__).resolve().parent
BIN = HERE / "bin"
RUNTIME = HERE / ".runtime"
RTSP_PORT = 8554  # 127.0.0.1 only: ffmpeg publishes here and the link session reads from here
log = logging.getLogger("agent")
# The Proactor loop queues every UDP datagram through IOCP and falls seconds behind at video rates (the WebRTC
# data channel then starves); the selector loop sends straight away.
LOOP_FACTORY = asyncio.SelectorEventLoop

def tool(name: str) -> str:
    local = BIN / f"{name}{hostos.exe_suffix}"
    if local.exists():
        return str(local)
    found = shutil.which(name)
    if not found:
        raise SystemExit(f"{name} not found. Run `python setup.py` first (it downloads it into {BIN}).")
    return found


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Stream this laptop's windows or desktop to the Spatial Launcher headset.")
    what = p.add_mutually_exclusive_group()
    what.add_argument("--window", metavar="TITLE",
                      help="also start streaming the first visible window whose title contains TITLE right away")
    what.add_argument("--monitor", type=int, metavar="N", help="also start streaming monitor N from --list right away")
    p.add_argument("--no-picker", action="store_true",
                   help="with --window or --monitor: offer only that target, not the window list")
    p.add_argument("--list", action="store_true", help="list monitors and windows, then exit")
    p.add_argument("--pairs", action="store_true", help="list paired headsets, then exit")
    p.add_argument("--revoke", metavar="NAME", help="forget the paired headset called NAME, then exit")
    p.add_argument("--max-width", type=int, default=1920, help="scale each stream down to at most this width (default 1920)")
    p.add_argument("--max-height", type=int, default=1080, help="scale each stream down to at most this height (default 1080)")
    p.add_argument("--fps", type=int, default=30, help="frame rate (default 30)")
    p.add_argument("--bitrate", default="6M", help="video bitrate per stream, e.g. 4M or 8000k (default 6M)")
    p.add_argument("--encoder", choices=["auto"] + [e[0] for e in hostos.ENCODERS], default="auto",
                   help="H.264 encoder (default: first hardware encoder that works, else x264)")
    p.add_argument("--headset", action="append", type=headset_address, metavar="IP[:PORT]",
                   help=f"the headset's address (port {discovery.LINK_PORT} unless given): dialled instead of looking "
                        "for headsets over Bonjour; repeat for several (the first to answer is used)")
    p.add_argument("--pair", action="store_true",
                   help="pair a new headset: type the code it shows, confirm on the headset, then exit")
    p.add_argument("--max-windows", type=int, default=4, choices=range(1, 9), metavar="N",
                   help="most windows the headset can have open at once (default 4, at most 8)")
    p.add_argument("--input", action="store_true",
                   help="let the paired headset move and click the mouse in the windows it streams")
    p.add_argument("--keyboard", action="store_true",
                   help="with --input: also let the headset type into the windows it streams")
    p.add_argument("--dry-run-input", action="store_true",
                   help="with --input or --cast-only: log the mapped events instead of injecting them")
    p.add_argument("--cast-only", action="store_true",
                   help="Cast mode: the headset may only play/pause, seek, skip and change volume; every other key, "
                        "mouse, typing and scroll event is dropped here (can't be combined with --input)")
    p.add_argument("--wired", action="store_true",
                   help="also look for the headset iPhone's USB Personal Hotspot (172.20.10.x) and also "
                        "dial the headset iPhone over it (172.20.10.1; see Doc/wired.md)")
    p.add_argument("--verbose", action="store_true", help="show ffmpeg and MediaMTX logs")
    return p.parse_args()


def headset_address(text: str) -> tuple[str, int]:
    try:
        return discovery.parse_headset(text)
    except ValueError as e:
        raise argparse.ArgumentTypeError(str(e)) from None


def list_targets() -> None:
    print("Monitors:")
    for m in hostos.list_monitors():
        note = '  (primary: "Whole desktop")' if m.primary else ""
        print(f"  {m.index}: {m.rect}{note}")
    print("\nWindows (the headset's picker lists these; use part of a title with --window):")
    for w in hostos.list_windows():
        r = hostos.window_rect(w.handle)
        state = "  (minimized: not listed)" if hostos.window_is_minimized(w.handle) else f"  {r}" if r else ""
        print(f"  {w.title[:70]!r}{state}")


def pinned_target(args) -> Target | None:
    """The target named on the command line, if any."""
    if args.window:
        window = hostos.find_window(args.window)
        if window is None:
            raise SystemExit(f"No visible window title contains {args.window!r}. Try --list.")
        return window_target(window)
    if args.monitor is None:
        return None
    monitors = hostos.list_monitors()
    if not 0 <= args.monitor < len(monitors):
        raise SystemExit(f"Monitor {args.monitor} doesn't exist. Try --list.")
    return monitor_target(monitors[args.monitor])


def output_size(width: int, height: int, max_width: int, max_height: int) -> tuple[int, int]:
    """Fit within the maximum, keeping the aspect ratio (no padding, so normalized input maps 1:1), even sizes."""
    scale = min(1.0, max_width / width, max_height / height)
    return max(2, int(width * scale) // 2 * 2), max(2, int(height * scale) // 2 * 2)


def probe_encoder(ffmpeg: str, choice: str) -> tuple[str, str, str]:
    candidates = hostos.ENCODERS if choice == "auto" else [e for e in hostos.ENCODERS if e[0] == choice]
    for name, codec, pix_fmt in candidates:
        test = [ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=black:s=640x360:r=30",
                "-frames:v", "5", "-pix_fmt", pix_fmt, "-c:v", codec, "-f", "null", "-"]
        result = subprocess.run(test, capture_output=True, text=True, timeout=30)
        if result.returncode == 0:
            return name, codec, pix_fmt
        log.info("Encoder %s unavailable: %s", name, (result.stderr.strip().splitlines() or ["?"])[-1])
    raise SystemExit("No working H.264 encoder found (not even libx264).")


def bitrate_bits(text: str) -> int:
    text = text.strip().lower()
    factor = {"k": 1_000, "m": 1_000_000}.get(text[-1], 1)
    return int(float(text[:-1] if factor > 1 else text) * factor)


def encoder_args(name: str, codec: str, fps: int, bitrate: int) -> list[str]:
    return hostos.encoder_args(name, codec, fps, bitrate)


def write_mediamtx_config(rtsp_port: int = RTSP_PORT, directory: Path = RUNTIME) -> Path:
    """MediaMTX only relays ffmpeg's streams to the link session on this machine: RTSP on loopback, nothing else."""
    directory.mkdir(exist_ok=True)
    config = f"""# Generated by agent.py for one run.
logLevel: info
logDestinations: [stdout]

authMethod: internal
authInternalUsers:
  # ffmpeg publishes the streams (one path per streamed window) and the link session reads them, both on this machine.
  - user: any
    ips: ['127.0.0.1', '::1']
    permissions:
      - action: publish
      - action: read

api: no
metrics: no
pprof: no
playback: no

rtsp: yes
rtspAddress: 127.0.0.1:{rtsp_port}
rtspTransports: [tcp]
rtmp: no
hls: no
srt: no
moq: no
webrtc: no

paths:
  all_others:
    source: publisher
"""
    path = directory / "mediamtx.yml"
    path.write_text(config, encoding="utf-8")
    return path


def wait_for_port(port: int, process: subprocess.Popen, timeout: float) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise SystemExit("MediaMTX exited during startup. Run with --verbose to see why (port in use?).")
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=0.3):
                return
        except OSError:
            time.sleep(0.2)
    raise SystemExit(f"MediaMTX didn't open port {port} within {timeout:g} s.")


def start_mediamtx(args, job: hostos.ChildReaper) -> subprocess.Popen:
    config = write_mediamtx_config()
    mediamtx = job.spawn([tool("mediamtx"), str(config)], cwd=RUNTIME,
                         stdout=None if args.verbose else subprocess.DEVNULL, stderr=subprocess.STDOUT)
    wait_for_port(RTSP_PORT, mediamtx, 10)
    return mediamtx


def capture_launcher(args, encoder: tuple[str, str, str], job: hostos.ChildReaper, rtsp_port: int = RTSP_PORT):
    """Returns launch(target, path): starts ffmpeg capturing the target and publishing it on the path."""
    ffmpeg = tool("ffmpeg")
    name, codec, pix_fmt = encoder

    def launch(target: Target, path: str) -> tuple[subprocess.Popen, tuple[int, int]]:
        rect = target.rect()
        if rect is None or rect.width < 16 or rect.height < 16:
            raise LookupError("minimized")
        rect = hostos.visible_rect(rect)
        width, height = output_size(rect.width, rect.height, args.max_width, args.max_height)
        command = [ffmpeg, "-hide_banner", "-loglevel", "info" if args.verbose else "warning"]
        command += hostos.capture_input_args(target.selector, args.fps, width, height, pix_fmt, ffmpeg=ffmpeg)
        command += hostos.encoder_args(name, codec, args.fps, bitrate_bits(args.bitrate))
        command += ["-f", "rtsp", "-rtsp_transport", "tcp", f"rtsp://127.0.0.1:{rtsp_port}/{path}"]
        log.debug("ffmpeg: %s", subprocess.list2cmdline(command))
        process = job.spawn(command, stdin=subprocess.DEVNULL)
        return process, (width, height)

    return launch


def check_permissions(args: argparse.Namespace, rows: list) -> None:
    """Tells the user which OS permissions are missing (macOS; Windows has none, so rows is empty and nothing prints).
    Missing Screen Recording stops the agent unless it only lists or pairs; missing Accessibility stops --input and
    --cast-only; missing Input Monitoring only costs the kill-switch hotkey, so it warns."""
    lists_only = args.list or args.pairs or args.pair or args.revoke is not None
    needs_input = args.input or args.cast_only
    refused = False
    for row in rows:
        if row.ok:
            continue
        if row.name == "Accessibility" and not needs_input:
            continue  # nothing in this run injects input or raises windows
        refuse = (row.name == "Screen Recording" and not lists_only) or (row.name == "Accessibility")
        refused = refused or refuse
        print(("error: " if refuse else "warning: ") + row.text, file=sys.stderr)
    if refused:
        raise SystemExit("Grant the permission above, restart the app you run this from, and start the agent again.")


def watch_kill_switch(runner: LinkRunner) -> None:
    """Ctrl+Alt+Shift+Q ends every session and stops video and dialling for the rest of the run. The hotkey thread hops
    onto the event loop before touching anything."""
    loop = asyncio.get_running_loop()
    if hostos.watch_kill_switch(lambda: loop.call_soon_threadsafe(runner.kill_switch)):
        log.info("Kill switch: press %s on this laptop to end every session and stop the video", hostos.KILL_SWITCH)
    else:
        log.warning("Kill switch %s is unavailable (taken by another program, or Input Monitoring is not granted on "
                    "macOS); stop the agent with Ctrl+C instead",
                    hostos.KILL_SWITCH)


async def supervise(mediamtx: subprocess.Popen, streams: StreamManager, runner: LinkRunner) -> None:
    watch_kill_switch(runner)
    tasks = [asyncio.create_task(runner.run())]
    try:
        while True:
            if mediamtx.poll() is not None:
                raise SystemExit(f"MediaMTX stopped (exit code {mediamtx.returncode}); shutting down.")
            for task in tasks:
                if task.done():
                    task.result()  # re-raises
            streams.tick()
            await asyncio.sleep(0.5)
    finally:
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)


def pairs_command(args: argparse.Namespace) -> int:
    try:
        return _pairs_command(args)
    except (ValueError, OSError) as e:
        print(f"error: {' '.join(str(e).split())}", file=sys.stderr)
        return 1


def _pairs_command(args: argparse.Namespace) -> int:
    store = pairing.PairStore(identity.default_home())
    if args.revoke is not None:
        if store.revoke(args.revoke):
            print(f"Revoked {args.revoke}.")
            return 0
        print(f"no pair named {args.revoke}")
        return 1
    pairs = store.list()
    if not pairs:
        print("no paired headsets")
    for p in pairs:
        print(f"{p.name}  {identity.format_fp(p.fingerprint)}  {p.added}")
    return 0


# --- --pair: the typed-code flow ------------------------------------------------------------

PAIR_DISCOVERY_S = 5.0
PAIR_GIVE_UP_S = 120.0
PAIR_NOT_FOUND = ("No headset is showing a pairing code yet. On the headset open Laptop > Pair new laptop, "
                  "or pass --headset IP[:PORT] to connect to a known address. Waiting (Ctrl+C to stop)...")


async def _collect(browse, seconds: float, out: list, first_only: bool = False) -> None:
    """Appends what discovery yields to `out` for up to `seconds` (or until the first result)."""
    gen = browse(pairing=True)
    try:
        async with asyncio.timeout(seconds):
            async for item in gen:
                if item not in out:
                    out.append(item)
                if first_only:
                    return
    except TimeoutError:
        pass
    finally:
        await gen.aclose()


async def find_pairing_headset(ask, *, browse=discovery.browse, say=print, seconds: float = PAIR_DISCOVERY_S,
                               give_up: float = PAIR_GIVE_UP_S) -> tuple[str, int] | None:
    """Browses for headsets showing a pairing code. Exactly one is used; with several the user picks one."""
    out: list = []
    await _collect(browse, seconds, out)
    if not out:
        say(PAIR_NOT_FOUND)
        await _collect(browse, max(0.0, give_up - seconds), out, first_only=True)
        if out:
            await _collect(browse, 1.0, out)  # a moment for any others to show up
    if not out:
        say("No headset showed a pairing code. Open Laptop > Pair new laptop on the headset, or use --headset.")
        return None
    if len(out) == 1:
        say(f"Found a headset at {out[0][0]}:{out[0][1]}.")
        return out[0]
    say("More than one headset is showing a pairing code:")
    for i, (host, port) in enumerate(out, 1):
        say(f"  {i}. {host}:{port}")
    answer = (await ask("Which one is yours? Enter its number: ")).strip()
    if answer.isascii() and answer.isdigit() and 1 <= int(answer) <= len(out):
        return out[int(answer) - 1]
    say("That is not one of the numbers above. Run again with --headset IP[:PORT] to choose the headset.")
    return None


class _AnnounceWriter:
    """Wraps the pairing socket's writer and calls `announce` once the agent's proof (its 2nd frame, P3) is sent."""

    def __init__(self, writer, announce):
        self._writer, self._announce, self._frames = writer, announce, 0

    def write(self, data):
        self._writer.write(data)
        self._frames += 1
        if self._frames == 2:
            self._announce()

    def __getattr__(self, name):
        return getattr(self._writer, name)


async def _pair_once(host, port, ident, store, code, name, announce, pair_kw):
    import pair_protocol
    from handshake import HandshakeError
    try:
        reader, writer = await asyncio.wait_for(asyncio.open_connection(host, port), 5.0)
    except (asyncio.TimeoutError, OSError):
        raise HandshakeError("can't connect") from None
    return await pair_protocol.agent_pair(reader, _AnnounceWriter(writer, announce), ident, store, code, name,
                                          **pair_kw)


async def read_on_daemon_thread(read_line, prompt: str) -> str:
    """input() can't be interrupted; on an executor thread asyncio.run would wait for it at Ctrl+C. A daemon thread
    is simply abandoned at exit."""
    loop = asyncio.get_running_loop()
    future = loop.create_future()

    def deliver(setter, value):
        if not future.done():
            setter(value)

    def work():
        try:
            line = read_line(prompt)
        except BaseException as e:
            result = (future.set_exception, e)
        else:
            result = (future.set_result, line)
        try:
            loop.call_soon_threadsafe(deliver, *result)
        except RuntimeError:
            pass  # the loop is already closed

    threading.Thread(target=work, daemon=True, name="stdin").start()
    return await future


async def pair_command(headset: tuple[str, int] | None, ident, store, name: str, *, read_line=input,
                       browse=discovery.browse, say=print, find_seconds: float = PAIR_DISCOVERY_S,
                       give_up: float = PAIR_GIVE_UP_S, **pair_kw) -> int:
    """Pairs with one headset by the code typed in. read_line(prompt) -> str runs on a daemon thread, so Ctrl+C never waits for stdin. Returns the exit
    code."""
    from handshake import HandshakeError
    loop = asyncio.get_running_loop()

    async def ask(prompt: str) -> str:
        return await read_on_daemon_thread(read_line, prompt)

    try:
        if headset is None:
            headset = await find_pairing_headset(ask, browse=browse, say=say, seconds=find_seconds, give_up=give_up)
            if headset is None:
                return 1
        host, port = headset
        wrong = 0
        while True:
            text = await ask("Enter the code shown on the headset (XXXX-XXXX-XXXX): ")
            try:
                code = pairing.parse_code(text)
            except ValueError as e:
                say(str(e))
                continue  # nothing was sent
            try:
                result = await _pair_once(host, port, ident, store, code, name,
                                          lambda: say("Waiting for you to confirm on the headset..."), pair_kw)
            except HandshakeError as e:
                reason = e.reason.removeprefix("peer refused: ")
                if reason == "wrong code":
                    wrong += 1
                    if wrong < pairing.MAX_WRONG_ATTEMPTS:
                        say("That code is wrong. Check it on the headset and try again.")
                        continue
                    say("That code is wrong again, and the headset allows no more tries. Start pairing again on the headset.")
                elif reason == "declined":
                    say("The headset declined (or the confirmation timed out). Nothing was paired.")
                elif reason == "not pairing":
                    say("The headset is not showing a pairing code. Open Laptop > Pair new laptop on it and run --pair again.")
                elif reason == "name taken":
                    say("A paired headset with that name or key already exists here. Revoke it first (--revoke NAME).")
                else:
                    say(f"Pairing failed: {reason}")
                return 1
            say(f"Paired with {result.name} ({identity.format_fp(identity.fingerprint(result.headset_spki))})")
            return 0
    except (EOFError, KeyboardInterrupt, asyncio.CancelledError):
        say("Pairing cancelled.")
        return 1


def pair_main(args: argparse.Namespace) -> int:
    if args.headset and len(args.headset) > 1:
        print("--pair talks to exactly one headset: give --headset once.", file=sys.stderr)
        return 1
    try:
        ident = identity.load_or_create(identity.default_home())
        store = pairing.PairStore(identity.default_home())
        return asyncio.run(pair_command(args.headset[0] if args.headset else None, ident, store,
                                        socket.gethostname()), loop_factory=LOOP_FACTORY)
    except (identity.IdentityError, OSError, ValueError) as e:
        print(f"error: {' '.join(str(e).split())}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("Pairing cancelled.")
        return 1


def main() -> None:
    args = parse_args()
    logging.basicConfig(level=logging.DEBUG if args.verbose else logging.INFO,
                        format="%(asctime)s %(name)s: %(message)s", datefmt="%H:%M:%S")
    hostos.make_dpi_aware()
    check_permissions(args, hostos.preflight())

    if args.list:
        list_targets()
        return
    if args.pairs or args.revoke is not None:
        sys.exit(pairs_command(args))
    if args.pair:
        sys.exit(pair_main(args))
    if args.cast_only and (args.input or args.keyboard):
        raise SystemExit("--cast-only is view-only plus media controls; it can't be combined with --input or --keyboard.")
    if args.dry_run_input and not (args.input or args.cast_only):
        raise SystemExit("--dry-run-input only makes sense together with --input or --cast-only.")
    if args.keyboard and not args.input:
        raise SystemExit("--keyboard only makes sense together with --input.")
    pinned = pinned_target(args)
    if args.no_picker and pinned is None:
        raise SystemExit("--no-picker needs --window or --monitor (otherwise there would be nothing to stream).")

    try:
        ident = identity.load_or_create(identity.default_home())
    except (identity.IdentityError, OSError) as e:
        raise SystemExit(f"error: {' '.join(str(e).split())}")
    store = pairing.PairStore(identity.default_home())

    encoder = probe_encoder(tool("ffmpeg"), args.encoder)
    job = hostos.ChildReaper()
    mediamtx = start_mediamtx(args, job)
    streams = StreamManager(capture_launcher(args, encoder, job), only_pinned=args.no_picker)
    pinned_stream = None
    if pinned is not None:
        try:
            pinned_stream = streams.pin(pinned.id)
        except LookupError:
            raise SystemExit(f"Can't capture {pinned.description}: it has no usable size (minimized?).")
    runner = LinkRunner(ident, store, streams, RTSP_PORT, max_windows=args.max_windows,
                        input_enabled=args.input, keyboard_enabled=args.keyboard, cast_only=args.cast_only,
                        dry_run=args.dry_run_input, pinned=args.headset,
                        approver=approval.LaptopApprover(keyboard=args.keyboard) if args.input and not args.cast_only else None, discover=not args.headset)

    keyboard = "off (start with --input --keyboard to allow it)"
    if args.keyboard:
        keyboard = ("ON - " + ("DRY RUN, logging only" if args.dry_run_input else "the paired headset can type into streamed windows"))
    kill_switch = f"{hostos.KILL_SWITCH} ends every headset session and stops the video; restart the agent to reconnect"
    cast = ("ON - view only; the headset may play/pause, seek, skip and change volume. Every other key, mouse,"
            "\n                    typing and scroll event is dropped" + (" (DRY RUN, logging only)" if args.dry_run_input else "")) \
        if args.cast_only else "off (start with --cast-only for a view-only Cast window)"
    wired = network.wired_report() if args.wired else None
    wired_lines = ("\n " + "\n ".join(wired.lines())) if wired is not None else ""
    offered = f"only {pinned.description}" if args.no_picker else "the headset picks a window or the whole desktop"
    if args.headset:
        link = "dialling " + ", ".join(f"{h}:{p}" for h, p in args.headset)
    else:
        link = "looking for headsets over Bonjour (_spatiallauncher._tcp)" + (" and the USB hotspot" if wired is not None else "")
    started = (f"\n Streaming now:     {pinned_stream.target.description} on /{pinned_stream.path}"
               f"   ({pinned_stream.size[0]}x{pinned_stream.size[1]})") if pinned_stream else ""
    print(f"""
==============================================================================
 Offering:          {offered}
                    up to {args.fps} fps, {args.bitrate}bit/s per stream, encoder {encoder[0]}{started}

 Headset link:      {link}
                    up to {args.max_windows} windows at once; this laptop is {identity.format_fp(ident.fingerprint)}
 Mouse input:       {'ON - ' + ('DRY RUN, logging only' if args.dry_run_input else 'the paired headset can move and click your mouse, once you click Yes on this laptop') if args.input else 'off (start with --input to allow it)'}
 Keyboard input:    {keyboard}
 Cast only:         {cast}
 Kill switch:       {kill_switch}{wired_lines}

 Press Ctrl+C to stop.
==============================================================================
""", flush=True)

    try:
        asyncio.run(supervise(mediamtx, streams, runner), loop_factory=LOOP_FACTORY)
    except KeyboardInterrupt:
        pass
    finally:
        streams.stop_all()
        if mediamtx.poll() is None:
            mediamtx.terminate()
        try:
            mediamtx.wait(timeout=5)
        except subprocess.TimeoutExpired:
            mediamtx.kill()
        print("Stopped.")


if __name__ == "__main__":
    main()
