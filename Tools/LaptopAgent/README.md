# Laptop Agent

Streams a laptop's (Windows, macOS, or Linux on X11) windows, or its whole desktop, to the Spatial Launcher headset, and, if you allow it, lets
the headset move and click the laptop's mouse in them and type into them. The headset's **Laptop** icon opens a picker listing what the
laptop can stream ("Whole desktop" first, then the open windows); each one picked opens in its own window in the
headset, and several can be open at once, each with its own video stream and its own input routing.

> Pairing, connecting and what is protected are explained in [Doc/connect.md](../../Doc/connect.md).

> Prototype. It has been tested with the agent and the Unity editor on the same Windows machine, not yet with an
> iPhone over Wi-Fi. Read **Risks** before using `--input` or `--keyboard`.

## What runs

| Piece | Role | Listens on |
|---|---|---|
| `ffmpeg` (one per streamed window) | Captures a window or the screen (Windows Graphics Capture) and encodes H.264: NVENC, QuickSync or AMF if one works, else x264 with zero-latency settings. Started when the headset opens that window, stopped 15 s after the last headset window showing it closes | nothing (publishes to MediaMTX on 127.0.0.1) |
| `MediaMTX` | A local relay between ffmpeg and the agent: ffmpeg publishes each stream to it over RTSP and the agent reads the H.264 packets back out and forwards them to the headset over WebRTC. WebRTC, HLS, RTMP, SRT, the API, metrics, pprof and playback are all switched off in its config | TCP 8554 on 127.0.0.1 only |
| `session.py` | Dials the headset (found over Bonjour, or `--headset`), runs the signed handshake, and then one WebRTC session: a control data channel, one input channel and one video track per window slot. The headset opens its own listening socket; the laptop dials out | outbound TCP to the headset (port 47810 unless given); media goes over WebRTC (UDP) between the two |
| `dispatch.py` | Turns the headset's input messages (from the per-window `input-k` data channels) into mouse and keyboard input, and runs the control state machine (view only until you allow it). Only with `--input` does it inject mouse input, and with `--keyboard` also keyboard input, into the window of the channel it arrived on | nothing: it reads the DTLS data channel of the existing session |

`agent.py` starts and stops all of them. They run only while `agent.py` runs: nothing is installed as a service or
started at login, and if `agent.py` is closed or killed, Windows ends ffmpeg and MediaMTX with it.

## Setup (once)

Needs Python 3.10 or later, and on Windows Windows 10 1903 or later (Windows 11 recommended). Per-OS setup and permissions (Windows, macOS, Linux X11) are in
[Doc/connect.md](../../Doc/connect.md#setup-on-the-laptop). On Windows, `python setup.py` downloads pinned versions of
MediaMTX and FFmpeg into `bin/` (git-ignored), verifies their SHA-256 checksums, and installs the Python dependencies
(`requirements.txt`). Nothing downloaded is committed to the repository.

## Use

```
python agent.py --list                                   # monitors and windows
python agent.py                                          # the headset picks what to stream; view only
python agent.py --input                                  # ...and may move and click the mouse in streamed windows
python agent.py --input --keyboard                       # ...and type into them
python agent.py --window "Notepad"                       # also stream one window right away (part of its title)
python agent.py --window "Notepad" --no-picker           # offer only that window, no window list
python agent.py --max-width 1280 --max-height 720 --fps 60 --bitrate 8M --encoder nvenc
```

### Run modes

```
python agent.py --pair                          # pair a new headset: type the code it shows, confirm there, exit
python agent.py --pair --headset 192.168.1.5    # ...at this address instead of finding it over Bonjour
python agent.py                                 # normal run: finds a paired headset over Bonjour and dials it
python agent.py --headset 192.168.1.5[:PORT]    # dial this paired headset directly (repeatable; first to answer wins)
python agent.py --pairs                         # list paired headsets, then exit
python agent.py --revoke NAME                   # forget one, then exit
```

**Pairing (once per headset):** on the headset open Laptop > Pair new laptop; it shows a code. Run
`python agent.py --pair`: it lists headsets that announce they are showing a code (if exactly one, it is used; with
several you pick one; with none it waits and says what to do), asks for the code, and after you confirm on the headset
both sides pin each other's key. `--headset` skips discovery and then needs exactly one address.

**Normal run:** the agent dials only headsets it has paired, over the signed handshake, then streams. If the headset is
not there it keeps retrying. In the headset, tap **Laptop** and pick a window or **Whole desktop**; each pick opens
its own window, up to `--max-windows`. With USB Personal Hotspot use `--wired` (see Doc/wired.md).

Options: `--list`, `--window TITLE`, `--monitor N`, `--no-picker`, `--max-width` and `--max-height` (default 1920x1080;
each picture is scaled down to fit, never padded, so pointer positions map 1:1), `--fps`, `--bitrate` (per stream),
`--encoder auto|nvenc|qsv|amf|x264`, `--max-windows N` (default 4, 1 to 8), `--headset`, `--pair`, `--pairs`, `--revoke`,
`--wired`, `--input`, `--keyboard` (with `--input`), `--cast-only`, `--dry-run-input` (log mapped mouse and key events
instead of injecting them), `--verbose`.

Windows Graphics Capture only delivers a frame when a window changes, so a window nobody is typing in (or one behind
other windows) would stream nothing. The agent asks each streamed window to repaint twice a second, which keeps it
streaming, and forces a keyframe every second so a headset that joins late gets a picture at once. Minimized windows
can't be captured and windows smaller than 160x90 are popups or tooltips: both are left out of the list.

### Typing

With `--input --keyboard`, a stream window's **Keys** button opens a floating keyboard (QWERTY with Shift, Ctrl, Alt,
Enter, Backspace and arrows; point and pinch or click the keys). Shift, Ctrl and Alt apply to the next key: tap Ctrl,
then C, for Ctrl+C. Keys go to the laptop window whose stream window was focused last. In the Unity editor the physical
keyboard is also forwarded while a stream window (or the keyboard) is in front.

