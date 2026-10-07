#!/usr/bin/env python3
"""Renders the weapon, armour and research upgrade icons in CastleStoryPlus/Assets/Upgrades/.

One icon per upgrade line and tier (<line>_t<tier>.png, 128 x 128), plus one icon per building
(building_<name>.png). Every icon is a rounded badge:

- background colour = category: warm red for weapons (the forge), deep blue for armour (the armoury), purple
  for the research station
- frame colour and stars = tier: bronze + 1 star, silver + 2 stars, gold + 3 stars
- metal of the item itself = tier: iron, steel, blue crystal

Also writes a preview sheet with all lines and tiers side by side (path given as the first argument,
default upgrade_icons_preview.png in the current directory).

Needs rsvg-convert (librsvg) and Pillow. Font: Luckiest Guy, tools/fonts/LuckiestGuy-Regular.ttf.
Run: python3 tools/make_upgrade_icons.py [preview.png]
"""
import os
import subprocess
import sys
import tempfile
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
FONT = os.path.join(HERE, "fonts", "LuckiestGuy-Regular.ttf")
OUT = os.path.join(HERE, "..", "CastleStoryPlus", "Assets", "Upgrades")
SIZE = 128

INK = "#141414"

# Per tier: item metal (base, light, dark) and frame (base, dark)
TIERS = {
    1: {"metal": "#9aa3ab", "light": "#d3d9de", "dark": "#5b636b", "frame": "#c27a3e", "frame_dark": "#6e3f17", "name": "Iron"},
    2: {"metal": "#cdd6de", "light": "#ffffff", "dark": "#7b8794", "frame": "#d9dfe5", "frame_dark": "#6f7a85", "name": "Steel"},
    3: {"metal": "#58c4ff", "light": "#dcf5ff", "dark": "#1b68a8", "frame": "#ffc20e", "frame_dark": "#9a6500", "name": "Crystal"},
}

CATEGORY_BG = {
    "weapon": ("#8a3a2c", "#3d1813"),
    "armour": ("#2f5384", "#14233b"),
    "market": ("#7a5a1e", "#33240a"),
    "storage": ("#5d6672", "#23282f"),
    "research": ("#5e3a8a", "#24133b"),
}

WOOD = "#8b5a2b"
WOOD_LIGHT = "#b07a40"
LEATHER = "#6b3f1f"
GOLD = "#e6a823"
RED = "#d6453a"
CLOTH = "#cfa86a"
CLOTH_DARK = "#9a7740"


def badge(category, tier, inner):
    bg_light, bg_dark = CATEGORY_BG[category]
    t = TIERS[tier] if tier else {"frame": "#ffc20e", "frame_dark": "#9a6500"}
    stars = ""
    if tier:
        spacing = 20
        x0 = 64 - (tier - 1) * spacing / 2
        for i in range(tier):
            stars += star(x0 + i * spacing, 113, 9, t["frame"])
    return f"""<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="{SIZE}" height="{SIZE}" viewBox="0 0 128 128">
  <defs>
    <radialGradient id="bg" cx="50%" cy="38%" r="70%">
      <stop offset="0%" stop-color="{bg_light}"/>
      <stop offset="100%" stop-color="{bg_dark}"/>
    </radialGradient>
    <filter id="glow" x="-50%" y="-50%" width="200%" height="200%">
      <feGaussianBlur stdDeviation="3.5" result="b"/>
      <feMerge><feMergeNode in="b"/><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge>
    </filter>
    <filter id="shadow" x="-20%" y="-20%" width="140%" height="140%">
      <feDropShadow dx="2" dy="3" stdDeviation="1.5" flood-color="#000" flood-opacity="0.45"/>
    </filter>
    <pattern id="rings" width="9" height="8" patternUnits="userSpaceOnUse">
      <circle cx="4.5" cy="4" r="3.1" fill="none" stroke="{TIERS[tier or 2]['dark']}" stroke-width="1.6"/>
    </pattern>
  </defs>
  <rect x="5" y="5" width="118" height="118" rx="20" fill="url(#bg)" stroke="{INK}" stroke-width="4"/>
  <rect x="10" y="10" width="108" height="108" rx="16" fill="none" stroke="{t['frame']}" stroke-width="4"/>
  <rect x="13" y="13" width="102" height="102" rx="13" fill="none" stroke="{t['frame_dark']}" stroke-width="1.5" opacity="0.8"/>
  <g filter="url(#shadow)" stroke="{INK}" stroke-width="4" stroke-linejoin="round" stroke-linecap="round">
{inner}
  </g>
  {stars}
</svg>"""


