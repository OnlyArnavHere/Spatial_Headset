"""Reads the target of a Windows shortcut (.lnk, the MS-SHLLINK format) without COM: enough to tell which program a
Start Menu or taskbar shortcut starts, so a running window can be matched to its app. Shortcuts are only ever *launched*
through the shell (os.startfile on the .lnk itself), never by running what this parser read.

parse() never raises on bad input beyond ValueError, and reads at most what the file holds.
"""
import struct
from dataclasses import dataclass

HEADER_SIZE = 0x4C
LINK_CLSID = bytes.fromhex("0114020000000000c000000000000046")

HAS_ID_LIST = 0x1
HAS_LINK_INFO = 0x2
HAS_NAME = 0x4
HAS_RELATIVE_PATH = 0x8
HAS_WORKING_DIR = 0x10
HAS_ARGUMENTS = 0x20
HAS_ICON_LOCATION = 0x40
IS_UNICODE = 0x80
HAS_EXP_STRING = 0x200
HAS_DARWIN_ID = 0x1000

VOLUME_ID_AND_LOCAL_BASE_PATH = 0x1
ENVIRONMENT_BLOCK = 0xA0000001
DARWIN_BLOCK = 0xA0000006
MAX_FILE = 1 << 20  # shortcuts are a few KiB; anything bigger is not one


@dataclass(frozen=True)
class LinkTarget:
    path: str | None        # the target file, environment variables not yet expanded, or None (advertised, folder...)
    arguments: str
    icon_location: str
    advertised: bool        # an MSI "advertised" shortcut: the target is resolved by Windows Installer at launch


def _cstr(data: bytes, offset: int, wide: bool) -> str:
    if offset <= 0 or offset >= len(data):
        return ""
    if wide:
        end = offset
        while end + 1 < len(data) and data[end:end + 2] != b"\x00\x00":
            end += 2
        return data[offset:end].decode("utf-16-le", "replace")
    end = data.find(b"\x00", offset)
    return data[offset:end if end >= 0 else len(data)].decode("mbcs" if _has_mbcs() else "latin-1", "replace")


def _has_mbcs() -> bool:
    try:
        "".encode("mbcs")
        return True
    except LookupError:
        return False


def _fixed_wide(data: bytes) -> str:
    return data.decode("utf-16-le", "replace").split("\x00", 1)[0]


def _fixed_ansi(data: bytes) -> str:
    return data.split(b"\x00", 1)[0].decode("latin-1", "replace")


def parse(data: bytes) -> LinkTarget:
    if len(data) > MAX_FILE or len(data) < HEADER_SIZE:
        raise ValueError("not a shortcut")
    size, clsid, flags = struct.unpack_from("<I16sI", data, 0)
    if size != HEADER_SIZE or clsid != LINK_CLSID:
        raise ValueError("not a shortcut")
    pos = HEADER_SIZE
    try:
        if flags & HAS_ID_LIST:
            (id_size,) = struct.unpack_from("<H", data, pos)
            pos += 2 + id_size
        path = None
        if flags & HAS_LINK_INFO:
            info = data[pos:]
            info_size, header_size, info_flags = struct.unpack_from("<III", info, 0)
            if info_size < 28 or info_size > len(info):
                raise ValueError("bad link info")
            info = info[:info_size]
            _volume, base_offset, _network, suffix_offset = struct.unpack_from("<IIII", info, 12)
            if info_flags & VOLUME_ID_AND_LOCAL_BASE_PATH:
                if header_size >= 0x24:
                    base_w, suffix_w = struct.unpack_from("<II", info, 28)
                    path = _cstr(info, base_w, True) + _cstr(info, suffix_w, True)
                else:
                    path = _cstr(info, base_offset, False) + _cstr(info, suffix_offset, False)
            pos += info_size
        wide = bool(flags & IS_UNICODE)
        strings = {}
        for bit in (HAS_NAME, HAS_RELATIVE_PATH, HAS_WORKING_DIR, HAS_ARGUMENTS, HAS_ICON_LOCATION):
            if flags & bit:
                (count,) = struct.unpack_from("<H", data, pos)
                pos += 2
                length = count * (2 if wide else 1)
                if pos + length > len(data):
                    raise ValueError("string past the end")
                raw = data[pos:pos + length]
                strings[bit] = raw.decode("utf-16-le", "replace") if wide else raw.decode("latin-1", "replace")
                pos += length
        advertised = bool(flags & HAS_DARWIN_ID)
        # Extra data: an environment-variable target (%ProgramFiles%\...) wins over nothing; a Darwin block marks an
        # advertised (MSI) shortcut.
        while pos + 8 <= len(data):
            block_size, signature = struct.unpack_from("<II", data, pos)
            if block_size < 4 or pos + block_size > len(data):
                break
            if block_size >= 8 + 260 + 520 and signature == ENVIRONMENT_BLOCK and not path:
                block = data[pos + 8:pos + block_size]
                path = _fixed_wide(block[260:780]) or _fixed_ansi(block[:260]) or None
            elif signature == DARWIN_BLOCK:
                advertised = True
            pos += block_size
    except struct.error as e:
        raise ValueError(f"truncated shortcut ({e})") from e
    return LinkTarget(path or None, strings.get(HAS_ARGUMENTS, ""), strings.get(HAS_ICON_LOCATION, ""), advertised)


