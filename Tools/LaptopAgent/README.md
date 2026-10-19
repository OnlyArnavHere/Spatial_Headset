# Laptop Agent

Streams a Windows laptop's windows, or its whole desktop, to the Spatial Launcher headset, and, if you allow it, lets
the headset move and click the laptop's mouse in them and type into them. The headset's **Laptop** icon opens a picker listing what the
laptop can stream ("Whole desktop" first, then the open windows); each one picked opens in its own window in the
headset, and several can be open at once, each with its own video stream and its own input routing.

> Prototype. It has been tested with the agent and the Unity editor on the same Windows machine, not yet with an
> iPhone over Wi-Fi. Read **Risks** before using `--input` or `--keyboard`.

> **The input link is not encrypted.** Everything typed on the headset's keyboard travels over the local network in
> plain text (`ws://`), as do clicks and the pairing token. Anyone who can watch your Wi-Fi can read it. **Don't type
> passwords or anything else secret through the headset until the link uses TLS.**

## What runs

| Piece | Role | Listens on |
|---|---|---|
| `ffmpeg` (one per streamed window) | Captures a window or the screen (Windows Graphics Capture) and encodes H.264: NVENC, QuickSync or AMF if one works, else x264 with zero-latency settings. Started when the headset opens that window, stopped 15 s after the last headset window showing it closes | nothing (publishes to MediaMTX on 127.0.0.1) |
| `MediaMTX` | Serves each stream over WebRTC on its own path, named after the window title (e.g. `/untitled-notepad`, `/desktop`); the headset connects with WHEP | TCP 8889 (WHEP/HTTP), UDP 8189 (media); RTSP 8554 on 127.0.0.1 only |
| `input_server.py` | The control server: lists windows, starts streams, and (only with `--input`) injects mouse input, and with `--keyboard` also keyboard input, into the window each headset connection opened | TCP 8765 (WebSocket) |

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
python agent.py                                          # the headset picks what to stream; view only
python agent.py --input                                  # ...and may move and click the mouse in streamed windows
python agent.py --input --keyboard                       # ...and type into them (unencrypted: no passwords)
python agent.py --window "Notepad"                       # also stream one window right away (part of its title)
python agent.py --window "Notepad" --no-picker           # offer only that window, no window list
python agent.py --max-width 1280 --max-height 720 --fps 60 --bitrate 8M --encoder nvenc
```

The console prints a **pairing token** (new every run) and the address to use. In the headset, tap the **Laptop** icon,
enter the laptop's address, port `8889` and the token, then **List**, and pick a window or **Whole desktop**. Each pick
opens its own window. While a stream runs you can also watch it in a browser at `http://<laptop>:8889/<path>/` (user
`viewer`, password the token); the path is the title in lowercase with dashes, e.g. `/untitled-notepad`.

Options: `--monitor N`, `--max-width`, `--max-height` (each picture is scaled down to fit, never padded, so pointer
positions map 1:1), `--fps`, `--bitrate` (per stream), `--encoder auto|nvenc|qsv|amf|x264`, `--webrtc-port`,
`--ice-udp-port`, `--input`, `--keyboard` (with `--input`), `--input-port` (the control server), `--dry-run-input` (log
mapped mouse and key events instead of injecting them), `--no-picker`, `--verbose`.

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

**Kill switch:** press **Ctrl+Alt+Shift+Q** on the laptop to stop all mouse and keyboard input at once. Held keys and
buttons are released, the headset shows "Input stopped", and video keeps streaming. Input stays off until you restart
the agent.

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

### Firewall

On the same machine nothing needs opening. For an iPhone on the same Wi-Fi, Windows Firewall must allow inbound
TCP 8889 and UDP 8189 (video) and TCP 8765 (control and input) for `mediamtx.exe` and `python.exe`. Windows asks the
first time.
**Allow them on Private networks only**, and set your home Wi-Fi to Private (Settings > Network > Wi-Fi > your
network). On a Public network profile Windows blocks them, which is the safe default for cafés and hotels.

## Security model

- **Pairing token:** `agent.py` generates a random token (18 random bytes, URL-safe) every run and prints it. Every
  video (WHEP) request needs it as the password, and every control connection must send it as its first message
  within 5 seconds. It's compared in constant time. After 5 wrong tokens from one address, that address is locked out
  until the agent restarts; each wrong attempt also waits 2 seconds.
- **Local network only:** the control server accepts only loopback, private (10/8, 172.16/12, 192.168/16, fc00::/7)
  and link-local addresses. MediaMTX only lets viewers in from the same ranges, and only lets ffmpeg on this machine
  publish. Its control API, metrics, RTMP, HLS, SRT, MoQ and playback servers are switched off, and RTSP listens on
  127.0.0.1 only.
- **Nothing streams until asked:** a window is captured only while a paired headset has it open (plus 15 seconds),
  or when you name it with `--window`/`--monitor`. `--no-picker` limits the headset to that one target.
- **Opt-in input:** without `--input` the headset can list windows and watch them, but the agent never moves or
  clicks the mouse. Without `--keyboard` it never types.
- **Paired connections only:** key and text events are accepted only on a connection that sent the right pairing
  token first. Anything else sent first (a key, say) counts as a wrong token.
- **Limited keys:** only named keys are accepted: letters, digits, punctuation, F1-F12, Enter, Backspace, Tab, Esc,
  Space, Shift, Ctrl, Alt, arrows and the Insert/Delete/Home/End/Page block. There is no Windows key, Apps key, Print
  Screen or media key, so the headset can't open the Start menu, Run (Win+R) or lock the screen (Win+L). Ctrl+Alt+Del
  can't be injected on Windows. Text is at most 64 characters per message, control characters removed.
