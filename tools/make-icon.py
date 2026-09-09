"""Turns the artwork into Jerboa.ico.

The source picture is a sticker with a wide white margin, which would leave the
animal a few pixels tall in the notification area. So the drawing is cropped to
what is actually on it and redrawn on a rounded white tile at every size Windows
asks for, each rendered from the full-resolution original rather than scaled down
from one another.

    pip install Pillow
    python tools/make-icon.py

The result is committed, so this only needs running when the artwork changes.
"""

import struct
import sys
from pathlib import Path

from PIL import Image, ImageDraw

SIZES = [16, 20, 24, 32, 48, 64, 128, 256]

# Low enough to ignore the card edge and its shadow, which are barely off-white,
# and to catch only the drawing's own dark outlines.
CONTENT_THRESHOLD = 160

ROOT = Path(__file__).resolve().parent.parent


def content_box(image, square=True, pad=1.08):
    """The area the drawing actually occupies.

    Square with a little air around it for the icon, where a fixed aspect ratio is
    required; tight and unpadded for anything that only wants the animal, since
    squaring the box pulls the sticker's card corners back into the crop."""
    grey = image.convert("L")
    mask = grey.point(lambda v: 255 if v < CONTENT_THRESHOLD else 0)
    box = mask.getbbox()
    if box is None:
        raise SystemExit("The source picture looks blank.")

    if not square and pad == 1.0:
        return box

    left, top, right, bottom = box
    width = (max(right - left, bottom - top) if square else right - left) * pad
    height = width if square else (bottom - top) * pad
    cx, cy = (left + right) / 2, (top + bottom) / 2
    return (round(cx - width / 2), round(cy - height / 2),
            round(cx + width / 2), round(cy + height / 2))


def render(source, box, size):
    """One icon size, drawn on a rounded white tile."""
    # Small sizes get less inset, otherwise there is nothing left of the animal.
    inset = 0.02 if size <= 24 else 0.04 if size <= 48 else 0.07
    margin = round(size * inset)
    radius = max(2, round(size * 0.18))

    supersample = 4 if size < 128 else 1
    big = size * supersample

    tile = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (0, 0, big - 1, big - 1), radius=radius * supersample, fill=255)
    tile.paste((255, 255, 255, 255), (0, 0), mask)

    inner = big - 2 * margin * supersample
    drawing = source.crop(box).resize((inner, inner), Image.LANCZOS)
    tile.paste(drawing, (margin * supersample, margin * supersample), drawing)

    tile.putalpha(mask)
    return tile.resize((size, size), Image.LANCZOS) if supersample > 1 else tile


def dib_bytes(image):
    """A 32-bit BMP entry: what icon editors emit below 256 px, and what every
    reader accepts without argument."""
    width, height = image.size
    pixels = image.load()

    header = struct.pack(
        "<IiiHHIIiiII", 40, width, height * 2, 1, 32, 0, width * height * 4, 0, 0, 0, 0)

    rows = bytearray()
    for y in range(height - 1, -1, -1):
        for x in range(width):
            r, g, b, a = pixels[x, y]
            rows += bytes((b, g, r, a))

    mask_stride = ((width + 31) // 32) * 4
    return header + bytes(rows) + bytes(mask_stride * height)


def png_bytes(image):
    from io import BytesIO
    buffer = BytesIO()
    image.save(buffer, format="PNG")
    return buffer.getvalue()


def main():
    source_path = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "Jerboa-Icon.jpeg"
    output_path = Path(sys.argv[2]) if len(sys.argv) > 2 else ROOT / "Jerboa.ico"

    source = Image.open(source_path).convert("RGBA")
    print(f"Source {source.width}x{source.height}")

    box = content_box(source)
    print(f"Cropped to {box[2] - box[0]}x{box[3] - box[1]} at {box[0]},{box[1]}")

    entries = []
    for size in SIZES:
        image = render(source, box, size)
        data = png_bytes(image) if size >= 256 else dib_bytes(image)
        entries.append((size, data))
        print(f"  {size:3} px  {len(data):7} bytes")

    out = bytearray(struct.pack("<HHH", 0, 1, len(entries)))
    offset = 6 + 16 * len(entries)
    for size, data in entries:
        dimension = 0 if size >= 256 else size
        out += struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in entries:
        out += data

    output_path.write_bytes(out)
    print(f"\nWrote {output_path} ({len(out) // 1024} KB)")


if __name__ == "__main__":
    main()
