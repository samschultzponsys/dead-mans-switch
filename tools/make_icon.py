"""Draws assets/DeadMansSwitch.ico: a red power symbol on the app's dark tile.

Run: python3 tools/make_icon.py   (needs Pillow)
"""
from pathlib import Path

from PIL import Image, ImageDraw

SIZE = 1024  # drawn large, then downsampled for clean edges
BG = (30, 32, 38, 255)        # window background, #1E2026
RING = (55, 58, 66, 255)      # bar track, #373A42
RED = (231, 76, 60, 255)      # final-warning red, #E74C3C

img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

d.rounded_rectangle([0, 0, SIZE - 1, SIZE - 1], radius=200, fill=BG)
d.ellipse([90, 90, SIZE - 90, SIZE - 90], outline=RING, width=40)

# Power symbol: an open arc with a vertical bar through the gap at the top.
stroke = 110
c, r = SIZE // 2, 300
d.arc([c - r, c - r + 40, c + r, c + r + 40], start=-55, end=235, fill=RED, width=stroke)
d.rounded_rectangle([c - stroke // 2, 170, c + stroke // 2, 540], radius=stroke // 2, fill=RED)

out = Path(__file__).resolve().parent.parent / "assets" / "DeadMansSwitch.ico"
out.parent.mkdir(exist_ok=True)
img.save(out, sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
img.resize((256, 256), Image.LANCZOS).save(out.with_suffix(".png"))
print(f"wrote {out}")
