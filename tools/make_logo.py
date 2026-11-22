#!/usr/bin/env python3
"""Renders the Castle Story Plus artwork in CastleStoryPlus/Assets/:

- plus.png: the "PLUS" badge the plugin puts on the game's own logo texture (main menu, loading screen).
  The game's logo itself is not redistributed; the plugin finds its texture at runtime.
- logo.png: a full "CASTLE STORY PLUS" logo in the same style, for the splash screen, where the game's
  logo texture is not loaded yet.
- plus_essentials.png, logo_essentials.png: the same for the Essentials edition (fixes and performance only),
  with an "ESSENTIALS" ribbon under PLUS.

Style: yellow letters with a thick black outline and a black extrusion down to the right; "STORY" in
black with a white outline.

Font: Luckiest Guy (Apache License 2.0), tools/fonts/LuckiestGuy-Regular.ttf.
Run: python3 tools/make_logo.py
"""
import os
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
FONT = os.path.join(HERE, "fonts", "LuckiestGuy-Regular.ttf")
ASSETS = os.path.join(HERE, "..", "CastleStoryPlus", "Assets")

YELLOW = (255, 194, 14, 255)
TEAL = (38, 170, 160, 255)
BLACK = (12, 12, 12, 255)
WHITE = (255, 255, 255, 255)
OUTLINE = 14
EXTRUDE = 16


def word(text, size, fill, outline_fill, extrude_fill, outline=OUTLINE, extrude=EXTRUDE, tilt=0):
    """One word with outline and extrusion, rotated by tilt degrees, cropped to its pixels."""
    font = ImageFont.truetype(FONT, size)
    left, top, right, bottom = font.getbbox(text, stroke_width=outline)
    pad = outline + extrude + 8
    img = Image.new("RGBA", (right - left + 2 * pad, bottom - top + 2 * pad), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    x, y = pad - left, pad - top
    for i in range(extrude, 0, -1):
        draw.text((x + i, y + i), text, font=font, fill=extrude_fill, stroke_width=outline, stroke_fill=extrude_fill)
    draw.text((x, y), text, font=font, fill=fill, stroke_width=outline, stroke_fill=outline_fill)
    img = img.rotate(tilt, resample=Image.BICUBIC, expand=True)
    return img.crop(img.getbbox())


def save(img, name):
    path = os.path.join(ASSETS, name)
    img.save(path)
    print("wrote", os.path.normpath(path), img.size)


def under(top, bottom, overlap, shift=0):
    """bottom centred under top (moved right by shift), overlapping it by overlap pixels."""
    w = max(top.width, bottom.width + abs(shift))
    h = top.height + bottom.height - overlap
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    img.alpha_composite(top, ((w - top.width) // 2, 0))
    img.alpha_composite(bottom, ((w - bottom.width) // 2 + shift, top.height - overlap))
    return img.crop(img.getbbox())


def essentials(width):
    """The "ESSENTIALS" ribbon: white letters, black outline, on a teal band, scaled to width."""
    text = word("ESSENTIALS", 120, WHITE, BLACK, BLACK, outline=10, extrude=8)
    pad_x, pad_y = 36, 18
    band = Image.new("RGBA", (text.width + 2 * pad_x + 16, text.height + 2 * pad_y + 16), (0, 0, 0, 0))
    draw = ImageDraw.Draw(band)
    box = (8, 8, band.width - 8, band.height - 8)
    draw.rounded_rectangle((box[0] + 10, box[1] + 10, box[2] + 8, box[3] + 8), radius=22, fill=BLACK)
    draw.rounded_rectangle(box, radius=22, fill=TEAL, outline=BLACK, width=10)
    band.alpha_composite(text, ((band.width - text.width) // 2, (band.height - text.height) // 2))
    band = band.rotate(4, resample=Image.BICUBIC, expand=True)
    band = band.crop(band.getbbox())
    return band.resize((width, int(band.height * width / band.width)), Image.LANCZOS)


def main():
    plus = word("PLUS", 220, YELLOW, BLACK, BLACK, tilt=4)
    save(plus, "plus.png")
    save(under(plus, essentials(int(plus.width * 0.85)), 10), "plus_essentials.png")

    castle = word("CASTLE", 300, YELLOW, BLACK, BLACK, outline=18, extrude=22, tilt=3)
    story = word("STORY", 150, BLACK, WHITE, BLACK, outline=12, extrude=10, tilt=3)
    small_plus = plus.resize((int(plus.width * 0.7), int(plus.height * 0.7)), Image.LANCZOS)
    # CASTLE, with STORY overlapping its bottom right like the game's logo, and PLUS stacked under STORY
    story_y = int(castle.height * 0.7)
    plus_y = story_y + int(story.height * 0.8)
    w = castle.width + 40
    h = plus_y + small_plus.height
    logo = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    logo.alpha_composite(castle, (0, 0))
    logo.alpha_composite(story, (w - story.width - 10, story_y))
    logo.alpha_composite(small_plus, (w - small_plus.width - 30, plus_y))
    logo = logo.crop(logo.getbbox())
    save(logo, "logo.png")
    save(under(logo, essentials(int(logo.width * 0.4)), 14, shift=int(logo.width * 0.28)), "logo_essentials.png")


if __name__ == "__main__":
    main()
