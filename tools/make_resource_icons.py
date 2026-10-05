#!/usr/bin/env python3
"""Makes the coal, steel and dark crystal icons in CastleStoryPlus/Assets/Resources/ from the game's own iron icons.

Coal is the raw iron icon (lumps) turned near black with grey highlights; steel is the iron ingot icon turned a
light, cool silver. Dark crystal is the raw blue crystal icon turned deep violet. The stockpiled icons keep the pallet's brown board. The game's icons are read from
resources.assets with UnityPy, so the game must be installed:

    python3 tools/make_resource_icons.py [path to "Castle Story_Data"]
"""
import colorsys
import os
import sys

import UnityPy
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "CastleStoryPlus", "Assets", "Resources")
DEFAULT_DATA = os.path.expanduser("~/.local/share/Steam/steamapps/common/Castle Story/Castle Story_Data")

# Game sprite name -> (output name, recolour)
ICONS = {
    "Raw Iron": ("coal", "coal"),
    "Stockpiled Raw Iron": ("coal_stockpiled", "coal"),
    "Iron": ("steel", "steel"),
    "Stockpiled Iron": ("steel_stockpiled", "steel"),
    "Raw Blue Crystal": ("dark_crystal", "dark"),
    "Stockpiled Raw Blue Crystal": ("dark_crystal_stockpiled", "dark"),
}


def is_board(r, g, b):
    """The pallet's brown wood: clearly warmer than it is blue."""
    h, s, v = colorsys.rgb_to_hsv(r, g, b)
    return s > 0.25 and 0.03 < h < 0.15


def recolour(image, kind):
    out = image.convert("RGBA")
    pixels = out.load()
    for y in range(out.height):
        for x in range(out.width):
            r, g, b, a = (c / 255.0 for c in pixels[x, y])
            if a == 0.0 or is_board(r, g, b):
                continue
            light = 0.299 * r + 0.587 * g + 0.114 * b
            if light < 0.08:
                continue  # outlines stay black
            if kind == "coal":
                # Dark lumps with a faint blue-grey shine on the highlights.
                v = 0.07 + 0.55 * light ** 1.6
                rgb = (v * 0.95, v * 0.97, v * 1.05)
            elif kind == "dark":
                # Deep violet crystal, its highlights a glowing magenta.
                v = 0.06 + 0.8 * light ** 1.4
                rgb = (min(1.0, v * 0.85), v * 0.35, min(1.0, v * 1.1))
            else:
                # Light silver with a cool tint, brighter than the iron ingot.
                v = min(1.0, 0.25 + 0.85 * light)
                rgb = (v * 0.88, v * 0.94, min(1.0, v * 1.06))
            pixels[x, y] = tuple(int(round(c * 255.0)) for c in rgb) + (int(round(a * 255.0)),)
    return out


def main():
    data = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_DATA
    env = UnityPy.load(os.path.join(data, "resources.assets"))
    os.makedirs(OUT, exist_ok=True)
    # Some names are used twice (an old "deprecated" sprite); the largest one is the icon in use.
    found = {}
    for obj in env.objects:
        if obj.type.name != "Sprite":
            continue
        sprite = obj.read()
        if sprite.m_Name not in ICONS:
            continue
        image = sprite.image
        if sprite.m_Name not in found or image.width * image.height > found[sprite.m_Name].width * found[sprite.m_Name].height:
            found[sprite.m_Name] = image
    for game_name, image in found.items():
        name, kind = ICONS[game_name]
        path = os.path.join(OUT, name + ".png")
        recolour(image, kind).save(path)
        print("wrote", os.path.relpath(path, os.path.join(HERE, "..")))
    done = set(found)
    missing = set(ICONS) - done
    if missing:
        sys.exit("game sprites not found: " + ", ".join(sorted(missing)))


if __name__ == "__main__":
    main()