def star(cx, cy, r, fill):
    import math
    pts = []
    for i in range(10):
        a = -math.pi / 2 + i * math.pi / 5
        rr = r if i % 2 == 0 else r * 0.45
        pts.append(f"{cx + rr * math.cos(a):.1f},{cy + rr * math.sin(a):.1f}")
    return f'<polygon points="{" ".join(pts)}" fill="{fill}" stroke="{INK}" stroke-width="2.5" stroke-linejoin="round"/>'


def metal_stroke(path, t, width):
    """A thick metal line with a black outline (drawn as two strokes)."""
    return (f'<path d="{path}" fill="none" stroke="{INK}" stroke-width="{width + 8}"/>'
            f'<path d="{path}" fill="none" stroke="{t["metal"]}" stroke-width="{width}"/>'
            f'<path d="{path}" fill="none" stroke="{t["light"]}" stroke-width="{max(width / 3, 1.5)}" opacity="0.7"/>')


# ---------- weapon lines ----------

def sword(t):
    return f"""<g transform="rotate(45 64 60)">
    <path d="M64 8 L72 20 L72 76 L56 76 L56 20 Z" fill="{t['metal']}"/>
    <path d="M64 14 L64 72" stroke="{t['light']}" stroke-width="3"/>
    <path d="M66 18 L70 22 L70 74 L66 74 Z" fill="{t['dark']}" stroke="none" opacity="0.6"/>
    <rect x="62" y="84" width="4" height="0" fill="none"/>
    <rect x="59" y="84" width="10" height="16" rx="2" fill="{LEATHER}"/>
    <rect x="40" y="76" width="48" height="10" rx="5" fill="{GOLD}"/>
    <circle cx="64" cy="105" r="7" fill="{GOLD}"/>
  </g>
  <path d="M100 18 L103 26 L111 29 L103 32 L100 40 L97 32 L89 29 L97 26 Z" fill="#ffffff" stroke-width="2.5"/>"""


def axe(t):
    return f"""<path d="M24 74 L70 74 L70 100 L24 100 Z" fill="#b5523b"/>
  <path d="M24 87 L70 87 M47 74 L47 87 M36 87 L36 100 M58 87 L58 100" stroke-width="3" fill="none"/>
  <path d="M44 74 L50 82 L45 88 L52 96" stroke="#ffe7c2" stroke-width="2.5" fill="none"/>
  <g transform="rotate(-35 76 60)">
    <rect x="72" y="22" width="9" height="84" rx="3" fill="{WOOD}"/>
    <path d="M81 26 C104 20 114 40 108 62 L81 52 Z" fill="{t['metal']}"/>
    <path d="M84 30 C100 28 106 40 104 54" stroke="{t['light']}" stroke-width="3" fill="none"/>
    <path d="M72 30 L56 38 L72 46 Z" fill="{t['dark']}"/>
  </g>"""


def arrow(t):
    return f"""<g transform="rotate(45 64 64)">
    <rect x="61" y="28" width="6" height="76" rx="2" fill="{WOOD_LIGHT}"/>
    <path d="M64 6 L78 34 L64 28 L50 34 Z" fill="{t['metal']}"/>
    <path d="M64 12 L64 26" stroke="{t['light']}" stroke-width="2.5"/>
    <path d="M61 78 L44 92 L44 114 L61 100 Z" fill="{RED}"/>
    <path d="M67 78 L84 92 L84 114 L67 100 Z" fill="#f4efe6"/>
  </g>"""


def target(t):
    return f"""<circle cx="58" cy="56" r="40" fill="#f4efe6" stroke-width="5"/>
  <circle cx="58" cy="56" r="40" fill="none" stroke="{t['metal']}" stroke-width="4"/>
  <circle cx="58" cy="56" r="29" fill="{RED}" stroke-width="3"/>
  <circle cx="58" cy="56" r="19" fill="#f4efe6" stroke-width="3"/>
  <circle cx="58" cy="56" r="9" fill="{RED}" stroke-width="3"/>
  <path d="M58 56 L104 92" stroke="{INK}" stroke-width="9"/>
  <path d="M58 56 L104 92" stroke="{WOOD_LIGHT}" stroke-width="4"/>
  <path d="M92 83 L92 100 L104 92 Z M98 78 L112 80 L104 92 Z" fill="{RED}" stroke-width="3"/>"""


