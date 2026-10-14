# Laptop Agent

Streams a Windows laptop's screen, or a single window, to the Spatial Launcher headset's **Laptop** window, and, if you
allow it, lets the headset move and click the laptop's mouse.

> Prototype. It has been tested with the agent and the Unity editor on the same Windows machine, not yet with an
> iPhone over Wi-Fi. Read **Risks** before using `--input`.

## What runs

| Piece | Role | Listens on |
|---|---|---|
| `ffmpeg` | Captures the screen or window (Windows Graphics Capture) and encodes H.264: NVENC, QuickSync or AMF if one works, else x264 with zero-latency settings | nothing (publishes to MediaMTX on 127.0.0.1) |
| `MediaMTX` | Serves the stream over WebRTC; the headset connects with WHEP | TCP 8889 (WHEP/HTTP), UDP 8189 (media); RTSP 8554 on 127.0.0.1 only |
| `input_server.py` | Receives pointer events and injects mouse input. **Only with `--input`** | TCP 8765 (WebSocket) |

`agent.py` starts and stops all of them. They run only while `agent.py` runs: nothing is installed as a service or
started at login, and if `agent.py` is closed or killed, Windows ends ffmpeg and MediaMTX with it.

## Setup (once)

Requires Windows 10 1903 or later (Windows 11 recommended) and Python 3.10 or later.

```
cd Tools/LaptopAgent
python setup.py
```

This downloads pinned versions of MediaMTX and FFmpeg into `bin/` (git-ignored), verifies their SHA-256 checksums,
and installs the Python dependency (`websockets`). Nothing downloaded is committed to the repository.

## Use

```
python agent.py --list                                   # monitors and windows
python agent.py                                          # stream the primary monitor, no input
python agent.py --window "Notepad"                       # stream one window (part of its title)
python agent.py --window "Notepad" --input               # ...and accept mouse input from the headset
python agent.py --max-width 1280 --max-height 720 --fps 60 --bitrate 8M --encoder nvenc
```

The console prints a **pairing token** (new every run) and the address to use. In the headset's Laptop window, enter
the laptop's address, port `8889` and the token, then **Connect**. You can also watch the stream in a browser at
`http://<laptop>:8889/laptop/` (user `viewer`, password the token).

Options: `--monitor N`, `--max-width`, `--max-height` (the picture is scaled down to fit, never padded, so pointer
positions map 1:1), `--fps`, `--bitrate`, `--encoder auto|nvenc|qsv|amf|x264`, `--webrtc-port`, `--ice-udp-port`,
`--input`, `--input-port`, `--dry-run-input` (log mapped mouse events instead of injecting them), `--verbose`.

### Measuring delay

`python clock.py` opens a window showing this laptop's clock to the millisecond (`clock.html` is a browser version).
Stream it with `--window "Spatial Launcher Test Clock"`, then compare:

- **By eye:** put the headset view (or the Unity editor) next to the clock and take one photo; the difference between
  the two clocks is the end-to-end video delay.
- **In one screenshot:** with input connected, the Laptop window's status line shows the laptop's clock as estimated
  over the input link. The streamed clock minus that readout is the video delay, give or take half the round-trip
  time shown in the title bar.

`clock.py` also marks and prints where it is clicked, which is handy for checking that injected clicks land where the
headset pointed.

### Firewall

On the same machine nothing needs opening. For an iPhone on the same Wi-Fi, Windows Firewall must allow inbound
TCP 8889 and UDP 8189 (video) and TCP 8765 (input) for `mediamtx.exe` and `python.exe`. Windows asks the first time.
**Allow them on Private networks only**, and set your home Wi-Fi to Private (Settings > Network > Wi-Fi > your
network). On a Public network profile Windows blocks them, which is the safe default for cafés and hotels.

## Security model

- **Pairing token:** `agent.py` generates a random token (18 random bytes, URL-safe) every run and prints it. Every
  video (WHEP) request needs it as the password, and every input connection must send it as its first message within
  5 seconds. It's compared in constant time. After 5 wrong tokens from one address, that address is locked out
  until the agent restarts; each wrong attempt also waits 2 seconds.
