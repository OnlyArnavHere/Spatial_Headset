"""The parts of the macOS shell backend that need no pyobjc: Dock pins from com.apple.dock.plist, the app bundles in
the Applications folders, and which Quartz windows the shell lists. Testable on any OS."""
import posixpath
from dataclasses import dataclass
from urllib.parse import unquote, urlparse

from ._shell_types import AppEntry, WindowEntry

FINDER = "/System/Library/CoreServices/Finder.app"
MAX_BUNDLES = 2000
MIN_WINDOW = (80, 40)  # smaller windows are palettes, badges and helpers


@dataclass(frozen=True)
class RunningApp:
    pid: int
    name: str
    bundle: str | None     # the .app path, or None
    started: float         # launch date, epoch seconds
    regular: bool          # NSApplicationActivationPolicyRegular: has a Dock icon


def program_key(bundle: str | None) -> str | None:
    """How apps and windows are matched: the bundle path, normalized and case-folded (APFS is case-insensitive)."""
    if not bundle:
        return None
    path = posixpath.normpath(bundle).rstrip("/")
    return path.casefold() if path.lower().endswith(".app") else None


def bundle_entry(path: str) -> AppEntry | None:
    path = posixpath.normpath(path).rstrip("/")
    key = program_key(path)
    if key is None or not path.startswith("/"):
        return None
    name = posixpath.splitext(posixpath.basename(path))[0].strip()
    if not name:
        return None
    return AppEntry(f"bundle:{key}", name, key, ("bundle", path), ("bundle", path))


def dock_pins(doc, exists=lambda path: True, locate=lambda bundle_id: None, by_name=lambda name: None) -> list[str]:
    """The .app paths of the Dock's pinned apps, in Dock order, from the parsed com.apple.dock.plist. Finder is always
    first in the Dock but isn't stored there, so it is put first. Pins whose path doesn't exist are left out.

    A pin's stored path can be stale: an app first opened from a quarantined download is stored under a temporary
    AppTranslocation folder. Like the Dock, such a pin is found again by its bundle identifier with locate(). A
    translocated pin whose identifier is unknown (another edition of the app was installed) is matched by its bundle
    name with by_name(), which looks only in the Applications folders."""
    paths = [FINDER] if exists(FINDER) else []
    items = doc.get("persistent-apps") if isinstance(doc, dict) else None
    for item in items if isinstance(items, list) else []:
        try:
            tile = item["tile-data"]
            url = tile["file-data"]["_CFURLString"]
        except (KeyError, TypeError):
            continue
        if not isinstance(url, str):
            continue
        parsed = urlparse(url)
        path = unquote(parsed.path) if parsed.scheme == "file" else (url if url.startswith("/") else "")
        path = posixpath.normpath(path).rstrip("/") if path else ""
        bundle_id = tile.get("bundle-identifier") if isinstance(tile, dict) else None
        translocated = "/AppTranslocation/" in path
        if not path or translocated or not exists(path):
            found = locate(bundle_id) if isinstance(bundle_id, str) and bundle_id else None
            if not (isinstance(found, str) and found.startswith("/")) and translocated:
                found = by_name(posixpath.basename(path))
            if isinstance(found, str) and found.startswith("/"):
                path = posixpath.normpath(found).rstrip("/")
        if path.lower().endswith(".app") and path not in paths and exists(path):
            paths.append(path)
    return paths


def scan_bundles(roots: list[str], listdir, isdir) -> list[str]:
    """Every .app in the roots and one level of subfolders (Utilities, vendor folders), sorted, at most MAX_BUNDLES.
    The contents of bundles are never entered."""
    found = []
    for root in roots:
        try:
            names = sorted(listdir(root))
        except OSError:
            continue
        for name in names:
            path = posixpath.join(root, name)
            if name.lower().endswith(".app"):
                found.append(path)
            elif not name.startswith(".") and isdir(path):
                try:
                    found += [posixpath.join(path, sub) for sub in sorted(listdir(path)) if sub.lower().endswith(".app")]
                except OSError:
                    continue
            if len(found) >= MAX_BUNDLES:
                return found[:MAX_BUNDLES]
    return found


def shell_windows(all_infos, onscreen_ids: set, apps: dict, own_pid: int, focused_id: int) -> list[WindowEntry]:
    """The windows the Dock would show for running apps: normal (layer 0) windows of regular apps, big enough to be
    a document window. A window that isn't on screen (minimized, hidden or on another Space) is listed only when it
    has a title, which leaves out the invisible helper windows many apps keep."""
    out = []
    for info in all_infos:
        try:
            handle, pid = int(info["kCGWindowNumber"]), int(info["kCGWindowOwnerPID"])
        except (KeyError, TypeError, ValueError):
            continue
        app = apps.get(pid)
        if pid == own_pid or app is None or not app.regular or info.get("kCGWindowLayer", 0) != 0:
            continue
        if float(info.get("kCGWindowAlpha", 1.0) or 0.0) <= 0.0:
            continue
        bounds = info.get("kCGWindowBounds") or {}
        if bounds.get("Width", 0) < MIN_WINDOW[0] or bounds.get("Height", 0) < MIN_WINDOW[1]:
            continue
        name = str(info.get("kCGWindowName") or "").strip()
        onscreen = handle in onscreen_ids
        if not onscreen and not name:
            continue
        out.append(WindowEntry(handle, name or app.name, program_key(app.bundle), app.name, not onscreen,
                               handle == focused_id, app.started, pid, ("bundle", app.bundle) if app.bundle else None))
    return out
