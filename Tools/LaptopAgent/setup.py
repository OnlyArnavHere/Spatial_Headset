"""Downloads the agent's third-party programs into bin/ (git-ignored) and installs its Python dependency.

Versions and SHA-256 checksums are pinned below; a download that doesn't match is rejected. To update,
change the version, URL and checksum together (both projects publish checksums with each release).
"""
import hashlib
import subprocess
import sys
import urllib.request
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
BIN = HERE / "bin"
DOWNLOADS = HERE / ".runtime" / "downloads"

PACKAGES = [
    {
        "name": "MediaMTX v1.21.2 (MIT)",
        "url": "https://github.com/bluenviron/mediamtx/releases/download/v1.21.2/mediamtx_v1.21.2_windows_amd64.zip",
        "sha256": "a84224f5a490855684e62ba6fb71e80ea2a5e16aed61883bb58380290e93e467",
        # archive member -> file in bin/
        "extract": {"mediamtx.exe": "mediamtx.exe", "LICENSE": "LICENSE-mediamtx.txt"},
    },
    {
        "name": "FFmpeg 9.0.2 essentials build by gyan.dev (GPLv3)",
        "url": "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip",
        "sha256": "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba",
        "extract": {"ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe": "ffmpeg.exe",
                    "ffmpeg-9.0.2-essentials_build/LICENSE": "LICENSE-ffmpeg.txt",
                    "ffmpeg-9.0.2-essentials_build/README.txt": "README-ffmpeg-build.txt"},
    },
]


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def download(package: dict) -> Path:
    DOWNLOADS.mkdir(parents=True, exist_ok=True)
    archive = DOWNLOADS / package["url"].rsplit("/", 1)[1]
    if archive.exists() and sha256(archive) == package["sha256"]:
        print(f"  using cached {archive.name}")
        return archive
    print(f"  downloading {package['url']}")
    urllib.request.urlretrieve(package["url"], archive)
    actual = sha256(archive)
    if actual != package["sha256"]:
        archive.unlink()
        raise SystemExit(f"Checksum mismatch for {archive.name}:\n  expected {package['sha256']}\n  got      {actual}\n"
                         "Not installing it. Check the release page before changing the pinned checksum.")
    return archive


def extract(package: dict, archive: Path) -> None:
    BIN.mkdir(exist_ok=True)
    with zipfile.ZipFile(archive) as zf:
        names = set(zf.namelist())
        for member, target in package["extract"].items():
            if member not in names:
                raise SystemExit(f"{member} is missing from {archive.name}; the archive layout changed.")
            (BIN / target).write_bytes(zf.read(member))
            print(f"  installed bin/{target}")


def mac_tools() -> None:
    """macOS: nothing is downloaded; ffmpeg and MediaMTX come from Homebrew."""
    import shutil
    missing = [name for name in ("ffmpeg", "mediamtx") if not shutil.which(name)]
    if missing:
        raise SystemExit(f"Missing: {', '.join(missing)}. Install them with Homebrew, then run setup.py again:"
                         "\n  brew install ffmpeg mediamtx")
    print("ffmpeg and mediamtx found on PATH (Homebrew)")


def main() -> None:
    if sys.platform == "darwin":
        mac_tools()
    for package in ([] if sys.platform == "darwin" else PACKAGES):
        print(package["name"])
        extract(package, download(package))
    print("Python dependencies")
    subprocess.check_call([sys.executable, "-m", "pip", "install", "-r", str(HERE / "requirements.txt")])
    print("\nDone. Next: python agent.py --list")


if __name__ == "__main__":
    main()
