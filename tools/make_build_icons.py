#!/usr/bin/env python3
"""Renders the build menu icons of the mod's buildings and blocks in CastleStoryPlus/Assets/Build/.

In the style of the game's own build icons: an isometric drawing of the thing itself, flat colours with a lit top,
a mid front and a dark right side, a dark outline and no background. One 128 x 128 PNG per entry. The game shows
them through CastleStoryPlus.BuildIcon("<name>") in its build menu Lua.

Needs rsvg-convert (librsvg). Run: python3 tools/make_build_icons.py
"""
import math
import os
import subprocess
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "CastleStoryPlus", "Assets", "Build")
SIZE = 128
INK = "#1b1b1f"

STONE = ("#d3d1cb", "#aeaba3", "#8d8a83")
# The game's brick icons: top, short end (front), long side (right).
GAME_STONE = ("#b3b3b3", "#4c4d4c", "#656565")
WOOD = ("#c99652", "#9c6c33", "#7a5226")
WOOD_DARK = ("#8a5d2e", "#6d4520", "#553418")
BRICK = ("#c96a4a", "#a5512f", "#863f24")
ROOF = ("#b4553a", "#93412b", "#76331f")
IRON = ("#a7adb4", "#80868d", "#62676e")
AWNING_RED = ("#e0523f", "#bd3a2b", "#9b2c20")
AWNING_WHITE = ("#f5efe2", "#d8d0bf", "#bcb3a0")
DARK = ("#3a2a1e", "#2c2017", "#211811")
FIRE = ("#ffd34d", "#ff9a2e", "#e0701a")
CRYSTAL = ("#9be0ff", "#58c4ff", "#2a8fd0")
DARK_CRYSTAL = ("#c78bff", "#9a4ee0", "#6e2ab0")
BOOKS = [("#c05048", "#9a3a32", "#7a2a24"), ("#4f8a56", "#3a6a40", "#2a5030"), ("#4f6ea8", "#3a5488", "#2a3e68"),
         ("#c8a050", "#a07c38", "#7c5e28")]
PAPER = ("#fbf4e0", "#e6dcc0", "#cbbf9e")


class Scene:
    """Isometric drawing: x to the right-down, z to the left-down, y up. Shapes are drawn in the order given."""

    def __init__(self, span=None):
        self.polys = []
        # A fixed size in projected units, so blocks of different sizes keep their relative size.
        self.span = span

    @staticmethod
    def project(x, y, z):
        return ((x - z) * math.cos(math.radians(30)), (x + z) * 0.5 - y)

    def poly(self, points, fill, width=3.0):
        self.polys.append(([self.project(*p) for p in points], fill, width))

    def box(self, x, y, z, w, h, d, colours, width=3.0):
        top, front, right = colours
        self.poly([(x, y + h, z), (x + w, y + h, z), (x + w, y + h, z + d), (x, y + h, z + d)], top, width)
        self.poly([(x, y, z + d), (x + w, y, z + d), (x + w, y + h, z + d), (x, y + h, z + d)], front, width)
        self.poly([(x + w, y, z), (x + w, y, z + d), (x + w, y + h, z + d), (x + w, y + h, z)], right, width)

    def gable(self, x, y, z, w, d, rise, colours, end=None):
        """A gable roof over x .. x+w, z .. z+d with the ridge along x; the front slope and the right gable end."""
        top, front, right = colours
        ridge = (z + d / 2.0)
        self.poly([(x, y, z + d), (x + w, y, z + d), (x + w, y + rise, ridge), (x, y + rise, ridge)], top)
        self.poly([(x + w, y, z), (x + w, y, z + d), (x + w, y + rise, ridge)], end or right)

    def line(self, a, b, colour, width):
        self.polys.append(([self.project(*a), self.project(*b)], colour, -width))

    def svg(self):
        xs = [p[0] for poly, _, _ in self.polys for p in poly]
        ys = [p[1] for poly, _, _ in self.polys for p in poly]
        span = self.span or max(max(xs) - min(xs), max(ys) - min(ys))
        scale = (SIZE - 16) / span
        ox = (SIZE - (max(xs) - min(xs)) * scale) / 2 - min(xs) * scale
        oy = (SIZE - (max(ys) - min(ys)) * scale) / 2 - min(ys) * scale
        parts = []
        for poly, fill, width in self.polys:
            pts = " ".join(f"{px * scale + ox:.1f},{py * scale + oy:.1f}" for px, py in poly)
            if width < 0:
                parts.append(f'<polyline points="{pts}" fill="none" stroke="{fill}" stroke-width="{-width}" stroke-linecap="round"/>')
            else:
                parts.append(f'<polygon points="{pts}" fill="{fill}" stroke="{INK}" stroke-width="{width}" stroke-linejoin="round"/>')
        return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{SIZE}" height="{SIZE}" viewBox="0 0 {SIZE} {SIZE}">'
                + "".join(parts) + "</svg>")


