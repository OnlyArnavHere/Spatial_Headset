"""Laptop agent: streams this laptop's windows or whole desktop to the headset and, if asked, accepts its mouse input.

  python agent.py --list                          show monitors and windows
  python agent.py                                 let the headset pick what to stream (Laptop icon), view only
  python agent.py --input                         ...and let it move and click the mouse in the streamed windows
  python agent.py --input --keyboard              ...and type into them (Ctrl+Alt+Shift+Q stops all input)
  python agent.py --pairs                         list paired headsets (name, fingerprint, date), then exit
  python agent.py --revoke NAME                   forget a paired headset, then exit
  python agent.py --window "Notepad"              also start streaming one window right away (e.g. for the browser)
  python agent.py --cast-only                     view only, plus play/pause, seek, next/previous and volume (Cast)
  python agent.py --wired                         print the USB Personal Hotspot address and check the firewall for it

Run `python agent.py --help` for every option. See README.md, especially the risks section, before using --input.
"""
import argparse
import asyncio
import logging
import secrets
import shutil
import socket
import subprocess
import sys
import time
from pathlib import Path

import identity
import network
import pairing
import winapi
from input_server import InputServer
from streams import StreamManager, Target, monitor_target, window_target

HERE = Path(__file__).resolve().parent
BIN = HERE / "bin"
RUNTIME = HERE / ".runtime"
RTSP_PORT = 8554  # local only: ffmpeg publishes here
log = logging.getLogger("agent")

# Probe order: hardware encoders first, then x264. Each entry: (name, ffmpeg encoder, pixel format).
ENCODERS = [
    ("nvenc", "h264_nvenc", "yuv420p"),
    ("qsv", "h264_qsv", "nv12"),
    ("amf", "h264_amf", "nv12"),
    ("x264", "libx264", "yuv420p"),
]

# Viewers (WHEP) may connect from these networks only, and only with the pairing token as password.
LOCAL_NETWORKS = ["127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16",
                  "::1/128", "fc00::/7", "fe80::/10"]


def tool(name: str) -> str:
    local = BIN / f"{name}.exe"
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
    p.add_argument("--encoder", choices=["auto"] + [e[0] for e in ENCODERS], default="auto",
                   help="H.264 encoder (default: first hardware encoder that works, else x264)")
    p.add_argument("--webrtc-port", type=int, default=8889, help="MediaMTX WebRTC/WHEP HTTP port (default 8889)")
    p.add_argument("--ice-udp-port", type=int, default=8189, help="MediaMTX WebRTC media UDP port (default 8189)")
    p.add_argument("--input", action="store_true",
                   help="let the paired headset move and click the mouse in the windows it streams")
    p.add_argument("--input-port", type=int, default=8765, help="control and input WebSocket port (default 8765)")
    p.add_argument("--keyboard", action="store_true",
                   help="with --input: also let the headset type into the windows it streams (unencrypted: no passwords)")
    p.add_argument("--dry-run-input", action="store_true",
                   help="with --input or --cast-only: log the mapped events instead of injecting them")
    p.add_argument("--cast-only", action="store_true",
                   help="Cast mode: the headset may only play/pause, seek, skip and change volume; every other key, "
                        "mouse, typing and scroll event is dropped here (can't be combined with --input)")
    p.add_argument("--wired", action="store_true",
                   help="also look for the headset iPhone's USB Personal Hotspot (172.20.10.x) and check that the "
                        "firewall lets the headset in on it (see Doc/wired.md)")
    p.add_argument("--verbose", action="store_true", help="show ffmpeg and MediaMTX logs")
    return p.parse_args()


def list_targets() -> None:
    print("Monitors:")
    for m in winapi.list_monitors():
        note = '  (primary: "Whole desktop")' if m.primary else ""
        print(f"  {m.index}: {m.rect}{note}")
    print("\nWindows (the headset's picker lists these; use part of a title with --window):")
    for w in winapi.list_windows():
        r = winapi.window_rect(w.handle)
        state = "  (minimized: not listed)" if winapi.window_is_minimized(w.handle) else f"  {r}" if r else ""
        print(f"  {w.title[:70]!r}{state}")


