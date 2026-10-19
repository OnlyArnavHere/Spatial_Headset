# One session on a borrowed Mac

The goal of the session: get the app onto the iPhone, run the first device tests, and come back with recordings and
numbers. Everything else (fixes, tuning) happens back on the Windows machine.

## Before the session

On the Windows machine, the day before:

- [ ] Push the branch you'll build (`device-prep` exists only locally until you push it):
      `git push -u origin device-prep`.
- [ ] Run **Tools > Spatial Windows > Check iOS Device Build**: expect 0 failures (the only warning is the missing
      Apple Team ID, which you set in Xcode).
- [ ] Optional shortcut: run **Tools > Spatial Windows > Export iOS Xcode Project** (keep the Unity window in front
      until the log says `[DeviceBuild] Exporting`; it waits for the editor to be focused) and copy `Builds/iOS`
      (about 1.2 GB) to a USB stick. With it you can skip steps 2–6 below and need only Xcode on the Mac.
- [ ] Check the laptop agent still runs: `python agent.py --input --dry-run-input`, then Ctrl+C.

Ask the Mac's owner to have installed (each is a long download):

- Xcode 16 or later, and new enough for the iPhone's iOS version (iOS 26 needs Xcode 26). Open it once so it
  installs its components. Check the phone's version in Settings > General > About.
- Unless you use the USB shortcut: Unity Hub with **Unity 6000.3.26f1** and its **iOS Build Support** module, and the
  Xcode Command Line Tools (`xcode-select --install`; Unity needs git to fetch the HoloKit package).

Bring:

- [ ] The iPhone (a LiDAR model: iPhone 12 Pro or a later Pro) on **iOS 18 or later**, charged. The app won't install on
      older iOS, and hand tracking needs both LiDAR and iOS 18.
- [ ] Its cable, the HoloKit headset, and the Windows laptop with the agent.
- [ ] Your Apple ID and password, and the device that receives its two-factor codes.
- [ ] A phone tripod or a second phone for filming through the lens (optional), and an IR thermometer if you have one.

## Build and install, in this order

1. **Clone.** `git clone https://github.com/OnlyArnavHere/Spatial_Headset.git && cd Spatial_Headset && git checkout device-prep`
2. **Open in Unity.** Unity Hub > Add > the folder, editor 6000.3.26f1. The first import takes 10–30 minutes. If Hub
   asks to change the editor version, say no.
3. **Switch the platform to iOS.** File > Build Profiles > iOS > Switch Platform. (The platform is stored per machine,
   so a fresh clone starts on macOS.)
4. **Expect WebRTC errors in the Console and ignore them for the build:** `EntryPointNotFoundException: RegisterDebugLog`
   comes from a bug in `com.unity.webrtc` 3.0.0 in the editor on an iOS target (README, "WebRTC in the editor"). It
   doesn't affect the iPhone build. Any *other* compile error must be fixed first.
5. **Check.** Tools > Spatial Windows > **Apply iOS Device Settings** (expect "0 change(s)"), then **Check iOS Device
   Build** (expect 0 failures).
6. **Build.** File > Build Profiles > iOS > **Build**, folder `Builds/iOS`. Or Tools > Spatial Windows > Export iOS
   Xcode Project with the Unity window in front. 5–10 minutes.
7. **Open the Xcode project.** `Builds/iOS/Unity-iPhone.xcodeproj`. USB shortcut: copy the folder to the Mac's disk
   first, then restore the execute permissions a Windows copy loses, in Terminal inside the folder:
   `chmod -R u+x Il2CppOutputProject/IL2CPP/build *.sh`, and open its `Unity-iPhone.xcodeproj`.
8. **Sign with a free Apple ID.** Xcode > Settings > Accounts > + > Apple ID. Then the **Unity-iPhone** target >
   Signing & Capabilities: tick *Automatically manage signing*, Team = *(your name) (Personal Team)*. If Xcode says
   the bundle identifier `com.onlyarnavhere.spatiallauncher` isn't available, change it to something unique (add
   your initials) here.
9. **Connect the iPhone** by cable, unlock it and tap *Trust This Computer*. Pick it as the run destination at the
   top of Xcode.
10. **Enable Developer Mode on the iPhone.** Settings > Privacy & Security > Developer Mode > on; the phone restarts;
    after the restart confirm *Turn On*. (The switch appears only once Xcode has seen the phone; if it's missing, try
    step 11 once, then look again.)
11. **Install.** Product > Run (⌘R). The first build compiles the IL2CPP code: 5–15 minutes on Apple silicon.
12. **Trust your certificate**, if the phone says *Untrusted Developer*: Settings > General > VPN & Device Management >
    your Apple ID > Trust. Run again.
13. On first launch allow **Camera** and, when you open a laptop window, **Local Network**.

Free signing lasts 7 days; after that, run from Xcode again. Keep the cable connected during the tests: Xcode's console
(View > Debug Area) shows the app's log.

## What to test first, and what to capture

Do these in order; each one needs the one before. Save the Xcode console after each (right-click > Save) and note the
time, so recordings and logs line up. Record the phone's screen with Control Center > Screen Recording (add it in
Settings > Control Center if missing).

1. **Mono AR (phone in hand).** The app starts in landscape with the camera view, and the Home launcher floats in front
   of you; open the Image and Video windows.
   *Capture:* a screen recording walking around the windows (do they stay put in the room?).
