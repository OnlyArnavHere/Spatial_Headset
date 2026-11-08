# Progress

Last updated: 2026-11-07. Update this file whenever a branch is merged or a chunk of work lands.

Related docs: [Doc/plan.md](Doc/plan.md) (milestones M0–M6), [Doc/TASKS.md](Doc/TASKS.md) (task checklist T01–T20),
[Doc/features.md](Doc/features.md), [Doc/connect.md](Doc/connect.md), [Doc/wired.md](Doc/wired.md),
[Doc/ui-design.md](Doc/ui-design.md).

## Where things stand

- **main** is at `029a4e0` (2026-10-23, PR #17 laptop-shell).
- **Current branch: `pairing-fixes`**, 3 commits ahead of main, **not pushed** to origin, plus a large set of
  uncommitted changes (see [In progress](#in-progress-uncommitted-on-pairing-fixes)).
- `../Spatial_Launcher-agent/` is a git worktree on `laptop-shell` (already merged into main).

## In progress (uncommitted on `pairing-fixes`)

**Connection methods (2026-11-07):** only two ways remain for the laptop to reach the headset:
1. **Wi-Fi**: the agent finds the headset over Bonjour and connects with WebRTC.
2. **USB cable**: through Apple's USB service, no network (any cable: Lightning or USB-C phone, USB-A or USB-C port).

Removed: typing a headset address (`--headset`, the app's Headset address field and setting) and Personal Hotspot
over the cable (`network.py`, `selftest_wired.py`, `test_wired_shell.py`, the headset's `UsbHotspot` → `UsbCable`).
Side effect: the agent can no longer connect to the Unity editor (it was reached at `127.0.0.1`; the editor doesn't
announce itself over Bonjour). Agent tests: 730 run, only the already-failing Bonjour QR test fails. Headset scripts
compile with .NET; not yet recompiled in Unity.

**Keyboard and windows (2026-11-07)**, see [Doc/features.md](Doc/features.md):
- The keyboard is docked to the window it types into: typing no longer sends windows back and forth (each key press
  used to focus the keyboard, which pushed the laptop window back). It follows that window and closes with it.
- It pops up by itself when a text field gets focus, on the headset or on the laptop (new `textfocus.py` and the
  `text-focus` control message; Windows check verified live on this PC), and closes when the focus leaves.
- Mic key for voice typing: tap to start and stop, or hold to talk.
- Laptop windows are bezel-less: no padding, and the window keeps the picture's aspect, so there are no side bars.
- Checked: agent tests incl. 8 new ones in `test_textfocus.py`; headset scripts and the updated Unity tests
  (`KeyboardInputTest`, `VoiceTest`) compile with .NET. Not yet run in Unity or on the phone.

**Stereo view comfort (2026-11-07):**
- Less wobble. The passthrough image now hangs from HoloKit's predicted eye pose (`StereoPassthrough.PlaneParent`).
  Before, it hung from the AR camera, whose pose is 40–60 ms older: the image slid behind the eyes on every head
  turn, then caught up.
- Windows stay still. A world-locked window turns to face you only when it moves (spawned, dragged, docked), not
  every frame (`SpatialWindow.LateUpdate`).
- No HoloKit bar along the top in Stereo (MORE, RECORD, MONO, OPTIONS, ZOOM/TRIGGER), and HoloKit's own buttons
  are hidden in Stereo (new `StereoChrome.cs`).
  - Leave Stereo with Settings > Display > Leave Stereo.
  - The "TRIGGER button" setting is gone from Settings. Zoom stays in Settings and on F5.
  - Consent is still approved on its window.
- Full-screen eyes: each eye fills its half of the screen, so there are no black margins or middle gap. The
  projection grows with it (`SettingsService.GrowProjection`), so scale and stereo alignment are unchanged.
  - HoloKit's alignment guide (outline and marker line) is hidden.
  - The camera image stays at real size (not enlarged to fill the eyes, which made the room swing faster than the
    head): black past the camera's edges, windows still drawn there.
- Default eye distance (IPD) is 63 mm, the VR standard. Settings version 2: an untouched 64 mm from version 1 moves to
  63 mm.
- Steadier windows: HoloKit's predicted pose smooths ARKit's per-frame corrections over 20 ms rotation (up from 10 ms);
  position stays at HoloKit's 30 ms (`StereoChrome.SetSmoothing`). The first try (50 / 100 ms) made the whole view
  creep sideways on device (windows drifting right pixel by pixel): between corrections the pose rides on the IMU, so a
  long time constant lets sensor bias build up. Untested on device at 20 / 30 ms.
- Unity recompiles with 0 errors and 0 warnings. Test Device Fixes passes 51/51 in DeviceMain. Test Settings has not
  been run.
- The on-screen status/stats overlay (`DeviceStatusHud`: frame rate, hand tracking, camera...) is no longer added to
  the app. The class stays for its tests.
- Unity compiles with 0 errors. Tools > Spatial Windows > Test Device Fixes passes 46/46 in DeviceMain
  (Unity in batch mode). Its checks that the overlay draws on screen were removed, since the overlay is no longer in
  the app. Not yet tried on the phone.

**Laptop app as a desktop widget (2026-11-07):** the app window (`Tools/LaptopAgent/gui/web/`) is now a frameless
glass widget in the style of macOS Control Center: a status pill (ring, headset, power), round Watch / Control / Cast /
USB cable buttons, Pair and Headsets pills, and small Settings / minimize / close buttons. Pairing and settings
(paired headsets with Forget, open at login, permissions, emergency stop, version, logs, quit) open as glass cards.
`gui/glass.py`: hidden from screen capture (Windows `WDA_EXCLUDEFROMCAPTURE`, verified on this PC; macOS
`NSWindowSharingNone`), Windows 11 acrylic and rounded corners; on macOS the vibrancy is masked to the tiles.
The agent's own window is also left out of the Windows window list (macOS already did this).
Fixed: opening the app while it was already running froze the window ("not responding"). Bringing the window to the
front set pywebview's `on_top` from a background thread, which deadlocks under pythonnet; it now uses Win32 calls
(`gui/web_app.py` `_lift_windows`, with a test). The installed app (`%LOCALAPPDATA%\Programs\Spatial Launcher Agent`)
and `dist\` were rebuilt from today's code on 2026-11-07.
2026-11-08:
- Closing the widget (×) now quits the app, so the next start is a fresh process. Paired headsets and settings are
  kept on disk.
- Wi-Fi fix: the agent gave up on a Bonjour-found headset after 5 failures of any kind, including "not paired", so after
  QR pairing Wi-Fi was never tried again. Now only "can't connect" counts towards giving up (`session.py`, tests in
  `test_approval.py`).
- "Connection lost" was the panic gesture firing by itself. The iPhone's log (read on Windows with
  `pymobiledevice3 syslog live`) showed `[Consent] panic stop (cross and sweep)` one second before each drop. Hand
  tracking swapping left and right reads as crossed wrists and then a sweep. The panic gesture is now opt-in: Settings >
  Hands > Cross to stop the laptop, off by default. While a screen is live, the gesture otherwise does nothing; F8 and
  Stop still work.
- The agent no longer gives up on an address that has linked before (after a session ends it may be unreachable for a
  while).
- Unity: Test Gestures 33/33, Test Settings 29/29. Agent connection tests pass. Laptop app rebuilt and reinstalled;
  the phone app needs a new build.
On Windows the widget is now an opaque frosted-glass panel painted by the page (2026-11-08). Windows' blur turned flat
light grey whenever the widget wasn't the active window. The whole widget drags from anywhere but its controls. The macOS look is untested. The Tk fallback window is unchanged.
Building and testing the macOS .app is handed off to a session on a Mac: [Doc/mac-agent-handoff.md](Doc/mac-agent-handoff.md).

**Open bug:** over the USB cable, nothing happens after tapping Allow control. Not investigated yet; needs the
agent's log (`python agent.py --wired --input --keyboard --verbose`) from the moment of the tap.

Wired mode without a network, and QR pairing:

- **USB via Apple's multiplexer (usbmuxd)** instead of Personal Hotspot: `Tools/LaptopAgent/usbmux.py`. No Wi-Fi,
  hotspot or IP network needed.
- **Stream mode**: after the signed handshake, one TCP stream carries control, input and H.264 video instead of WebRTC
  (`stream_link.py`, `StreamLinkTransport.cs`). The headset only offers it on loopback connections, so only over the cable.
- **Hardware H.264 decoding on iOS**: `Assets/Plugins/iOS/SpatialVideoDecoder.mm`, `StreamVideoDecoder.cs`.
- **QR pairing**: the headset reads the laptop's QR code through the passthrough camera
  (`PairQrScanner.cs`, `SpatialQrScanner.mm`, `qr_image.py`, `test_qr_pairing.py`).
- Agent app icons and packaging updates (`Tools/LaptopAgent/packaging/`).
- `Doc/wired.md` rewritten for the cable-only flow.

Checked off-device: usbmux client against a fake multiplexer and Apple's service on Windows; full stream sessions
against a fake headset (`test_stream_link.py`); Python agent against the C# handshake; iOS compile; headset link tests.

**Still needs the phone:** loopback arrival through usbmux, decoder picture and colours with several windows,
latency, shell responsiveness over the cable. Steps are in [Doc/wired.md](Doc/wired.md#test-steps-on-the-device).

Housekeeping before committing:
- `tatus` at the repo root is a stray file (a mistyped `git status` redirect from 2026-10-20); safe to delete.
- `Assets/_Recovery/` is a Unity crash-recovery scene; check it and delete.
- `UIGallery/` is untracked; its `.gitignore` line is still pending.
- The font `.asset` diffs (~650k lines) are Unity re-saves; decide whether to commit them.

## Committed on `pairing-fixes` (not yet in main)

| Date | Commit | Change |
| --- | --- | --- |
| 11-02 | `335c348` | Laptop app redesigned as widgets; Forget for each paired headset |
| 11-02 | `26e3ba8` | Fix windows failing to open on idle screens; switcher gesture firing on its own |
| 10-30 | `ae81f21` | Pair over the USB hotspot, pairing progress, agent app restyle, pinch-hold no longer toggles the switcher |

## Done (in main)

**Laptop shell and app switcher** (10-22 → 10-23, PR #16, #17)
- Laptop shell protocol with Windows and macOS backends; headset data layer (lists, icons, search, launch).
- Laptop Shell window (search, taskbar/dock, all apps, window lists, styled per laptop OS).
- App switcher across headset and laptop windows (pinch, trigger or keys).
- "Always allow this headset for control", revocable.

**Laptop agent app** (10-22, PR #15)
- Packaged app for Windows and macOS: status window, tray icon, background mode, start at login, single instance,
  pairing dialog, release workflows, licence report, first-run tool download.

**Glass UI polish** (10-20 → 10-21, `ui-polish`)
- Design spec, theme tokens, Inter + Material Icons, rounded glass slab windows, grab bar, hover controls,
  type scale, control states, open/close animation (Reduce motion), passthrough blur (Performance toggle),
  UI gallery capture tool.

**Cross-platform agent** (10-20, PR #14)
- `hostos` platform layer; macOS capture/encode (AVFoundation, VideoToolbox), Quartz input, Keychain, approval prompt,
  kill hotkey; experimental Linux X11 backend.

**Secure laptop link** (10-19, PR #11)
- Laptop identity keys, signed handshake, mDNS discovery, one-time pairing code shown on the headset (replaced QR),
  laptop asks before granting control, kill hotkey, input over WebRTC data channels, iOS key store and Bonjour.

**Device fixes and features** (10-16 → 10-19, PR #6–#10)
- Device status lines, stereo passthrough, hand-size depth without LiDAR, two-hand tracking with stable IDs.
- Passthrough zoom with presets; Hand Setup calibration; relative pointer; consent gate for remote screens; panic stop.
- Cast windows with an agent-enforced media whitelist; wired USB hotspot mode (first version).
- Camera feed with depth, camera diagnostics, HoloKit fix toggle.

**Features** (10-15 → 10-16, PR #4, #5)
- Settings window, curved windows, aspect presets; immersion level, dial and three environments.
- Keyboard input to laptop windows; voice typing (push to talk); tunable gesture recognizers.

**Foundation** (10-14 → 10-15, PR #1–#3)
- Spatial window system, image/video/launcher windows, hand input, device scene (TASKS T01–T04).
- Laptop agent streaming desktop windows over WebRTC with input, multi-window picker (T05).
- Unity 6.3 upgrade, iOS device build settings (T06), HoloKit hand source, autodev setup, task checklist.

## Milestones (from Doc/plan.md)

| # | Outcome | Status |
| --- | --- | --- |
| M0 | Toolchain on the phone | Device builds have run (device-fixes branches); T12 not ticked in TASKS.md |
| M1 | Stereo calibrated to lenses/helmet | Stereo passthrough exists; calibration acceptance (T13) not recorded |
| M2 | Floating windows | Done in editor (T01, T02); gaze cursor (T08) not done; on-device check (T14) not recorded |
| M3 | Hand input | Done in editor (T03, T04), two-hand tracking on device; two-hand resize (T09) and 45/50 trial test (T15) not recorded |
| M4 | Passthrough with windows over it | Passthrough, zoom and blur built; T16 acceptance not recorded |
| M5 | Environments and mode switch | Immersion dial and three environments built; T10/T18 acceptance not recorded |
| M6 | Streamed laptop desktop | Well beyond plan in editor (multi-window, control, shell, switcher, cast); latency (T19) and on-device 1080p30 (T20) not recorded |

Doc/TASKS.md has not been updated since 2026-10-15. Much of the work above landed outside its checklist, so
T08–T20 are still unticked even where related features exist.

## Next up

1. Fix Allow control over the USB cable, then test the cable mode and QR pairing on the iPhone; then commit and push
   `pairing-fixes` and open a PR.
2. Clean up the stray files listed above.
3. Bring Doc/TASKS.md up to date: tick what's met, record device measurements (latency, thermal soak, fps).
4. Remaining planned work: gaze cursor (T08), two-hand resize (T09), web page window (T11), thermal soak (T17).
