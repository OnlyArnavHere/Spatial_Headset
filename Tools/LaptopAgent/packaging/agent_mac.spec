# PyInstaller spec for the macOS build (onedir .app bundle, arm64). UNTESTED: written on Windows.
import os
import sys
from PyInstaller.utils.hooks import collect_all, collect_submodules

HERE = os.path.abspath(os.path.join(SPECPATH, ".."))  # Tools/LaptopAgent
sys.path.insert(0, HERE)
import handshake  # SERVICE_TYPE for NSBonjourServices
import re
VERSION = re.search(r'^VERSION = "([^"]+)"', open(os.path.join(HERE, "app.py"), encoding="utf-8").read(), re.M).group(1)

datas, binaries, hiddenimports = [], [], []
for pkg in ("av", "aiortc", "cryptography", "pylibsrtp", "keyring"):
    d, b, h = collect_all(pkg)
    datas += d
    binaries += b
    hiddenimports += h
hiddenimports += collect_submodules("keyring.backends")
# pyobjc frameworks that hostos/macos.py and hostos/_macos_perms.py import lazily inside functions.
hiddenimports += ["objc", "Quartz", "AppKit", "Foundation", "ApplicationServices", "CoreFoundation"]
# The app window: pywebview (with its own JS files) and the page in gui/web.
d, b, h = collect_all("webview")
datas += d
binaries += b
hiddenimports += h + ["WebKit", "Security", "UniformTypeIdentifiers"]
datas.append((os.path.join(HERE, "gui", "web"), os.path.join("gui", "web")))
hiddenimports += collect_submodules("hostos") + collect_submodules("gui") + ["usbmux", "stream_link", "qr_image", "segno"]

licenses = os.path.join(HERE, "THIRD_PARTY_LICENSES.txt")
if os.path.exists(licenses):
    datas.append((licenses, "."))

a = Analysis(
    [os.path.join(HERE, "app.py")],
    pathex=[HERE],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    module_collection_mode={"zeroconf": "py"},
    noarchive=False,
)
pyz = PYZ(a.pure)
exe = EXE(pyz, a.scripts, [], exclude_binaries=True, name="SpatialLauncherAgent", console=False,
          target_arch="arm64", icon=os.path.join(HERE, "packaging", "icon.icns"))
coll = COLLECT(exe, a.binaries, a.datas, name="SpatialLauncherAgent")
app = BUNDLE(
    coll,
    name="SpatialLauncherAgent.app",
    bundle_identifier="com.spatiallauncher.agent",
    icon=os.path.join(HERE, "packaging", "icon.icns"),
    info_plist={
        "CFBundleName": "Spatial Launcher Agent",
        "CFBundleDisplayName": "Spatial Launcher Agent",
        "CFBundleShortVersionString": VERSION,
        "CFBundleVersion": VERSION,
        "NSHighResolutionCapable": True,
        "LSMinimumSystemVersion": "12.0",
        "NSLocalNetworkUsageDescription": "Spatial Launcher Agent connects to your headset on the local network.",
        "NSBonjourServices": [handshake.SERVICE_TYPE.rstrip(".").removesuffix(".local")],
    },
)
