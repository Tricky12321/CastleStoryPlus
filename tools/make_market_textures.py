#!/usr/bin/env python3
"""Renders the market building's textures in CastleStoryPlus/Assets/Market/.

Every texture is 128 x 128, tiles seamlessly and covers one block (1 x 1 world unit), in the soft, painted look
of the game's own buildings: low-contrast colour variation, clear shapes (boards, stones, shingles), no fine noise.
Wood grain runs along the texture's width; the model turns it along each part's long side.

    python3 tools/make_market_textures.py
"""
import os

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "CastleStoryPlus", "Assets", "Market")
SIZE = 128
RNG = np.random.default_rng(1729)


def hex_rgb(value):
    return np.array([(value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF], dtype=np.float64) / 255.0


def noise(cells_x, cells_y, size=SIZE):
    """Smooth value noise in 0..1 that wraps around both edges (cosine-interpolated random grid)."""
    grid = RNG.random((cells_y, cells_x))
    ys = np.arange(size) * cells_y / size
    xs = np.arange(size) * cells_x / size
    y0 = np.floor(ys).astype(int)
    x0 = np.floor(xs).astype(int)
    fy = (1 - np.cos((ys - y0) * np.pi)) / 2
    fx = (1 - np.cos((xs - x0) * np.pi)) / 2
    y1 = (y0 + 1) % cells_y
    x1 = (x0 + 1) % cells_x
    a = grid[np.ix_(y0, x0)]
    b = grid[np.ix_(y0, x1)]
    c = grid[np.ix_(y1, x0)]
    d = grid[np.ix_(y1, x1)]
    top = a + (b - a) * fx[None, :]
    bottom = c + (d - c) * fx[None, :]
    return top + (bottom - top) * fy[:, None]


def shade(base, amount):
    """base colour times (1 + amount), amount an array of -1..1 values."""
    return np.clip(base[None, None, :] * (1 + amount[:, :, None]), 0, 1)


def save(pixels, name):
    os.makedirs(OUT, exist_ok=True)
    image = Image.fromarray((np.clip(pixels, 0, 1) * 255).astype(np.uint8), "RGB")
    image.save(os.path.join(OUT, name + ".png"), optimize=True)
    print("wrote", name + ".png")


def wood(base, boards=4):
    """Horizontal boards with long grain streaks, dark seams and a few knots."""
    y = np.arange(SIZE)[:, None].repeat(SIZE, 1)
    x = np.arange(SIZE)[None, :].repeat(SIZE, 0)
    height = SIZE // boards
    board = y // height
    within = (y % height) / height
    # Each board its own tone; long, stretched grain along x.
    tone = RNG.uniform(-0.09, 0.09, boards)[board]
    grain = (noise(4, 24) - 0.5) * 0.16 + (noise(2, 48) - 0.5) * 0.10
    amount = tone + grain
    # Soft rounded board edges and a dark seam between boards.
    amount -= 0.10 * (np.abs(within - 0.5) * 2) ** 6
    amount -= 0.28 * (within < 1.5 / height)
    # Butt joints: one per board, staggered.
    joints = RNG.integers(0, SIZE, boards)
    joint = np.abs(((x - joints[board]) + SIZE // 2) % SIZE - SIZE // 2) < 1
    amount -= 0.22 * joint
    pixels = shade(base, amount)
    # Knots: small darker rings.
    for _ in range(3):
        cx, cy = RNG.integers(0, SIZE, 2)
        dx = ((x - cx + SIZE // 2) % SIZE - SIZE // 2) / 1.0
        dy = ((y - cy + SIZE // 2) % SIZE - SIZE // 2) * 2.2
        r = np.sqrt(dx * dx + dy * dy)
        pixels *= (1 - 0.22 * np.exp(-(r / 3.0) ** 2))[:, :, None]
    return pixels


def stone(base, rows=4, mortar_color=0xB8B2A4):
    """Irregular dressed stones in staggered rows, bevelled edges, light mortar."""
    y = np.arange(SIZE)[:, None].repeat(SIZE, 1)
    x = np.arange(SIZE)[None, :].repeat(SIZE, 0)
    height = SIZE // rows
    row = y // height
    # Stones per row of varying length; offsets stagger the rows.
    stone_id = np.zeros((SIZE, SIZE), dtype=int)
    edge_x = np.full((SIZE, SIZE), 99.0)
    for r in range(rows):
        cuts = np.sort(RNG.choice(np.arange(8, SIZE, 8), 2 + r % 2, replace=False))
        cuts = (cuts + r * 23) % SIZE
        cuts = np.sort(cuts)
        mask = row == r
        xs = x[mask]
        ids = np.searchsorted(cuts, xs, side="right") % len(cuts)
        stone_id[mask] = ids + r * 10
        dist = np.min(np.abs(((xs[:, None] - cuts[None, :]) + SIZE // 2) % SIZE - SIZE // 2), axis=1)
        edge_x[mask] = dist
    edge_y = np.minimum(y % height, height - 1 - y % height).astype(np.float64)
    edge = np.minimum(edge_x, edge_y)
    tones = {i: RNG.uniform(-0.08, 0.08) for i in np.unique(stone_id)}
    tone = np.vectorize(tones.get)(stone_id)
    amount = tone + (noise(8, 8) - 0.5) * 0.14 + (noise(24, 24) - 0.5) * 0.05
    # Bevel: lighter top edge, darker bottom edge of each stone.
    amount += 0.10 * ((y % height) < 4) - 0.12 * ((y % height) > height - 5)
    pixels = shade(base, amount)
    mortar = edge < 1.5
    pixels[mortar] = hex_rgb(mortar_color) * (0.95 + 0.05 * noise(16, 16)[mortar][:, None])
    return pixels


def shingles(base, rows=4, per_row=4):
    """Overlapping wooden shingles: staggered rows, each lit at the bottom, shadowed under the row above."""
    y = np.arange(SIZE)[:, None].repeat(SIZE, 1)
    x = np.arange(SIZE)[None, :].repeat(SIZE, 0)
    height = SIZE // rows
    width = SIZE // per_row
    row = y // height
    within_y = (y % height) / height
    shifted = (x + (row % 2) * width // 2) % SIZE
    column = shifted // width
    within_x = (shifted % width) / width
    tone = RNG.uniform(-0.10, 0.10, (rows, per_row))[row, column]
    amount = tone + (noise(16, 6) - 0.5) * 0.10
    # Darker at the top (under the row above), lighter towards the rounded lower edge.
    amount += -0.25 * (1 - within_y) ** 3 + 0.08 * within_y
    rounded = np.abs(within_x - 0.5) * 2
    lower_edge = within_y > 1 - 0.18 * (1 - rounded ** 2)
    amount -= 0.30 * lower_edge
    gap = (shifted % width) < 2
    amount -= 0.30 * gap
    return shade(base, amount)


def stripes(colors, count=4):
    """Canvas with wide vertical stripes and a faint weave."""
    x = np.arange(SIZE)[None, :].repeat(SIZE, 0)
    y = np.arange(SIZE)[:, None].repeat(SIZE, 1)
    stripe = (x * count // SIZE) % len(colors)
    base = np.stack([hex_rgb(c) for c in colors])[stripe]
    weave = 0.03 * (((x + y) % 4) < 2) - 0.015
    folds = (noise(3, 2) - 0.5) * 0.12
    return np.clip(base * (1 + weave + folds)[:, :, None], 0, 1)


def metal(base):
    """Dark wrought iron: mottled, with rivets."""
    y = np.arange(SIZE)[:, None].repeat(SIZE, 1)
    x = np.arange(SIZE)[None, :].repeat(SIZE, 0)
    amount = (noise(6, 6) - 0.5) * 0.30 + (noise(20, 20) - 0.5) * 0.10
    pixels = shade(base, amount)
    for cx in (16, 80):
        for cy in (16, 80):
            r = np.sqrt((x - cx) ** 2 + (y - cy) ** 2)
            pixels *= (1 + 0.35 * np.exp(-(r / 3.5) ** 2) - 0.2 * np.exp(-((r - 5) / 1.5) ** 2))[:, :, None]
    return pixels


def crystal(base):
    """Glowing gem: bright facets."""
    y = np.arange(SIZE)[:, None].repeat(SIZE, 1)
    x = np.arange(SIZE)[None, :].repeat(SIZE, 0)
    facets = ((x // 32 + y // 32) % 3) * 0.10 - 0.05
    sparkle = (noise(8, 8) - 0.5) * 0.20
    return shade(base, facets + sparkle + 0.08)


def main():
    save(wood(hex_rgb(0xC69A62)), "wood_light")
    save(wood(hex_rgb(0x9C6B3D)), "wood")
    save(wood(hex_rgb(0x6A4527), boards=2), "wood_dark")
    save(stone(hex_rgb(0x9E9A90)), "stone")
    save(stone(hex_rgb(0xB9563A), rows=8, mortar_color=0xC9BFAE), "brick")
    save(shingles(hex_rgb(0xA4492C)), "roof")
    save(stripes([0xC23B2C, 0xEFE4CC]), "awning")
    save(metal(hex_rgb(0x4A4D52)), "metal")
    save(metal(hex_rgb(0x9AA0A6)), "iron")
    save(crystal(hex_rgb(0x58B8E8)), "crystal_blue")
    save(crystal(hex_rgb(0xF0A23A)), "crystal_orange")


if __name__ == "__main__":
    main()
