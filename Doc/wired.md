# Wired mode (USB Personal Hotspot)

The headset iPhone shares its Personal Hotspot with the laptop over the USB cable. The phone is the gateway
(172.20.10.1) and the laptop gets one of 172.20.10.2 to .14. The headset then reaches the agent over the cable, with no
Wi-Fi involved.

## Steps

1. On the iPhone: Settings > Personal Hotspot > Allow Others to Join. The option only appears if the carrier plan
   allows a hotspot.
2. Plug the iPhone into the laptop and tap Trust on the phone. Windows needs Apple's driver, which comes with the Apple
   Devices app or iTunes. An "Apple Mobile Device Ethernet" adapter then appears.
3. On the laptop: `python agent.py --wired` (add `--cast-only` or `--input` as usual). The banner shows the line
   `Wired (USB): host 172.20.10.x` and checks the firewall for that adapter.
4. On the headset: open the Laptop picker and tap the paired laptop. The agent dials 172.20.10.1, the phone, over the
   cable. Pair first if you haven't (see [connect.md](connect.md)).

## What works over the cable

Everything the Wi-Fi link does, because it is the same link: the agent dials 172.20.10.1, runs the same signed
handshake and opens the same WebRTC session. Only how the headset is found differs. Nothing below has a wired-only
code path:

- view-only and cast-only windows, the picker and the per-window streams;
- full control: mouse, keyboard and media keys, after the headset's consent prompt and the laptop's Yes (or its
  "Always allow this headset for control");
- the Laptop Shell (Laptop > Laptop apps): running, pinned and installed apps with their icons, search, launching apps,
  and window raise, minimize, restore and close. It rides the control data channel, so it gets the same encryption,
  the same gate (control granted, not cast-only, not stopped) and the same rate limits;
- the app switcher, which only uses the headset's windows and the shell above;
- the LIVE indicator, the panic stop and the laptop's kill switch.

Checked without a phone (`python -m unittest test_wired_shell`): with the hotspot adapter present, the agent dials
172.20.10.1 without `--headset`, and the session it builds for that address carries the shell with the same gate
(view only refused until control is granted, cast-only and no-input refused, the kill switch stops it).

If the laptop is also on Wi-Fi, WebRTC may pick the Wi-Fi path for the media. To be sure the cable carries
everything, turn Wi-Fi off on the laptop while testing.

## Firewall

No inbound rule is needed. The laptop dials the headset, and the video and input travel back over that same
connection. The only port the agent opens is MediaMTX on 127.0.0.1:8554, which no other machine can reach.

See [connect.md](connect.md) for pairing, the safeguards and what is protected.

## Checked so far, and what only the phone can show

Checked on the laptop (`python selftest_wired.py`): the hotspot adapter is picked and the firewall logic and fix
commands are right, all with made-up adapters, plus a read-only live check. The headset's Find laptop was checked in
the editor against this machine.

## Test steps (on the device)

1. Pair the laptop once if you haven't (see [connect.md](connect.md)).
2. iPhone: Settings > Personal Hotspot > Allow Others to Join. Plug it into the laptop and tap Trust.
3. Laptop: turn Wi-Fi off. In the agent app turn on Wired (USB) and Allow control (or run
   `python agent.py --wired --input --keyboard`). The banner or status should show `Wired (USB): host 172.20.10.x`.
4. Headset: Settings > Laptop > Link: Wired (USB). Open Laptop and tap the laptop. Expect "Connected (view only)".
5. Tap **Laptop apps**. Expect "Allow control". Tap it, approve on the headset, then on the laptop. Tick "Always
   allow this headset for control" once to check that the next session skips the laptop's box.
6. In Laptop apps:
   - the bar shows your pins and running apps with real icons and indicators, styled for your laptop's OS;
   - search by typing and by holding the mic;
   - tap a running app: its window opens as a stream; tap a pinned app that isn't running: it launches and opens;
   - hold a bar icon: its windows are listed; minimize one, restore it, close one.
7. Switcher: Settings > Display > TRIGGER button: Switcher. Press TRIGGER, press again, stop: the selected window
   comes forward. Pinch and hold on empty space: the overview opens; pinch a card. Check that a laptop window
   that isn't open yet opens as a stream.
8. Safety: cross and sweep (panic stop) ends the session and closes the stream windows; the laptop's kill switch
   (Ctrl+Alt+Shift+Q, Ctrl+Option+Shift+Q on a Mac) does the same from the laptop. Run `--pairs`, then
   `--revoke-always NAME`, and connect again: the laptop asks again.
9. Unplug the cable: the session ends within about 10 s and the agent keeps redialling; plug it back in and it
   reconnects.

Please note the latency (each laptop stream window shows the link's round trip in ms on its status line) and whether anything above didn't work.

Only the phone can show:
- that the adapter appears and gets a 172.20.10.x address;
- that iOS lets the app reach the laptop over the cable (Local Network permission);
- that WebRTC video uses the cable;
- the latency;
- that the shell's icons and lists arrive quickly enough over the cable (they are small: an icon is at most 9 KB).