def crossbow(t):
    limbs = "M18 48 Q64 18 110 48"
    return f"""{metal_stroke(limbs, t, 9)}
  <path d="M20 50 L64 66 L108 50" stroke="{INK}" stroke-width="5" fill="none"/>
  <path d="M20 50 L64 66 L108 50" stroke="#f4efe6" stroke-width="2" fill="none"/>
  <rect x="57" y="26" width="14" height="70" rx="4" fill="{WOOD}"/>
  <path d="M64 18 L70 30 L58 30 Z" fill="{t['metal']}" stroke-width="3"/>
  <path d="M64 30 L64 68" stroke="{t['light']}" stroke-width="3"/>
  <circle cx="64" cy="98" r="12" fill="{t['metal']}"/>
  <circle cx="64" cy="98" r="4" fill="{t['dark']}" stroke-width="2.5"/>
  <path d="M64 98 L88 92" stroke="{INK}" stroke-width="8"/>
  <path d="M64 98 L88 92" stroke="{t['metal']}" stroke-width="4"/>
  <rect x="84" y="82" width="8" height="16" rx="3" fill="{LEATHER}" stroke-width="3"/>"""


# ---------- armour lines ----------

SHIRT = "M42 22 L54 16 Q64 26 74 16 L86 22 L110 46 L98 60 L90 52 L90 108 L38 108 L38 52 L30 60 L18 46 Z"


def chainmail(t):
    return f"""<defs><clipPath id="shirt"><path d="{SHIRT}"/></clipPath></defs>
  <path d="{SHIRT}" fill="{t['metal']}"/>
  <rect x="10" y="10" width="108" height="108" fill="url(#rings)" stroke="none" clip-path="url(#shirt)"/>
  <path d="M54 16 Q64 26 74 16" fill="none" stroke="{LEATHER}" stroke-width="5"/>
  <rect x="38" y="70" width="52" height="9" fill="{LEATHER}" stroke-width="3"/>
  <rect x="58" y="69" width="12" height="11" rx="2" fill="{GOLD}" stroke-width="3"/>
  <path d="{SHIRT}" fill="none"/>"""


def gambeson(t):
    quilt = " ".join(f"M{x} 20 L{x + 60} 110" for x in range(-10, 100, 14))
    quilt += " " + " ".join(f"M{x} 20 L{x - 60} 110" for x in range(40, 160, 14))
    rivets = "".join(f'<circle cx="{x}" cy="{y}" r="3" fill="{t["metal"]}" stroke-width="1.5"/>'
                     for x, y in ((46, 40), (82, 40), (46, 96), (82, 96), (64, 60), (64, 84)))
    return f"""<defs><clipPath id="gamb"><path d="{SHIRT}"/></clipPath></defs>
  <path d="{SHIRT}" fill="{CLOTH}"/>
  <path d="{quilt}" stroke="{CLOTH_DARK}" stroke-width="2" fill="none" clip-path="url(#gamb)"/>
  <path d="M54 16 Q64 26 74 16" fill="none" stroke="{CLOTH_DARK}" stroke-width="5"/>
  {rivets}
  <path d="{SHIRT}" fill="none"/>
  <g transform="rotate(-30 104 30)">
    <path d="M90 30 L114 30" stroke="{INK}" stroke-width="8"/>
    <path d="M90 30 L114 30" stroke="{WOOD_LIGHT}" stroke-width="3"/>
    <path d="M84 30 L94 24 L94 36 Z" fill="#9aa3ab" stroke-width="2.5"/>
  </g>
  <path d="M84 40 L78 44 M88 46 L84 52" stroke="#ffffff" stroke-width="3"/>"""


