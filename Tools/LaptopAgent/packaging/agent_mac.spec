# PyInstaller spec for the macOS build (onedir .app bundle, arm64). UNTESTED: written on Windows.
import os
import sys
from PyInstaller.utils.hooks import collect_all, collect_submodules

HERE = os.path.abspath(os.path.join(SPECPATH, ".."))  # Tools/LaptopAgent
sys.path.insert(0, HERE)
import handshake  # SERVICE_TYPE for NSBonjourServices

datas, binaries, hiddenimports = [], [], []
for pkg in ("av", "aiortc", "cryptography", "pylibsrtp", "keyring"):
    d, b, h = collect_all(pkg)
    datas += d
    binaries += b
    hiddenimports += h
hiddenimports += collect_submodules("keyring.backends")
# pyobjc frameworks that hostos/macos.py and hostos/_macos_perms.py import lazily inside functions.
hiddenimports += ["objc", "Quartz", "AppKit", "Foundation", "ApplicationServices", "CoreFoundation"]
hiddenimports += collect_submodules("hostos") + collect_submodules("gui")

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
          target_arch="arm64")
coll = COLLECT(exe, a.binaries, a.datas, name="SpatialLauncherAgent")
app = BUNDLE(
    coll,
    name="SpatialLauncherAgent.app",
    bundle_identifier="com.spatiallauncher.agent",
    info_plist={
        "CFBundleName": "Spatial Launcher Agent",
        "NSHighResolutionCapable": True,
        "LSMinimumSystemVersion": "12.0",
        "NSLocalNetworkUsageDescription": "Spatial Launcher Agent connects to your headset on the local network.",
        "NSBonjourServices": [handshake.SERVICE_TYPE.rstrip(".").removesuffix(".local")],
    },
)