2. **HoloKit stereo view.** Tap HoloKit's on-screen **Stereo** button, then put the phone in the headset. The stereo
   bar along the phone's top edge has **MONO** to come back.
   *Capture:* a short video filmed through one lens, a screen recording, and a note of how sharp and comfortable it
   is and whether the two images line up (double vision means the phone model's calibration is off).
3. **Passthrough.** In the headset, look at the room: it's the single rear camera, so it's flat (mono).
   *Capture:* through-the-lens video while moving your head; note brightness, sharpness and how far the real world lags.
4. **Hand tracking.** Hold a hand 30–60 cm in front of the phone in good light. A cursor dot and ray should follow it;
   pinch (thumb on index tip) a window's title bar and move it; pinch the close button.
   *Capture:* a screen recording of select, drag and close, and notes on how often the cursor drops or jumps (HoloKit
   updates hands about 20 times a second). Note whether a pinch is missed or triggers on its own: the thresholds are
   untested on a device.
5. **WebRTC over Wi-Fi with the laptop agent.** Phone and laptop on the same Wi-Fi (set to a *Private* network on
   Windows). On the laptop: `python agent.py --input --dry-run-input` (dry run first: nothing gets clicked), and
   `python clock.py`. In the app: Home > **Laptop** > pair the laptop once, then tap it in the picker and run the agent
   (see [connect.md](connect.md); the old port 8889 and token are gone) > pick the clock, then **Whole desktop**.
   *Capture:* a screen recording, the agent's console text, and both windows' status lines.
6. **Latency.** With the clock streaming, film the phone screen and the laptop's real clock window together (or take
   several photos). Each photo's difference between the two clocks is the end-to-end delay; take at least 6. Also note
   the round-trip time in the window's title bar.
   *Compare with:* 163 ms mean (140–177 ms) measured in the Unity editor on the same Windows machine.
7. **Phone temperature after 10 minutes.** Stereo, hand tracking and one laptop stream running, phone in the headset,
   for 10 minutes by the clock.
   *Capture:* the back of the phone's temperature (IR thermometer, or by hand: warm / hot / too hot to hold), whether
   iOS showed a temperature warning, and whether the frame rate dropped (HoloKit lowers the stereo frame rate as the
   phone heats up). Xcode's Debug Navigator shows the frame rate while running from Xcode; for the thermal state
   itself, profile with Instruments' Game Performance template (Product > Profile).
   Then try with the laptop stream closed, to see what it costs.

If time is left: input with `--input` (without dry run) on a harmless window such as Notepad; two laptop windows at
once; battery drain over the 10 minutes.

Voice typing (untested on a device): with the agent on `--input --keyboard` and Notepad streamed, open the keyboard (Keys),
hold **Talk**, allow Speech Recognition and Microphone the first time, then hold Talk again and say a sentence; it should
appear in Notepad. Note how long the text takes after letting go, whether the status line says "on this phone" (Settings >
Voice > On-device only), and any error the keyboard's status line shows.

## Likely failures and fixes

| What you see | Likely cause | Fix |
|---|---|---|
| Unity: "Cannot resolve package io.holokit.unity-sdk" | git missing on the Mac | `xcode-select --install`, then reopen the project |
| Unity Console full of `RegisterDebugLog` errors | `com.unity.webrtc` bug on an iOS target, editor only | Ignore for the build, or apply the patch in README ("WebRTC in the editor") |
| Export never starts | The export waits for an idle, focused editor | Click into the Unity window; or use File > Build Profiles > Build |
| Xcode: "Signing for Unity-iPhone requires a development team" | No team chosen | Step 8: Personal Team, automatic signing |
| Xcode: "Failed to register bundle identifier" | Someone else uses it | Change the bundle ID in Signing & Capabilities |
| Xcode: "iPhone is not available" / "Developer Mode disabled" | Phone not prepared | Steps 9–10; unlock the phone; restart Xcode |
| Xcode: "Could not locate device support files" | Xcode older than the phone's iOS | Update Xcode, or use a Mac whose Xcode matches |
| Xcode: "requires iOS 18.0" | Phone on iOS 17 or older | Update the phone; hand tracking needs iOS 18 anyway |
| Xcode: build error in UnityFramework about signing | The framework target has no team | Set the same Personal Team on the UnityFramework target |
| Xcode: "Permission denied" running `il2cpp`, `bee_backend` or a `.sh` script | Project copied from Windows | `chmod -R u+x Il2CppOutputProject/IL2CPP/build *.sh` in the project folder |
| Phone: "Untrusted Developer" | Free certificate not trusted yet | Step 12 |
| Black screen instead of the camera | Camera permission denied | Settings > (app) > Camera on |
| No Stereo button | The HoloKit canvas is missing | Check iOS Device Build flags it; HoloKit starts in mono |
| Stereo images don't line up | Phone model not in HoloKit's list | Note the model; see HoloKit's Phone_Calibration_Guide |
| No hand cursor | Not LiDAR, iOS < 18, hand too close/far, dim light | Check the model and iOS; 30–60 cm; more light |
| Laptop window: "Can't reach …" | Different network, firewall, wrong address, Local Network denied | Same Wi-Fi; Windows network Private and allow the prompts; use the address the agent prints; Settings > Privacy & Security > Local Network > (app) on. Guest networks often block devices from each other: use the phone's hotspot instead (laptop joins it) |
| Laptop window: pairing code rejected | Expired (2 minutes), already used, or 3 wrong tries | Start Pair new laptop again; see [connect.md](connect.md) (replaces the old token) |
| Laptop window: connected, but no picture | Captured window minimized, or the phone can't decode the stream | Restore the window; restart the agent with `--encoder x264` |
| App gets slow after a few minutes | Heat: HoloKit lowers the stereo frame rate | That's the temperature test's result: note it, let the phone cool |