def helmet(t):
    return f"""<path d="M64 10 C74 8 86 14 82 26" fill="none" stroke="{INK}" stroke-width="12"/>
  <path d="M64 10 C74 8 86 14 82 26" fill="none" stroke="{RED}" stroke-width="7"/>
  <path d="M26 74 Q26 22 64 22 Q102 22 102 74 L102 100 Q64 110 26 100 Z" fill="{t['metal']}"/>
  <path d="M40 42 Q46 30 62 28" fill="none" stroke="{t['light']}" stroke-width="4"/>
  <path d="M64 24 L64 100" stroke="{t['dark']}" stroke-width="3"/>
  <rect x="32" y="56" width="64" height="10" rx="3" fill="{INK}" stroke-width="2"/>
  <rect x="58" y="66" width="12" height="30" rx="2" fill="{t['dark']}" stroke-width="3"/>
  <g fill="{INK}" stroke="none">
    <circle cx="42" cy="82" r="2.5"/><circle cx="42" cy="90" r="2.5"/>
    <circle cx="86" cy="82" r="2.5"/><circle cx="86" cy="90" r="2.5"/>
  </g>"""


SHIELD = "M24 20 L104 20 L104 56 Q104 94 64 112 Q24 94 24 56 Z"


def shield(t):
    return f"""<path d="{SHIELD}" fill="{t['metal']}" stroke-width="5"/>
  <path d="M34 29 L94 29 L94 56 Q94 86 64 101 Q34 86 34 56 Z" fill="#2f5fa8" stroke-width="3"/>
  <path d="M64 29 L64 101 M34 54 L94 54" stroke="{GOLD}" stroke-width="8"/>
  <path d="M64 29 L64 101 M34 54 L94 54" stroke="{INK}" stroke-width="0"/>
  <path d="M28 24 L46 24" stroke="{t['light']}" stroke-width="3"/>
  <g fill="{t['light']}" stroke-width="2">
    <circle cx="30" cy="26" r="3"/><circle cx="98" cy="26" r="3"/>
    <circle cx="29" cy="62" r="3"/><circle cx="99" cy="62" r="3"/>
    <circle cx="64" cy="106" r="3"/>
  </g>"""


PLATE = "M34 24 Q50 32 64 28 Q78 32 94 24 L106 42 L96 58 L96 98 Q64 112 32 98 L32 58 L22 42 Z"


def warded_plate(t):
    return f"""<path d="{PLATE}" fill="{t['metal']}"/>
  <path d="M64 30 L64 106" stroke="{t['dark']}" stroke-width="3"/>
  <path d="M40 40 Q46 34 56 34" stroke="{t['light']}" stroke-width="4" fill="none"/>
  <path d="M32 76 Q64 88 96 76" stroke="{t['dark']}" stroke-width="3" fill="none"/>
  <g filter="url(#glow)">
    <path d="M64 46 L76 60 L64 74 L52 60 Z" fill="#c07bff" stroke="#3b1460" stroke-width="3"/>
    <path d="M64 52 L64 68 M58 60 L70 60" stroke="#f3e2ff" stroke-width="2.5"/>
  </g>"""


# ---------- research lines ----------

def housing(t):
    return f"""<path d="M24 60 L64 26 L104 60 Z" fill="#b4553a"/>
  <path d="M30 58 L64 30 L98 58" fill="none" stroke="{t['metal']}" stroke-width="4"/>
  <rect x="32" y="60" width="64" height="42" fill="#c99652"/>
  <rect x="56" y="74" width="16" height="28" rx="2" fill="#6d4520"/>
  <rect x="38" y="68" width="12" height="12" fill="#ffd34d" stroke-width="3"/>
  <rect x="78" y="68" width="12" height="12" fill="#ffd34d" stroke-width="3"/>
  <path d="M96 22 L96 34 M90 28 L102 28" stroke="{t['light']}" stroke-width="5"/>
  <path d="M96 22 L96 34 M90 28 L102 28" stroke="#3ccf5a" stroke-width="2" />"""


def work_methods(t):
    teeth = "".join(f'<rect x="80" y="62" width="10" height="14" rx="2" fill="{t["metal"]}" stroke-width="3" transform="rotate({a} 85 85)"/>'
                    for a in range(0, 360, 45))
    return f"""{teeth}
  <circle cx="85" cy="85" r="17" fill="{t['metal']}"/>
  <circle cx="85" cy="85" r="6" fill="{t['dark']}" stroke-width="3"/>
  <g transform="rotate(-40 50 54)">
    <rect x="46" y="30" width="8" height="70" rx="3" fill="{WOOD}"/>
    <rect x="30" y="18" width="40" height="18" rx="3" fill="{t['metal']}"/>
    <path d="M34 22 L66 22" stroke="{t['light']}" stroke-width="3"/>
  </g>"""


