"""Laptop agent: streams this laptop's screen (or one window) to the headset and, if asked, accepts its mouse input.

  python agent.py --list                          show monitors and windows
  python agent.py                                 stream the primary monitor (no input)
  python agent.py --window "Notepad" --input      stream one window and accept mouse input for it

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

import winapi
from input_server import InputServer, Target

HERE = Path(__file__).resolve().parent
BIN = HERE / "bin"
RUNTIME = HERE / ".runtime"
STREAM_PATH = "laptop"
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
    p = argparse.ArgumentParser(description="Stream this laptop's screen to the Spatial Launcher headset.")
    what = p.add_mutually_exclusive_group()
    what.add_argument("--window", metavar="TITLE", help="stream the first visible window whose title contains TITLE")
    what.add_argument("--monitor", type=int, metavar="N", help="stream monitor N from --list (default: primary)")
    p.add_argument("--list", action="store_true", help="list monitors and windows, then exit")
    p.add_argument("--max-width", type=int, default=1920, help="scale down to at most this width (default 1920)")
    p.add_argument("--max-height", type=int, default=1080, help="scale down to at most this height (default 1080)")
    p.add_argument("--fps", type=int, default=30, help="frame rate (default 30)")
    p.add_argument("--bitrate", default="6M", help="video bitrate, e.g. 4M or 8000k (default 6M)")
    p.add_argument("--encoder", choices=["auto"] + [e[0] for e in ENCODERS], default="auto",
                   help="H.264 encoder (default: first hardware encoder that works, else x264)")
    p.add_argument("--webrtc-port", type=int, default=8889, help="MediaMTX WebRTC/WHEP HTTP port (default 8889)")
    p.add_argument("--ice-udp-port", type=int, default=8189, help="MediaMTX WebRTC media UDP port (default 8189)")
    p.add_argument("--input", action="store_true",
                   help="also start the input server, letting the paired headset move and click the mouse")
    p.add_argument("--input-port", type=int, default=8765, help="input WebSocket port (default 8765)")
    p.add_argument("--dry-run-input", action="store_true",
                   help="with --input: log the mapped mouse events instead of injecting them")
    p.add_argument("--verbose", action="store_true", help="show ffmpeg and MediaMTX logs")
    return p.parse_args()


def list_targets() -> None:
    print("Monitors:")
    for m in winapi.list_monitors():
        print(f"  {m.index}: {m.rect}{'  (primary)' if m.primary else ''}")
    print("\nWindows (use part of a title with --window):")
    for w in winapi.list_windows():
        r = winapi.window_rect(w.handle)
        state = "  (minimized)" if winapi.window_is_minimized(w.handle) else f"  {r}" if r else ""
        print(f"  {w.title[:70]!r}{state}")


def choose_target(args) -> tuple[Target, str]:
    """The capture target and its gfxcapture selector (an exact HWND/HMONITOR, so input maps to the same thing)."""
    if args.window:
        window = winapi.find_window(args.window)
        if window is None:
            raise SystemExit(f"No visible window title contains {args.window!r}. Try --list.")
        handle = window.handle
        target = Target("window", handle, f"window {window.title!r}",
                        rect=lambda: winapi.window_rect(handle),
                        usable=lambda: winapi.window_exists(handle) and not winapi.window_is_minimized(handle))
        return target, f"hwnd={handle}"

    monitors = winapi.list_monitors()
    if args.monitor is None:
        monitor = next((m for m in monitors if m.primary), monitors[0])
    elif 0 <= args.monitor < len(monitors):
        monitor = monitors[args.monitor]
    else:
        raise SystemExit(f"Monitor {args.monitor} doesn't exist. Try --list.")
    handle = monitor.handle
    target = Target("monitor", handle, f"monitor {monitor.index} ({monitor.rect})",
                    rect=lambda: winapi.monitor_rect(handle), usable=lambda: True)
    return target, f"hmonitor={handle}"


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
    a keyframe every second so a reconnecting headset gets a picture quickly, and the baseline profile."""
    b, buf, gop = str(bitrate), str(bitrate // 2), str(fps)
    common = ["-b:v", b, "-maxrate", b, "-bufsize", buf, "-g", gop, "-bf", "0"]
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
  # ffmpeg on this machine publishes the stream.
  - user: any
    ips: ['127.0.0.1', '::1']
    permissions:
      - action: publish
        path: {STREAM_PATH}
  # The headset reads it over WebRTC (WHEP) with the pairing token, from the local network only.
  - user: viewer
    pass: '{token}'
    ips: [{networks}]
    permissions:
      - action: read
        path: {STREAM_PATH}

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

webrtc: yes
webrtcAddress: :{webrtc_port}
webrtcLocalUDPAddress: :{ice_udp_port}
webrtcLocalTCPAddress: ''

paths:
  {STREAM_PATH}:
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


def start_processes(args, selector: str, size: tuple[int, int], encoder: tuple[str, str, str], token: str):
    job = winapi.KillOnExitJob()
    mediamtx_out = None if args.verbose else subprocess.DEVNULL
    config = write_mediamtx_config(token, args.webrtc_port, args.ice_udp_port)
    mediamtx = subprocess.Popen([tool("mediamtx"), str(config)], cwd=RUNTIME,
                                stdout=mediamtx_out, stderr=subprocess.STDOUT)
    job.add(mediamtx.pid)
    wait_for_port(RTSP_PORT, mediamtx, 10)

    name, codec, pix_fmt = encoder
    width, height = size
    capture = (f"gfxcapture={selector}:max_framerate={args.fps}:capture_cursor=1,"
               f"hwdownload,format=bgra,scale={width}:{height}:flags=bilinear,format={pix_fmt}[v]")
    command = [tool("ffmpeg"), "-hide_banner", "-loglevel", "info" if args.verbose else "warning",
               "-filter_complex", capture, "-map", "[v]", "-fps_mode", "vfr"]
    command += encoder_args(name, codec, args.fps, bitrate_bits(args.bitrate))
    command += ["-f", "rtsp", "-rtsp_transport", "tcp", f"rtsp://127.0.0.1:{RTSP_PORT}/{STREAM_PATH}"]
    log.debug("ffmpeg: %s", subprocess.list2cmdline(command))
    ffmpeg = subprocess.Popen(command, stdin=subprocess.DEVNULL)
    job.add(ffmpeg.pid)
    return mediamtx, ffmpeg, job


async def supervise(processes: dict, input_server: InputServer | None) -> None:
    tasks = [asyncio.create_task(input_server.run())] if input_server else []
    try:
        while True:
            for name, process in processes.items():
                if process.poll() is not None:
                    raise SystemExit(f"{name} stopped (exit code {process.returncode}); shutting down.")
            for task in tasks:
                if task.done():
                    task.result()  # re-raises, e.g. the input port is already in use
            await asyncio.sleep(0.5)
    finally:
        for task in tasks:
            task.cancel()


def main() -> None:
    args = parse_args()
    logging.basicConfig(level=logging.DEBUG if args.verbose else logging.INFO,
                        format="%(asctime)s %(name)s: %(message)s", datefmt="%H:%M:%S")
    logging.getLogger("websockets").setLevel(logging.WARNING)
    winapi.make_dpi_aware()

    if args.list:
        list_targets()
        return
    if args.dry_run_input and not args.input:
        raise SystemExit("--dry-run-input only makes sense together with --input.")

    target, selector = choose_target(args)
    rect = target.rect()
    if rect is None or rect.width < 16 or rect.height < 16:
        raise SystemExit(f"Can't capture {target.description}: it has no usable size (minimized?).")
    size = output_size(rect.width, rect.height, args.max_width, args.max_height)
    encoder = probe_encoder(tool("ffmpeg"), args.encoder)
    token = secrets.token_urlsafe(18)

    mediamtx, ffmpeg, _job = start_processes(args, selector, size, encoder, token)
    input_server = InputServer(token, target, port=args.input_port, dry_run=args.dry_run_input) if args.input else None

    hosts = lan_addresses()
    print(f"""
==============================================================================
 Streaming {target.description}
   {rect.width}x{rect.height} -> {size[0]}x{size[1]} at up to {args.fps} fps, {args.bitrate}bit/s, encoder {encoder[0]}

 PAIRING TOKEN:  {token}
   (new every run; type it into the Laptop window's connect panel)

 Headset settings:  host {hosts[0]}   video port {args.webrtc_port}   input port {args.input_port}
                    (same machine: 127.0.0.1; other adapters: {', '.join(hosts[1:]) or 'none'})
 Browser preview:   http://{hosts[0]}:{args.webrtc_port}/{STREAM_PATH}/   (user: viewer, password: the token)
 Mouse input:       {'ON - ' + ('DRY RUN, logging only' if args.dry_run_input else 'the paired headset can move and click your mouse') if args.input else 'off (start with --input to allow it)'}

 Press Ctrl+C to stop.
==============================================================================
""", flush=True)

    try:
        asyncio.run(supervise({"MediaMTX": mediamtx, "ffmpeg": ffmpeg}, input_server))
    except KeyboardInterrupt:
        pass
    finally:
        for process in (ffmpeg, mediamtx):
            if process.poll() is None:
                process.terminate()
        for process in (ffmpeg, mediamtx):
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
        print("Stopped.")


if __name__ == "__main__":
    main()
