# Third-party notices

Spatial Launcher's own code is under the MIT License (see `LICENSE`). It depends on the components below. Keep this
file up to date when adding, removing or upgrading a dependency.

**No GPL code is used in the Unity project.** GPL software (FFmpeg) is used only by the laptop agent in
`Tools/LaptopAgent`, as a separate program that is downloaded on demand and never committed or built into the app.

## Unity project (`Packages/manifest.json`)

| Package | Version | License | Notes |
|---|---|---|---|
| HoloKit Unity SDK (`io.holokit.unity-sdk`) | 0.6.0 (git) | MIT; some files MIT AND Unity Companion License | Stereo rendering, low-latency tracking, hand tracking |
| WebRTC (`com.unity.webrtc`) | 3.0.0 | Unity Companion License | Laptop window video. **Deprecated by Unity** (its repository says so); 3.0.0 is the final release. Bundles the native components listed below. |
| MCP Unity (`com.gamelovers.mcp-unity`) | git | MIT | Editor-only tooling |
| AR Foundation, ARKit, ARCore, XR Interaction Toolkit, XR Management, XR Core Utils, XR Legacy Input Helpers | see manifest | Unity Companion License | |
| Universal Render Pipeline, Render Pipelines Core, Shader Graph, Light Transport | 17.0.x | Unity Companion License | |
| Input System, uGUI (includes TextMesh Pro), Burst, Collections, Mathematics, Editor Coroutines, Settings Manager, Searcher | see `Packages/packages-lock.json` | Unity Companion License | |
| Newtonsoft.Json (`com.unity.nuget.newtonsoft-json`) | 3.2.2 | MIT (Newtonsoft.Json); package by Unity | |
| Mono.Cecil (`com.unity.nuget.mono-cecil`) | 1.11.6 | MIT (Mono.Cecil); package by Unity | Editor only |
| Test Framework, Performance Testing, Code Coverage, Profile Analyzer, Rider/Visual Studio integration, Android Logcat, Multiplayer Center, Learn IET Framework | see lock file | Unity Companion License; Code Coverage includes ReportGenerator (Apache-2.0) | Editor only |
| Unity built-in modules (`com.unity.modules.*`) | 1.0.0 | Unity Terms of Service | |

### Native components bundled in `com.unity.webrtc` 3.0.0

From the package's `Third Party Notices.md` (full texts there, in `Library/PackageCache/com.unity.webrtc@*/`):

| Component | License |
|---|---|
| WebRTC (libwebrtc M116) | BSD-3-Clause |
| Abseil | Apache-2.0 |
| BoringSSL | Apache-2.0 / OpenSSL / ISC |
| base64, fft, fft4g, sigslot, spl_sqrt_floor | permissive custom licenses |
| g711, g722 | public domain |
| fiat, jsoncpp, Implib.so, glad | MIT |
| googletest, libyuv, opus, protobuf | BSD |
| libsrtp, rnnoise | BSD-3-Clause |
| nasm | BSD-2-Clause |
| libjpeg-turbo, libvpx, zlib | permissive custom licenses (IJG/BSD-style, BSD-style, zlib) |
| NVIDIA Video Codec SDK headers (nvencode, nvdecode) | NVIDIA custom license |

### Assets

| Asset | License |
|---|---|
| Liberation Sans font (`Assets/TextMesh Pro/Fonts`, from TextMesh Pro) | SIL Open Font License 1.1 (`LiberationSans - OFL.txt`) |
| Sample image and video (`Assets/Samples`) | Generated with FFmpeg's built-in test patterns (mandelbrot, gradients, testsrc2); no third-party content |

## Laptop agent (`Tools/LaptopAgent`, not part of the Unity project)

| Component | Version | License | How it's used |
|---|---|---|---|
| MediaMTX | v1.21.2 | MIT | Downloaded by `setup.py` into `bin/` (git-ignored); run as a separate program |
| FFmpeg, essentials build by gyan.dev | 9.0.2 | GPLv3 (includes x264 and other GPL components) | Downloaded by `setup.py` into `bin/` (git-ignored); run as a separate program, not linked |
| websockets | 16.0 | BSD-3-Clause | Python package installed by `setup.py` |
| Python standard library (incl. tkinter) | 3.10+ | PSF License | Runtime |

`setup.py` pins each download to a version and SHA-256 checksum. If you redistribute the downloaded `bin/` folder,
include `bin/LICENSE-ffmpeg.txt` and a pointer to FFmpeg's source, as the GPL requires.
