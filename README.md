# Spatial Launcher

> Early prototype: expect rough edges and breaking changes.

Spatial Launcher turns an iPhone into a Vision Pro-style spatial computing headset. The phone sits in a head-mounted viewer and places floating windows in the space around you, which you move, resize and focus with your hands, while camera passthrough keeps the real world in view.

**Built on:** [HoloKit Unity SDK](https://github.com/holokit/holokit-unity-sdk) (MIT), with Unity 6, AR Foundation and ARKit.

## Status

The window manager works in the Unity editor: spawning, focus, drag, resize and close, with mouse input standing in for hands. Device testing on iPhone is next.

### WebRTC in the editor

With the iOS build target active, `com.unity.webrtc` 3.0.0 throws `EntryPointNotFoundException: RegisterDebugLog` in the editor, because `Runtime/Scripts/WebRTC.cs` line 732 uses `#if UNITY_IOS` where it needs `#if UNITY_IOS && !UNITY_EDITOR`. To fix it locally, copy `Library/PackageCache/com.unity.webrtc@*` to `Packages/com.unity.webrtc` and make that one-line change. The embedded copy is git-ignored.

With the patched copy in place, `Packages/packages-lock.json` shows as modified: its `com.unity.webrtc` entry points at the embedded copy (`file:com.unity.webrtc`). Leave that change uncommitted. A clone without the patched copy resolves the registry version from `Packages/manifest.json` instead.

## License

MIT. See [LICENSE](LICENSE).