- **Rate limit:** each connection may send about 30 key presses or characters a second (bursts of 60); the rest are
  dropped and the headset is told. Releasing a held key is never dropped, and at most 6 keys can be held at once.
- **Kill switch:** Ctrl+Alt+Shift+Q on the laptop stops all input for the rest of the run (see Typing).
- **One headset:** only one headset (one address) can be connected at a time; it may open several windows, each on
  its own connection. Others are refused while it's connected.
- **Clicks go to the window they're meant for:** each connection's input goes only to the window it opened. Before a
  click or scroll the agent brings that window to the top, and drops the click if another window still covers the
  point, so it never lands on a different window.
- **Letting go:** when a connection closes or drops, any mouse button or key it held is released. Dead connections are
  detected by WebSocket pings within about 6 seconds, and a held button is released after 3 seconds without any
  message. Input aimed at a closed or minimized window is dropped rather than sent to whatever is behind it.
- **What it can do:** list the titles of the open windows, stream them or the whole desktop, (with `--input`) move the
  pointer, left/right click and scroll within the streamed window or monitor, and (with `--keyboard`) type into the
  streamed window. No file access, no commands.

## Pairing identity

The agent has one RSA-2048 identity key, created on first use in `%LOCALAPPDATA%\SpatialLauncher\agent\` (override with
the `SPATIAL_LAUNCHER_HOME` environment variable). It lives outside the repository. `identity.key` holds the private
key encrypted with Windows DPAPI for your Windows user, so it can't be read by another user or copied to another PC.
If it is damaged or belongs to another user the agent says so and does not replace it. Paired headsets are kept in
`pairs.json` in the same folder. `python agent.py --pairs` lists them (name, fingerprint, date added) and
`python agent.py --revoke NAME` forgets one.

## Risks

Read these before using `--input` or `--keyboard`.

- **Anyone with the token on your network can see any window and the whole desktop.** The picker lists every open
  window's title, and any of them can be streamed. Use `--window ... --no-picker` to offer only one window.
- **With `--input`, anyone with the token controls your mouse** in the windows they stream. They can click anything
  in them: buttons, links, dialogs. Treat the token like a password; it's shown in the console and stored in
  `.runtime/mediamtx.yml` while the agent runs.
- **The token and the input aren't encrypted.** The video itself is (WebRTC always uses DTLS-SRTP), but the token
  travels in plain HTTP (the WHEP request) and plain WebSocket (`ws://`), and so do all pointer events and **every key
  typed**. Anyone who can watch your Wi-Fi traffic can read what you type, read the token, then watch your screen or
  take over the mouse and keyboard. **Don't type passwords until TLS is added.** Use only on networks you trust; never
  on public Wi-Fi, and never forward these ports from a router.
- **With `--keyboard`, anyone with the token can type into the windows they stream:** in a terminal or a Run box that
  means running commands as you. Stream only the windows you mean to type into (`--window ... --no-picker`), and keep
  the kill switch (Ctrl+Alt+Shift+Q) in mind.
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
| `agent.py` | The launcher: MediaMTX, the capture processes and the control server |
| `streams.py` | What can be streamed (desktop, windows), each stream's path, starting and stopping captures |
| `input_server.py` | WebSocket control and input server (protocol documented at the top of the file) |
| `keys.py` | Key names the protocol accepts, validation of key and text events, the rate limit |
| `winapi.py` | Windows calls: DPI awareness, windows and monitors, raising, focusing and repainting windows, `SendInput` for mouse and keys, the kill-switch hotkey, the job object that ends child processes |
| `identity.py` | The agent's RSA identity key (kept DPAPI-encrypted), PSS signatures and public-key fingerprints |
| `pairing.py` | One-time pairing codes, the paired-headset store (`pairs.json`), pinning, and the QR / typed pairing offer |
| `setup.py` | Downloads MediaMTX and FFmpeg (pinned, checksummed) and installs `websockets` |
| `clock.py`, `clock.html` | Test clock for measuring delay; click target |
| `selftest_input.py` | Checks a running agent's control server: token, ping, window list, streams per window, one headset, per-window input routing, keys, text and the rate limit (dry run), and with `--kill-switch` the kill switch |
| `test_keyboard.py` | Unit tests for keyboard input (no agent needed): `python -m unittest test_keyboard` |
| `test_identity.py`, `test_pairing.py` | Unit tests for the identity key, fingerprints, pair store and pairing codes: `python -m unittest test_identity test_pairing` |

## Licenses

The agent's own code is MIT, like the rest of the repository. It uses, but does not include:

| Component | Version | License | How it's used |
|---|---|---|---|
| [MediaMTX](https://github.com/bluenviron/mediamtx) | v1.21.2 | MIT | downloaded by `setup.py`, run as a separate program |
| [FFmpeg](https://ffmpeg.org), essentials build by [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) | 9.0.2 | GPLv3 (the build includes x264 and other GPL components) | downloaded by `setup.py`, run as a separate program |
| [websockets](https://github.com/python-websockets/websockets) | 16.0 | BSD-3-Clause | Python package, installed by `setup.py` |
| [cryptography](https://github.com/pyca/cryptography) | 46.0.5 | Apache-2.0 OR BSD-3-Clause | Python package (`requirements.txt`): RSA key and signatures |
| [segno](https://github.com/heuer/segno) | 1.6.6 | BSD-3-Clause | Python package (`requirements.txt`): pairing QR code |

FFmpeg's GPL applies to FFmpeg itself. The agent only starts it as a separate process and doesn't link to it, and
nothing from FFmpeg goes into the Unity project. If you redistribute `bin/` (for example in a zip for a friend),
include `bin/LICENSE-ffmpeg.txt` and point to the FFmpeg source, as the GPL requires.
