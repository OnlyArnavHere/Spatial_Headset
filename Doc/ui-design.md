# Spatial Launcher glass UI (design spec)

Direction: visionOS-like frosted glass, tuned for the HoloKit stereo view. Units: 1 pt = 1 mm at 1 m viewing distance (world size = pt x distance(m) mm). Angles are per eye.

## 0. Ground rules
- HoloKit + iPhone 16: each eye gets about 1280x1180 px across about 60 deg, about 20 px/deg (1 px = 3 arcmin), roughly a third of visionOS density, and the lens magnifies further. So everything is larger and higher-contrast than visionOS.
- Do not change: the LIVE indicator (`ConsentGate.OnGUI`, ConsentGate.cs ~353; `liveTexture` red (0.75,0.08,0.08,0.85) ~398); the curved-text fix (`WindowCurver`, `Testing/Editor/CurvedTextTest`) - every stage keeps CurvedTextTest green and new chrome meshes bend with `WindowCurver`; consent gate semantics (section 9).

## 1. Legibility
- Minimum text: cap height >= 0.4 deg (24 arcmin, ~7 mm at 1 m, ~8 px per eye) = 10 pt font minimum, for single short labels only. Running text >= 12 pt.
- Contrast: >= 7:1 for text on glass (AAA), >= 4.5:1 for large text (>= 17 pt) and icons, measured against the worst case: a white wall behind the glass. Low px/deg, lens blur and a bright mono passthrough erase thin strokes.
- How the glass guarantees it: backdrop linear luminance clamped to <= 0.25 before tinting, tint alpha 0.72. Worst case glass luminance = 0.28*0.25 + 0.72*0.024 = 0.087, so white text = (1.05)/(0.137) = 7.6:1. text.secondary (~0.59 luminance) = 4.6:1, so secondary text is used only at >= 15 pt.
- No Light/Thin weights; Regular (400) or heavier.

## 2. Hand targets (HoloKit pinch-ray)
- Hit area >= 2.5 deg (44 mm at 1 m); the visible shape can be smaller, pad the collider.
- Gap between hit areas >= 0.5 deg (8 mm).
- Hover shows after the ray rests 80 ms (debounce).

## 3. Tokens (static `UITheme`)
| Token | RGBA |
|---|---|
| glass.tint | #2A2C33, alpha 0.72 |
| glass.fallback (blur off / no passthrough) | #1E2026, alpha 0.92 |
| glass.raised (controls) | #FFFFFF, alpha 0.12 |
| key.fill (keyboard keys; 17 pt white labels reach 4.5:1 on the worst glass) | #FFFFFF, alpha 0.08 |
| text.primary | #FFFFFF, alpha 1.0 |
| text.secondary | #EBEBF5, alpha 0.70 |
| accent | #0A84FF |
| destructive | #FF453A |
| success | #30D158 |
| accentFill | #0062CC (primary button fill under white text, ~5.8:1; accent is for text-free accents) |
| successFill | #1E7B34 (solid button fill under white text, ~5.3:1; success is for accents without text) |
| destructiveFill | #D70015 (solid button fill under white text, ~5.3:1; destructive is for accents without text) |
| rim | #FFFFFF alpha 0.28 (top) to 0.08 (bottom), 1 px |
| shadow | #000000, alpha 0.35 |
| focus ring | #FFFFFF alpha 0.9, 2 pt, 3 pt outside the edge |
| hover overlay | #FFFFFF, alpha 0.10 |
| pressed overlay | #FFFFFF alpha 0.18, plus scale 0.96 |

- Corner radius: window 24 pt; controls and tiles 12 pt; round buttons full circles.
- Spacing scale (pt): 4, 8, 12, 16, 24, 32. Window padding 24, item gap 12.
- Shadow: offset (0, -6 pt), blur 24 pt, drawn in the same SDF pass.

## 4. Glass
- Rounded-rect SDF quad, 1 px antialiased edge.
- Colour = lerp(clampLum(blurredBackdrop, 0.25), tint.rgb, tint.a); then rim, plus a vertical sheen (+0.04 top to 0 bottom).
- Blur: one shared pass per frame, ~1/8 resolution, 3 dual-Kawase passes, effective radius ~3 deg (see section 11).

## 5. Type and icons
- Font: Inter (SIL OFL 1.1), Regular / Medium / SemiBold, as TMP SDF assets.
- Icons: Material Symbols Rounded (Apache-2.0), as a TMP font asset addressed by codepoint (TMP has no ligature support).
- No Apple SF fonts or SF Symbols. Licences in THIRD_PARTY.md.

| Role | pt (mm at 1 m) | Weight |
|---|---|---|
| Large title | 29 | SemiBold |
| Title | 22 | SemiBold |
| Headline | 17 | SemiBold |
| Body | 15 | Regular |
| Caption (minimum running text) | 12 | Medium |
| Icon in round buttons | 20 | - |

