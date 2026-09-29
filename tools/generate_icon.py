"""Build APS's multi-size Windows icons. Requires Pillow: pip install Pillow.

The coordinates below are the source artwork. Run from any directory; the plain
icon is used by the compiler and Velopack, and both variants are embedded for the UI.
"""

from io import BytesIO
from math import cos, radians, sin
from pathlib import Path
import struct

from PIL import Image, ImageDraw


OUTPUT = Path(__file__).resolve().parents[1] / "src" / "Assets"
SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
BODY = "#191612"
GOLD = "#e8bd63"


def draw_icon(size: int, badged: bool) -> Image.Image:
    # Render each frame at four times its final size for smooth edges at every DPI.
    scale = size / 32 * 4
    canvas = Image.new("RGBA", (size * 4, size * 4), (0, 0, 0, 0))
    pen = ImageDraw.Draw(canvas)

    def box(x0, y0, x1, y1):
        return tuple(round(v * scale) for v in (x0, y0, x1, y1))

    pen.rounded_rectangle(box(2, 2, 30, 30), radius=round(6 * scale), fill=BODY)

    # Make room for the lock instead of obscuring the speaker with an overlay.
    glyph = 0.78 if badged else 1

    def point(x, y):
        return (round(x * glyph * scale), round(y * glyph * scale))

    pen.rounded_rectangle((*point(7, 12), *point(14, 20)),
                          radius=max(1, round(1.5 * glyph * scale)), fill=GOLD)
    pen.polygon([point(x, y) for x, y in ((14, 14), (21, 7), (21, 25), (14, 18))],
                fill=GOLD)

    # Keep the arcs' pixel weight when the speaker shrinks for the badge.
    width = max(4, round(2 * scale))
    for radius in (6, 10):
        points = [point(19 + radius * cos(radians(angle)),
                        16 + radius * sin(radians(angle))) for angle in range(-40, 41)]
        pen.line(points, fill=GOLD, width=width, joint="curve")
        for x, y in (points[0], points[-1]):
            pen.ellipse((x - width / 2, y - width / 2, x + width / 2, y + width / 2),
                        fill=GOLD)

    if badged:
        pen.ellipse(box(16.5, 16.5, 30.5, 30.5), fill=BODY)
        pen.arc(box(21, 20, 26, 25), 180, 360, fill=GOLD,
                width=max(4, round(2.2 * scale)))
        pen.rounded_rectangle(box(19.5, 23, 27.5, 29),
                              radius=max(1, round(1.4 * scale)), fill=GOLD)

    return canvas.resize((size, size), Image.Resampling.LANCZOS)


def write_icon(path: Path, badged: bool) -> None:
    frames = []
    for size in SIZES:
        data = BytesIO()
        draw_icon(size, badged).save(data, format="PNG")
        frames.append(data.getvalue())

    offset = 6 + len(frames) * 16
    with path.open("wb") as icon:
        icon.write(struct.pack("<HHH", 0, 1, len(frames)))
        for size, data in zip(SIZES, frames):
            icon.write(struct.pack("<BBBBHHII", size % 256, size % 256,
                                   0, 0, 1, 32, len(data), offset))
            offset += len(data)
        for data in frames:
            icon.write(data)

    print(path)


def main() -> None:
    OUTPUT.mkdir(parents=True, exist_ok=True)
    write_icon(OUTPUT / "aps.ico", badged=False)
    write_icon(OUTPUT / "aps-watching.ico", badged=True)


if __name__ == "__main__":
    main()
