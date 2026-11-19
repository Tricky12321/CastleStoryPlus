#!/usr/bin/env python3
"""Builds the guardians' sounds (CastleStoryPlus/Assets/Sounds/Guardians/*.wav) from real recordings only.

Every sound in every clip is a part of a CC0 / public domain recording (tools/guardian_sources, listed with their
licences in CastleStoryPlus/Assets/Sounds/Guardians/SOURCES.md): the animal voice, and the material of the guardian as
short accents (a struck crystal bowl, a flame burst or torch pops, a rock hit or a crumble, an anvil or chain clank,
footfalls and thuds). Nothing is synthesised. The script only cuts the parts, cleans them (spectral noise reduction of
the animal recordings, a noise gate on the tails, a rumble filter), pitch-shifts them for the size of the beast,
layers them (the roars get an octave lower copy of the voice under them), levels and fades. There are no noise beds,
no reverb and no ambience: between the sounds the clips are silent. Run it from anywhere:

    python3 tools/make_guardian_sounds.py                 # writes the WAVs next to the plugin's assets
    python3 tools/make_guardian_sounds.py --out DIR       # writes them somewhere else
    python3 tools/make_guardian_sounds.py --preview DIR   # also a spectrogram PNG per clip in DIR
    python3 tools/make_guardian_sounds.py --only Ironhorn

Needs numpy and scipy (and Pillow for --preview). Output is deterministic, 16-bit mono WAV at 32 kHz, levelled per
event to the same active RMS (a static soft clip keeps the peaks of impacts under 0.89, no pumping). The source clips
are read with the standard wave module; tools/cut_guardian_sources.py re-creates them from the original recordings.

File names: <Guardian>_<event>_<n>.wav, with event one of idle, roar, step, attack, hit, death and n from 1. The game
picks one at random per event (every numbered file in the folder is read), clips must be 0.15 - 6 s.

The sound of each guardian (stand-ins are named in SOURCES.md):
    Spirestalker  giraffe, blue crystal   dromedary and elk / moose moans and grunts, horse and alpaca snorts and
                                          bleats (no CC0 giraffe exists); struck crystal bowls
    Cindermane    lion, fire and embers   real lion roars, grunts and growls; flame bursts and torch pops
    Boulderback   gorilla, stone          a real gorilla roar, chest beats; rock hits and crumbles
    Ironhorn      rhino, iron             real Indian rhinoceros calls; anvil strikes, chains and clanks
"""
import argparse
import glob
import os
import re
import sys
import wave
from fractions import Fraction

import numpy as np
from scipy import signal

SR = 32000
NYQ = SR / 2.0
HERE = os.path.dirname(os.path.abspath(__file__))


# ---------------------------------------------------------------- source recordings

_SOURCES = {}
_CLEAN = {}

# the material recordings are clean (close, little noise), only the animals get the noise reduction
MATERIAL = {150453, 150454, 150456, 166553, 111331, 675771, 179365, 369981, 521956, 386129, 103071, 270589, 545547,
            401622, 342535, 640204, 334228}


def sources():
    """id -> (samples, offset): every WAV in tools/guardian_sources named <name>_<freesound id>_t<start>.wav."""
    if not _SOURCES:
        for path in sorted(glob.glob(os.path.join(HERE, "guardian_sources", "*.wav"))):
            m = re.search(r"_(\d+)_t([\d.]+)\.wav$", path)
            with wave.open(path, "rb") as w:
                if w.getframerate() != SR or w.getnchannels() != 1 or w.getsampwidth() != 2:
                    raise ValueError(path + " must be mono 16-bit 32 kHz")
                x = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float64) / 32768.0
            _SOURCES[int(m.group(1))] = (x, float(m.group(2)))
    return _SOURCES


def denoise(x, floor_pct=15.0, over=2.4, floor=0.04):
    """Spectral noise reduction: the noise spectrum is the quiet part of each frequency bin over the whole recording,
    subtracted from every frame (a Wiener-like gain that never goes below `floor`)."""
    f, t, z = signal.stft(x, SR, nperseg=1024, noverlap=768)
    mag = np.abs(z)
    noise = np.percentile(mag, floor_pct, axis=1, keepdims=True)
    noise = signal.medfilt(noise[:, 0], 5)[:, None]
    gain = np.clip(1.0 - over * noise / (mag + 1e-9), floor, 1.0)
    gain = signal.convolve2d(gain, np.ones((1, 3)) / 3.0, mode="same", boundary="symm")
    _, y = signal.istft(z * gain, SR, nperseg=1024, noverlap=768)
    return y[: len(x)]