def stacking(t):
    crates = ""
    for x, y in ((24, 76), (64, 76), (44, 40), (84, 40)):
        crates += (f'<rect x="{x}" y="{y}" width="36" height="32" rx="2" fill="{WOOD_LIGHT}"/>'
                   f'<path d="M{x} {y} L{x + 36} {y + 32} M{x + 36} {y} L{x} {y + 32}" stroke="{WOOD}" stroke-width="3"/>'
                   f'<rect x="{x}" y="{y}" width="36" height="32" rx="2" fill="none"/>')
    return crates + f"""<path d="M96 18 L96 34 M88 26 L96 18 L104 26" fill="none" stroke="{t['light']}" stroke-width="5"/>"""


def crystal_attunement(t):
    return f"""<g filter="url(#glow)">
    <path d="M64 14 L84 46 L74 104 L54 104 L44 46 Z" fill="#9a4ee0"/>
    <path d="M64 14 L64 104 M44 46 L84 46" stroke="#e6c8ff" stroke-width="2.5"/>
  </g>
  <path d="M22 60 Q30 50 38 60 Q46 70 54 60" fill="none" stroke="{t['metal']}" stroke-width="5"/>
  <path d="M74 80 Q82 70 90 80 Q98 90 106 80" fill="none" stroke="{t['metal']}" stroke-width="5"/>"""


def light_boots(t):
    return f"""<path d="M40 24 L66 24 L66 74 Q92 76 102 88 L102 102 L36 102 L36 70 Z" fill="{LEATHER}"/>
  <path d="M36 102 L102 102 L102 94 L36 94 Z" fill="{t['metal']}" stroke-width="3"/>
  <path d="M40 34 L66 34" stroke="{t['metal']}" stroke-width="4"/>
  <path d="M30 40 Q14 30 10 48 Q20 46 26 54 Q16 58 18 70 Q28 62 36 62" fill="#f4efe6" stroke-width="3"/>
  <path d="M106 40 L118 40 M108 52 L122 52 M106 64 L116 64" stroke="{t['light']}" stroke-width="4"/>"""


def firefly_lore(t):
    return f"""<path d="M14 92 Q38 80 62 92 L62 112 Q38 100 14 112 Z" fill="#fbf4e0"/>
  <path d="M66 92 Q90 80 114 92 L114 112 Q90 100 66 112 Z" fill="#fbf4e0"/>
  <path d="M62 92 L66 92 L66 112 L62 112" fill="{LEATHER}"/>
  <g filter="url(#glow)">
    <ellipse cx="50" cy="40" rx="14" ry="9" fill="#dcf5ff" opacity="0.9" transform="rotate(-30 50 40)"/>
    <ellipse cx="78" cy="40" rx="14" ry="9" fill="#dcf5ff" opacity="0.9" transform="rotate(30 78 40)"/>
    <circle cx="64" cy="56" r="13" fill="{t['metal']}"/>
    <circle cx="64" cy="56" r="6" fill="#ffffff" stroke="none"/>
  </g>"""


def giants(t):
    return f"""<rect x="22" y="70" width="20" height="30" rx="5" fill="#c99652"/>
  <rect x="24" y="50" width="16" height="18" rx="4" fill="#d3d1cb"/>
  <rect x="56" y="44" width="40" height="58" rx="8" fill="#c99652"/>
  <rect x="60" y="12" width="32" height="30" rx="6" fill="#d3d1cb"/>
  <circle cx="70" cy="26" r="3" fill="{INK}" stroke="none"/><circle cx="82" cy="26" r="3" fill="{INK}" stroke="none"/>
  <path d="M44 84 L54 84 M50 78 L56 84 L50 90" fill="none" stroke="#ffffff" stroke-width="4"/>
  <g filter="url(#glow)"><path d="M104 18 L110 30 L104 42 L98 30 Z" fill="#9a4ee0" stroke-width="2.5"/></g>"""


