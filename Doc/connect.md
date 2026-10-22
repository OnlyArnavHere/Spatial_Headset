# Connecting a laptop

The headset shows a laptop's windows or desktop. The laptop can run Windows, macOS, or Linux on X11 (experimental). This page covers setup, pairing, connecting, and what is and is not protected.

## How it works

The headset listens. The laptop agent dials out to it over one WebRTC connection that carries video and input. MediaMTX, the local relay between ffmpeg and the agent, listens on 127.0.0.1 only, so no other machine can reach it. The agent itself opens no port for others to connect to.

## Setup on the laptop

Needs Python 3.10 or later on every OS.

### Windows

Requires Windows 10 1903 or later (Windows 11 recommended).

```
cd Tools/LaptopAgent
python setup.py
```

### macOS

```
brew install ffmpeg mediamtx
cd Tools/LaptopAgent
python3 setup.py
```

On a Mac `setup.py` downloads nothing. It checks that `ffmpeg` and `mediamtx` are on the PATH, then runs `pip install -r requirements.txt` (which adds pyobjc and keyring on macOS). An ffmpeg you install yourself is under its own licence, which is yours to follow.

Give the app you run the agent from (Terminal, iTerm, VS Code) three permissions under System Settings > Privacy & Security, then quit and reopen that app:

| Permission | Where | Needed for |
|---|---|---|
| Screen Recording | Screen & System Audio Recording (macOS 15 and later) or Screen Recording (macOS 13-14). Shortcut: `open "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture"` | Capturing and listing windows and screens |
| Accessibility | Accessibility. Shortcut: `open "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility"` | Clicks, keys, text and raising windows (`--input`, `--cast-only`) |
| Input Monitoring | Input Monitoring. Shortcut: `open "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent"` | The kill hotkey |

At start the agent checks them. For each missing one it prints a line starting `error: ` or `warning: ` with the Settings path and the shortcut. Errors stop the agent; warnings do not:

- Screen Recording missing: error, unless the run only lists (`--list`) or pairs (`--pair`, `--pairs`, `--revoke`).
- Accessibility missing: error with `--input` or `--cast-only`. Without them it is not checked.
- Input Monitoring missing: warning only. The kill hotkey is deaf; Ctrl+C in the terminal still stops the agent.

On first run the agent creates a random key in your login Keychain (service `SpatialLauncher.agent.identity.v1`) that encrypts `identity.key`; macOS may ask you to allow Keychain access then. The agent's home is `~/Library/Application Support/SpatialLauncher/agent` (override with `SPATIAL_LAUNCHER_HOME`).

### Linux X11 (experimental)

Install, for example on Debian or Ubuntu:

```
sudo apt install ffmpeg libxtst6 x11-xserver-utils zenity
```

- `ffmpeg` must have `x11grab`; the distribution's package normally does.
- `libxtst6` is for input. Without it the agent can still stream view-only.
- `x11-xserver-utils` provides `xrandr`.
- `zenity` or `python3-tk` is for the approval prompt. With neither, every approval request is denied.
- `mediamtx` must be on the PATH.

Then:

```
cd Tools/LaptopAgent
pip install -r requirements.txt
```

Do not run `setup.py` on Linux: it still downloads the Windows binaries.

The key file is `identity.key` in `$XDG_DATA_HOME/SpatialLauncher/agent` (`~/.local/share/SpatialLauncher/agent` if `XDG_DATA_HOME` is unset; `SPATIAL_LAUNCHER_HOME` overrides). The agent creates it as 0600 and refuses a key file that someone else owns or that group or others can access.

The agent needs an X11 session. At start it checks the session, ffmpeg x11grab, libXtst and the approval prompt, and prints `error: ` or `warning: ` lines:

- X11 session missing: error, unless the run only pairs. A Wayland session is refused (XWayland would show black): log in to an Xorg session. The xdg-desktop-portal ScreenCast and RemoteDesktop interfaces are the future route and are not implemented. A missing `DISPLAY`, for example plain SSH, is refused too.
- ffmpeg without x11grab: error, unless the run only lists or pairs.
- libXtst missing: error with `--input` or `--cast-only`; otherwise not checked.
- No approval dialog: warning only.

### Running

Then run `agent.py`. The flags that matter here:

