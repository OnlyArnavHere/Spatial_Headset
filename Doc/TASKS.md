# Tasks

A checklist built from [plan.md](plan.md). Each task points at the plan section it comes from instead of repeating it.

Fields: **plan** is the anchor into plan.md (plus the milestone row where one applies); **size** is S (under a day),
M (a few days) or L (a week or more); **tag** says who can run it:

- `unattended-ok`: editor-only work whose checks run as EditMode tests through Unity MCP, without Play mode.
- `human-supervised`: enters Play mode with WebRTC, needs the laptop agent, or needs a judgement call.
- `needs-mac`: needs the iPhone, the headset or Xcode on a Mac.

Acceptance checks for `unattended-ok` tasks must be EditMode tests: the gate runs them through Unity MCP, which gives
the whole EditMode run 10 seconds.

## Done (on main)

- [x] **T01** Window manager in the editor: spawn, focus, drag, resize, close
  - plan: [Milestones](plan.md#milestones), row M2 · size: M · tag: unattended-ok · depends-on: none
  - accept:
    1. Windows spawn world-locked, come to the front on focus, and drag, resize and close with the mouse.
    2. Input goes through `IWindowInput`; the mouse implementation is swappable.
- [x] **T02** Image, video and launcher content windows with controls
  - plan: [Milestones](plan.md#milestones), row M2 · size: M · tag: unattended-ok · depends-on: T01
  - accept:
    1. The launcher opens Image and Video windows; video play/pause and seek work through window controls.
- [x] **T03** Hand input in the editor: point, pinch-select, grab-move, ray smoothing
  - plan: [Milestones](plan.md#milestones), row M3 · size: M · tag: unattended-ok · depends-on: T01
  - accept:
    1. Simulated hands focus, move, resize and close windows through `HandWindowInput`.
    2. A held hand with tremor moves a window by less than 1 mm (RayFilter dead zone).
- [x] **T04** HoloKit hand source mapping, checked on synthetic joints
  - plan: [What is and is not possible](plan.md#what-is-and-is-not-possible), hand tracking row · size: S · tag: unattended-ok · depends-on: T03
  - accept:
    1. Open hand, pinch and fist joint sets map to tracked / pinched / grabbing as expected.
- [x] **T05** Laptop agent and stream windows over WebRTC in the editor (stand-in for the streamed desktop)
  - plan: [What is and is not possible](plan.md#what-is-and-is-not-possible), desktop streaming row · size: L · tag: human-supervised · depends-on: T02
  - accept:
    1. The picker lists laptop windows and each pick opens its own stream window with input.
- [x] **T06** iOS device build settings and a device build check
  - plan: [Workflow](plan.md#workflow-vs-code-plus-unity-mcp), step 2 · size: S · tag: unattended-ok · depends-on: none
  - accept:
    1. Tools > Spatial Windows > Check iOS Device Build reports 0 failures.

## Open

- [x] **T07** EditMode test assembly with smoke tests for existing pure logic
  - dropped: tests already run through the project's own runner; no asmdefs
  - plan: [Workflow](plan.md#workflow-vs-code-plus-unity-mcp), "the fastest loop" paragraph · size: S · tag: unattended-ok · depends-on: none
  - first: First add ONE trivial test and confirm the Unity Test Runner/MCP lists it. If tests in the default Editor assembly aren't discovered, STOP (needs_human); do not create asmdefs.
  - accept:
    1. Unity Test Runner lists at least 5 EditMode tests in `Assets/Tests/Editor/` (our scripts have no asmdef, so the tests can't live in their own test assembly).
    2. They cover `RayFilter` and `HoloKitHandSource.FromJoints`, all pass, and the run finishes in under 10 s without entering Play mode.
- [ ] **T08** Gaze cursor input as a stand-in for hands
  - plan: [Milestones](plan.md#milestones), row M2 · size: S · tag: unattended-ok · depends-on: T07
  - accept:
    1. A `GazeWindowInput` implements `IWindowInput` with a head-centred ray and dwell-to-select.
    2. An EditMode test shows the cursor on the window the camera faces and a select after the dwell time.
- [ ] **T09** Two-hand resize gesture
  - plan: [Milestones](plan.md#milestones), row M3 · size: M · tag: unattended-ok · depends-on: T07
  - accept:
    1. Pinching one window with both hands and moving the hands apart scales it by the change in hand distance (within 2 cm).
    2. Releasing either hand ends the resize; the existing one-hand gestures keep passing their tests.
- [ ] **T10** Immersive environment and a passthrough/VR mode switch
  - plan: [Milestones](plan.md#milestones), row M5 · size: M · tag: unattended-ok · depends-on: T07
  - accept:
    1. One call switches between passthrough and an environment (skybox or scene) and back.
    2. An EditMode test shows every window's pose is unchanged after a round trip and the switch completes within one update.
- [ ] **T11** Web page content window
  - plan: [What is and is not possible](plan.md#what-is-and-is-not-possible), web pages row · size: L · tag: needs-mac · depends-on: T02
  - accept:
    1. A web window renders a page to a texture on the iPhone and takes taps through `IWindowInput`.
    2. Measured web-view-to-texture frame rate on the device is recorded in Doc/.
- [ ] **T12** Toolchain on the phone: HoloKit samples and our build run on the iPhone
  - plan: [Milestones](plan.md#milestones), row M0 · size: S · tag: needs-mac · depends-on: T06
  - accept:
    1. HoloKit stereo and hand-tracking samples run on the iPhone.
    2. Our DeviceMain build installs and shows the launcher.
- [ ] **T13** Stereo calibrated to the lenses and helmet
  - plan: [Milestones](plan.md#milestones), row M1 · size: L · tag: needs-mac · depends-on: T12
  - accept:
    1. A flat grid looks straight and fused with no double vision.
    2. Steady 60 fps for 10 minutes.
- [ ] **T14** World-anchored windows on the device
  - plan: [Milestones](plan.md#milestones), row M2 · size: S · tag: needs-mac · depends-on: T12
  - accept:
    1. Three windows (web, video, image) stay fixed while walking and turning.
- [ ] **T15** Hand input on the device
  - plan: [Milestones](plan.md#milestones), row M3 · size: M · tag: needs-mac · depends-on: T09, T12
  - accept:
    1. 45 of 50 pinch-grab trials succeed.
    2. Windows do not jitter at rest.
- [ ] **T16** Passthrough in both eyes with windows composited over it
  - plan: [Milestones](plan.md#milestones), row M4 · size: L · tag: needs-mac · depends-on: T13
  - accept:
    1. Windows stay locked to a real desk.
    2. A keyboard is readable through the view.
- [ ] **T17** Thermal soak tests
  - plan: [Milestones](plan.md#milestones), paragraph after the table · size: S · tag: needs-mac · depends-on: T15, T16
  - accept:
    1. 20-minute soak at the end of M3 and of M4, with throttling and frame rate recorded in Doc/.
- [ ] **T18** Mode switch on the device
  - plan: [Milestones](plan.md#milestones), row M5 · size: S · tag: needs-mac · depends-on: T10, T16
  - accept:
    1. Passthrough to VR and back in under 1 second without losing window positions.
- [ ] **T19** Stream latency measured in the editor
  - plan: [Milestones](plan.md#milestones), row M6 · size: S · tag: human-supervised · depends-on: T05
  - accept:
    1. Glass-to-glass lag of a laptop window (clock page) measured in the editor and recorded in Doc/.
- [ ] **T20** Streamed laptop window on the device
  - plan: [Milestones](plan.md#milestones), row M6 · size: M · tag: needs-mac · depends-on: T12, T19
  - accept:
    1. 1080p at 30 fps over 5 GHz Wi-Fi, with lag measured and typing usable.