# ---------- blocks ----------

# The projected size of the largest block (4 x 1 x 2), which fills the icon.
BLOCK_SPAN = (4 + 2) * math.cos(math.radians(30))

class GameView(Scene):
    """The view of the game's own brick icons: the length (z) runs to the right and a little up, the width (x) to the
    left and a little up, and blocks are drawn a bit taller than wide. The visible faces are the top, the long side
    at x = 0 (right) and the short end at z = 0 (left)."""

    @staticmethod
    def project(x, y, z):
        return (z * 1.0 - x * 0.98, -z * 0.33 - x * 0.31 - y * 1.3)

    def brick(self, x, y, z, w, h, d, colours, width=4.0):
        top, end, side = colours
        self.poly([(x, y + h, z), (x + w, y + h, z), (x + w, y + h, z + d), (x, y + h, z + d)], top, width)
        self.poly([(x, y, z), (x + w, y, z), (x + w, y + h, z), (x, y + h, z)], end, width)
        self.poly([(x, y, z), (x, y, z + d), (x, y + h, z + d), (x, y + h, z)], side, width)


# The projected size of the largest brick (2 x 4), which fills the icon.
GAME_SPAN = 4 * 1.0 + 2 * 0.98


def stone(w, d, h=1):
    """Like the game's own brick icons: flat grey faces (light top, dark short end, mid long side), a thick outline.
    One whole stone the size of the block, as the blocks are drawn in the game."""
    s = GameView(GAME_SPAN)
    length, width = max(w, d), min(w, d)
    s.brick(0, 0, 0, width, h, length, GAME_STONE)
    return s


def slab(w, d):
    s = Scene(BLOCK_SPAN)
    s.box(0, 0, 0, w, 0.45, d, WOOD)
    # boards along the long side, two per block
    if w >= d:
        for i in range(1, 2 * d):
            s.line((0, 0.45, i / 2), (w, 0.45, i / 2), "#8a5d2e", 2.5)
    else:
        for i in range(1, 2 * w):
            s.line((i / 2, 0.45, 0), (i / 2, 0.45, d), "#8a5d2e", 2.5)
    return s


def ladder():
    s = Scene(BLOCK_SPAN)
    # a stone wall with the ladder leaning on its front
    s.box(0, 0, 0, 2, 3, 1, STONE)
    for i in range(6):
        s.box(0.45, 0.35 + i * 0.5, 1.0, 1.1, 0.1, 0.08, WOOD_DARK)
    for x in (0.45, 1.4):
        s.box(x, 0, 1.0, 0.15, 3.1, 0.15, WOOD)
    return s


# ---------- buildings ----------

