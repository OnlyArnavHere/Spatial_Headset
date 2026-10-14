# iPhone Spatial Headset: Feasibility and Build Plan

Oct 14, 2026 · @Arnav

You can build a working iPhone headset with stereo rendering, 6DoF tracking, floating windows, hand input and camera passthrough, largely on open-source parts. You cannot run native iPhone apps in independent windows, and DRM video such as Netflix will not appear in any captured window.

## What already exists

HoloKit is the closest match: an open-source, iPhone-based headset with a [Unity SDK](https://github.com/holokit/holokit-unity-sdk) (MIT licence, 38 stars, so a small community). It covers three things you would otherwise build yourself.

- **Stereo rendering:** two eye views on the phone screen, built on AR Foundation 6 and ARKit for 6DoF tracking.
- **Hand tracking:** 21 joints per hand from Apple's Vision framework, lifted to 3D with LiDAR depth. It needs an iPhone with LiDAR (a Pro model) and iOS 18 or later.
- **Gesture recognition:** only two states today, none and pinched. Grab, resize and point are yours to write from the joint positions.

No project I found does your full goal: multiple independent app windows plus Vision Pro-style gestures on a phone. HoloKit is a toolkit, not a launcher. Two caveats from its docs: LiDAR hand tracking makes the phone overheat quickly, and its stereo mode draws on a black background, so passthrough in both eyes is something you add.

## What is and is not possible

Unity gives you the 3D space and nothing more: it cannot pull other iOS apps into it. The hard limits come from iOS, not from the engine.

| Goal | Verdict | Why and what to do instead |
| --- | --- | --- |
| Native iPhone apps as floating windows, on the same phone | Not possible | iPhone shows one foreground app, and there is no API to embed another app's UI. System-wide capture exists only through a ReplayKit broadcast extension that the user starts from a system picker, and [capture runs outside your app's process](https://support.apple.com/guide/security/seca5fc039dd/web). While your headset app is in front, it would capture itself. |
| Apps from a second iPhone via iPhone Mirroring on a Mac | Blocked | [Recording the mirrored display is blocked](https://jonathansblog.co.uk/?p=27416), and the window is phone-sized. |
| Desktop windows from a PC, streamed into the headset | Possible with real engineering | Run open-source Sunshine on a Windows or Linux PC and decode the stream in the headset app, with each virtual window showing a crop of one or more streamed displays. Needs an extra computer and a good Wi-Fi link. |
| Web pages and normal (non-DRM) video | Possible | Render web views and video players to textures inside your own app. Prototype the web-view-to-texture speed early; I have not verified it on current iOS. |
| Netflix and other DRM video | Not possible | [DRM-protected frames come out blank in captured output](https://levelup.gitconnected.com/how-netflix-and-others-lock-down-their-content-like-fort-knox-but-still-e1f184e5d562), and native apps cannot be embedded. Plan for YouTube-style and local video instead. |
| Hand tracking and pinch select | Possible | Needs a LiDAR iPhone on iOS 18+. HoloKit warns that [LiDAR use is power-hungry and heats the phone](https://docs.holokit.io/creators/unity-sdk/hand-tracking); track one hand when you can. |
| Camera passthrough | Possible, mono only | The rear camera feeds one image to both eyes through AR Foundation. There is no true depth parallax, and the camera sits off-axis from your eyes. This is my reading of the design, so test it. |
| Immersive environments | Easy | A Unity scene or skybox swapped in for the passthrough background. |
| Unlimited windows | Capped by heat | Budget a few live video windows at once and measure; static windows are cheap. |

Your assumption that Unity unlocks all your iPhone apps does not hold. The experience you can reach is a spatial launcher for web, video and streamed desktop content, not a general iOS window manager.

## Recommended architecture

&#91;embedded content: system architecture · one phone app, one optional PC\]

Tracking and hand input feed a window manager you write on top of the SDK. Windows show content your app renders itself, and a streamed PC desktop joins later as one more source. The stereo renderer draws everything to the phone screen, with the camera feed behind it for passthrough.

## Open-source tools worth integrating

Yes, reuse helps: the HoloKit SDK alone saves weeks of stereo, tracking and hand-input work. Choose the rest by need.

| Tool | Use it? | Role and caution |
| --- | --- | --- |
| [HoloKit Unity SDK](https://github.com/holokit/holokit-unity-sdk) | Yes, as the base | Stereo, ARKit tracking, 21-joint hands, pinch. Built for HoloKit's own optics, so check how to set a custom lens and phone profile; its repo has a phone calibration guide aimed at Android. Maintenance looks light. |
| [Sunshine](https://github.com/LizardByte/Sunshine) and [Moonlight](https://moonlight-stream.org) | Later, for streamed desktops | Self-hosted low-latency streaming with an iOS client. Sunshine is GPL-3.0, so check licence fit before linking its code into your app. |
| [MCP Unity (CoderGamester)](https://github.com/CoderGamester/mcp-unity) or [MCP for Unity (Coplay)](https://github.com/CoplayDev/unity-mcp) | Yes, pick one | Lets Claude drive the Unity Editor: scenes, scripts, console, tests. Editor only, so it cannot test on the phone. Avoid GameBooom's server unless GPLv3 suits you. |
| [XcodeBuildMCP](https://github.com/cameroncooke/XcodeBuildMCP) | Yes | Lets Claude build and install the exported Xcode project on your iPhone and read logs. Needs Xcode 16 or later. |
| MediaPipe hand tracking in Unity | Only as a fallback | Works without LiDAR, but the examples I found were Android-only or paid; verify iOS support first. |
| Project North Star, ALVR | Skip | North Star is a hardware design, and ALVR streams PC VR to other headsets. Neither fits an iPhone-in-a-helmet build. |

## Workflow: VS Code plus Unity MCP

This is not a choice between the two. VS Code with Claude Code edits your C# and Swift files; Unity MCP lets the same agent operate the Unity Editor; XcodeBuildMCP closes the loop on a real iPhone. You need a Mac, because iOS builds go through Xcode.

1. Install Unity Hub, Unity 6 LTS with iOS Build Support, and Xcode 16 or later. Sign in to Xcode with an Apple ID so you can run on your own phone.
2. Create a 3D URP project and switch the build target to iOS. Set the scripting backend to IL2CPP, architecture to ARM64, and add a camera usage description.
3. In Package Manager, add the git URL `https://github.com/holokit/holokit-unity-sdk.git`, then enable Apple ARKit under XR Plug-in Management.
4. Add MCP Unity from `https://github.com/CoderGamester/mcp-unity.git`. It needs Node.js 18 or later, and the project path must contain no spaces. Start the server from Tools, MCP Unity, Server Window, and copy the client config shown there.
5. Open the project folder in VS Code with the Claude Code extension, and connect XcodeBuildMCP with `claude mcp add XcodeBuildMCP -- npx -y xcodebuildmcp@latest mcp`.
6. Write a short `CLAUDE.md` at the project root with the goal, the verdict table above, and the milestone you are on, so every session starts with the same constraints.
7. Build the HoloKit stereo and hand-tracking samples to your phone before writing any of your own code.

The fastest loop: ask Claude to change a script, let MCP Unity recompile and read the console, then build to the phone only when the editor test passes. Device builds are the slow step, so batch changes between them.

## Milestones

Each milestone ends with a test you can run on the phone. Do not start the next one until the test passes. Time estimates assume part-time work and a LiDAR iPhone on iOS 18.

| # | Outcome | Pass test | Est. time |
| --- | --- | --- | --- |
| M0 | Toolchain works: Unity, HoloKit samples and MCP on your phone | HoloKit stereo and hand-tracking samples run on the iPhone; Claude edits a script and the Editor recompiles | 2 to 3 days |
| M1 | Stereo calibrated to your lenses and helmet | A flat grid looks straight and fused with no double vision; steady 60 fps for 10 minutes | 1 to 2 weeks |
| M2 | Floating windows, with a gaze cursor as stand-in input | Three world-anchored windows (web view, video, image) stay fixed as you walk and turn | 1 to 2 weeks |
| M3 | Hand input: point, pinch-select, grab-move, two-hand resize | 45 of 50 pinch-grab trials succeed; windows do not jitter at rest (add smoothing) | 2 to 3 weeks |
| M4 | Passthrough in both eyes with windows composited over it | Windows stay locked to a real desk; you can read a keyboard through the view | 1 to 2 weeks |
| M5 | Immersive environments and a mode switch | Toggle passthrough to full VR in under 1 second without losing window positions | 1 week |
| M6 | One streamed PC desktop as a window | 1080p at 30 fps from a PC over Wi-Fi 5 GHz; measure lag and keep it usable for typing | 3 to 4 weeks |

Thermal limits are the main unknown: run a 20-minute soak test at the end of M3 and M4. If the phone throttles, reduce hand tracking to one hand and lower the window count before changing anything else.

## Questions that change the plan

- [ ] Which iPhone model do you have? Hand tracking needs LiDAR (a Pro model), and iOS 18 or later.
- [ ] Do you have a Mac? Unity builds for iOS need Xcode.
- [ ] Do you have a Windows or Linux PC with a hardware video encoder for M6?
- [ ] Is the headset lens-based video see-through, or HoloKit-style optical see-through? This decides the calibration work in M1.
- [ ] Which apps matter most? If Netflix is a must-have, a spatial launcher cannot deliver it.

## Sources

Opened or searched on 14 October 2026. Hand-tracking and DRM claims rest on these pages; time estimates and the passthrough and calibration notes are my own judgement.

- [HoloKit Unity SDK (GitHub)](https://github.com/holokit/holokit-unity-sdk)
- [HoloKit hand tracking docs](https://docs.holokit.io/creators/unity-sdk/hand-tracking)
- [Apple: ReplayKit security](https://support.apple.com/guide/security/seca5fc039dd/web)
- [Apple: VNDetectHumanHandPoseRequest](https://developer.apple.com/documentation/vision/vndetecthumanhandposerequest)
- [ReplayKit screen sharing and broadcast extensions](https://www.forasoft.com/blog/article/how-to-implement-screen-sharing-in-ios-1193)
- [DRM and screen capture explainer](https://levelup.gitconnected.com/how-netflix-and-others-lock-down-their-content-like-fort-knox-but-still-e1f184e5d562)
- [iPhone Mirroring limits](https://jonathansblog.co.uk/?p=27416)
- [Sunshine and Moonlight overview](https://mustafa.net/2026/02/22/moonlight-sunshine-free-open-source-remote-gaming-stream-2026/)
- [MCP Unity (CoderGamester)](https://github.com/codergamester/mcp-unity)
- [MCP for Unity (Coplay)](https://github.com/CoplayDev/unity-mcp)
- [GameBooom Unity MCP](https://github.com/GameBooom/unity-mcp)
- [XcodeBuildMCP guide](https://mcp.directory/blog/xcodebuildmcp-complete-guide-2026)
