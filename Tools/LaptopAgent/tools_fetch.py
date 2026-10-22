"""First-run downloader for the programs the agent shells out to (ffmpeg, MediaMTX). Frozen builds ship without
them; ensure() fetches pinned archives, verifies the SHA-256 before anything is extracted, and installs only the
listed members into paths.tools_dir(). tools.json records what is installed so a second run does nothing.

To update a pin change version, url and sha256 together (BtbN and MediaMTX both publish checksums per release)."""
import hashlib
import inspect
import json
import logging
import os
import platform
import stat
import sys
import tarfile
import tempfile
import urllib.error
import urllib.request
import zipfile
from pathlib import Path, PurePosixPath
from urllib.request import urlopen

import paths

log = logging.getLogger("tools_fetch")
CHUNK = 1 << 20
RECORD = "tools.json"
TIMEOUT_S = 30  # per socket operation: a stalled download fails instead of hanging the first start

_BTBN = "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-09-30-13-08/"
_FFMPEG_DIR = "ffmpeg-n9.0.2-17-g2a571b6068-win64-lgpl-9.0/"
_MTX = "https://github.com/bluenviron/mediamtx/releases/download/v1.21.2/"

# Per platform and tool: version, url, sha256 ("" = not yet available), extract {archive member: file in tools_dir()}
# and binary (the target that ensure() returns). LGPL FFmpeg build from BtbN (FFmpeg 9.0.2, has gfxcapture).
MANIFEST: dict[str, dict[str, dict]] = {
    "win32-amd64": {
        "ffmpeg": {
            "version": "n9.0.2-17-g2a571b6068 (BtbN autobuild-2026-09-30-13-08, LGPL)",
            "url": _BTBN + "ffmpeg-n9.0.2-17-g2a571b6068-win64-lgpl-9.0.zip",
            "sha256": "6b264b9e6019103f601d98c292bd332fd87acf1c5e941ddff4fb71760fe63432",
            "extract": {_FFMPEG_DIR + "bin/ffmpeg.exe": "ffmpeg.exe",
                        _FFMPEG_DIR + "LICENSE.txt": "LICENSE-ffmpeg.txt"},
            "binary": "ffmpeg.exe",
        },
        "mediamtx": {
            "version": "v1.21.2",
            "url": _MTX + "mediamtx_v1.21.2_windows_amd64.zip",
            "sha256": "a84224f5a490855684e62ba6fb71e80ea2a5e16aed61883bb58380290e93e467",
            "extract": {"mediamtx.exe": "mediamtx.exe", "LICENSE": "LICENSE-mediamtx.txt"},
            "binary": "mediamtx.exe",
        },
    },
    "darwin-arm64": {
        "ffmpeg": {
            "version": "n8.1.3 (agent-deps, LGPL, static)",
            # Change the tag (agent-deps-v1) if the deps release uses another one.
            "url": "https://github.com/OnlyArnavHere/Spatial_Headset/releases/download/agent-deps-v1/"
                   "ffmpeg-8.1.3-macos-arm64-lgpl.tar.gz",
            # Fill this from that release's SHA256SUMS. Empty means "not yet available": ensure() raises
            # ToolUnavailable before any network call, and the release workflow stays a pre-release.
            "sha256": "",
            "extract": {"ffmpeg": "ffmpeg", "LICENSE.txt": "LICENSE-ffmpeg.txt"},
            "binary": "ffmpeg",
        },
        "mediamtx": {
            "version": "v1.21.2",
            "url": _MTX + "mediamtx_v1.21.2_darwin_arm64.tar.gz",
            "sha256": "7d0d0c5c11527cbd10bde1c3d728864eb0334a9b653566cd73c0259750cbf093",
            "extract": {"mediamtx": "mediamtx", "LICENSE": "LICENSE-mediamtx.txt"},
            "binary": "mediamtx",
        },
    },
}


class ToolUnavailable(Exception):
    """The tool can't be fetched (no pin for this platform, or the pin is a placeholder)."""


class ToolFetchError(Exception):
    """Download, checksum or archive problem; nothing was installed."""


def platform_key() -> str:
    machine = platform.machine().lower()
    machine = {"x86_64": "amd64", "aarch64": "arm64"}.get(machine, machine)
    return f"{sys.platform}-{machine}"


