# Features

Planned work that was deferred, with enough context to pick it up later.

## Deferred

### Hand calibration (open, pinch, fist)

Hand thresholds are fixed today:

- Pinch is fixed in code: `HoloKitHandSource.PinchOn` 0.25 and `PinchOff` 0.40, both as a fraction of wrist-to-middle-knuckle.
- The fist's curl ratio (1.15) is fixed in code.
- Fist and open-palm openness (0.25 and 0.65) and the 9 cm hand length used for hand-size depth live in
  `Assets/Settings/GestureConfig.asset`.

A calibration step would have the user hold an open hand, a pinch and a fist for a second each. It would measure their own
values, set thresholds between them, and save the result per user in `SpatialSettings` (Settings > Hands > Calibrate).
The hand length would make hand-size depth more accurate on phones without LiDAR (iPhone 16).

Needs: a guided window (one step per pose, with a progress ring), per-user threshold fields in `HandSettings`, and
`HoloKitHandSource` / `GestureRecognizers` reading them instead of the constants.

### Relative pointer that freezes during a pinch

The hand pointer is absolute today: a ray from an estimated shoulder through the index knuckle, smoothed with a dead
zone (`RayFilter`). It keeps moving while a pinch closes, so a pinch can land slightly off target.

A relative mode would move the cursor by the hand's motion (with gain), hold it still from the start of a pinch
until it either clicks or turns into a drag, and re-anchor when the hand comes back. That needs a cursor state per hand
in `HandWindowInput`, a pinch-onset detector (pinch strength rising quickly), and a setting to choose absolute or
relative.
