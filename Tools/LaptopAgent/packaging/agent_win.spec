# PyInstaller spec for the Windows build (onedir, windowed). Run from anywhere: pyinstaller packaging/agent_win.spec
import os
from PyInstaller.utils.hooks import collect_all, collect_submodules

HERE = os.path.abspath(os.path.join(SPECPATH, ".."))  # Tools/LaptopAgent

datas, binaries, hiddenimports = [], [], []
for pkg in ("av", "aiortc", "cryptography", "pylibsrtp"):
    d, b, h = collect_all(pkg)
    datas += d
    binaries += b
    hiddenimports += h
# Modules the app imports lazily or by platform.
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
    # zeroconf is LGPL: ship it as readable .py rather than compiled into the PYZ archive.
    module_collection_mode={"zeroconf": "py"},
    noarchive=False,
)
pyz = PYZ(a.pure)
exe = EXE(pyz, a.scripts, [], exclude_binaries=True, name="SpatialLauncherAgent", console=False)
coll = COLLECT(exe, a.binaries, a.datas, name="SpatialLauncherAgent")