def scouting(t):
    return f"""<path d="M30 104 L40 46 L64 46 L74 104 Z" fill="{WOOD}"/>
  <path d="M36 80 L68 80 M38 64 L66 64" stroke-width="3" fill="none"/>
  <path d="M30 46 L52 26 L74 46 Z" fill="#b4553a"/>
  <g transform="rotate(-25 92 50)">
    <rect x="66" y="42" width="48" height="16" rx="4" fill="{t['metal']}"/>
    <rect x="58" y="44" width="12" height="12" rx="3" fill="{t['dark']}"/>
    <rect x="110" y="40" width="8" height="20" rx="3" fill="{t['light']}"/>
  </g>
  <path d="M84 96 L118 96 M102 86 L118 96 L102 106" fill="none" stroke="#ffd34d" stroke-width="5"/>"""


def efficient_mining(t):
    return f"""<path d="M18 104 L30 74 L58 66 L78 80 L74 104 Z" fill="#7d7f86"/>
  <path d="M30 74 L46 86 L58 66 M46 86 L50 104" fill="none" stroke-width="3"/>
  <path d="M36 92 L42 86 L48 92 L42 98 Z" fill="#e8853a" stroke-width="2.5"/>
  <path d="M58 84 L64 78 L70 84 L64 90 Z" fill="#58c4ff" stroke-width="2.5"/>
  <g transform="rotate(35 70 46)">
    <rect x="66" y="34" width="8" height="66" rx="3" fill="{WOOD}"/>
    <path d="M38 40 Q70 18 102 40 L98 46 Q70 30 42 46 Z" fill="{t['metal']}"/>
    <path d="M46 38 Q70 24 94 38" fill="none" stroke="{t['light']}" stroke-width="3"/>
  </g>
  <path d="M96 66 L96 90 M84 78 L108 78" stroke="{INK}" stroke-width="11"/>
  <path d="M96 66 L96 90 M84 78 L108 78" stroke="#3ccf5a" stroke-width="5"/>"""


# ---------- buildings ----------

def forge_building(_):
    t = TIERS[2]
    return f"""<path d="M26 66 L102 66 L102 74 Q88 76 84 86 L84 96 L96 104 L32 104 L44 96 L44 86 Q40 76 26 74 Z" fill="#4a4f57"/>
  <path d="M30 68 L98 68" stroke="#8a919b" stroke-width="3"/>
  <path d="M18 66 L30 66 L30 72 Q22 72 18 66 Z" fill="#4a4f57" stroke-width="3"/>
  <g transform="rotate(-40 70 44)">
    <rect x="66" y="30" width="8" height="58" rx="3" fill="{WOOD}"/>
    <rect x="52" y="18" width="36" height="18" rx="3" fill="{t['metal']}"/>
    <path d="M56 22 L84 22" stroke="{t['light']}" stroke-width="3"/>
  </g>
  <g stroke="none">
    <circle cx="34" cy="52" r="3" fill="#ffd34d"/><circle cx="26" cy="44" r="2.5" fill="#ff9a2e"/>
    <circle cx="40" cy="40" r="2" fill="#ffd34d"/>
  </g>"""


def armoury_building(_):
    t = TIERS[2]
    return f"""<rect x="60" y="86" width="8" height="18" fill="{WOOD}"/>
  <rect x="40" y="102" width="48" height="8" rx="3" fill="{WOOD}"/>
  <path d="M38 52 Q50 58 64 55 Q78 58 90 52 L98 66 L90 76 L90 96 Q64 106 38 96 L38 76 L30 66 Z" fill="{t['metal']}"/>
  <path d="M64 56 L64 100" stroke="{t['dark']}" stroke-width="3"/>
  <path d="M42 22 Q42 0 64 0" fill="none" stroke="none"/>
  <path d="M44 44 Q44 14 64 14 Q84 14 84 44 L84 52 L44 52 Z" fill="{t['metal']}"/>
  <rect x="48" y="30" width="32" height="6" rx="2" fill="{INK}" stroke-width="1.5"/>
  <path d="M50 24 Q54 18 62 17" stroke="{t['light']}" stroke-width="3" fill="none"/>"""


