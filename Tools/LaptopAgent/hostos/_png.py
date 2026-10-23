"""A small PNG encoder for app icons (RGBA, 8 bits a channel), and a box-filter downscale. Pure Python: icons are at
most a few dozen pixels a side, so speed doesn't matter, and nothing here needs Pillow."""
import struct
import zlib


def _chunk(kind: bytes, data: bytes) -> bytes:
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)


def encode_rgba(width: int, height: int, rgba: bytes) -> bytes:
    """PNG bytes for a top-down RGBA image (4 bytes a pixel, rows without padding)."""
    if width <= 0 or height <= 0 or len(rgba) != width * height * 4:
        raise ValueError("RGBA data doesn't match the size")
    stride = width * 4
    raw = b"".join(b"\x00" + rgba[y * stride:(y + 1) * stride] for y in range(height))
    header = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + _chunk(b"IHDR", header) + _chunk(b"IDAT", zlib.compress(raw, 9)) + _chunk(b"IEND", b"")


def bgra_to_rgba(bgra: bytes) -> bytes:
    out = bytearray(bgra)
    out[0::4], out[2::4] = bgra[2::4], bgra[0::4]
    return bytes(out)


def downscale(width: int, height: int, rgba: bytes, size: int) -> tuple[int, int, bytes]:
    """Shrinks the image so neither side is over `size`, averaging each block with alpha weighting (so transparent
    edges don't turn dark). Images that already fit come back unchanged."""
    if width <= size and height <= size:
        return width, height, rgba
    scale = max(width, height) / size
    out_w, out_h = max(1, round(width / scale)), max(1, round(height / scale))
    out = bytearray(out_w * out_h * 4)
    for oy in range(out_h):
        y0, y1 = int(oy * height / out_h), max(int(oy * height / out_h) + 1, int((oy + 1) * height / out_h))
        for ox in range(out_w):
            x0, x1 = int(ox * width / out_w), max(int(ox * width / out_w) + 1, int((ox + 1) * width / out_w))
            r = g = b = a = 0
            count = 0
            for y in range(y0, y1):
                row = y * width * 4
                for x in range(x0, x1):
                    i = row + x * 4
                    alpha = rgba[i + 3]
                    r += rgba[i] * alpha
                    g += rgba[i + 1] * alpha
                    b += rgba[i + 2] * alpha
                    a += alpha
                    count += 1
            o = (oy * out_w + ox) * 4
            if a:
                out[o], out[o + 1], out[o + 2] = r // a, g // a, b // a
            out[o + 3] = a // count
    return out_w, out_h, bytes(out)


def decode_size(png: bytes) -> tuple[int, int]:
    """Width and height from a PNG's header (for checks and tests). Raises ValueError for anything else."""
    if len(png) < 24 or png[:8] != b"\x89PNG\r\n\x1a\n" or png[12:16] != b"IHDR":
        raise ValueError("not a PNG")
    return struct.unpack(">II", png[16:24])