def clean_source(fid):
    if fid not in _CLEAN:
        x, off = sources()[fid]
        _CLEAN[fid] = (x if fid in MATERIAL else denoise(x), off)
    return _CLEAN[fid]


def lowpass(x, f, order=2):
    return signal.sosfilt(signal.butter(order, min(f, NYQ * 0.98) / NYQ, "low", output="sos"), x)


def highpass(x, f, order=2):
    return signal.sosfilt(signal.butter(order, max(f, 20.0) / NYQ, "high", output="sos"), x)


def pitch(x, semitones):
    """Pitch (and speed) shift by resampling: -12 is an octave down and twice as long."""
    if abs(semitones) < 1e-6:
        return x.copy()
    r = Fraction(2.0 ** (semitones / 12.0)).limit_denominator(500)
    return signal.resample_poly(x, r.denominator, r.numerator)


def gate(x, lo_db=-52.0, hi_db=-40.0):
    """Noise gate for the tails: the envelope below lo_db (relative to the peak) is silent, above hi_db untouched."""
    env = np.sqrt(np.maximum(lowpass(x * x, 40.0, 1), 0.0)) + 1e-9
    db = 20.0 * np.log10(env / (env.max() + 1e-9))
    g = np.clip((db - lo_db) / (hi_db - lo_db), 0.0, 1.0)
    return x * lowpass(g, 60.0, 1)