def market_building(_):
    stripes = "".join(f'<path d="M{20 + i * 11} 34 L{31 + i * 11} 34 L{33 + i * 11} 52 L{18 + i * 11} 52 Z" fill="{RED if i % 2 == 0 else "#f4efe6"}" stroke-width="2.5"/>' for i in range(8))
    scallops = "".join(f'<path d="M{18 + i * 11.5} 52 Q{23.75 + i * 11.5} 60 {29.5 + i * 11.5} 52 Z" fill="{RED if i % 2 == 0 else "#f4efe6"}" stroke-width="2.5"/>' for i in range(8))
    return f"""<rect x="24" y="30" width="6" height="74" fill="{WOOD}"/>
  <rect x="98" y="30" width="6" height="74" fill="{WOOD}"/>
  {stripes}
  {scallops}
  <rect x="20" y="78" width="88" height="10" rx="2" fill="{WOOD_LIGHT}"/>
  <rect x="26" y="88" width="76" height="16" fill="{WOOD}"/>
  <path d="M38 88 L38 104 M52 88 L52 104 M66 88 L66 104 M80 88 L80 104 M94 88 L94 104" stroke-width="2" fill="none"/>
  <rect x="32" y="64" width="18" height="14" fill="{WOOD_LIGHT}" stroke-width="3"/>
  <rect x="56" y="66" width="16" height="12" fill="#b5523b" stroke-width="3"/>
  <path d="M86 78 L92 62 L98 78 Z" fill="#58c4ff" stroke-width="3"/>"""


def warehouse_building(_):
    # a stack of bricks by the door, every other row shifted
    bricks = "".join(f'<rect x="{80 + (r % 2) * 7}" y="{92 - r * 8}" width="14" height="7" rx="1" fill="#b5523b" stroke-width="2"/>'
                     for r in range(3))
    return f"""<path d="M14 58 L64 28 L114 58 Z" fill="#9b4a32"/>
  <path d="M24 54 L64 32 L104 54" stroke="#c96a4a" stroke-width="3" fill="none"/>
  <rect x="20" y="58" width="88" height="46" fill="{WOOD_LIGHT}"/>
  <rect x="20" y="84" width="88" height="20" fill="#8d8f93"/>
  <path d="M20 84 L108 84 M42 84 L42 104 M64 84 L64 94 M86 84 L86 104 M20 94 L108 94" stroke-width="2" fill="none"/>
  <path d="M44 104 L44 70 Q64 58 84 70 L84 104 Z" fill="{WOOD}"/>
  <path d="M64 64 L64 104 M46 74 L62 102 M82 74 L66 102" stroke-width="3" fill="none"/>
  <rect x="58" y="44" width="12" height="10" fill="#3a2a1a" stroke-width="2.5"/>
  {bricks}"""


LINES = [
    # key, display name, category, drawing, effect per tier
    ("sharpened_blades", "Sharpened Blades", "weapon", sword, ["+15% melee", "+30% melee", "+50% melee"]),
    ("heavy_blades", "Heavy Blades", "weapon", axe, ["+25% siege", "+50% siege", "+75% siege"]),
    ("fletching", "Fletching", "weapon", arrow, ["+15% arrows", "+30% arrows", "+50% arrows"]),
    ("steady_aim", "Steady Aim", "weapon", target, ["+1 precision", "+2 precision", "+3 precision"]),
    ("winch", "Winch", "weapon", crossbow, ["-10% reload", "-20% reload", "-30% reload"]),
    ("chainmail", "Chainmail", "armour", chainmail, ["10% vs melee", "20% vs melee", "30% vs melee"]),
    ("gambeson", "Padded Gambeson", "armour", gambeson, ["10% vs arrows", "20% vs arrows", "30% vs arrows"]),
    ("helmet", "Reinforced Helmet", "armour", helmet, ["+15 HP", "+30 HP", "+50 HP"]),
    ("shield_rims", "Shield Rims", "armour", shield, ["+10% block", "+20% block", "+30% block"]),
    ("warded_plate", "Warded Plate", "armour", warded_plate, ["5% vs magic", "10% vs magic", "15% vs magic"]),
    ("housing", "Housing", "research", housing, ["+5 bricktrons", "+10 bricktrons", "+15 bricktrons"]),
    ("work_methods", "Work Methods", "research", work_methods, ["+10% work", "+20% work", "+30% work"]),
    ("stacking", "Stacking", "research", stacking, ["+33% storage", "+67% storage", "+100% storage"]),
    ("crystal_attunement", "Crystal Attunement", "research", crystal_attunement, ["-15% energy", "-30% energy", "-45% energy"]),
    ("light_boots", "Light Boots", "research", light_boots, ["+5% walking", "+10% walking", "+15% walking"]),
    ("firefly_lore", "Firefly Lore", "research", firefly_lore, ["+10% energy", "+20% energy", "+30% energy"]),
    ("giants", "Giant Bricktrons", "research", giants, ["unlocks giants"]),
    ("scouting", "Scouting", "research", scouting, ["+15% wave time", "+30% wave time", "+50% wave time"]),
    ("efficient_mining", "Efficient Mining", "research", efficient_mining, ["10% extra ore", "20% extra ore", "30% extra ore"]),
]

