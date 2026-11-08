# Features

Planned work that was deferred, with enough context to pick it up later, and how the finished items work.

## Done

### Hand Setup: calibration of open palm, pinch and fist

Settings > Hands > Hand setup > Calibrate opens a guided window (`HandSetupContent`). For each pose (open palm, pinch,
fist) it counts down while a hand is in view, then takes about a second of samples while a progress ring fills; a
lost hand pauses the ring and a pose not done within 15 s ends the run. While it measures, the hand pointers and
gestures are paused, so the pinch and fist can't press buttons or move windows.

`HandCalibrator` sets the thresholds between the user's own poses: pinch on at 30% and off at 50% of the way from
their pinch to their open hand (thumb to index as a fraction of wrist to middle knuckle); a finger is curled at 25% and
fully open at 90% of the way from their fist to their open hand (tip-to-wrist over knuckle-to-wrist), so openness and
the fist/open-palm thresholds in `GestureConfig` fall at the same places in the user's own range. The hand length for
hand-size depth is measured only with LiDAR depth; on the iPhone 16 it is typed in on the intro page (wrist crease to
the middle finger's knuckle). The result is saved in `SpatialSettings.hands.calibration`; Reset puts the defaults back.
`HoloKitHandSource.Thresholds` and `GestureRecognizers.HandLength` apply it.

### Relative pointer that freezes during a pinch

Settings > Hands > Pointer: Absolute (the ray from the estimated shoulder through the index knuckle) or Relative
(`RelativePointer`). Relative moves the cursor by the hand's motion times Relative speed (0.5 to 3), freezes it from the
start of a pinch (pinch strength rising fast) until the pinch opens (a click) or the pinched hand turns more than 3
degrees (a drag), stays within 30 degrees of where the hand really points, and re-anchors when the hand comes back.

### Consent gate, live indicator and panic stop

Nothing from another device shows in the headset until the user allows it, every session (`ConsentGate`). A remote
screen asks before it connects: a modal window "Allow <source> (<address>) to show its screen on this headset?
(<mode>)" with Approve and Deny, answered by pinch or mouse, or in
Mono the on-screen Approve/Deny buttons. Deny, closing the modal, or 30 s without an answer connects nothing. An
approval covers that source in that mode only while one of its screens stays live; nothing is remembered. While live, a
red "LIVE: <source>" line is drawn at the top of the view (in each eye in Stereo). Panic stop disconnects every remote
screen, closes their windows, refuses anything waiting and brings Home to the front: cross and sweep (only when Settings >
Hands > Cross to stop the laptop is on; off by default, as hand tracking swapping sides set it off by itself), F8, or in
Mono the indicator's Stop.

The laptop picker's list connection (window titles only) and Settings > Laptop's latency link show no screen and are
not gated.

### Stereo view

In Stereo there is no HoloKit bar along the top (MORE, RECORD, MONO, OPTIONS, TRIGGER) and HoloKit's own buttons are
hidden (`StereoChrome`); leave Stereo with Settings > Display > Leave Stereo. The passthrough image is locked to the
eyes' (predicted) pose, so it doesn't slide when the head turns, and world-locked windows turn to face you only when
they move, not with every head movement. There is no status overlay on screen (`DeviceStatusHud` is no longer added). Each eye fills its half of the screen (no black margins; the projection
grows so scale and alignment hold), HoloKit's alignment guide is hidden, the camera image stays at real size (black past
the camera's edges; windows still draw there), the default IPD is 63 mm, and HoloKit's predicted pose smooths ARKit's corrections longer (steadier windows).

### App switcher

`AppSwitcher` brings a window forward from the headset's own windows and the connected laptop's (a laptop window that
isn't streamed yet opens as a stream window through the Laptop Shell, which needs control). The overview
(`SwitcherContent`) shows a card per window: headset windows first, most recently used first, then the laptop's
windows in its taskbar or Dock order, with their real icons; a streamed laptop window appears once, as its headset
window. Ways in:

- **Pinch:** pinch a card to switch at once. A pinch held for 0.8 s while pointing at no window (and the other hand
  not pinching) opens the overview (`PinchHoldRecognizer`; Settings > Gestures > Pinch and hold to switch).
- **Home > Switch**, or **F7** in the editor: the overview, waiting for a pick.
- **F6:** quick switch to the previous window without the overview; pressed again within 1.2 s it goes further back.

Switching to a window while all windows are hidden shows them again.

### Keyboard that pops up and stays put

- **Pops up by itself** when a text field gets focus: a field on the headset (Laptop apps search, Settings), or a text
  field on the laptop. The laptop agent checks a few times a second whether the laptop's focus is in a text field
  (`Tools/LaptopAgent/textfocus.py`: UI Automation on Windows, the Accessibility API on macOS, nothing on Linux) and
  tells the headset (`text-focus` on the control channel). It only does this while the headset has control with
  typing, after its first click. The keyboard opens under the window you clicked in and closes again when the focus
  leaves the text field. If you opened it with **Keys**, it stays until you close it.
- **Docked to its window** (`WindowManager.Dock`): pressing its keys never takes focus, so neither the keyboard nor
  the window moves back and forth. It comes forward and recedes with that window, follows it when you move it, and
  closes with it. Drag the keyboard to place it somewhere else relative to the window.
- **Voice typing:** the mic key. Tap it, speak, tap it again. Or hold it while you speak and let go. It turns red
  while listening. The text goes to the focused text field, else to the window the keyboard is docked to.

### Bezel-less laptop windows

Laptop stream windows have no padding: the picture runs to the window's edges with the window's rounded corners, and
the window keeps exactly the picture's aspect (it refits when the stream's size changes and after a resize), so there
are no empty bars beside the picture.

## Deferred

### Other devices' screens

See the feasibility notes in the device-fixes-4 report: AirPlay mirroring to the laptop and streaming that window
through the agent needs no new code; a ReplayKit broadcast extension on a second iPhone/iPad needs a Mac, Xcode and
most likely a paid developer account; a web view window needs a paid plugin (3D WebView) and renders slowly on iOS.
