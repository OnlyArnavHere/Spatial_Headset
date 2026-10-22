# Third-party notices

Spatial Launcher's own code is under the MIT License (see `LICENSE`). It depends on the components below. Keep this
file up to date when adding, removing or upgrading a dependency.

**No GPL code is used in the Unity project.** GPL software (FFmpeg) is used only by the laptop agent in
`Tools/LaptopAgent`, as a separate program that is downloaded on demand and never committed or built into the Unity headset app. One exception: the packaged laptop agent bundles PyAV, whose PyPI wheels contain FFmpeg libraries with GPL x264/x265 (see the laptop agent table below). Those builds are published as pre-releases until the LGPL-only PyAV from the `agent-deps` workflow is installed.

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
| Inter Regular, Medium, SemiBold (`Assets/Fonts/Inter`), v4.1, https://github.com/rsms/inter/releases/tag/v4.1 | SIL Open Font License 1.1 (`Assets/Fonts/Inter/OFL.txt`) |
| Material Icons Round (`Assets/Fonts/MaterialIcons`), https://github.com/google/material-design-icons (`font/MaterialIconsRound-Regular.otf`, master at download, Oct 2026) | Apache License 2.0 (`Assets/Fonts/MaterialIcons/LICENSE.txt`) |
| Sample image and video (`Assets/Samples`) | Generated with FFmpeg's built-in test patterns (mandelbrot, gradients, testsrc2); no third-party content |

## Laptop agent (`Tools/LaptopAgent`, not part of the Unity project)

| Component | Version | License | How it's used |
|---|---|---|---|
| MediaMTX | v1.21.2 | MIT | Downloaded by `setup.py` into `bin/` (git-ignored); run as a separate program |
| FFmpeg, essentials build by gyan.dev | 9.0.2 | GPLv3 (includes x264 and other GPL components) | Downloaded by `setup.py` into `bin/` (git-ignored); run as a separate program, not linked |
| FFmpeg LGPL builds: Windows by BtbN (9.0.2); macOS the static LGPL 8.1.3 from the `agent-deps` release | 9.0.2 / 8.1.3 | LGPL-2.1-or-later (no x264) | Downloaded on first run by the packaged app, SHA-256 verified, into `%LOCALAPPDATA%\SpatialLauncher\agent\bin` (macOS: `~/Library/Application Support/SpatialLauncher/agent/bin`); run as a separate program, not bundled. On macOS this applies once the `agent-deps` ffmpeg SHA-256 is filled in `tools_fetch.py`; until then the system `ffmpeg` on PATH is used |
| MediaMTX (packaged app) | v1.21.2 | MIT | Downloaded on first run into the same `bin` folder; not bundled |
| PyAV (`av`) | 17.1.0 | BSD-3-Clause; its PyPI wheels bundle FFmpeg libraries including GPL x264/x265 | Bundled in packaged builds, so those builds are published as pre-releases until an LGPL-only build replaces it |
| LGPL-only FFmpeg 8.1 and PyAV wheels (`agent-deps` workflow) | 8.1.3 / 17.1.0 | LGPL-2.1-or-later / BSD-3-Clause | Built from the FFmpeg source tarball with `--disable-everything` and without `--enable-gpl`/`--enable-nonfree`; published with the source, configure lines and SHA256SUMS. The workflow is untested, so packaged builds still use the PyPI wheels. delvewheel (MIT) and delocate (BSD-2-Clause) are build tools only |
| PyInstaller | 6.22+ | GPL-2.0-or-later with a bootloader exception | Build tool. The exception allows its bootloader and runtime in the packaged app, which is not itself GPL |
| Python standard library (incl. tkinter) | 3.10+ | PSF License | Runtime |

Each GitHub release of the packaged agent includes `THIRD_PARTY_LICENSES-win.txt` / `THIRD_PARTY_LICENSES-mac.txt`, generated from the bundled Python packages' licence files (it also lists the native components).

`setup.py` pins each download to a version and SHA-256 checksum. If you redistribute the downloaded `bin/` folder,
include `bin/LICENSE-ffmpeg.txt` and a pointer to FFmpeg's source, as the GPL requires.
