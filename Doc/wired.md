# Wired mode (USB cable, no network)

The laptop talks to the headset app over the iPhone's charging cable through Apple's USB multiplexer, the same
channel iTunes, Finder and Xcode use. No Wi-Fi, no Personal Hotspot and no IP network are needed.

Any cable works the same way: Lightning or USB-C on the phone, USB-A or USB-C on the laptop, directly or through an
adapter or hub. Use a cable that carries data (some cheap cables only charge): if Finder, the Apple Devices app or
iTunes can see the phone, the cable is fine.

## Steps

1. Plug the iPhone into the laptop and tap **Trust** on the phone. Windows needs Apple's driver: install the
   **Apple Devices** app (or iTunes) from the Microsoft Store. macOS has it built in.
2. On the laptop: in the app turn on **Wired (USB cable)** (or run `python agent.py --wired`). The app's settings show
   "iPhone on the USB cable" once it sees the phone.
3. Pair once if you haven't: on the headset open Laptop > Pair new laptop; on the laptop click Pair new headset and
   type the code. Pairing goes over the cable too.
4. On the headset: open Laptop and tap the laptop. The agent connects over the cable.

## How it works

- `Tools/LaptopAgent/usbmux.py` asks the multiplexer (127.0.0.1:27015 on Windows, `/var/run/usbmuxd` on macOS and
  Linux) for iPhones attached by USB (phones it reaches over Wi-Fi are ignored) and opens a TCP connection to the
  headset app's link port through it. On the phone that connection arrives from the loopback address.
- The signed handshake runs in **stream mode** (`handshake.py`, `HandshakeHeadset.cs`). The headset offers it only
  for loopback connections, so it can only happen over the cable. The mode is signed into both transcripts.
- After the handshake the same connection carries the session (`stream_link.py`, `StreamLinkTransport.cs`): the
  usual control and input messages, plus each window's H.264, which the iPhone decodes in hardware
  (`Assets/Plugins/iOS/SpatialVideoDecoder.mm`). WebRTC isn't used, because there is no network for it.
- The stream is not encrypted beyond the authenticated handshake. It never leaves the cable.
- Everything else is the same as over Wi-Fi: view only and cast only, full control after both approvals, the laptop
  shell, the switcher, the LIVE indicator, the panic stop and the kill switch.

## Older builds

A headset app built before stream mode answers the laptop with "unsupported mode"; the laptop then says the headset
app is too old for the USB cable. Rebuild and install the headset app. (Personal Hotspot over the cable was removed.)

## Checked so far, and what only the phone can show

Checked on the laptop: the usbmux client against a fake multiplexer and against Apple's service on Windows (it
answers and lists no phone when none is plugged in); whole stream sessions between the agent and a fake headset over
localhost (`test_stream_link.py`: real ffmpeg, decoded frames on two slots, input, close, redial); the Python agent
against the C# headset handshake and transport built with .NET; the Unity project compiles for iOS and the headset's
link test suites pass.

## Test steps (on the device)

1. Turn Wi-Fi off on the laptop and on the phone, and Personal Hotspot off, so the cable is the only way through.
2. Plug the iPhone into the laptop and tap Trust. In the agent app turn on Wired (USB cable) and Allow control (or run
   `python agent.py --wired --input --keyboard`). Settings should say "iPhone on the USB cable".
3. Pair over the cable if you haven't (Laptop > Pair new laptop on the headset; Pair new headset in the app).
4. Headset: Settings > Laptop > Link: Wired (USB). Open Laptop and tap the laptop. Expect "Connected (view only)"
   and, in the agent's log, "Linked with ... (usb:..., paired-stream)".
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
- that connections through the multiplexer arrive from loopback (if not, the headset answers "paired" and the laptop
  logs "unsupported mode");
- that the hardware decoder shows the picture, in the right colours, and keeps up with several windows;
- the latency;
- that the shell's icons and lists arrive quickly enough over the cable (they are small: an icon is at most 9 KB).