- **Local network only:** the input server accepts only loopback, private (10/8, 172.16/12, 192.168/16, fc00::/7)
  and link-local addresses. MediaMTX only lets viewers in from the same ranges, and only lets ffmpeg on this machine
  publish. Its control API, metrics, RTMP, HLS, SRT and playback servers are switched off, and RTSP listens on
  127.0.0.1 only.
- **Opt-in input:** the input server never starts unless you run `agent.py --input`. Without it, the headset can
  only watch.
- **One controller:** only one headset can control the mouse at a time; others are refused while it's connected.
- **Letting go:** when the input connection closes or drops, any held mouse button is released and input stops.
  Dead connections are detected by WebSocket pings within about 6 seconds, and a held button is released after
  3 seconds without any message. Input aimed at a closed or minimized window is dropped rather than sent to
  whatever is behind it.
- **What it can do:** move the pointer, left/right click and scroll within the captured window or monitor. No
  keyboard input, no file access, no commands.

## Risks

Read these before using `--input`.

- **Anyone with the token on your network controls your mouse.** They can click anything visible in the captured
  area: buttons, links, dialogs. Treat the token like a password; it's shown in the console and stored in
  `.runtime/mediamtx.yml` while the agent runs.
- **The token and the input aren't encrypted.** The video itself is (WebRTC always uses DTLS-SRTP), but the token
  travels in plain HTTP (the WHEP request) and plain WebSocket (`ws://`), and so do all pointer events. Anyone who can
  watch your Wi-Fi traffic can read the token, then watch your screen or take over the mouse. Use only on networks
  you trust; never on public Wi-Fi, and never forward these ports from a router.
- **Window capture can show more than the window.** Menus, pop-ups and notifications drawn over it are not captured,
  but a full-monitor capture shows everything on that monitor, including notifications and anything you open.
- **Clicks follow the screen, not the window's content.** If another window covers the captured one, injected clicks
  land on whatever is on top. If the captured window is resized after the agent starts, positions drift (restart the
  agent).
- **Administrator windows can't be clicked** from a normal-privilege agent (Windows blocks it), and clicks on the
  Windows secure desktop (UAC prompts, lock screen) are impossible. Don't run the agent as administrator to "fix"
  this: that would let the headset click administrator windows too.
- **Stop it when you're done.** Press Ctrl+C or close the console. The stream and input stop immediately.

## Files

| File | Purpose |
|---|---|
| `agent.py` | The launcher |
| `input_server.py` | WebSocket input server (protocol documented at the top of the file) |
| `winapi.py` | Windows calls: DPI awareness, windows and monitors, `SendInput`, the job object that ends child processes |
| `setup.py` | Downloads MediaMTX and FFmpeg (pinned, checksummed) and installs `websockets` |
| `clock.py`, `clock.html` | Test clock for measuring delay; click target |
| `selftest_input.py` | Checks a running agent's input server: token, ping, single controller, dry-run mapping |

## Licenses

The agent's own code is MIT, like the rest of the repository. It uses, but does not include:

| Component | Version | License | How it's used |
|---|---|---|---|
| [MediaMTX](https://github.com/bluenviron/mediamtx) | v1.21.2 | MIT | downloaded by `setup.py`, run as a separate program |
| [FFmpeg](https://ffmpeg.org), essentials build by [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) | 9.0.2 | GPLv3 (the build includes x264 and other GPL components) | downloaded by `setup.py`, run as a separate program |
| [websockets](https://github.com/python-websockets/websockets) | 16.0 | BSD-3-Clause | Python package, installed by `setup.py` |

FFmpeg's GPL applies to FFmpeg itself. The agent only starts it as a separate process and doesn't link to it, and
nothing from FFmpeg goes into the Unity project. If you redistribute `bin/` (for example in a zip for a friend),
include `bin/LICENSE-ffmpeg.txt` and point to the FFmpeg source, as the GPL requires.
