"""Builds docs/social-preview.png — the 1280x640 card GitHub shows when the
repository link is pasted into Slack, X or a chat window.

    pip install Pillow
    python tools/make-social-preview.py

Upload it under Settings -> General -> Social preview. The result is committed,
so this only needs running when the artwork or the wording changes.
"""

import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(Path(__file__).resolve().parent))
from importlib import import_module

content_box = import_module("make-icon").content_box

WIDTH, HEIGHT = 1280, 640

PAPER = (250, 248, 244)
INK = (32, 32, 30)
MUTED = (108, 106, 100)
HAIRLINE = (222, 218, 210)

MIC_FILL, MIC_INK = (225, 245, 238), (15, 110, 86)
SYSTEM_FILL, SYSTEM_INK = (230, 241, 251), (24, 95, 165)

FONTS = Path("C:/Windows/Fonts")


def font(name, size):
    for candidate in (name, "segoeui.ttf", "arial.ttf"):
        path = FONTS / candidate
        if path.exists():
            return ImageFont.truetype(str(path), size)
    return ImageFont.load_default()


def knockout_white(image, opaque_below=222, clear_above=248, colour_tolerance=14):
    """Drops the sticker's white card, its halo and the soft grey shadow, so the animal
    sits on the paper rather than on a pale rectangle.

    The card and its shadow are neutral — red, green and blue within a few points of
    each other — while every part of the animal is warm. Testing for that as well as
    for brightness keeps the cream belly and the pale fur intact. The ramp between the
    two thresholds keeps the cut edge from going jagged."""
    pixels = image.load()
    span = clear_above - opaque_below

    for y in range(image.height):
        for x in range(image.width):
            r, g, b, a = pixels[x, y]
            lowest = min(r, g, b)
            if lowest <= opaque_below or max(r, g, b) - lowest > colour_tolerance:
                continue
            faded = 0 if lowest >= clear_above else int(a * (clear_above - lowest) / span)
            pixels[x, y] = (r, g, b, faded)
    return image


def main():
    card = Image.new("RGB", (WIDTH, HEIGHT), PAPER)
    draw = ImageDraw.Draw(card)

    # The animal, cropped out of its sticker card and dropped straight onto the paper.
    source = Image.open(ROOT / "Jerboa-Icon.jpeg").convert("RGBA")
    crop = source.crop(content_box(source, square=False, pad=1.0))
    scale = min(440 / crop.width, 450 / crop.height)
    animal = knockout_white(
        crop.resize((round(crop.width * scale), round(crop.height * scale)), Image.LANCZOS))
    card.paste(animal, (96 + (440 - animal.width) // 2, 106 + (450 - animal.height) // 2), animal)

    left = 588
    draw.text((left, 132), "Jerboa", font=font("seguisb.ttf", 86), fill=INK)

    tagline = ("Records your microphone and the system audio\n"
               "into a single MP3 — on separate channels.")
    draw.text((left + 4, 252), tagline, font=font("segoeui.ttf", 30), fill=MUTED, spacing=12)

    # One file, two channels: a single rounded block divided down the middle says
    # more than a sentence about it would.
    box = (left + 4, 392, WIDTH - 92, 492)
    middle = (box[0] + box[2]) // 2

    draw.rounded_rectangle(box, radius=14, fill=MIC_FILL)
    draw.rounded_rectangle((middle, box[1], box[2], box[3]), radius=14, fill=SYSTEM_FILL)
    draw.rectangle((middle, box[1], middle + 14, box[3]), fill=SYSTEM_FILL)
    draw.line((middle, box[1] + 10, middle, box[3] - 10), fill=(255, 255, 255), width=3)

    label = font("seguisb.ttf", 21)
    detail = font("segoeui.ttf", 20)
    draw.text((box[0] + 26, box[1] + 22), "LEFT", font=label, fill=MIC_INK)
    draw.text((box[0] + 26, box[1] + 52), "your microphone", font=detail, fill=MIC_INK)
    draw.text((middle + 26, box[1] + 22), "RIGHT", font=label, fill=SYSTEM_INK)
    draw.text((middle + 26, box[1] + 52), "everyone else", font=detail, fill=SYSTEM_INK)

    draw.line((left + 4, 556, WIDTH - 92, 556), fill=HAIRLINE, width=2)
    draw.text((left + 4, 574), "Windows  ·  free and open source  ·  MIT",
              font=font("segoeui.ttf", 21), fill=MUTED)

    output = ROOT / "docs" / "social-preview.png"
    output.parent.mkdir(exist_ok=True)
    card.save(output, "PNG")
    print(f"Wrote {output} ({WIDTH}x{HEIGHT})")


if __name__ == "__main__":
    main()