def build(path: str, *, arguments: str = "", unicode_info: bool = True, env_only: bool = False,
          advertised: bool = False, id_list: bytes = b"") -> bytes:
    """A minimal shortcut for tests: LinkInfo with a local base path (or only an environment block), optional arguments."""
    flags = IS_UNICODE | (HAS_ARGUMENTS if arguments else 0) | (HAS_ID_LIST if id_list else 0) | (
        HAS_DARWIN_ID if advertised else 0)
    body = b""
    if id_list:
        body += struct.pack("<H", len(id_list)) + id_list
    if not env_only and not advertised:
        flags |= HAS_LINK_INFO
        volume = struct.pack("<IIII", 16, 3, 0, 0)
        if unicode_info:
            header = 0x24
            ansi = path.encode("latin-1", "replace") + b"\x00"
            wide = path.encode("utf-16-le") + b"\x00\x00"
            volume_off = header
            base_off = volume_off + len(volume)
            suffix_off = base_off + len(ansi)
            base_w = suffix_off + 1
            suffix_w = base_w + len(wide)
            payload = volume + ansi + b"\x00" + wide + b"\x00\x00"
            fields = struct.pack("<IIIIIIII", header, 1, volume_off, base_off, 0, suffix_off, base_w, suffix_w)
        else:
            header = 0x1C
            ansi = path.encode("latin-1", "replace") + b"\x00"
            volume_off = header
            base_off = volume_off + len(volume)
            suffix_off = base_off + len(ansi)
            payload = volume + ansi + b"\x00"
            fields = struct.pack("<IIIIII", header, 1, volume_off, base_off, 0, suffix_off)
        info = fields + payload
        body += struct.pack("<I", 4 + len(info)) + info
    if arguments:
        body += struct.pack("<H", len(arguments)) + arguments.encode("utf-16-le")
    if env_only:
        block = path.encode("latin-1", "replace")[:259].ljust(260, b"\x00") + \
            path.encode("utf-16-le")[:518].ljust(520, b"\x00")
        body += struct.pack("<II", 8 + len(block), ENVIRONMENT_BLOCK) + block
    if advertised:
        block = b"\x00" * 260 + "[product]".encode("utf-16-le").ljust(520, b"\x00")
        body += struct.pack("<II", 8 + len(block), DARWIN_BLOCK) + block
    body += b"\x00\x00\x00\x00"  # terminal block
    header = struct.pack("<I16sI", HEADER_SIZE, LINK_CLSID, flags) + b"\x00" * (HEADER_SIZE - 24)
    return header + body
