"""The parts of the Windows shell backend that need no Windows: which Start Menu shortcuts count as apps, the taskbar
pin order, Store apps from Get-StartApps, and turning an icon's colour and mask bitmaps into RGBA."""
import json
import ntpath
import re

from . import _lnk
from ._shell_types import AppEntry

# Shortcuts in the Start Menu that are not apps.
_NOT_APPS = re.compile(r"\b(uninstall\w*|readme|read me|help|documentation|manual|license|licence|website|"
                       r"release notes|changelog|what'?s new|support)\b", re.IGNORECASE)
# A packaged app's AUMID: <name>_<13-char publisher hash>!<app id>. Nothing else is launched through AppsFolder.
AUMID = re.compile(r"[A-Za-z0-9.\-]{1,100}_[a-z0-9]{13}![A-Za-z0-9.\-_]{1,100}")
MAX_SHORTCUTS = 2000
# Shortcuts that point at a shell item instead of a file, by name: the program their windows run in.
KNOWN_SHELL_TARGETS = {"file explorer": r"%WINDIR%\explorer.exe"}


def program_key(path: str | None, expand=None) -> str | None:
    """A program path the way running windows report it: environment expanded, normalized, lower case. Only .exe."""
    if not path:
        return None
    if expand is not None:
        path = expand(path)
    path = ntpath.normcase(ntpath.normpath(path))
    return path if path.endswith(".exe") else None


def shortcut_entry(lnk_path: str, data: bytes, expand=None) -> AppEntry | None:
    """The app a Start Menu or taskbar shortcut starts, or None for documents, uninstallers and broken files."""
    name = ntpath.splitext(ntpath.basename(lnk_path))[0].strip()
    if not name or _NOT_APPS.search(name):
        return None
    try:
        target = _lnk.parse(data)
    except ValueError:
        return None
    program = program_key(target.path or KNOWN_SHELL_TARGETS.get(name.casefold()), expand)
    if program is None and not target.advertised:
        if target.path:
            return None  # a document, a folder, a web page: not an app
        # no resolvable target (a shell item such as File Explorer): still an app the shell can start
    if program is not None and _NOT_APPS.search(ntpath.basename(program)):
        return None
    return AppEntry(f"lnk:{ntpath.normcase(lnk_path)}", name, program, ("lnk", lnk_path), ("path", lnk_path))


def dedupe(entries) -> list[AppEntry]:
    """One entry per name (case-insensitive): the first one found wins."""
    seen, out = set(), []
    for entry in entries:
        if entry is None:
            continue
        name = entry.name.casefold()
        if name in seen:
            continue
        seen.add(name)
        out.append(entry)
    return out


def packaged_apps(get_start_apps_json: str) -> list[AppEntry]:
    """Store (packaged) apps from `Get-StartApps | ConvertTo-Json`. Desktop apps come from the Start Menu shortcuts."""
    try:
        doc = json.loads(get_start_apps_json or "[]")
    except ValueError:
        return []
    if isinstance(doc, dict):
        doc = [doc]
    out = []
    for item in doc if isinstance(doc, list) else []:
        if not isinstance(item, dict):
            continue
        name, aumid = item.get("Name"), item.get("AppID")
        if not isinstance(name, str) or not isinstance(aumid, str) or not AUMID.fullmatch(aumid):
            continue
        name = name.strip()
        if name:
            out.append(AppEntry(f"uwp:{aumid}", name, None, ("aumid", aumid), ("aumid", aumid)))
    return out


def pin_order(pinned_shortcuts: list[AppEntry], packaged: list[AppEntry], favorites: bytes | None) -> list[AppEntry]:
    """Taskbar pins in taskbar order, best effort. Windows keeps the order only inside the Taskband "Favorites" blob,
    which holds each pin's shell item and so its shortcut file name, or a Store app's AUMID, as UTF-16. Pins are sorted
    by where that text first appears; shortcuts the blob doesn't mention go last, alphabetically. Store apps count as
    pinned only when the blob names them."""
    blob = favorites or b""

    def where(text: str) -> int:
        return blob.find(text.encode("utf-16-le")) if blob and text else -1

    ranked = []
    for entry in pinned_shortcuts:
        file_name = ntpath.basename(entry.launch[1]) if entry.launch else entry.name + ".lnk"
        at = where(file_name)
        ranked.append((at if at >= 0 else len(blob) + 1, entry.name.casefold(), entry))
    for entry in packaged:
        at = where(entry.launch[1]) if entry.launch else -1
        if at >= 0:
            ranked.append((at, entry.name.casefold(), entry))
    ranked.sort(key=lambda r: (r[0], r[1]))
    return dedupe(r[2] for r in ranked)


def icon_rgba(color_bgra: bytes, mask_bgra: bytes | None, width: int, height: int) -> bytes:
    """An icon's 32-bit colour bitmap (BGRA, top-down) as RGBA. Old icons carry no alpha (all zero): then the AND mask
    says what is transparent (white in the mask = transparent)."""
    rgba = bytearray(color_bgra)
    rgba[0::4], rgba[2::4] = color_bgra[2::4], color_bgra[0::4]
    if not any(color_bgra[3::4]):
        if mask_bgra is not None and len(mask_bgra) >= width * height * 4:
            for i in range(width * height):
                rgba[i * 4 + 3] = 0 if mask_bgra[i * 4] else 255
        else:
            rgba[3::4] = b"\xff" * (width * height)
    return bytes(rgba)