The agent brings the streamed window to the front before typing and drops the key if it can't, so keys never land in
another window. For the whole desktop, keys go to whatever window is in front on the laptop.

**Control needs three things:** the agent started with `--input`, the headset's own consent gate ("full control"), and a
**Yes on this laptop**. When a headset asks, a top-most Yes/No box appears on the laptop: `Headset "<name>" (<fingerprint>)
wants to control this laptop's mouse[ and keyboard]. Allow?` The default button is No, and 30 s without an answer is a
No (the headset sees "timeout"). The box doesn't take focus by itself, so typing elsewhere shouldn't answer it; if Windows does activate it, typing "y" answers Yes, while Enter and Space choose the default, No. The
headset's name is shown as plain, shortened text. Only one box is open at a time. After a No or a timeout the headset
gets "busy" for 10 s without a new box. Closing the session or pressing the kill switch closes the box.

**Cast-only media needs no prompt:** with `--cast-only` the headset may play/pause, seek, skip and change the volume,
and nothing else; no control is ever granted.

**Kill switch:** press **Ctrl+Alt+Shift+Q** on the laptop (**Ctrl+Option+Shift+Q** on a Mac; on Windows it is always registered). It ends every headset session
(held keys and buttons are released, the peer connection is closed), stops all video, ffmpeg and dialling, and closes an
open prompt. The agent then sits idle until you restart it.

### Measuring delay

`python clock.py` opens a window showing this laptop's clock to the millisecond (`clock.html` is a browser version).
Pick it in the headset's Laptop picker (or start the agent with `--window "Spatial Launcher Test Clock"`), then
compare:

- **By eye:** put the headset view (or the Unity editor) next to the clock and take one photo; the difference between
  the two clocks is the end-to-end video delay.
- **In one screenshot:** with input connected, the Laptop window's status line shows the laptop's clock as estimated
  over the input link. The streamed clock minus that readout is the video delay, give or take half the round-trip
  time shown in the title bar.

`clock.py` also marks and prints where it is clicked, which is handy for checking that injected clicks land where the
headset pointed.

### macOS (capture and mouse/keyboard input)

On a Mac the agent lists screens and windows and streams them with ffmpeg's `avfoundation` input (cropped to the window)
and VideoToolbox, with libx264 as the fallback. Mouse, keyboard, text and media keys are posted as Quartz events
(`hostos/_macos_input.py` holds the key table). The approval prompt is an osascript box, the kill hotkey is
Ctrl+Option+Shift+Q (heard by a listen-only event tap) and the identity key is wrapped with a key in the login Keychain.

