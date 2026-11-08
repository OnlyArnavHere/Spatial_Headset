"""Draws the app icon (a white "A" mark on a black rounded square) and writes every size the builds need:
packaging/icon.ico (Windows exe and installer), packaging/icon.icns (macOS bundle), gui/web/icon.png and icon.ico
(the window). Run again after changing the drawing: python packaging/make_icons.py"""
import os
from PIL import Image, ImageDraw

HERE = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))


def draw(size: int) -> Image.Image:
    s = 4 * size  # draw large, then scale down for smooth edges
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = round(s * 0.06)
    d.rounded_rectangle((pad, pad, s - pad, s - pad), radius=round(s * 0.23), fill=(14, 15, 17, 255))
    w = round(s * 0.085)
    left, right, top, bottom = (s * 0.29, s * 0.675), (s * 0.71, s * 0.675), (s * 0.5, s * 0.25), s * 0.675
    d.line([left, top, right], fill="white", width=w, joint="curve")
    for x, y in (left, right, top):
        d.ellipse((x - w / 2, y - w / 2, x + w / 2, y + w / 2), fill="white")
    bar_y = s * 0.53
    d.line([(s * 0.385, bar_y), (s * 0.615, bar_y)], fill="white", width=w)
    for x in (s * 0.385, s * 0.615):
        d.ellipse((x - w / 2, bar_y - w / 2, x + w / 2, bar_y + w / 2), fill="white")
    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    big = draw(1024)
    ico_sizes = [(n, n) for n in (16, 20, 24, 32, 40, 48, 64, 128, 256)]
    big.save(os.path.join(HERE, "packaging", "icon.ico"), sizes=ico_sizes)
    big.save(os.path.join(HERE, "gui", "web", "icon.ico"), sizes=ico_sizes)
    big.save(os.path.join(HERE, "packaging", "icon.icns"))
    draw(256).save(os.path.join(HERE, "gui", "web", "icon.png"))
    draw(512).save(os.path.join(HERE, "packaging", "icon.png"))


if __name__ == "__main__":
    main()