| Flag | What it does |
|---|---|
| `--list` | List monitors and windows, then exit |
| `--pairs` | List paired headsets, then exit |
| `--revoke <name>` | Forget one paired headset, then exit |
| `--pair` | Pair a new headset: type the code it shows, confirm there, exit |
| `--headset ip[:port]` | Dial this address (port 47810 unless given) instead of looking over Bonjour. Repeatable |
| `--input` | Allow the headset to move and click the mouse, once you approve |
| `--keyboard` | With `--input`: also allow typing |
| `--cast-only` | Media keys only. Can't be combined with `--input` |
| `--dry-run-input` | With `--input` or `--cast-only`: log the mapped events instead of injecting them |
| `--no-picker` | With `--window` or `--monitor`: offer only that target |
| `--max-windows N` | Most windows open at once (default 4, 1 to 8) |
| `--max-width N`, `--max-height N` | Scale each stream down to fit (default 1920x1080) |
| `--wired` | Also look for the USB Personal Hotspot (see [wired.md](wired.md)) |
| `--verbose` | Show ffmpeg and MediaMTX logs |

Run `python agent.py --help` for the rest (`--window`, `--monitor`, `--fps`, `--bitrate`, `--encoder`). More detail is in [Tools/LaptopAgent/README.md](../Tools/LaptopAgent/README.md).

## Using the app

The packaged agent is a desktop app, so you don't need Python or the command line. The CLI above still works and is unchanged.

**Download.** Get the latest build from the repository's GitHub Releases page: on Windows the installer (`SpatialLauncherAgent-<version>-setup.exe`) or the zip; on macOS (Apple Silicon) the zipped `.app`. Releases are marked pre-release for now. The README has the SmartScreen and Gatekeeper steps for unsigned builds.

**First run.** The app doesn't ship the ffmpeg program or MediaMTX (the PyAV libraries inside the app are separate). On first start it downloads pinned, checksum-verified copies into its data folder and does nothing the second time:

- Windows: `%LOCALAPPDATA%\SpatialLauncher\agent\bin`
- macOS: `~/Library/Application Support/SpatialLauncher/agent/bin`

**The status window** shows the agent's state and a log. In it:

- **Pair new headset...** opens the pair dialog. Type the code the headset shows and press Pair. Pairing works only while the dialog is open, for 2 minutes at most.
- **Allow control** (off by default) lets the headset use the mouse and keyboard, and you are still asked at each grant. **Cast only** allows media keys only. The two exclude each other.
- **Start when I log in** is available in the installed app.
- **Headset address** dials a fixed address (empty means find headsets over Bonjour). **Wired** also looks for the USB Personal Hotspot (see [wired.md](wired.md)).
- **Stop all** ends every session now. The kill hotkey (Ctrl+Alt+Shift+Q on Windows and Linux, Ctrl+Option+Shift+Q on macOS) does the same from anywhere.

**Closing the window** keeps the agent running in the background: the tray icon on Windows, the Dock on macOS. Use the tray menu or the app's Quit to stop it. Launching the app again shows the window.

**macOS permissions** are asked for at the moment they're first needed, once per run: Screen Recording when the first stream starts, Accessibility when you turn on Allow control or Cast only, and Input Monitoring only if you press Enable hotkey (it is just for the kill hotkey). The window's macOS permissions section has an **Open Settings** button for each. On macOS 15 and later, the first connection to a headset also triggers the Local Network prompt; if it's denied, turn the app on under Privacy & Security > Local Network. Microphone access is never requested.

## Pairing by code

Do this once per laptop.

1. On the headset: Laptop > Pair new laptop. It shows a 12-character code, `XXXX-XXXX-XXXX`. The characters are Crockford base32. When typing, I and L read as 1, and O reads as 0.
2. The code is valid for 2 minutes and works once. Three wrong tries end it.
3. On the laptop: `python agent.py --pair` (add `--headset <ip>` if discovery fails). Type the code.
4. The headset asks "Pair with laptop X?". Check the name, then confirm with a pinch.

Both sides now remember each other's key. Later connections need no code.

To remove a laptop: Settings > Laptop > Paired laptops > Remove on the headset, or `python agent.py --revoke <name>` on the laptop.

## Connecting

1. On the headset, open the Laptop picker and tap the paired laptop.
2. On the laptop, run `python agent.py`. If the headset is not there yet, the agent keeps retrying.

Discovery uses Bonjour/mDNS (`_spatiallauncher._tcp`, with a random name each time). If it fails, pass the address: `python agent.py --headset <ip>`. In the Unity editor use `python agent.py --headset 127.0.0.1:47810`.

### Wired

Turn on Personal Hotspot and plug in the USB cable. The agent dials 172.20.10.1. No inbound firewall rule is needed. See [wired.md](wired.md).

### Firewall

Measured on Windows 11 on this project's laptop:

- With mDNS discovery, Windows showed its "allow access" prompt for python.exe once. It asked for inbound rules for TCP and UDP.
- With `--headset <ip>` or wired mode there was no prompt.
- If you click Cancel on the prompt, discovery may fail. Use `--headset`.