Setup and the three permissions (Screen Recording, Accessibility, Input Monitoring) are in
[Doc/connect.md](../../Doc/connect.md#macos). At start the agent checks them and prints the System Settings path and a
shortcut for each missing one.

Ctrl on the headset keyboard acts as Command and Alt as Option; the Mac Control key can't be sent. Only the visible part
of a window that hangs off the screen is streamed, and clicks map onto that part.
If the agent crashes while a key is held, macOS can keep Cmd logically down; pressing Cmd once clears it.

Child processes (ffmpeg, MediaMTX) are cleaned up by a small watcher process
(`hostos/_reaper.py`) when the agent exits, however it exits. One gap: a child started in the instant before the
agent is killed with `kill -9` can survive; stop it by hand if `--list` or a restart finds one still running.

### Linux X11 (experimental)

Screens (`xrandr --listmonitors`), windows (EWMH properties through libX11 and `ctypes`, no extra Python packages),
capture (ffmpeg `x11grab` + libx264), the identity key (a file created with mode 0600, refused if group/other-readable or owned
by someone else) and child cleanup (process groups plus a pidfd watcher). Mouse and keyboard input go through XTest
(libXtst; keys are mapped through your current keyboard layout, and text characters the layout lacks are skipped and
logged). The kill switch is Ctrl+Alt+Shift+Q, grabbed on the root window (with NumLock and CapsLock in any state); if
another program already holds that key the agent warns and you stop it with Ctrl+C. The approval prompt is a `zenity`
Yes/No box (default No, 30 s), or a `tkinter` window when zenity is missing; with neither, every request is denied and
the agent warns. Packages, the session checks and the Wayland refusal are in
[Doc/connect.md](../../Doc/connect.md#linux-x11-experimental).

### Firewall

The agent needs **no inbound firewall rule and listens on no network port**. The laptop dials the headset, and
video and input come back over that connection. The only socket it opens is MediaMTX on 127.0.0.1:8554, reachable from
this machine only. The WebRTC media uses ephemeral UDP ports chosen by the connection, with replies allowed by the
firewall's normal stateful handling, so the Public profile's default of blocking inbound connections is fine for
cafés and hotels. There is no `--webrtc-port`, `--ice-udp-port` or `--input-port`.

## Security model

- **No token and no listener:** the agent accepts no inbound connection. Input arrives on the DTLS-protected data
  channels of the one WebRTC session the signed handshake set up with a paired headset (pinned keys), so it is
  encrypted and only that headset can send it. There is no pairing token, no WebSocket and no port to scan.
- **View only until granted:** every session starts view only. The headset asks for control with the consent its own
  gate approved (`control-request`, `"consent":"full control"`); the laptop's approver then decides (a Yes/No box on the
  laptop; without one only `--input` would be the consent). Until it is granted, all mouse, key and text messages
  are dropped. The headset can hand control back (`control-release`) or end the session (`stop`).
- **Cast-only rules:** with `--cast-only` the only accepted input is a media action (play/pause, next, previous, volume
  up/down, mute, seek back/forward), each one press and release of one whitelisted key with no modifier, and no grant
  or approval is asked. Every other message (keys, text, moves, clicks, scrolls, unknown actions) is dropped before
  anything reaches Windows and counted. A control request is refused ("cast only").
- **Nothing streams until asked:** a window is captured only while a paired headset has it open (plus 15 seconds),
  or when you name it with `--window`/`--monitor`. `--no-picker` limits the headset to that one target.
- **Opt-in input:** without `--input` the headset can list windows and watch them, but the agent never moves or
  clicks the mouse. Without `--keyboard` it never types.
- **Limited keys:** only named keys are accepted: letters, digits, punctuation, F1-F12, Enter, Backspace, Tab, Esc,
  Space, Shift, Ctrl, Alt, arrows and the Insert/Delete/Home/End/Page block. There is no Windows key, Apps key, Print
  Screen or media key, so the headset can't open the Start menu, Run (Win+R) or lock the screen (Win+L). Ctrl+Alt+Del
  can't be injected on Windows. Text is at most 64 characters per message, control characters removed.
- **Size and rate limits:** an input message is at most 1 KiB (bigger ones are dropped), each window's channel may send
  about 240 messages a second, and about 30 key presses or characters a second (bursts of 60); the rest are dropped
  and the headset is told. Releasing a held key or button is never dropped, and at most 6 keys can be held at once.
- **Kill switch:** Ctrl+Alt+Shift+Q on the laptop ends every session, stops video and dialling, and keeps the agent idle until restarted (see Typing).
- **One headset:** only one paired headset is linked at a time; it may open several windows, each with its own input
  channel. Others are ignored while it's connected.
- **Clicks go to the window they're meant for:** each channel's input goes only to the window on its slot. Before a
  click or scroll the agent brings that window to the top, and drops the click if another window still covers the
  point, so it never lands on a different window.
- **Letting go:** when a window closes, the session ends or the headset drops (its peer connection closes), any mouse
  button or key it held is released, and a held button is released after 3 seconds without any message. Input aimed at
  a closed or minimized window is dropped rather than sent to whatever is behind it.
- **What it can do:** list the titles of the open windows, stream them or the whole desktop, (with `--input`, once
  granted) move the pointer, left/right click and scroll within the streamed window or monitor, and (with `--keyboard`)
  type into the streamed window. No file access, no commands.

## Pairing identity

The agent has one RSA-2048 identity key, created on first use in `%LOCALAPPDATA%\SpatialLauncher\agent\` on Windows (override with
the `SPATIAL_LAUNCHER_HOME` environment variable). It lives outside the repository. `identity.key` holds the private
key encrypted with Windows DPAPI for your Windows user, so it can't be read by another user or copied to another PC.
If it is damaged or belongs to another user the agent says so and does not replace it. Paired headsets are kept in
`pairs.json` in the same folder. `python agent.py --pairs` lists them (name, fingerprint, date added) and
`python agent.py --revoke NAME` forgets one. If a headset's name changed since a half-finished pairing, run `--revoke <old name>` first.

A new headset is paired with a one-time code. The **headset** shows a 12-character code (`XXXX-XXXX-XXXX`, letters and
digits without U; `I` and `L` are read as `1`, `O` as `0`) and advertises itself as ready to pair; you type it on the
laptop with `python agent.py --pair` (the exchange itself is in `pair_protocol.py`). The code never crosses the
network: both sides prove they know it with HMACs under a PBKDF2-stretched key that is bound to both public keys, then
you confirm on the headset, and then laptop and headset each pin the other's key. A code works once, lasts 120 seconds and
is dead after 3 wrong tries. There is no QR code.

On a Mac the same files live in the agent's home folder under `~/Library/Application Support` (or `SPATIAL_LAUNCHER_HOME`),
and `identity.key` is encrypted with a random 32-byte key kept in your login Keychain (service `SpatialLauncher.agent.identity.v1`), so it can't be used on another Mac or user account. A missing or changed Keychain key is reported, never replaced silently.

On Linux the files live in `$XDG_DATA_HOME/SpatialLauncher/agent` (`~/.local/share/SpatialLauncher/agent` if unset, or `SPATIAL_LAUNCHER_HOME`), and `identity.key` is a file the agent creates as 0600; it refuses one that someone else owns or that group or others can access.

## Risks

Read these before using `--input` or `--keyboard`.

- **Video and the headset link are protected.** They use the signed v1 handshake with pinned keys (a headset must be
  paired first, with a code shown on the headset) and DTLS-SRTP. MediaMTX listens only on 127.0.0.1.
- **Input is protected too.** It travels over the same DTLS data channel, only from a paired headset, and only after
  a grant; the agent has no inbound listener at all. The remaining risk is a paired headset that is stolen or
  compromised: while control is granted it can move and click your mouse and, with `--keyboard`, type as you. Revoke it
  with `--revoke NAME`, keep the kill switch (Ctrl+Alt+Shift+Q) in mind, and use `--window ... --no-picker` to offer
  only one window.
- **With `--keyboard`, a granted headset can type into the windows it streams:** in a terminal or a Run box that means
  running commands as you. Stream only the windows you mean to type into (`--window ... --no-picker`).
- **Window capture can show more than the window.** Menus, pop-ups and notifications drawn over it are not captured,
  but a full-monitor capture shows everything on that monitor, including notifications and anything you open.
- **Clicks follow the screen, not the window's content.** The agent raises the streamed window before each click and
  drops clicks that would land on another window, but a window that stays on top (an "always on top" app) can still
  block them. If a streamed window is resized while it streams, positions drift: close its headset window and pick it
  again.
- **Administrator windows can't be clicked** from a normal-privilege agent (Windows blocks it), and clicks on the
  Windows secure desktop (UAC prompts, lock screen) are impossible. Don't run the agent as administrator to "fix"
  this: that would let the headset click administrator windows too.
- **Stop it when you're done.** Press Ctrl+C or close the console. The stream and input stop immediately.

## Files

| File | Purpose |
|---|---|
| `agent.py` | The launcher: MediaMTX, the capture processes, the headset link and the kill switch |
| `streams.py` | What can be streamed (desktop, windows), each stream's path, starting and stopping captures |
| `dispatch.py` | The input dispatcher: per-window input channels, the control state machine, cast-only rules, rate limits (protocol documented at the top of the file) |
| `keys.py` | Key names the protocol accepts, validation of key and text events, the rate limit |
| `approval.py` | The laptop approval prompt (Yes/No box, one at a time, closed on cancel) and the sanitised headset name |
| `hostos/__init__.py`, `hostos/types.py` | Picks the backend by `sys.platform` and re-exports its names; the shared data types (`Rect`, `Monitor`, `Window`) and the list of names every backend must expose |
| `hostos/_stub.py`, `hostos/_ffmpeg.py` | Fills a backend that isn't implemented with functions that say so; the ffmpeg low-latency settings shared by the backends |
| `hostos/` | The OS layer every other module calls (`hostos.key(...)` etc.): `windows.py` wraps `winapi.py`; `macos.py` is the macOS backend; `linux_x11.py` is the experimental Linux backend |
| `hostos/macos.py`, `hostos/_macos_pure.py`, `hostos/_macos_input.py` | macOS capture and input: Quartz screens and windows, the ffmpeg avfoundation arguments and crop maths (pure, tested everywhere), the VK-to-kVK table, click counting and wheel maths (pure, too), the permission rows |
| `hostos/_reaper.py` | The watcher process that ends child processes when the agent exits (kqueue on macOS, pidfd on Linux) |
| `hostos/linux_x11.py`, `hostos/_linux_pure.py`, `hostos/_xlib.py` | Linux X11 (experimental): screens, windows, capture, XTest input, the XGrabKey kill hotkey, the zenity prompt, key-file check, session rows (pure parts, the VK-to-keysym table and the dialog commands tested everywhere); the libX11 and libXtst calls through `ctypes` |
| `hostos/_linux_dialog.py` | The tkinter Yes/No box the Linux prompt falls back to, run as a child process so it can be killed |
| `hostos/_macos_secrets.py`, `_test_keychain.py` | The macOS identity wrap key (AES-GCM under a Keychain entry, through `keyring`); the test helper that points tests at a throwaway Keychain entry |
| `winapi.py` | The Windows backend's calls: DPI awareness, windows and monitors, raising, focusing and repainting windows, `SendInput` for mouse and keys, the kill-switch hotkey, the Yes/No prompt, the job object that ends child processes |
| `identity.py` | The agent's RSA identity key (kept DPAPI-encrypted), RSASSA-PKCS1-v1_5 (SHA-256) signatures and public-key fingerprints |
| `pairing.py` | The one-time pairing code (Crockford base32: generating, formatting, parsing, the headset-side policy), the paired-headset store (`pairs.json`) and pinning |
| `handshake.py` | The signed link handshake for already-paired headsets (wire protocol v1, documented at the top of the file): framing, transcripts, the agent and headset roles, and the connection limits |
| `pair_protocol.py` | First-time pairing with the code the headset shows (wire protocol v2, documented at the top of the file; the spec for the C# side) |
| `discovery.py` | Finding the headset: Bonjour browsing (`_spatiallauncher._tcp.local.`; `m=pair` marks a headset showing a code), `ip[:port]` parsing, the USB-hotspot address |
| `session.py` | The link to the headset: dialling, the WebRTC session, window slots, the control channel |
| `fake_headset.py` | A Python stand-in for the headset (pairing or paired mode) for testing the agent without a phone |
| `network.py` | The USB-hotspot address and the firewall check for `--wired` (the agent needs no inbound port) |
| `test_session_e2e.py` | End-to-end tests: real MediaMTX and ffmpeg against the fake headset (x264 and, if present, NVENC), pairing and discovery: `python -m unittest test_session_e2e` |
| `setup.py` | Windows: downloads MediaMTX and FFmpeg (pinned, checksummed) and installs the Python packages. macOS: checks Homebrew's tools and installs the packages. Not for Linux yet |
| `clock.py`, `clock.html` | Test clock for measuring delay; click target |
| `selftest_wired.py` | Checks wired-mode detection and the firewall logic with made-up adapters and rules: `python selftest_wired.py --no-live` |
| `test_dispatch.py` | Unit tests for the input dispatcher (no network, no real input): routing per window, the control states, cast-only, rate limits, malformed input, releasing held input: `python -m unittest test_dispatch` |
| `test_hostos.py`, `test_macos.py`, `test_macos_input.py`, `test_macos_secrets.py`, `test_linux.py`, `test_reaper.py` | Unit tests for the OS layer: every backend exposes the same names, macOS (screens, input, Keychain wrap), Linux (pure parts, key file, rows, dialog commands) and the child watcher: `python -m unittest test_hostos test_macos test_macos_input test_macos_secrets test_linux test_reaper` |
| `_test_keychain.py` | Not a test: imported first by the test files that create identities, it points the macOS Keychain wrap key at a throwaway entry |
| `test_identity.py`, `test_pairing.py` | Unit tests for the identity key, fingerprints, pair store and pairing codes: `python -m unittest test_identity test_pairing` |
| `test_handshake.py`, `test_pair_protocol.py`, `test_discovery.py` | Unit tests for the link handshake and the code-based pairing exchange (both roles over loopback sockets, with known-answer vectors) and discovery (Bonjour on 127.0.0.1; skipped if multicast is unavailable): `python -m unittest test_handshake test_pair_protocol test_discovery` |

## Licenses

The agent's own code is MIT, like the rest of the repository. It uses, but does not include:

| Component | Version | License | How it's used |
|---|---|---|---|
| [MediaMTX](https://github.com/bluenviron/mediamtx) | v1.21.2 | MIT | downloaded by `setup.py`, run as a separate program |
| [FFmpeg](https://ffmpeg.org), essentials build by [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) | 9.0.2 | GPLv3 (the build includes x264 and other GPL components) | downloaded by `setup.py`, run as a separate program |
| [cryptography](https://github.com/pyca/cryptography) | 46.0.5 | Apache-2.0 OR BSD-3-Clause | Python package (`requirements.txt`): RSA key and signatures |
| [aiortc](https://github.com/aiortc/aiortc) | 1.15.0 | BSD-3-Clause | Python package (`requirements.txt`): WebRTC link to the headset |
| [PyAV](https://github.com/PyAV-Org/PyAV) (`av`) | 17.1.0 | BSD-3-Clause | Python package (`requirements.txt`): video frames for aiortc; its wheels bundle LGPL-licensed FFmpeg libraries |
| [python-zeroconf](https://github.com/python-zeroconf/python-zeroconf) | 0.151.5 | LGPL-2.1-or-later | Python package (`requirements.txt`): Bonjour discovery. Used as an unmodified, separately installed library |
| [pyobjc](https://github.com/ronaldoussoren/pyobjc): pyobjc-core, pyobjc-framework-Quartz, -Cocoa, -ApplicationServices | 11.1 | MIT | Python packages (`requirements.txt`, macOS only): screens, windows, Quartz events, permission checks |
| ffmpeg and mediamtx from Homebrew | | | macOS: installed by you, run as separate programs; an ffmpeg you install is under its own licence |
| [keyring](https://github.com/jaraco/keyring) | 25.7.0 | MIT | Python package (`requirements.txt`, macOS only): the Keychain wrap key |
| jaraco.classes, jaraco.functools, jaraco.context, more-itertools | | MIT | keyring dependencies (macOS only) |
| importlib_metadata, zipp, backports.tarfile | | Apache-2.0, MIT, MIT | keyring dependencies (macOS only, Python < 3.12) |
| libX11, libXtst, xrandr | | MIT | system libraries and tool the Linux backend calls (`ctypes` / subprocess); not included or installed by the agent |
| zenity | | LGPL-2.1-or-later | the Linux approval prompt runs it as a separate program if installed; not included or installed by the agent |
| aioice 0.10.2, pylibsrtp 1.0.0 | | BSD-3-Clause | aiortc dependencies |
| google-crc32c 1.9.0, pyOpenSSL 26.2.0 | | Apache-2.0 | aiortc dependencies (pyOpenSSL is pinned so `cryptography` stays at 46.0.5) |
| ifaddr 0.2.0, pyee 15.0.0 | | MIT | aiortc / zeroconf dependencies |

FFmpeg's GPL applies to FFmpeg itself. The agent only starts it as a separate process and doesn't link to it, and
nothing from FFmpeg goes into the Unity project. If you redistribute `bin/` (for example in a zip for a friend),
include `bin/LICENSE-ffmpeg.txt` and point to the FFmpeg source, as the GPL requires.