def fade(x, fin=0.006, fout=0.03):
    x = x.copy()
    a = min(int(fin * SR), len(x) // 2)
    b = min(int(fout * SR), len(x) // 2)
    if a:
        x[:a] *= np.linspace(0.0, 1.0, a)
    if b:
        x[-b:] *= np.linspace(1.0, 0.0, b)
    return x


def cut(fid, t0, t1, st=0.0, sub=0.0):
    """One part of a source recording (times in the original recording), cleaned, pitched by st semitones.
    sub adds a copy an octave lower under it (a bigger chest). Every part has the same peak (0.8), so the gains
    given when the parts are layered are relative to each other."""
    x, off = clean_source(fid)
    i0 = max(0, int(round((t0 - off) * SR)))
    i1 = min(len(x), int(round((t1 - off) * SR)))
    seg = fade(gate(x[i0:i1]), 0.008, 0.02)
    out = pitch(seg, st)
    if sub > 0.0:
        low = pitch(seg, st - 12.0)
        out = out.copy()
        m = min(len(out), len(low))
        out[:m] += sub * low[:m] * np.linspace(1.0, 0.0, m) ** 0.5
    out = highpass(out, 30.0, 2)
    return out * (0.8 / (np.max(np.abs(out)) + 1e-9))


def seq(fid, parts, st=0.0, sub=0.0, gap=0.14):
    """Several parts of one recording one after the other (true silence of `gap` seconds between them)."""
    out = np.zeros(0)
    for t0, t1 in parts:
        c = cut(fid, t0, t1, st, sub)
        out = np.concatenate([out, np.zeros(int(gap * SR)) if len(out) else out, c])
    return out


def track(*items, tail=0.15):
    """items: (samples, start seconds, gain)."""
    n = max(int((a + len(x) / SR) * SR) for x, a, g in items) + int(tail * SR)
    out = np.zeros(n)
    for x, a, g in items:
        i = int(a * SR)
        j = min(n, i + len(x))
        out[i:j] += x[: j - i] * g
    return out


# ---------------------------------------------------------------- the clips
# Freesound ids, see SOURCES.md. Times are in the original recording. Every layer is (samples, start seconds, gain),
# the voice has gain 1 and the accents 0.2 - 0.5 of it.

LION_A, LION_B, LION_R, LION_N, LION_E = 69570, 69571, 223978, 405211, 270383
GORILLA, CHEST, RHINO = 397054, 367821, 535810
CAMEL, MOOSE, HORSE, ALPACA = 693564, 486554, 397236, 852641
CRY_A, CRY_B, CRY_C = 150453, 150454, 150456
FIRE_BURST, FIRE_LIGHTER, FIRE_TORCH = 166553, 111331, 675771
STONE_HIT, STONE_DROP, STONE_CRUMBLE = 179365, 369981, 521956
ANVIL_A, ANVIL_B, ANVIL_C, CLANK, CHAIN = 386129, 103071, 270589, 545547, 401622
THUD_SOFT, THUD_IRON, STOMP = 342535, 640204, 334228


# --- Spirestalker (giraffe, crystal): moans, grunts, snorts and bleats; a struck crystal bowl on top ---

def spire_idle(v):
    if v == 0:
        return track((cut(CAMEL, 3.2, 5.6, -1.0), 0.0, 1.0), (cut(CRY_A, 0.0, 1.8, 7.0), 1.9, 0.25))
    if v == 1:
        return track((cut(CAMEL, 6.8, 8.8, 0.0), 0.0, 1.0), (cut(CRY_B, 0.0, 1.8, 5.0), 0.1, 0.22))
    if v == 2:
        return track((cut(MOOSE, 0.8, 2.1, -1.0), 0.0, 1.0), (cut(HORSE, 0.5, 0.9, -3.0), 1.5, 0.8), (cut(CRY_A, 0.0, 1.8, 10.0), 1.5, 0.2))
    return track((cut(ALPACA, 0.0, 0.6, -2.0), 0.0, 0.9), (cut(MOOSE, 4.2, 5.5, 0.0), 0.8, 1.0), (cut(CRY_B, 0.0, 1.8, 9.0), 0.05, 0.2))


def spire_roar(v):
    if v == 0:
        return track((cut(CAMEL, 3.2, 5.9, 1.0, 0.25), 0.0, 1.0), (cut(CRY_A, 0.0, 1.8, 5.0), 0.0, 0.28), (cut(CRY_B, 0.0, 1.8, 12.0), 0.7, 0.16))
    if v == 1:
        return track((seq(MOOSE, [(0.8, 2.1), (4.2, 5.5)], 0.0, 0.2, 0.12), 0.0, 1.0), (cut(CRY_B, 0.0, 1.8, 7.0), 0.0, 0.26))
    return track((cut(CAMEL, 9.0, 11.2, 2.0, 0.2), 0.0, 1.0), (cut(CRY_A, 0.0, 1.8, 3.0), 0.0, 0.26), (cut(CRY_B, 0.0, 1.8, 10.0), 0.9, 0.16))


def spire_step(v):
    # a light hoof tick and a tiny crystal glint
    hit = [(0.35, 0.75), (1.55, 1.95), (3.35, 3.8)][v]
    return track((cut(STONE_HIT, hit[0], hit[1], 3.0), 0.0, 0.9), (cut(CRY_C, 0.0, 0.3, [6.0, 9.0, 12.0][v]), 0.01, 0.14), tail=0.05)


def spire_attack(v):
    if v == 0:
        return track((cut(HORSE, 2.0, 2.4, 0.0), 0.0, 1.0), (cut(STONE_HIT, 3.35, 3.8, 0.0), 0.3, 0.5), (cut(CRY_A, 0.0, 1.8, 5.0), 0.3, 0.3))
    return track((cut(ALPACA, 0.0, 0.6, 0.0), 0.0, 1.0), (cut(STONE_HIT, 1.55, 1.95, 0.0), 0.35, 0.5), (cut(CRY_B, 0.0, 1.8, 7.0), 0.35, 0.3))


def spire_hit(v):
    if v == 0:
        return track((cut(ALPACA, 0.0, 0.6, -1.0), 0.0, 1.0), (cut(CRY_C, 0.0, 0.3, 12.0), 0.0, 0.25))
    if v == 1:
        return track((cut(MOOSE, 4.2, 5.0, 0.0), 0.0, 1.0), (cut(CRY_C, 0.0, 0.3, 9.0), 0.0, 0.25), (cut(CRY_C, 0.0, 0.3, 14.0), 0.08, 0.15))
    return track((cut(CAMEL, 10.0, 10.8, 1.0), 0.0, 1.0), (cut(CRY_C, 0.0, 0.3, 7.0), 0.02, 0.25))


def spire_death(v):
    glints = [(cut(CRY_A if i % 2 == 0 else CRY_B, 0.0, 1.8, st), 0.9 + i * 0.3, 0.25 - 0.03 * i) for i, st in enumerate([9.0, 5.0, 2.0, -3.0])]
    if v == 0:
        return track((cut(CAMEL, 3.2, 5.9, -4.0, 0.25), 0.0, 1.0), *glints)
    return track((seq(MOOSE, [(0.8, 2.1), (4.2, 5.5)], -5.0, 0.25, 0.15), 0.0, 1.0), *glints)


# --- Cindermane (lion, fire and embers): real lion voice; flame bursts and torch pops ---

def cinder_idle(v):
    src = [(LION_B, 1.8, 2.5), (LION_B, 4.8, 5.7), (LION_B, 7.5, 8.6), (LION_R, 0.1, 1.9)][v]
    return track((cut(src[0], src[1], src[2], -2.0), 0.0, 1.0))


def cinder_roar(v):
    if v == 0:
        return track((cut(LION_N, 11.1, 14.2, -2.0, 0.3), 0.0, 1.0))
    if v == 1:
        return track((seq(LION_A, [(14.0, 15.4), (16.2, 17.5)], -2.0, 0.3, 0.1), 0.0, 1.0))
    if v == 2:
        return track((seq(LION_R, [(10.6, 12.2), (13.9, 15.2)], -2.5, 0.3, 0.1), 0.0, 1.0))
    return track((seq(LION_A, [(20.0, 21.4), (21.9, 23.1), (23.6, 24.7)], -2.0, 0.3, 0.08), 0.0, 1.0))


def cinder_step(v):
    # a big cat's padded paw: a soft low thud
    if v == 0:
        return track((cut(THUD_SOFT, 0.15, 0.45, -3.0), 0.0, 1.0), tail=0.05)
    if v == 1:
        return track((cut(THUD_SOFT, 0.45, 0.75, -2.0), 0.0, 1.0), tail=0.05)
    return track((cut(STOMP, 3.8, 4.3, 4.0), 0.0, 1.0), tail=0.05)


def cinder_attack(v):
    src = [(LION_R, 7.2, 8.8), (LION_A, 18.2, 19.4), (LION_E, 3.5, 4.5)][v]
    fire = [(FIRE_BURST, 0.9, 1.9), (FIRE_LIGHTER, 0.0, 0.9), (FIRE_BURST, 3.9, 4.8)][v]
    return track((cut(src[0], src[1], src[2], -2.0, 0.2), 0.0, 1.0), (cut(fire[0], fire[1], fire[2]), 0.0, 0.35))


def cinder_hit(v):
    src = [(LION_E, 0.5, 1.2), (LION_E, 1.3, 2.1), (LION_R, 4.0, 4.9)][v]
    pop = [(0.2, 0.5), (0.9, 1.2), (1.4, 1.7)][v]
    return track((cut(src[0], src[1], src[2], -2.0), 0.0, 1.0), (cut(FIRE_TORCH, pop[0], pop[1]), 0.0, 0.35))


def cinder_death(v):
    if v == 0:
        voice = seq(LION_A, [(21.9, 23.1), (23.6, 24.7), (25.1, 26.1)], -4.0, 0.3, 0.12)
        return track((voice, 0.0, 1.0), (cut(FIRE_TORCH, 0.9, 1.2), 1.2, 0.3), (cut(FIRE_TORCH, 2.4, 2.7), 2.2, 0.25), (cut(FIRE_BURST, 6.0, 7.2), 2.6, 0.2))
    return track((cut(LION_N, 16.9, 18.9, -5.0, 0.3), 0.0, 1.0), (cut(FIRE_TORCH, 1.4, 1.7), 1.0, 0.3), (cut(FIRE_TORCH, 3.0, 3.2), 1.9, 0.25), (cut(FIRE_BURST, 0.9, 1.9), 1.5, 0.2))


# --- Boulderback (gorilla, stone): a real gorilla roar, bonobo hoots, real chest beats, rock hits and crumbles ---

def boulder_idle(v):
    if v == 0:
        return track((cut(CHEST, 3.55, 4.5, -5.0), 0.0, 1.0), (cut(STONE_HIT, 0.35, 0.75, -4.0), 0.02, 0.3))
    if v == 1:
        return track((cut(GORILLA, 0.2, 0.9, -9.0, 0.3), 0.0, 0.8))
    if v == 2:
        return track((cut(GORILLA, 0.2, 0.6, -10.0, 0.3), 0.0, 0.8), (cut(CHEST, 3.55, 4.1, -5.0), 0.9, 0.9))
    return track((cut(GORILLA, 0.2, 0.8, -7.0, 0.3), 0.0, 0.7), (cut(CHEST, 4.6, 5.1, -5.0), 1.2, 0.9))


def boulder_roar(v):
    if v == 0:
        return track((cut(CHEST, 3.55, 5.6, -4.0), 0.0, 0.9), (cut(GORILLA, 0.2, 0.9, -5.0, 0.35), 2.0, 1.0))
    if v == 1:
        return track((cut(GORILLA, 0.2, 0.9, -4.0, 0.4), 0.0, 1.0), (cut(CHEST, 4.6, 5.1, -4.0), 1.2, 0.9))
    if v == 2:
        return track((cut(GORILLA, 0.2, 0.9, -6.0, 0.3), 0.0, 1.0), (cut(GORILLA, 0.2, 0.9, -9.0, 0.4), 1.2, 0.9), (cut(CHEST, 4.6, 5.1, -5.0), 2.5, 0.8))
    return track((cut(CHEST, 3.55, 4.5, -5.0), 0.0, 0.9), (cut(GORILLA, 0.2, 0.9, -7.0, 0.5), 1.1, 1.0), (cut(STONE_DROP, 9.4, 9.9, -2.0), 1.1, 0.3))


def boulder_step(v):
    # a heavy knuckle or foot: a deep thud and a rock knock
    st = [(1.6, 2.1), (3.8, 4.3), (4.4, 4.9)][v]
    src = STOMP if v < 2 else STONE_DROP
    hit = [(0.35, 0.75), (1.55, 1.95), (3.35, 3.8)][v]
    return track((cut(src, st[0], st[1], -2.0), 0.0, 1.0), (cut(STONE_HIT, hit[0], hit[1], -3.0), 0.005, 0.3), tail=0.05)


def boulder_attack(v):
    if v == 0:
        return track((cut(GORILLA, 0.2, 0.9, -5.0, 0.3), 0.0, 1.0), (cut(STONE_DROP, 9.4, 9.9, -2.0), 0.55, 0.8), (cut(STONE_CRUMBLE, 0.9, 2.0), 0.6, 0.3))
    if v == 1:
        return track((cut(CHEST, 4.6, 5.1, -4.0), 0.0, 0.9), (cut(GORILLA, 0.2, 0.8, -8.0, 0.3), 0.3, 1.0), (cut(STONE_DROP, 4.4, 4.9, -3.0), 0.85, 0.8), (cut(STONE_CRUMBLE, 0.9, 2.0), 0.9, 0.3))
    return track((cut(GORILLA, 0.2, 0.7, -3.0, 0.2), 0.0, 1.0), (cut(STONE_DROP, 9.4, 9.9, -4.0), 0.45, 0.8), (cut(STONE_HIT, 4.2, 5.0, -2.0), 0.5, 0.4))


def boulder_hit(v):
    src = [(0.2, 0.6, -6.0), (0.3, 0.8, -9.0), (0.3, 0.8, -4.0)][v]
    hit = [(0.35, 0.75), (1.55, 1.95), (3.35, 3.8)][v]
    return track((cut(GORILLA, src[0], src[1], src[2], 0.2), 0.0, 1.0), (cut(STONE_HIT, hit[0], hit[1], -2.0), 0.0, 0.5))


def boulder_death(v):
    spots = [(4.4, 4.9), (9.4, 9.9), (4.4, 4.9)]
    drops = [(cut(STONE_DROP, spots[i][0], spots[i][1], -4.0), at, 0.7 - 0.12 * i) for i, at in enumerate([1.6, 2.2, 2.9])]
    st = -12.0 if v == 0 else -10.0
    return track((cut(GORILLA, 0.2, 0.9 if v == 0 else 0.8, st, 0.3), 0.0, 1.0), *drops, (cut(STONE_CRUMBLE, 0.0, 2.3, -2.0), 1.7, 0.3))


# --- Ironhorn (rhino, iron): real rhinoceros calls; anvil strikes, chains and clanks ---

def iron_idle(v):
    if v == 0:
        return track((cut(RHINO, 1.2, 1.9, -3.0), 0.0, 1.0))
    if v == 1:
        return track((cut(RHINO, 0.1, 0.7, -4.0), 0.0, 1.0))
    if v == 2:
        return track((seq(RHINO, [(2.1, 2.6), (2.8, 3.1)], -4.0, 0.0, 0.2), 0.0, 1.0))
    return track((seq(RHINO, [(3.7, 3.9), (4.2, 4.8)], -3.0, 0.0, 0.2), 0.0, 1.0))


def iron_roar(v):
    if v == 0:
        return track((cut(RHINO, 7.9, 9.0, -6.0, 0.3), 0.0, 1.0), (cut(ANVIL_C, 0.1, 1.0, -2.0), 0.1, 0.3))
    if v == 1:
        return track((cut(RHINO, 4.2, 5.0, -6.0, 0.3), 0.0, 1.0), (cut(ANVIL_A, 0.0, 1.4, 0.0), 0.2, 0.3))
    return track((seq(RHINO, [(1.2, 1.9), (2.1, 2.6)], -5.0, 0.3, 0.12), 0.0, 1.0), (cut(CLANK, 7.4, 8.0, -2.0), 0.4, 0.3))


def iron_step(v):
    # a heavy trot: a deep thud and a short clank of plates
    clank = [(7.4, 7.9), (8.0, 8.5), (11.2, 11.7)][v]
    return track((cut(THUD_IRON, 0.1, 0.6, [-2.0, 0.0, 2.0][v]), 0.0, 1.0), (cut(CLANK, clank[0], clank[1], 0.0), 0.012, 0.3), tail=0.05)


def iron_attack(v):
    if v == 0:
        return track((cut(RHINO, 0.1, 0.7, -4.0), 0.0, 1.0), (cut(THUD_IRON, 0.1, 0.6, -2.0), 0.6, 0.7), (cut(ANVIL_A, 0.0, 1.4, 0.0), 0.6, 0.5))
    if v == 1:
        return track((cut(RHINO, 1.2, 1.9, -4.0), 0.0, 1.0), (cut(THUD_IRON, 0.1, 0.6, -2.0), 0.8, 0.7), (cut(ANVIL_B, 3.1, 4.4, -2.0), 0.8, 0.5))
    return track((cut(RHINO, 3.7, 3.9, -4.0), 0.0, 1.0), (cut(RHINO, 4.2, 4.7, -4.0), 0.3, 0.9), (cut(THUD_IRON, 0.1, 0.6, 0.0), 0.65, 0.7), (cut(ANVIL_B, 5.9, 7.0, 0.0), 0.65, 0.5))


def iron_hit(v):
    src = [(7.9, 8.5, -5.0), (2.8, 3.1, -4.0), (4.2, 4.7, -5.0)][v]
    clank = [(0.1, 0.9), (7.4, 8.0), (5.9, 6.7)][v]
    fid = [ANVIL_C, CLANK, ANVIL_B][v]
    return track((cut(RHINO, src[0], src[1], src[2], 0.2), 0.0, 1.0), (cut(fid, clank[0], clank[1], 0.0), 0.0, 0.4))


def iron_death(v):
    plan = [(7.4, 7.9, 0.0, 0.9), (8.0, 8.5, -2.0, 1.25), (8.7, 9.3, 2.0, 1.55), (9.6, 10.2, -4.0, 1.8), (11.2, 11.7, 0.0, 2.1)]
    clanks = [(cut(CLANK, a, b, st), at, 0.4 - 0.05 * i) for i, (a, b, st, at) in enumerate(plan)]
    rattle = (cut(CHAIN, 5.2, 6.7, 0.0), 2.2, 0.3)
    voice = cut(RHINO, 7.9, 9.0, -9.0, 0.3) if v == 0 else seq(RHINO, [(1.2, 1.9), (2.1, 2.6)], -8.0, 0.3, 0.12)
    return track((voice, 0.0, 1.0), *clanks, rattle)


# ---------------------------------------------------------------- the set

# name -> {event: (function, variants)}
SOUNDS = {
    "Spirestalker": {"idle": (spire_idle, 4), "roar": (spire_roar, 3), "step": (spire_step, 3), "attack": (spire_attack, 2), "hit": (spire_hit, 3), "death": (spire_death, 2)},
    "Cindermane": {"idle": (cinder_idle, 4), "roar": (cinder_roar, 4), "step": (cinder_step, 3), "attack": (cinder_attack, 3), "hit": (cinder_hit, 3), "death": (cinder_death, 2)},
    "Boulderback": {"idle": (boulder_idle, 4), "roar": (boulder_roar, 4), "step": (boulder_step, 3), "attack": (boulder_attack, 3), "hit": (boulder_hit, 3), "death": (boulder_death, 2)},
    "Ironhorn": {"idle": (iron_idle, 4), "roar": (iron_roar, 3), "step": (iron_step, 3), "attack": (iron_attack, 3), "hit": (iron_hit, 3), "death": (iron_death, 2)},
}

# the active RMS each event is levelled to (the level of the earlier sound set)
TARGET_RMS = {"idle": 0.18, "roar": 0.18, "attack": 0.15, "hit": 0.17, "death": 0.15, "step": 0.20}
PEAK = 0.89


def soft(x, knee=0.5):
    """A static soft clip: unchanged below the knee, bends smoothly to PEAK above it (no pumping, only the tops of
    impacts are rounded)."""
    a = np.abs(x)
    room = PEAK - knee
    return np.where(a <= knee, x, np.sign(x) * (knee + room * np.tanh((a - knee) / room)))


def active_rms(x):
    """RMS over the active frames (within 20 dB of the loudest 20 ms frame)."""
    h = int(0.02 * SR)
    n = len(x) // h
    rms = np.sqrt(np.mean(x[: n * h].reshape(n, h) ** 2, axis=1)) + 1e-12
    return np.sqrt(np.mean(rms[rms > rms.max() * 0.1] ** 2))


def level(x, event):
    """Highpass, then level to the event's RMS over the active part; the peaks above the knee are soft clipped and
    the gain is corrected for what the clip took away."""
    x = highpass(x, 30.0, 2)
    g = TARGET_RMS[event] / active_rms(x)
    for _ in range(4):
        g *= TARGET_RMS[event] / active_rms(soft(x * g))
    return fade(soft(x * g), 0.002, 0.04)


def write_wav(path, x):
    data = np.clip(x, -1.0, 1.0)
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes((data * 32767.0).astype("<i2").tobytes())


def spectrogram_png(path, x, title):
    from PIL import Image, ImageDraw
    f, t, s = signal.spectrogram(x, SR, nperseg=512, noverlap=384)
    db = 10 * np.log10(s + 1e-10)
    db = np.clip((db - (db.max() - 80)) / 80.0, 0, 1)
    w, h = 720, 256
    img = np.array(Image.fromarray((db[::-1] * 255).astype(np.uint8)).resize((w, h), Image.BILINEAR))
    r = np.clip(img * 2.0, 0, 255)
    g = np.clip((img.astype(float) - 90) * 2.0, 0, 255)
    b = np.clip(np.where(img < 128, img * 1.6 + 40, 255 - (img - 128) * 2), 0, 255)
    rgb = np.stack([r, g, b], axis=-1).astype(np.uint8)
    wave_h = 70
    canvas = Image.new("RGB", (w, h + wave_h + 20), (16, 16, 24))
    canvas.paste(Image.fromarray(rgb), (0, 20))
    d = ImageDraw.Draw(canvas)
    d.text((6, 4), "%s  (%.2f s)" % (title, len(x) / SR), fill=(230, 230, 230))
    mid = 20 + h + wave_h // 2
    step = max(1, len(x) // w)
    for px in range(w):
        seg = x[px * step:(px + 1) * step]
        if len(seg) == 0:
            continue
        d.line([(px, mid - int(seg.max() * wave_h / 2)), (px, mid - int(seg.min() * wave_h / 2))], fill=(120, 200, 255))
    canvas.save(path)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", default=os.path.join(HERE, "..", "CastleStoryPlus", "Assets", "Sounds", "Guardians"))
    parser.add_argument("--preview", default="", help="folder for a spectrogram + waveform PNG per clip")
    parser.add_argument("--only", default="", help="only this guardian")
    args = parser.parse_args()
    os.makedirs(args.out, exist_ok=True)
    if args.preview:
        os.makedirs(args.preview, exist_ok=True)
    total = 0
    for name, events in SOUNDS.items():
        if args.only and args.only != name:
            continue
        for event, (fn, variants) in events.items():
            for v in range(variants):
                x = level(fn(v), event)
                if not 0.15 < len(x) / SR < 6.0:
                    raise ValueError("%s_%s_%d is %.2f s (must be 0.15 - 6 s)" % (name, event, v + 1, len(x) / SR))
                base = "%s_%s_%d" % (name, event, v + 1)
                write_wav(os.path.join(args.out, base + ".wav"), x)
                if args.preview:
                    spectrogram_png(os.path.join(args.preview, base + ".png"), x, base)
                total += 1
    print("wrote %d clips to %s" % (total, os.path.abspath(args.out)))


if __name__ == "__main__":
    sys.exit(main())
