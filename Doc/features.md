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
(<mode>)" with Approve and Deny, answered by pinch or mouse, the stereo bar's TRIGGER (reads ALLOW while asking), or in
Mono the on-screen Approve/Deny buttons. Deny, closing the modal, or 30 s without an answer connects nothing. An
approval covers that source in that mode only while one of its screens stays live; nothing is remembered. While live, a
red "LIVE: <source>" line is drawn at the top of the view (in each eye in Stereo). Panic stop disconnects every remote
screen, closes their windows, refuses anything waiting and brings Home to the front: cross and sweep (on even when the
gesture is switched off in Settings), F8, or in Mono the indicator's Stop.

The laptop picker's list connection (window titles only) and Settings > Laptop's latency link show no screen and are
not gated.

### App switcher

`AppSwitcher` brings a window forward from the headset's own windows and the connected laptop's (a laptop window that
isn't streamed yet opens as a stream window through the Laptop Shell, which needs control). The overview
(`SwitcherContent`) shows a card per window: headset windows first, most recently used first, then the laptop's
windows in its taskbar or Dock order, with their real icons; a streamed laptop window appears once, as its headset
window. Ways in:

- **TRIGGER (ZOOM) button**, with Settings > Display > TRIGGER button set to Switcher (it then reads SWITCH): a press
  opens the overview with the next window selected, each press moves on, and 1.5 s after the last press the selection
  comes forward, like letting go of Alt+Tab. While the consent prompt asks, TRIGGER is still ALLOW. Set to Zoom (the
  default), TRIGGER zooms as before; F5 and the Mono button always zoom.
- **Pinch:** pinch a card to switch at once. A pinch held for 0.8 s while pointing at no window (and the other hand
  not pinching) opens the overview (`PinchHoldRecognizer`; Settings > Gestures > Pinch and hold to switch).
- **Home > Switch**, or **F7** in the editor: the overview, waiting for a pick.
- **F6:** quick switch to the previous window without the overview; pressed again within 1.2 s it goes further back.

Switching to a window while all windows are hidden shows them again.

## Deferred

### Other devices' screens

See the feasibility notes in the device-fixes-4 report: AirPlay mirroring to the laptop and streaming that window
through the agent needs no new code; a ReplayKit broadcast extension on a second iPhone/iPad needs a Mac, Xcode and
most likely a paid developer account; a web view window needs a paid plugin (3D WebView) and renders slowly on iOS.