def warehouse():
    s = Scene()
    s.box(0, 0, 0, 9, 0.3, 6, STONE)
    # the hall: brick below, planks above, the cart doors open, a gable roof
    s.box(0, 0.3, 0, 7, 1.1, 6, BRICK)
    s.box(0, 1.4, 0, 7, 1.9, 6, WOOD)
    s.poly([(2, 0.3, 6.02), (5, 0.3, 6.02), (5, 2.7, 6.02), (2, 2.7, 6.02)], DARK[0])
    s.gable(-0.3, 3.3, -0.3, 7.6, 6.6, 1.9, ROOF, end=WOOD[2])
    # the brick works on the right: a lean-to on posts over a pallet of bricks and a stone heap
    s.box(7.4, 0.3, 3.8, 1.2, 0.8, 1.4, BRICK)
    s.box(7.4, 0.3, 0.8, 1.0, 0.6, 1.2, STONE)
    s.box(8.7, 0.3, 5.6, 0.25, 2.2, 0.25, WOOD_DARK)
    s.poly([(7, 3.2, -0.2), (9.2, 2.5, -0.2), (9.2, 2.5, 6.2), (7, 3.2, 6.2)], ROOF[0])
    return s


def smithy():
    s = Scene()
    s.box(0, 0, 0, 4, 0.3, 3, STONE)
    s.box(0, 0.3, 0, 4, 2, 1, STONE)
    s.box(0, 2.3, 0, 1, 2.2, 1, STONE)
    s.box(0.2, 0.3, 1, 1.2, 0.6, 0.9, BRICK)
    s.box(0.4, 0.9, 1.1, 0.8, 0.25, 0.6, FIRE)
    s.poly([(-0.1, 2.4, 1), (4.2, 2.4, 1), (4.2, 3.2, 3.2), (-0.1, 3.2, 3.2)], ROOF[0])
    s.box(3.8, 0.3, 2.8, 0.2, 2.9, 0.2, WOOD_DARK)
    s.box(2, 0.3, 1.6, 0.8, 0.4, 0.6, IRON)
    s.box(1.8, 0.7, 1.6, 1.2, 0.25, 0.6, IRON)
    return s


def armoury():
    s = Scene()
    s.box(0, 0, 0, 4, 0.3, 3, STONE)
    s.box(0, 0.3, 0, 4, 2.6, 0.15, WOOD)
    s.box(0, 0.3, 0, 0.15, 2.6, 3, WOOD)
    # armour stand with a helmet and a shield on the wall
    s.box(1.6, 0.3, 1.2, 0.15, 1.3, 0.15, WOOD_DARK)
    s.box(1.3, 1.0, 1.05, 0.75, 0.75, 0.45, IRON)
    s.box(1.45, 1.75, 1.1, 0.45, 0.4, 0.35, IRON)
    s.poly([(2.6, 1.4, 0.16), (3.4, 1.4, 0.16), (3.4, 2.1, 0.16), (3.0, 2.4, 0.16), (2.6, 2.1, 0.16)], AWNING_RED[0])
    for x, z in ((3.85, 2.85), (0, 2.85), (3.85, 0)):
        s.box(x, 0.3, z, 0.15, 2.6, 0.15, WOOD_DARK)
    s.gable(-0.2, 2.9, -0.2, 4.4, 3.4, 1.0, ROOF, end=WOOD[2])
    return s


def market():
    s = Scene()
    s.box(0, 0, 0, 4, 0.3, 3, STONE)
    s.box(0.2, 0.3, 0.2, 3.6, 1.3, 0.4, WOOD_DARK)
    for x in (0, 3.8):
        s.box(x, 0.3, 0, 0.2, 2.3, 0.2, WOOD_DARK)
        s.box(x, 0.3, 2.8, 0.2, 2.3, 0.2, WOOD_DARK)
    s.box(0.3, 0.3, 2.3, 3.4, 0.8, 0.3, WOOD)
    s.box(0.5, 1.1, 2.2, 0.5, 0.35, 0.5, WOOD)
    s.box(1.5, 1.1, 2.2, 0.6, 0.25, 0.4, BRICK)
    s.box(2.7, 1.1, 2.25, 0.35, 0.5, 0.35, CRYSTAL)
    # striped awning
    for i in range(8):
        colours = AWNING_RED if i % 2 == 0 else AWNING_WHITE
        x = i * 0.5
        s.poly([(x, 2.6, 2.6), (x + 0.5, 2.6, 2.6), (x + 0.5, 2.1, 3.5), (x, 2.1, 3.5)], colours[0], 2.5)
    s.gable(-0.2, 2.6, -0.2, 4.4, 3.0, 1.2, ROOF, end=WOOD[2])
    return s