Line height 1.3x.

## 6. Chrome
- Grab bar: pill 120x10 pt (hit area 140x44), 12 pt below the window, #FFFFFF alpha 0.45 (0.8 on hover). New `WindowPartType.GrabBar` appended at the end of the enum, same move code as `TitleBar`. The title bar stays draggable but is no longer drawn as a grey band; the title becomes text inside the glass, 24 pt from the top.
- Round controls: 32 pt circles (hit area 44), glass.raised, icons close / more_horiz / open_in_full. Close and more sit 12 pt left of the grab bar; resize is a 32 pt corner arc at bottom-right mapped to `ResizeHandle`. Hidden (alpha 0) until the ray hovers the window or the control; fade in 150 ms, out after 600 ms. Colliders always active so hit-testing and tests don't change. `CloseButtonSize` (0.045 m) stays 44 mm.

## 7. States
| State | Look |
|---|---|
| Idle | tint only |
| Hover | + hover overlay, rim alpha x1.5 |
| Pressed | pressed overlay, scale 0.96, 60 ms |
| Focused (keyboard target, selected tile) | focus ring |
| Disabled | content alpha 0.38, no hover |

All animation uses `Time.unscaledDeltaTime` and exponential smoothing k = 1 - exp(-dt/tau), so it is frame-rate independent.

## 8. Motion
- Open: scale 0.92 to 1, alpha 0 to 1, 250 ms, ease-out-cubic.
- Close: scale 1 to 0.95, alpha to 0, 180 ms, ease-in-cubic, then destroy.
- Hover tau 60 ms. A "Reduce motion" setting gives fade only.

## 9. Window types
- Home: 4-column grid of app tiles, 72 pt square, 12 pt radius, icon plus caption.
- Image / Video: content fills the glass with inset 0, clipped to the radius (12 pt: 24 pt less the 20 pt padding, never below the control radius). Video controls are a floating glass capsule (44 pt high) on hover.
- Settings: 160 pt sidebar of 44 pt rows, accent capsule on the selected row; switches 52x32 pt.
- Keyboard: keys 44 pt with 6 pt gaps (the one allowed exception, still >= 0.3 deg), glass.raised, Return in accent.
- Connect laptop: body text plus a primary accent button 44 pt high.
- Remote stream: content opaque; only the chrome is glass.
- Consent (exception, clarity over style): solid, not glass. Body #1E2026 alpha 1.0 with a 2 pt rim #FFFFFF alpha 0.5. Approve solid successFill, Deny solid destructiveFill (white text needs the darker fills), both 56 pt high, white SemiBold 17 pt text. Countdown text.secondary 15 pt. Close always visible (no hover-hide). No open scale (120 ms fade only). Semantics and timeouts unchanged.

## 10. Stages
- C2: static `UITheme` tokens + `ThemeApplier`; Inter and Material Symbols as TMP font assets under Assets/Fonts/; THIRD_PARTY.md.
- C3: Assets/Shaders/SpatialGlass.shader (hand-written HLSL): SDF, rim, shadow, tint, fallback. SpatialGlass.mat referenced from WindowManager (not Shader.Find); update DeviceBuildCheck's material check.
- C4: grab bar, round controls, hover reveal, `WindowPartType.GrabBar`.
- C5: type scale everywhere; 10 pt floor asserted in a test.
- C6: state machine + tests.
- C7: open/close motion.
- C8: shared blur pass + Settings > Performance "Glass blur" toggle (off = glass.fallback).
- C9: consent/LIVE check, CurvedTextTest, AFTER gallery.
- No asmdef or package change needed (TMP ships in com.unity.ugui). C8 may add a ScriptableRendererFeature to the URP renderer asset under Assets/Settings, or drive a CommandBuffer from a MonoBehaviour instead.

## 11. Backdrop blur feasibility
- Today: stereo mode `StereoPassthrough` keeps `ARCameraBackground` running and copies the camera image through `background.material` into a RenderTexture (`CopyFeed`, exposed as `Feed`). Mono mode: `ARCameraBackground` draws straight to the camera target. `CameraBackgroundFallback` covers the editor.
- Plan: once per frame, blit the camera feed into a ~240x180 RT (1/8 of 1920x1440), 3 dual-Kawase passes into a global `_SpatialBackdrop`. Glass projects its world position with the AR camera view-projection and display matrix to get feed UVs. The feed is mono, so one blur serves both eyes with no stereo cost; a 3 deg blur hides the missing disparity.
- Estimated cost on iPhone 16 (A18): ~0.1 ms downsample + 0.1-0.15 ms passes, < 0.3 ms GPU, ~0.5 MB. To be measured on device.
- Fallback: when passthrough is off (immersive environment showing) or the Performance toggle is off, skip the pass and use glass.fallback. Avoid `_CameraOpaqueTexture` (full-resolution copy, heat).