BUILDINGS = [
    ("forge", "Forge (weapons)", "weapon", forge_building),
    ("armoury", "Armoury (armour)", "armour", armoury_building),
    ("market", "Market", "market", market_building),
    ("warehouse", "Warehouse", "storage", warehouse_building),
]


def render(svg, path, tmp):
    src = os.path.join(tmp, os.path.basename(path) + ".svg")
    with open(src, "w") as f:
        f.write(svg)
    subprocess.run(["rsvg-convert", "-w", str(SIZE), "-h", str(SIZE), "-o", path, src], check=True)


def main():
    preview = sys.argv[1] if len(sys.argv) > 1 else "upgrade_icons_preview.png"
    os.makedirs(OUT, exist_ok=True)
    icons = {}
    with tempfile.TemporaryDirectory() as tmp:
        for key, _, category, draw, effects in LINES:
            for tier in range(1, len(effects) + 1):
                path = os.path.join(OUT, f"{key}_t{tier}.png")
                render(badge(category, tier, draw(TIERS[tier])), path, tmp)
                icons[(key, tier)] = path
        for key, _, category, draw in BUILDINGS:
            path = os.path.join(OUT, f"building_{key}.png")
            render(badge(category, 0, draw(None)), path, tmp)
            icons[(key, 0)] = path
    print("wrote", len(icons), "icons to", os.path.normpath(OUT))
    make_preview(icons, preview)


def make_preview(icons, path):
    title_font = ImageFont.truetype(FONT, 40)
    head_font = ImageFont.truetype(FONT, 22)
    name_font = ImageFont.truetype(FONT, 20)
    small_font = ImageFont.truetype(FONT, 15)
    label_w, cell, gap, top = 250, SIZE, 24, 150
    row_h = cell + 34
    rows = len(LINES)
    w = label_w + 3 * (cell + gap) + gap + 40
    h = top + rows * row_h + 60 + SIZE + 70
    sheet = Image.new("RGBA", (w, h), (30, 32, 36, 255))
    d = ImageDraw.Draw(sheet)
    d.text((30, 24), "Upgrade tiers", font=title_font, fill="#ffc20e")
    for i in range(3):
        t = TIERS[i + 1]
        x = label_w + gap + i * (cell + gap)
        d.text((x + cell / 2, top - 30), f"T{i + 1} {t['name']}", font=head_font, fill=t["frame"], anchor="mm")
    category = None
    y = top
    for key, name, cat, _, effects in LINES:
        if cat != category:
            category = cat
            heading, colour = {"weapon": ("WEAPONS (Smithy)", "#e8836f"), "armour": ("ARMOUR (Armoury)", "#7fa8e0"),
                               "research": ("COLONY (Research station)", "#c7a0ff")}[cat]
            d.text((30, y - 52 if y == top else y + 4), heading, font=head_font, fill=colour)
            if y != top:
                y += 34
        d.text((30, y + cell / 2), name, font=name_font, fill="#f2f2f2", anchor="lm")
        for i in range(len(effects)):
            x = label_w + gap + i * (cell + gap)
            sheet.alpha_composite(Image.open(icons[(key, i + 1)]).convert("RGBA"), (x, y))
            d.text((x + cell / 2, y + cell + 14), effects[i], font=small_font, fill="#bdbdbd", anchor="mm")
        y += row_h
    y += 30
    d.text((30, y), "BUILDINGS", font=head_font, fill="#ffc20e")
    y += 36
    x = 30
    for key, name, _, _ in BUILDINGS:
        sheet.alpha_composite(Image.open(icons[(key, 0)]).convert("RGBA"), (x, y))
        d.text((x + SIZE + 16, y + SIZE / 2), name, font=name_font, fill="#f2f2f2", anchor="lm")
        x += SIZE + 260
    sheet = sheet.crop((0, 0, w, y + SIZE + 30))
    sheet.save(path)
    print("wrote preview", os.path.normpath(path), sheet.size)


if __name__ == "__main__":
    main()