def pinned_target(args) -> Target | None:
    """The target named on the command line, if any."""
    if args.window:
        window = winapi.find_window(args.window)
        if window is None:
            raise SystemExit(f"No visible window title contains {args.window!r}. Try --list.")
        return window_target(window)
    if args.monitor is None:
        return None
    monitors = winapi.list_monitors()
    if not 0 <= args.monitor < len(monitors):
        raise SystemExit(f"Monitor {args.monitor} doesn't exist. Try --list.")
    return monitor_target(monitors[args.monitor])


def output_size(width: int, height: int, max_width: int, max_height: int) -> tuple[int, int]:
    """Fit within the maximum, keeping the aspect ratio (no padding, so normalized input maps 1:1), even sizes."""
    scale = min(1.0, max_width / width, max_height / height)
    return max(2, int(width * scale) // 2 * 2), max(2, int(height * scale) // 2 * 2)


def probe_encoder(ffmpeg: str, choice: str) -> tuple[str, str, str]:
    candidates = ENCODERS if choice == "auto" else [e for e in ENCODERS if e[0] == choice]
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
    """Low-latency settings: constant bitrate, a small buffer, no B-frames (WebRTC can't use them),
    a keyframe every second so a reconnecting headset gets a picture quickly, and the baseline profile.
    Keyframes go by time, not frame count: a static window delivers only a couple of frames a second."""
    b, buf, gop = str(bitrate), str(bitrate // 2), str(fps)
    common = ["-b:v", b, "-maxrate", b, "-bufsize", buf, "-g", gop, "-bf", "0",
              "-force_key_frames", "expr:gte(t,n_forced*1)"]
    if name == "nvenc":
        return ["-c:v", codec, "-preset", "p1", "-tune", "ull", "-rc", "cbr", "-zerolatency", "1",
                "-profile:v", "baseline", "-forced-idr", "1"] + common
    if name == "qsv":
        return ["-c:v", codec, "-preset", "veryfast", "-profile:v", "baseline", "-async_depth", "1",
                "-look_ahead", "0"] + common
    if name == "amf":
        return ["-c:v", codec, "-usage", "ultralowlatency", "-quality", "speed", "-rc", "cbr",
                "-profile:v", "constrained_baseline"] + common
    return ["-c:v", codec, "-preset", "ultrafast", "-tune", "zerolatency", "-profile:v", "baseline"] + common


def write_mediamtx_config(token: str, webrtc_port: int, ice_udp_port: int) -> Path:
    RUNTIME.mkdir(exist_ok=True)
    networks = ", ".join(f"'{n}'" for n in LOCAL_NETWORKS)
    config = f"""# Generated by agent.py for one run; holds this run's pairing token. Do not share.
logLevel: info
logDestinations: [stdout]

authMethod: internal
authInternalUsers:
  # ffmpeg on this machine publishes the streams (one path per streamed window).
  - user: any
    ips: ['127.0.0.1', '::1']
    permissions:
      - action: publish
  # The headset reads them over WebRTC (WHEP) with the pairing token, from the local network only.
  - user: viewer
    pass: '{token}'
    ips: [{networks}]
    permissions:
      - action: read

api: no
metrics: no
pprof: no
playback: no

rtsp: yes
rtspAddress: 127.0.0.1:{RTSP_PORT}
rtspTransports: [tcp]
rtmp: no
hls: no
srt: no
moq: no

webrtc: yes
webrtcAddress: :{webrtc_port}
webrtcLocalUDPAddress: :{ice_udp_port}
webrtcLocalTCPAddress: ''

paths:
  all_others:
    source: publisher
"""
    path = RUNTIME / "mediamtx.yml"
    path.write_text(config, encoding="utf-8")
    return path


def lan_addresses() -> list[str]:
    """This laptop's IPv4 addresses, the one on the default route (usually Wi-Fi) first. Virtual adapters
    (WSL, Hyper-V, VirtualBox) also appear, but the headset can only reach the first one."""
    addresses = set()
    try:
        for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET):
            addresses.add(info[4][0])
    except socket.gaierror:
        pass
    primary = None
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
            probe.connect(("192.0.2.1", 9))  # TEST-NET address: picks the outgoing interface, sends nothing
            primary = probe.getsockname()[0]
    except OSError:
        pass
    others = sorted(a for a in addresses if not a.startswith("127.") and a != primary)
    return ([primary] if primary else []) + others or ["127.0.0.1"]


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


def start_mediamtx(args, token: str, job: winapi.KillOnExitJob) -> subprocess.Popen:
    config = write_mediamtx_config(token, args.webrtc_port, args.ice_udp_port)
    mediamtx = subprocess.Popen([tool("mediamtx"), str(config)], cwd=RUNTIME,
                                stdout=None if args.verbose else subprocess.DEVNULL, stderr=subprocess.STDOUT)
    job.add(mediamtx.pid)
    wait_for_port(RTSP_PORT, mediamtx, 10)
    return mediamtx


def capture_launcher(args, encoder: tuple[str, str, str], job: winapi.KillOnExitJob):
    """Returns launch(target, path): starts ffmpeg capturing the target and publishing it on the path."""
    ffmpeg = tool("ffmpeg")
    name, codec, pix_fmt = encoder

    def launch(target: Target, path: str) -> tuple[subprocess.Popen, tuple[int, int]]:
        rect = target.rect()
        if rect is None or rect.width < 16 or rect.height < 16:
            raise LookupError("minimized")
        width, height = output_size(rect.width, rect.height, args.max_width, args.max_height)
        capture = (f"gfxcapture={target.selector}:max_framerate={args.fps}:capture_cursor=1,"
                   f"hwdownload,format=bgra,scale={width}:{height}:flags=bilinear,format={pix_fmt}[v]")
        command = [ffmpeg, "-hide_banner", "-loglevel", "info" if args.verbose else "warning",
                   "-filter_complex", capture, "-map", "[v]", "-fps_mode", "vfr"]
        command += encoder_args(name, codec, args.fps, bitrate_bits(args.bitrate))
        command += ["-f", "rtsp", "-rtsp_transport", "tcp", f"rtsp://127.0.0.1:{RTSP_PORT}/{path}"]
        log.debug("ffmpeg: %s", subprocess.list2cmdline(command))
        process = subprocess.Popen(command, stdin=subprocess.DEVNULL)
        job.add(process.pid)
        return process, (width, height)

    return launch


async def supervise(mediamtx: subprocess.Popen, streams: StreamManager, server: InputServer) -> None:
    task = asyncio.create_task(server.run())
    try:
        while True:
            if mediamtx.poll() is not None:
                raise SystemExit(f"MediaMTX stopped (exit code {mediamtx.returncode}); shutting down.")
            if task.done():
                task.result()  # re-raises, e.g. the control port is already in use
            streams.tick()
            await asyncio.sleep(0.5)
    finally:
        task.cancel()


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


def main() -> None:
    args = parse_args()
    logging.basicConfig(level=logging.DEBUG if args.verbose else logging.INFO,
                        format="%(asctime)s %(name)s: %(message)s", datefmt="%H:%M:%S")
    logging.getLogger("websockets").setLevel(logging.WARNING)
    winapi.make_dpi_aware()

    if args.list:
        list_targets()
        return
    if args.pairs or args.revoke is not None:
        sys.exit(pairs_command(args))
    if args.cast_only and (args.input or args.keyboard):
        raise SystemExit("--cast-only is view-only plus media controls; it can't be combined with --input or --keyboard.")
    if args.dry_run_input and not (args.input or args.cast_only):
        raise SystemExit("--dry-run-input only makes sense together with --input or --cast-only.")
    if args.keyboard and not args.input:
        raise SystemExit("--keyboard only makes sense together with --input.")
    pinned = pinned_target(args)
    if args.no_picker and pinned is None:
        raise SystemExit("--no-picker needs --window or --monitor (otherwise there would be nothing to stream).")

    encoder = probe_encoder(tool("ffmpeg"), args.encoder)
    token = secrets.token_urlsafe(18)
    job = winapi.KillOnExitJob()
    mediamtx = start_mediamtx(args, token, job)
    streams = StreamManager(capture_launcher(args, encoder, job), only_pinned=args.no_picker)
    pinned_stream = None
    if pinned is not None:
        try:
            pinned_stream = streams.pin(pinned.id)
        except LookupError:
            raise SystemExit(f"Can't capture {pinned.description}: it has no usable size (minimized?).")
    server = InputServer(token, streams, port=args.input_port, input_enabled=args.input,
                         keyboard_enabled=args.keyboard, dry_run=args.dry_run_input, cast_only=args.cast_only)

    keyboard = "off (start with --input --keyboard to allow it)"
    if args.keyboard:
        keyboard = ("ON - " + ("DRY RUN, logging only" if args.dry_run_input else "the paired headset can type into streamed windows")
                    + "\n                    UNENCRYPTED on the network: don't type passwords")
    kill_switch = f"{winapi.KILL_SWITCH} stops all mouse and keyboard input for the rest of the run" if args.input \
        else f"{winapi.KILL_SWITCH} stops the media controls for the rest of the run" if args.cast_only else "-"
    cast = ("ON - view only; the headset may play/pause, seek, skip and change volume. Every other key, mouse,"
            "\n                    typing and scroll event is dropped" + (" (DRY RUN, logging only)" if args.dry_run_input else "")) \
        if args.cast_only else "off (start with --cast-only for a view-only Cast window)"
    hosts = lan_addresses()
    wired = network.wired_report(args.webrtc_port, args.ice_udp_port, args.input_port) if args.wired else None
    if wired is not None and wired.interface is not None and wired.interface.address in hosts:
        hosts.remove(wired.interface.address)
        hosts.insert(0, wired.interface.address)  # over the cable first
    wired_lines = ("\n " + "\n ".join(wired.lines())) if wired is not None else ""
    offered = f"only {pinned.description}" if args.no_picker else "the headset picks a window or the whole desktop"
    started = (f"\n Streaming now:     {pinned_stream.target.description} on /{pinned_stream.path}"
               f"   ({pinned_stream.size[0]}x{pinned_stream.size[1]})") if pinned_stream else ""
    print(f"""
==============================================================================
 Offering:          {offered}
                    up to {args.fps} fps, {args.bitrate}bit/s per stream, encoder {encoder[0]}{started}

 PAIRING TOKEN:  {token}
   (new every run; type it into the Laptop picker's connect panel)

 Headset settings:  host {hosts[0]}   video port {args.webrtc_port}   control port {args.input_port}
                    (same machine: 127.0.0.1; other adapters: {', '.join(hosts[1:]) or 'none'})
 Browser preview:   http://{hosts[0]}:{args.webrtc_port}/<path>/ while a stream runs (user: viewer, password: the token)
 Mouse input:       {'ON - ' + ('DRY RUN, logging only' if args.dry_run_input else 'the paired headset can move and click your mouse') if args.input else 'off (start with --input to allow it)'}
 Keyboard input:    {keyboard}
 Cast only:         {cast}
 Kill switch:       {kill_switch}{wired_lines}

 Press Ctrl+C to stop.
==============================================================================
""", flush=True)

    try:
        asyncio.run(supervise(mediamtx, streams, server))
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
