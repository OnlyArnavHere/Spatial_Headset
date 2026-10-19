# Connecting a laptop

The headset shows a Windows laptop's windows or desktop. This page covers setup, pairing, connecting, and what is and is not protected.

## How it works

The headset listens. The laptop agent dials out to it over one WebRTC connection that carries video and input. MediaMTX, the local relay between ffmpeg and the agent, listens on 127.0.0.1 only, so no other machine can reach it. The agent itself opens no port for others to connect to.

## Setup on the laptop

```
cd Tools/LaptopAgent
python setup.py
```

Then run `agent.py`. The flags that matter here:

| Flag | What it does |
|---|---|
| `--pairs` | List paired headsets, then exit |
| `--revoke <name>` | Forget one paired headset, then exit |
| `--pair` | Pair a new headset: type the code it shows, confirm there, exit |
| `--headset ip[:port]` | Dial this address (port 47810 unless given) instead of looking over Bonjour. Repeatable |
| `--input` | Allow the headset to move and click the mouse, once you approve |
| `--keyboard` | With `--input`: also allow typing |
| `--cast-only` | Media keys only. Can't be combined with `--input` |
| `--max-windows N` | Most windows open at once (default 4, at most 8) |
| `--wired` | Also look for the USB Personal Hotspot (see [wired.md](wired.md)) |

Run `python agent.py --help` for the rest (`--window`, `--monitor`, `--fps`, `--bitrate`, `--encoder`). More detail is in [Tools/LaptopAgent/README.md](../Tools/LaptopAgent/README.md).

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
- Kill hotkey: Ctrl+Alt+Shift+Q on the laptop closes every session, stops video, and stops reconnecting until the agent restarts.
- On the headset, a LIVE indicator shows while a session runs. Panic ends the session and cancels a waiting connection: F8 in the editor, the cross-sweep gesture, or touch Stop.

## What is protected

- **Encryption:** DTLS-SRTP for video and DTLS for the data channels.
- **Mutual pinning:** each side has an RSA-2048 identity key. The laptop's is kept in DPAPI, the headset's in the iOS Keychain. Signatures are RSASSA-PKCS1-v1_5 with SHA-256 over a transcript that binds both keys, both nonces and the DTLS fingerprints. A man in the middle can't swap certificates.
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

From `Tools/LaptopAgent/requirements.txt`. None is GPL.

| Package | Version | Licence |
|---|---|---|
| aiortc | 1.15.0 | BSD-3-Clause |
| av | 17.1.0 | BSD-3-Clause (its wheels bundle LGPL FFmpeg libraries) |
| zeroconf | 0.151.5 | LGPL-2.1-or-later |
| cryptography | 46.0.5 | Apache-2.0 OR BSD-3-Clause |
| aioice | 0.10.2 | BSD-3-Clause |
| pylibsrtp | 1.0.0 | BSD-3-Clause |
| pyee | 15.0.0 | MIT |
| ifaddr | 0.2.0 | MIT |
| google-crc32c | 1.9.0 | Apache-2.0 |
| pyOpenSSL | 26.2.0 | Apache-2.0 |

segno is no longer used.