def research():
    s = Scene()
    s.box(0, 0, 0, 4, 0.3, 3, STONE)
    s.box(0, 0.3, 0, 0.15, 2.6, 3, WOOD)
    # bookshelf along the back, full of coloured books
    s.box(0.15, 0.3, 0, 3.7, 2.2, 0.15, WOOD_DARK)
    for row in range(3):
        y = 0.4 + row * 0.7
        for i in range(9):
            colours = BOOKS[(i + row * 2) % len(BOOKS)]
            height = 0.45 + ((i * 7 + row * 3) % 4) * 0.05
            s.box(0.3 + i * 0.38, y, 0.15, 0.3, height, 0.35, colours, width=1.0)
        s.box(0.15, y - 0.08, 0.15, 3.7, 0.08, 0.4, WOOD_DARK, width=1.5)
    # reading desk with an open book and the glowing dark crystal
    s.box(1.9, 0.3, 1.4, 0.2, 0.9, 0.2, WOOD_DARK)
    s.box(1.6, 1.2, 1.2, 0.8, 0.1, 0.6, WOOD)
    s.box(1.65, 1.3, 1.25, 0.34, 0.05, 0.5, PAPER, width=1.5)
    s.box(2.01, 1.3, 1.25, 0.34, 0.05, 0.5, PAPER, width=1.5)
    s.box(2.8, 0.3, 1.0, 0.4, 0.8, 0.4, STONE)
    s.poly([(3.0, 1.1, 1.2), (2.85, 1.5, 1.2), (3.0, 1.95, 1.2), (3.15, 1.5, 1.2)], DARK_CRYSTAL[1])
    # a pile of books in the front corner
    for i, colours in enumerate(BOOKS[:3]):
        s.box(3.1 + i * 0.03, 0.3 + i * 0.16, 2.3, 0.6 - i * 0.06, 0.16, 0.45, colours, width=1.5)
    for x, z in ((3.85, 2.85), (0, 2.85), (3.85, 0)):
        s.box(x, 0.3, z, 0.15, 2.6, 0.15, WOOD_DARK)
    # only the back slope of the roof, so the shelves and the desk show
    s.poly([(-0.2, 2.9, -0.2), (4.2, 2.9, -0.2), (4.2, 3.6, 0.9), (-0.2, 3.6, 0.9)], ROOF[0])
    return s


ICONS = {
    "stone_brick_2x2": lambda: stone(2, 2),
    "stone_brick_2x4": lambda: stone(4, 2),
    "stone_brick_1x3": lambda: stone(3, 1),
    "stone_brick_1x4": lambda: stone(4, 1),
    "stone_brick_tall": lambda: stone(1, 1, 2),
    "wood_slab_1x1": lambda: slab(1, 1),
    "wood_slab_2x1": lambda: slab(2, 1),
    "wood_slab_2x2": lambda: slab(2, 2),
    "wood_slab_2x4": lambda: slab(4, 2),
    "wood_ladder": ladder,
    "warehouse": warehouse,
    "smithy": smithy,
    "armoury": armoury,
    "market": market,
    "research": research,
}


def main():
    os.makedirs(OUT, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        for name, draw in ICONS.items():
            src = os.path.join(tmp, name + ".svg")
            with open(src, "w") as f:
                f.write(draw().svg())
            subprocess.run(["rsvg-convert", "-w", str(SIZE), "-h", str(SIZE), "-o", os.path.join(OUT, name + ".png"), src], check=True)
    print("wrote", len(ICONS), "icons to", os.path.normpath(OUT))


if __name__ == "__main__":
    main()