def _entries() -> dict[str, dict]:
    key = platform_key()
    if key not in MANIFEST:
        raise ToolUnavailable(f"No downloadable tools for {key}; install ffmpeg and mediamtx yourself.")
    return MANIFEST[key]


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with Path(path).open("rb") as f:
        for chunk in iter(lambda: f.read(CHUNK), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _read_record(directory: Path) -> dict:
    try:
        data = json.loads((directory / RECORD).read_text(encoding="utf-8"))
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return {}


def _write_record(directory: Path, record: dict) -> None:
    tmp = directory / (RECORD + ".tmp")
    tmp.write_text(json.dumps(record, indent=2, sort_keys=True), encoding="utf-8")
    os.replace(tmp, directory / RECORD)


def _installed(entry: dict, record: dict, name: str, directory: Path) -> bool:
    """True if the recorded install matches this pin and every installed file still has its recorded size and hash
    (a half-written, replaced or damaged file means a reinstall)."""
    rec = record.get(name) or {}
    files = rec.get("files")
    if not (entry["sha256"] and rec.get("sha256") == entry["sha256"] and rec.get("version") == entry["version"]
            and isinstance(files, dict)):
        return False
    for target in entry["extract"].values():
        info, path = files.get(target), directory / target
        try:
            if (not isinstance(info, dict) or path.stat().st_size != info.get("size")
                    or sha256_of(path) != info.get("sha256")):
                return False
        except OSError:
            return False
    return True


class _HttpsOnly(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        if not newurl.lower().startswith("https://"):
            raise urllib.error.URLError(f"Refusing a redirect to a non-HTTPS address: {newurl}")
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def _default_opener():
    return urllib.request.build_opener(_HttpsOnly).open


def _call_opener(opener, request):
    """opener(request, timeout=...) when the opener takes a timeout; plain opener(request) for simple fakes."""
    try:
        takes_timeout = "timeout" in inspect.signature(opener).parameters
    except (TypeError, ValueError):
        takes_timeout = False
    return opener(request, timeout=TIMEOUT_S) if takes_timeout else opener(request)


def status(names=None) -> dict[str, dict]:
    """{name: {"installed", "available", "version", "path"}} for this platform (path is None until installed)."""
    directory = paths.tools_dir()
    record = _read_record(directory)
    entries = _entries()
    out = {}
    for name in (names or entries):
        entry = entries[name]
        done = _installed(entry, record, name, directory)
        out[name] = {"installed": done, "available": bool(entry["sha256"]), "version": entry["version"],
                     "path": str(directory / entry["binary"]) if done else None}
    return out


def _open_verified(archive: Path, sha256: str):
    """Opens the archive once, hashes it and rewinds: the file object that was checked is the one that is unpacked,
    so the file can't be swapped in between. None (file closed) if the hash is wrong."""
    f = archive.open("rb")
    digest = hashlib.sha256()
    for chunk in iter(lambda: f.read(CHUNK), b""):
        digest.update(chunk)
    if digest.hexdigest() != sha256:
        f.close()
        return None
    f.seek(0)
    return f


def _download(name: str, entry: dict, opener, progress):
    """Returns the cached or freshly downloaded archive as a verified open file object."""
    if not entry["url"].lower().startswith("https://"):
        raise ToolFetchError(f"Refusing to download {name} over a non-HTTPS address: {entry['url']}")
    folder = paths.data_dir() / "downloads"
    folder.mkdir(parents=True, exist_ok=True)
    for stale in folder.glob("*.part"):  # a previous run that was killed mid-download never got to clean up
        stale.unlink(missing_ok=True)
    archive = folder / entry["url"].rsplit("/", 1)[1]
    if archive.is_file():
        cached = _open_verified(archive, entry["sha256"])
        if cached is not None:
            log.info("Using the downloaded %s archive already on disk", name)
            return cached
        archive.unlink()
    part = archive.with_name(archive.name + ".part")
    digest = hashlib.sha256()
    try:
        log.info("Downloading %s %s from %s", name, entry["version"], entry["url"])
        request = urllib.request.Request(entry["url"], headers={"User-Agent": "SpatialLauncherAgent"})
        with _call_opener(opener, request) as response, part.open("wb") as f:
            headers = getattr(response, "headers", None)
            total = int(headers.get("Content-Length") or 0) if headers is not None else 0
            done = 0
            if progress:
                progress(name, 0, total)
            while True:
                chunk = response.read(CHUNK)
                if not chunk:
                    break
                f.write(chunk)
                digest.update(chunk)
                done += len(chunk)
                if progress:
                    progress(name, done, total)
        if digest.hexdigest() != entry["sha256"]:
            raise ToolFetchError(f"Checksum mismatch for {archive.name}: expected {entry['sha256']}, "
                                 f"got {digest.hexdigest()}. Not installing it.")
        os.replace(part, archive)
    except BaseException as e:
        part.unlink(missing_ok=True)
        log.warning("Download of %s failed: %s", name, e)
        raise
    log.info("Downloaded %s (%d bytes)", name, done)
    verified = _open_verified(archive, entry["sha256"])
    if verified is None:
        archive.unlink(missing_ok=True)
        raise ToolFetchError(f"{archive.name} changed on disk after it was downloaded. Not installing it.")
    return verified


def _check_name(name: str) -> None:
    posix = PurePosixPath(name.replace("\\", "/"))
    if posix.is_absolute() or ".." in posix.parts or (len(name) > 1 and name[1] == ":"):
        raise ToolFetchError(f"Unsafe path in archive: {name!r}")


def _read_members(archive, wanted: list[str], label: str = "the archive") -> dict[str, bytes]:
    """Validates every entry of the archive (an open binary file), then reads only the wanted members. Raises on any
    unsafe entry."""
    out: dict[str, bytes] = {}
    if zipfile.is_zipfile(archive):
        archive.seek(0)
        with zipfile.ZipFile(archive) as zf:
            for info in zf.infolist():
                _check_name(info.filename)
                mode = info.external_attr >> 16
                if mode and (stat.S_ISLNK(mode) or stat.S_ISCHR(mode) or stat.S_ISBLK(mode)
                             or stat.S_ISFIFO(mode) or stat.S_ISSOCK(mode)):
                    raise ToolFetchError(f"Link or device in archive: {info.filename!r}")
            names = set(zf.namelist())
            for member in wanted:
                if member not in names:
                    raise ToolFetchError(f"{member} is missing from {label}; the archive layout changed.")
                out[member] = zf.read(member)
        return out
    archive.seek(0)
    try:
        tf = tarfile.open(fileobj=archive)
    except tarfile.TarError as exc:
        raise ToolFetchError(f"{label} is not a zip or tar archive: {exc}") from exc
    with tf:
        members = {}
        for info in tf.getmembers():
            _check_name(info.name)
            if info.issym() or info.islnk() or info.isdev() or info.isfifo():
                raise ToolFetchError(f"Link or device in archive: {info.name!r}")
            try:
                tarfile.data_filter(info, "/extract-root")  # the same checks filter="data" applies on extraction
            except tarfile.FilterError as exc:
                raise ToolFetchError(f"Unsafe member {info.name!r}: {exc}") from exc
            members[info.name] = info
        for member in wanted:
            info = members.get(member)
            if info is None or not info.isreg():
                raise ToolFetchError(f"{member} is missing from {label}; the archive layout changed.")
            out[member] = tf.extractfile(info).read()
    return out


def _install(entry: dict, archive, directory: Path, label: str = "the archive") -> dict:
    """Unpacks the listed members; returns {installed file: {"size", "sha256"}} for tools.json."""
    data = _read_members(archive, list(entry["extract"]), label)  # everything validated before any file is written
    directory.mkdir(parents=True, exist_ok=True)
    installed = {}
    for member, target in entry["extract"].items():
        fd, tmp_name = tempfile.mkstemp(dir=directory, prefix=target + ".", suffix=".tmp")
        try:
            with os.fdopen(fd, "wb") as f:
                f.write(data[member])
            if sys.platform != "win32" and target == entry["binary"]:
                os.chmod(tmp_name, 0o755)
            os.replace(tmp_name, directory / target)
            installed[target] = {"size": len(data[member]), "sha256": hashlib.sha256(data[member]).hexdigest()}
        except BaseException:
            Path(tmp_name).unlink(missing_ok=True)
            raise
    return installed


def ensure(names=None, progress=None, opener=None) -> dict[str, Path]:
    """Makes sure the named tools (default all) are installed in paths.tools_dir(); returns {name: binary path}.
    progress(name, bytes_done, bytes_total) is called while downloading (total 0 if unknown).
    Raises ToolUnavailable for a missing or placeholder pin, ToolFetchError for a bad download or archive.
    Downloads are HTTPS only (redirects too) and time out after 30 s of silence."""
    opener = opener or _default_opener()
    entries = _entries()
    names = list(names or entries)
    for name in names:
        if name not in entries:
            raise ToolUnavailable(f"Unknown tool {name!r}.")
        if not entries[name]["sha256"]:
            raise ToolUnavailable(f"{name} is not yet available for {platform_key()}.")
    directory = paths.tools_dir()
    record = _read_record(directory)
    result = {}
    for name in names:
        entry = entries[name]
        if not _installed(entry, record, name, directory):
            archive = _download(name, entry, opener, progress)
            with archive:
                files = _install(entry, archive, directory, Path(archive.name).name)
            record[name] = {"version": entry["version"], "sha256": entry["sha256"], "files": files}
            _write_record(directory, record)
        result[name] = directory / entry["binary"]
    return result