## Control safeguards

- Sessions start view-only. Tapping Connect is the consent to view.
- Control needs all three: the agent run with `--input`, approval on the headset's consent prompt, and Yes on the laptop's prompt. The laptop prompt defaults to No and is refused after 30 s. Nothing is injected before both approvals.
- Cast-only mode allows media keys only, from a fixed list, with no laptop prompt. Control requests are refused.
- Kill hotkey on the laptop: Ctrl+Alt+Shift+Q (Windows, Linux) or Ctrl+Option+Shift+Q (macOS). It closes every session, stops video, and stops reconnecting until the agent restarts.
- On the headset, a LIVE indicator shows while a session runs. Panic ends the session and cancels a waiting connection: F8 in the editor, the cross-sweep gesture, or touch Stop.

## What is protected

- **Encryption:** DTLS-SRTP for video and DTLS for the data channels.
- **Mutual pinning:** each side has an RSA-2048 identity key. The laptop's is kept in DPAPI on Windows, encrypted under a login Keychain key on macOS, and in a 0600 file on Linux; the headset's in the iOS Keychain. Signatures are RSASSA-PKCS1-v1_5 with SHA-256 over a transcript that binds both keys, both nonces and the DTLS fingerprints. A man in the middle can't swap certificates.
- **Pairing code:** it is bound with PBKDF2-HMAC-SHA256 (100,000 iterations) and HMAC. The headset proves nothing derived from the code to an unverified laptop. The laptop sends its code proof first.
- **Two approvals for control**, and the kill hotkey.
- **Limits before authentication** on the headset listener: 64 KiB frames, 5 s until the laptop is verified, 5 attempts per minute per IP, one at a time.

## What is NOT protected

- An unlocked, paired phone in someone else's hands. Revoke it on the laptop.
- The headset's listener is reachable on the LAN while the picker or pairing screen is open, before authentication.
- mDNS shows that a Spatial Launcher headset is present. The name is random, but the service type is visible.
- Someone who sees the code while you pair and types it first could pair. That is why the code expires after 2 minutes, works once, and needs your confirmation on the headset. Check the laptop name in the prompt.
- A fake headset on the same network that the laptop dials while pairing receives the laptop's code proof and could try to guess the code offline. PBKDF2 with 100,000 iterations, the 60-bit code and the 2-minute life make that impractical, and the code is useless once it expires.
- A malicious app already running on the laptop.

## Limits

- Networks that isolate devices (hotels, campus Wi-Fi) need wired mode.
- iOS devices can't be controlled.
- DRM video isn't possible.
- Old app builds and old agents are incompatible with each other.

## Dependencies and licences

From `Tools/LaptopAgent/requirements.txt`. No Python package's own licence is GPL, but the PyPI `av` wheels bundle FFmpeg libraries that include GPL x264/x265 (see the av row), so the packaged app is a pre-release for now. ffmpeg itself is a separate program, not a Python package; see the README's licence table.

| Package | Version | Licence |
|---|---|---|
| aiortc | 1.15.0 | BSD-3-Clause |
| av | 17.1.0 | BSD-3-Clause (the PyPI wheels bundle FFmpeg libraries including GPL x264/x265; an LGPL-only build is planned) |
| zeroconf | 0.151.5 | LGPL-2.1-or-later |
| cryptography | 46.0.5 | Apache-2.0 OR BSD-3-Clause |
| aioice | 0.10.2 | BSD-3-Clause |
| pylibsrtp | 1.0.0 | BSD-3-Clause |
| pyee | 15.0.0 | MIT |
| ifaddr | 0.2.0 | MIT |
| google-crc32c | 1.9.0 | Apache-2.0 |
| pyOpenSSL | 26.2.0 | Apache-2.0 |
| pyobjc-core | 11.1 | MIT (macOS only) |
| pyobjc-framework-Quartz | 11.1 | MIT (macOS only) |
| pyobjc-framework-Cocoa | 11.1 | MIT (macOS only) |
| pyobjc-framework-ApplicationServices | 11.1 | MIT (macOS only) |
| keyring | 25.7.0 | MIT (macOS only) |
| jaraco.classes, jaraco.functools, jaraco.context, more-itertools | 3.4.0, 4.4.0, 6.0.2, 10.8.0 | MIT (macOS only) |
| importlib_metadata, zipp, backports.tarfile | 8.7.1, 3.23.0, 1.2.0 | Apache-2.0, MIT, MIT (macOS only, Python before 3.12) |

segno is no longer used.
