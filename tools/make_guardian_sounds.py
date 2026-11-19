#!/usr/bin/env python3
"""Builds the guardians' sounds (CastleStoryPlus/Assets/Sounds/Guardians/*.wav).

The animal voice is real: short parts of CC0 / public domain recordings (tools/guardian_sources, listed with their
licences in CastleStoryPlus/Assets/Sounds/Guardians/SOURCES.md). Each part is cleaned (spectral noise reduction, a
noise gate on the tails, rumble filter), pitched down for the size of the beast and layered (a sub octave copy under
the roars). On top of the voice sit the guardian's material as a few short, clean transients only (one crystal chime,
one stone thud, one iron clank, a few ember pops), made here with damped resonances. There are no noise beds, no reverb
and no ambience: between the sounds the clips are silent. Run it from anywhere:

    python3 tools/make_guardian_sounds.py                 # writes the WAVs next to the plugin's assets
    python3 tools/make_guardian_sounds.py --out DIR       # writes them somewhere else
    python3 tools/make_guardian_sounds.py --preview DIR   # also a spectrogram PNG per clip in DIR
    python3 tools/make_guardian_sounds.py --only Ironhorn

Needs numpy and scipy (and Pillow for --preview). Output is deterministic (fixed seeds), 16-bit mono WAV at 32 kHz,
levelled per event to the same active RMS (peak at most 0.89). The source clips are read with the standard wave
module; tools/cut_guardian_sources.py re-creates them from the original recordings.

File names: <Guardian>_<event>_<n>.wav, with event one of idle, roar, step, attack, hit, death and n from 1. The game
picks one at random per event (every numbered file in the folder is read), clips must be 0.15 - 6 s.

The sound of each guardian:
    Spirestalker  giraffe, blue crystal   dromedary moans and horse / alpaca snorts and bleats (giraffes are almost
                                          silent and there is no CC0 giraffe recording), a crystal chime on top
    Cindermane    lion, fire and embers   real lion roars, grunts and growls, pitched down, a few ember pops
    Boulderback   gorilla, stone          a real gorilla roar at several depths, real chest beats, stone thuds
    Ironhorn      rhino, iron             real Indian rhinoceros calls and horse snorts pitched down, iron clanks
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


def denoise(x, floor_pct=12.0, over=1.7, floor=0.06):
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
        _CLEAN[fid] = (denoise(x), off)
    return _CLEAN[fid]


def lowpass(x, f, order=2):
    return signal.sosfilt(signal.butter(order, min(f, NYQ * 0.98) / NYQ, "low", output="sos"), x)


def highpass(x, f, order=2):
    return signal.sosfilt(signal.butter(order, max(f, 20.0) / NYQ, "high", output="sos"), x)


def bandpass(x, lo, hi, order=2):
    return signal.sosfilt(signal.butter(order, [max(lo, 20.0) / NYQ, min(hi, NYQ * 0.98) / NYQ], "band", output="sos"), x)


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
    """One part of a source recording (times in the original recording), cleaned, pitched down by st semitones.
    sub adds a copy an octave lower under it (a bigger chest)."""
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
    return out * (0.8 / (np.max(np.abs(out)) + 1e-9))  # every part has the same peak: the gains below are relative


def seq(fid, parts, st=0.0, sub=0.0, gap=0.14, gains=None):
    """Several parts of one recording one after the other (true silence of `gap` seconds between them)."""
    out = np.zeros(0)
    for k, (t0, t1) in enumerate(parts):
        c = cut(fid, t0, t1, st, sub) * (1.0 if gains is None else gains[k])
        out = np.concatenate([out, np.zeros(int(gap * SR)) if len(out) else out, c])
    return out


def place(buf, snippet, at, gain=1.0):
    i = int(at * SR)
    if i < 0 or i >= len(buf):
        return
    j = min(len(buf), i + len(snippet))
    buf[i:j] += snippet[: j - i] * gain


def track(*items, tail=0.15):
    """items: (samples, start seconds, gain)."""
    n = max(int((a + len(x) / SR) * SR) for x, a, g in items) + int(tail * SR)
    out = np.zeros(n)
    for x, a, g in items:
        place(out, x, a, g)
    return out


# ---------------------------------------------------------------- material: short clean transients

def modal(duration, partials, rng, strike=0.002):
    """Damped resonances: (frequency, amplitude, decay seconds). A struck bell, plate or stone."""
    n = int(duration * SR)
    t = np.arange(n) / SR
    out = np.zeros(n)
    for freq, amp, decay in partials:
        if freq >= NYQ * 0.95:
            continue
        out += amp * np.sin(2 * np.pi * freq * t + rng.uniform(0, 2 * np.pi)) * np.exp(-t / decay)
    if strike > 0:
        k = int(strike * SR * 4)
        click = rng.uniform(-1, 1, n) * np.exp(-t / strike)
        out += 0.5 * click * (np.arange(n) < k)
    return fade(out, 0.0005, 0.06)


def thump(dur, f_start, f_end, decay, rng, click=0.2, click_hz=900):
    """A body hitting the ground: a falling sine with a short dull click on top."""
    n = int(dur * SR)
    t = np.arange(n) / SR
    f = f_end + (f_start - f_end) * np.exp(-t / (decay * 0.35))
    body = np.sin(2 * np.pi * np.cumsum(f) / SR) * np.exp(-t / decay)
    knock = lowpass(rng.uniform(-1, 1, n), click_hz, 2) * np.exp(-t / 0.012) * click
    return fade(body + knock, 0.0005, 0.05)


def chime(f0, decay, rng, dur=None, bright=1.0):
    """Glass or crystal: strongly inharmonic partials which decay faster the higher they are."""
    dur = dur or decay * 3
    ratios = [1.0, 2.76, 5.40, 8.93, 13.34]
    amps = [1.0, 0.55 * bright, 0.32 * bright, 0.2 * bright, 0.1 * bright]
    parts = [(f0 * r, a, decay / (1.0 + 0.9 * i)) for i, (r, a) in enumerate(zip(ratios, amps))]
    parts2 = [(f * 1.004, a, d * 0.9) for f, a, d in parts]
    return (modal(dur, parts, rng, 0.0015) + 0.5 * modal(dur, parts2, rng, 0.0)) * 0.6


def iron_clank(f0, decay, rng, dur=None):
    """Struck iron plate: dense inharmonic partials."""
    dur = dur or decay * 3
    ratios = [1.0, 1.59, 2.14, 2.65, 3.33, 4.1, 5.3, 6.7]
    amps = [1.0, 0.8, 0.7, 0.55, 0.45, 0.3, 0.22, 0.15]
    parts = [(f0 * r * rng.uniform(0.985, 1.015), a, decay / (1.0 + 0.45 * i)) for i, (r, a) in enumerate(zip(ratios, amps))]
    return modal(dur, parts, rng, 0.003)


def stone_knock(f0, decay, rng, dur=None):
    """Struck stone: a few low, quickly dying resonances and a dull crack."""
    dur = dur or decay * 4
    ratios = [1.0, 1.7, 2.6, 3.9, 5.6]
    amps = [1.0, 0.7, 0.45, 0.3, 0.18]
    parts = [(f0 * r * rng.uniform(0.97, 1.03), a, decay / (1.0 + 0.8 * i)) for i, (r, a) in enumerate(zip(ratios, amps))]
    return modal(dur, parts, rng, 0.004)


def ember_pop(rng, centre=2500.0):
    """One ember cracking: a very short band-limited burst."""
    m = int(0.012 * SR)
    b = rng.uniform(-1, 1, m) * np.exp(-np.arange(m) / SR / 0.003)
    return fade(bandpass(b, centre * 0.6, centre * 1.6, 1), 0.0003, 0.003)


# ---------------------------------------------------------------- the clips
# Source ids (Freesound): 69570 69571 69572 270383 lion; 397054 gorilla roar; 367821 chest beats;
# 535810 Indian rhinoceros; 693564 dromedary; 397236 horse snort; 852641 alpaca. Times are in the original recording.

LION_A, LION_B, LION_C, LION_E = 69570, 69571, 69572, 270383
GORILLA, CHEST, RHINO, CAMEL, HORSE, ALPACA = 397054, 367821, 535810, 693564, 397236, 852641


# --- Spirestalker (giraffe, crystal): moans, snorts and bleats; a chime on top ---

def spire_idle(rng, v):
    if v == 0:
        return track((cut(CAMEL, 3.2, 5.6, -1.0), 0.0, 1.0), (chime(1320, 0.5, rng, 1.0), 1.9, 0.12))
    if v == 1:
        return track((cut(CAMEL, 6.8, 8.8, 0.0), 0.0, 1.0), (chime(1760, 0.45, rng, 0.9), 0.1, 0.1))
    if v == 2:
        return track((cut(HORSE, 0.5, 0.9, -3.0), 0.0, 1.0), (cut(CAMEL, 10.0, 11.2, -1.0), 0.6, 0.9), (chime(990, 0.5, rng, 1.0), 1.5, 0.1))
    return track((cut(ALPACA, 0.0, 0.6, -2.0), 0.0, 1.0), (cut(CAMEL, 14.2, 15.4, 0.0), 0.8, 0.8), (chime(1480, 0.4, rng, 0.8), 0.05, 0.1))


def spire_roar(rng, v):
    if v == 0:
        return track((cut(CAMEL, 17.0, 19.4, 1.0, 0.25), 0.0, 1.0), (chime(1320, 0.7, rng, 1.4), 0.0, 0.16), (chime(1980, 0.6, rng, 1.2), 0.5, 0.1))
    if v == 1:
        return track((seq(CAMEL, [(14.0, 15.5), (16.4, 17.6)], 0.0, 0.2), 0.0, 1.0), (chime(1760, 0.7, rng, 1.4), 0.0, 0.14))
    return track((cut(CAMEL, 9.0, 11.2, 2.0, 0.2), 0.0, 1.0), (chime(990, 0.8, rng, 1.5), 0.0, 0.14), (chime(1480, 0.6, rng, 1.2), 0.9, 0.1))


def spire_step(rng, v):
    # a light hoof tick and a tiny crystal glint
    return track((thump(0.18, 150 + 15 * v, 75, 0.05, rng, 0.5, 1800), 0.0, 0.8),
                 (chime([1980, 2480, 2960][v], 0.12, rng, 0.4), 0.012, 0.16), tail=0.05)


def spire_attack(rng, v):
    if v == 0:
        return track((cut(HORSE, 2.0, 2.4, 0.0), 0.0, 1.0), (thump(0.3, 220, 80, 0.07, rng, 0.8, 2500), 0.3, 0.5), (chime(1318, 0.4, rng, 0.9), 0.3, 0.22))
    return track((cut(ALPACA, 0.0, 0.6, 0.0), 0.0, 1.0), (thump(0.3, 220, 80, 0.07, rng, 0.8, 2500), 0.35, 0.5), (chime(1760, 0.4, rng, 0.9), 0.35, 0.22))


def spire_hit(rng, v):
    if v == 0:
        return track((cut(ALPACA, 0.0, 0.6, -1.0), 0.0, 1.0), (chime(2400, 0.15, rng, 0.5), 0.0, 0.2), (chime(3300, 0.12, rng, 0.4), 0.05, 0.14))
    if v == 1:
        return track((cut(CAMEL, 10.0, 10.8, 1.0), 0.0, 1.0), (chime(2900, 0.15, rng, 0.5), 0.0, 0.2))
    return track((cut(CAMEL, 14.3, 15.1, 1.0), 0.0, 1.0), (chime(2100, 0.15, rng, 0.5), 0.02, 0.2), (chime(3700, 0.1, rng, 0.4), 0.08, 0.12))


def spire_death(rng, v):
    chimes = [(chime(f, 0.9, rng, 1.8), 0.9 + i * 0.3, 0.16) for i, f in enumerate([1980, 1480, 990, 660])]
    if v == 0:
        return track((cut(CAMEL, 17.0, 19.4, -4.0, 0.25), 0.0, 1.0), *chimes)
    return track((seq(CAMEL, [(6.8, 8.8)], -5.0, 0.25), 0.0, 1.0), *chimes)


# --- Cindermane (lion, fire and embers): real lion voice; a few ember pops ---

def pops(rng, times, gain=0.3):
    return [(ember_pop(rng, rng.uniform(1800, 4200)), t, gain * rng.uniform(0.5, 1.0)) for t in times]


def cinder_idle(rng, v):
    parts = [(LION_B, 1.8, 2.5), (LION_B, 4.8, 5.7), (LION_B, 7.5, 8.6)]
    if v < 3:
        return track((cut(parts[v][0], parts[v][1], parts[v][2], -2.0), 0.0, 1.0))
    return track((seq(LION_C, [(5.2, 5.8), (5.9, 6.4)], -2.0, 0.0, 0.05), 0.0, 1.0))


def cinder_roar(rng, v):
    if v == 0:
        return track((seq(LION_A, [(14.0, 15.4), (16.2, 17.5)], -2.0, 0.3, 0.1), 0.0, 1.0))
    if v == 1:
        return track((seq(LION_A, [(20.0, 21.4), (21.9, 23.1), (23.6, 24.7)], -2.0, 0.3, 0.08), 0.0, 1.0))
    if v == 2:
        return track((cut(LION_A, 11.4, 12.9, -2.5, 0.35), 0.0, 1.0))
    return track((seq(LION_C, [(5.1, 6.4), (13.5, 15.3)], -2.5, 0.3, 0.2), 0.0, 1.0))


def cinder_step(rng, v):
    # a big cat's padded paw: a soft low thud
    return track((thump(0.25, 130 + 10 * v, 55, 0.06, rng, 0.35, 700), 0.0, 0.8), tail=0.05)


def cinder_attack(rng, v):
    src = [(LION_A, 9.2, 10.2), (LION_A, 18.1, 19.4), (LION_E, 3.5, 4.5)][v]
    return track((cut(src[0], src[1], src[2], -2.0, 0.2), 0.0, 1.0), *pops(rng, [0.05, 0.16, 0.3], 0.25))


def cinder_hit(rng, v):
    src = [(LION_C, 11.3, 12.1, -2.0), (LION_E, 0.5, 1.2, -2.0), (LION_E, 1.3, 2.1, -2.0)][v]
    return track((cut(src[0], src[1], src[2], src[3]), 0.0, 1.0), *pops(rng, [0.0, 0.07], 0.3))


def cinder_death(rng, v):
    if v == 0:
        voice = seq(LION_A, [(21.9, 23.1), (23.6, 24.7), (25.1, 26.1)], -4.0, 0.3, 0.12)
    else:
        voice = seq(LION_C, [(8.5, 9.7), (11.2, 12.8)], -4.0, 0.3, 0.15)
    return track((voice, 0.0, 1.0), *pops(rng, [1.2, 1.9, 2.4], 0.2))


# --- Boulderback (gorilla, stone): a real gorilla roar at several depths, real chest beats, stone thuds ---

def boulder_idle(rng, v):
    if v == 0:
        return track((cut(CHEST, 3.55, 4.5, -5.0), 0.0, 1.0), (stone_knock(160, 0.08, rng, 0.4), 0.02, 0.15))
    if v == 1:
        return track((cut(GORILLA, 0.2, 0.9, -9.0, 0.3), 0.0, 0.7))
    if v == 2:
        return track((cut(CHEST, 8.5, 9.4, -4.0), 0.0, 1.0))
    return track((cut(GORILLA, 0.2, 0.8, -7.0, 0.3), 0.0, 0.6), (cut(CHEST, 4.6, 5.1, -5.0), 1.2, 0.9))


def boulder_roar(rng, v):
    if v == 0:
        return track((cut(CHEST, 3.55, 5.6, -4.0), 0.0, 1.0), (cut(GORILLA, 0.2, 0.9, -5.0, 0.35), 2.0, 1.0))
    if v == 1:
        return track((cut(GORILLA, 0.2, 0.9, -4.0, 0.4), 0.0, 1.0), (cut(CHEST, 8.5, 9.4, -4.0), 1.2, 0.9))
    if v == 2:
        return track((cut(GORILLA, 0.2, 0.9, -6.0, 0.3), 0.0, 1.0), (cut(GORILLA, 0.2, 0.9, -8.0, 0.4), 1.5, 0.9))
    return track((cut(CHEST, 3.55, 4.5, -5.0), 0.0, 1.0), (cut(GORILLA, 0.2, 0.9, -7.0, 0.5), 1.1, 1.0), (stone_knock(140, 0.1, rng, 0.5), 1.1, 0.2))


def boulder_step(rng, v):
    # a heavy knuckle or foot: a deep thud and a stone knock
    return track((thump(0.5, 95 + 8 * v, 38, 0.13, rng, 0.25, 500), 0.0, 1.0), (stone_knock(210 * [0.9, 1.0, 1.1][v], 0.06, rng, 0.4), 0.005, 0.25), tail=0.05)


def boulder_attack(rng, v):
    if v == 0:
        return track((cut(GORILLA, 0.2, 0.9, -5.0, 0.3), 0.0, 1.0), (thump(0.9, 90, 32, 0.22, rng, 0.45, 600), 0.55, 0.9), (stone_knock(180, 0.14, rng, 0.8), 0.55, 0.5))
    if v == 1:
        return track((cut(CHEST, 4.6, 5.1, -4.0), 0.0, 0.9), (cut(GORILLA, 0.2, 0.8, -8.0, 0.3), 0.3, 1.0), (thump(0.9, 90, 32, 0.22, rng, 0.45, 600), 0.85, 0.9), (stone_knock(170, 0.14, rng, 0.8), 0.85, 0.5))
    return track((cut(GORILLA, 0.2, 0.7, -3.0, 0.2), 0.0, 1.0), (thump(0.9, 100, 34, 0.2, rng, 0.45, 600), 0.45, 0.9), (stone_knock(200, 0.12, rng, 0.8), 0.45, 0.5))


def boulder_hit(rng, v):
    src = [(0.2, 0.6, -6.0), (0.3, 0.8, -9.0), (0.3, 0.8, -4.0)][v]
    return track((cut(GORILLA, src[0], src[1], src[2], 0.2), 0.0, 1.0), (stone_knock([260, 220, 300][v], 0.09, rng, 0.5), 0.0, 0.5))


def boulder_death(rng, v):
    thumps = [(thump(0.6, 80, 32, 0.18, rng, 0.2, 400), at, 0.8 - i * 0.15) for i, at in enumerate([1.6, 2.2, 2.9])]
    knocks = [(stone_knock(rng.uniform(120, 260), 0.1, rng, 0.6), at, 0.3) for at in [1.6, 2.2, 2.9]]
    st = -12.0 if v == 0 else -10.0
    return track((cut(GORILLA, 0.2, 0.9 if v == 0 else 0.8, st, 0.3), 0.0, 1.0), *thumps, *knocks)


# --- Ironhorn (rhino, iron): real rhinoceros calls and snorts, pitched down; iron clanks ---

def iron_idle(rng, v):
    if v == 0:
        return track((cut(HORSE, 0.5, 0.9, -5.0), 0.0, 1.0), (cut(RHINO, 1.2, 1.9, -3.0), 0.8, 1.0), (iron_clank(520, 0.25, rng, 0.6), 1.6, 0.1))
    if v == 1:
        return track((cut(RHINO, 0.1, 0.7, -4.0), 0.0, 1.0))
    if v == 2:
        return track((cut(HORSE, 2.0, 2.4, -5.0), 0.0, 1.0), (cut(RHINO, 2.1, 2.6, -4.0), 0.7, 0.9))
    return track((seq(RHINO, [(2.8, 3.1), (3.7, 3.9)], -3.0, 0.0, 0.2), 0.0, 1.0))


def iron_roar(rng, v):
    if v == 0:
        return track((cut(RHINO, 7.9, 9.0, -6.0, 0.3), 0.0, 1.0), (iron_clank(520, 0.3, rng, 0.9), 0.1, 0.12))
    if v == 1:
        return track((cut(RHINO, 4.2, 5.0, -6.0, 0.3), 0.0, 1.0), (iron_clank(640, 0.3, rng, 0.9), 0.2, 0.12))
    return track((seq(RHINO, [(1.2, 1.9), (2.1, 2.6)], -5.0, 0.3, 0.12), 0.0, 1.0), (iron_clank(430, 0.3, rng, 0.9), 0.4, 0.1))


def iron_step(rng, v):
    # a heavy trot: a deep thump and a short clank of plates
    return track((thump(0.4, 100 + 8 * v, 45, 0.09, rng, 0.3, 700), 0.0, 0.95), (iron_clank([430, 560, 690][v], 0.09, rng, 0.45), 0.012, 0.25), tail=0.05)


def iron_attack(rng, v):
    if v == 0:
        return track((cut(HORSE, 0.5, 0.9, -5.0), 0.0, 1.0), (thump(0.4, 110, 45, 0.1, rng, 0.8, 1200), 0.5, 0.8), (iron_clank(820, 0.55, rng, 1.2), 0.5, 0.35))
    if v == 1:
        return track((cut(RHINO, 0.1, 0.7, -4.0), 0.0, 1.0), (thump(0.4, 110, 45, 0.1, rng, 0.8, 1200), 0.7, 0.8), (iron_clank(700, 0.5, rng, 1.2), 0.7, 0.35))
    return track((cut(HORSE, 2.0, 2.4, -5.0), 0.0, 0.9), (cut(RHINO, 3.7, 3.9, -4.0), 0.4, 1.0), (thump(0.4, 110, 45, 0.1, rng, 0.8, 1200), 0.55, 0.8), (iron_clank(940, 0.5, rng, 1.2), 0.55, 0.35))


def iron_hit(rng, v):
    src = [(7.9, 8.5, -5.0), (2.8, 3.1, -4.0), (4.2, 4.7, -5.0)][v]
    return track((cut(RHINO, src[0], src[1], src[2], 0.2), 0.0, 1.0), (iron_clank([500, 640, 780][v], 0.35, rng, 0.85), 0.0, 0.3), (thump(0.2, 150, 60, 0.05, rng, 0.9, 2000), 0.0, 0.4))


def iron_death(rng, v):
    clanks = []
    at, gap = 0.9, 0.3
    for i in range(5):
        clanks.append((iron_clank(rng.uniform(300, 1100), rng.uniform(0.1, 0.4), rng, 0.9), at, 0.4 * (1 - i / 7)))
        clanks.append((thump(0.25, 90, 45, 0.06, rng, 0.5, 1500), at, 0.35 * (1 - i / 7)))
        gap *= 0.84
        at += gap * rng.uniform(0.8, 1.3)
    voice = cut(RHINO, 7.9, 9.0, -9.0, 0.3) if v == 0 else seq(RHINO, [(1.2, 1.9), (2.1, 2.6)], -8.0, 0.3, 0.12)
    return track((voice, 0.0, 1.0), *clanks)


# ---------------------------------------------------------------- the set

# name -> {event: (function, variants)}
SOUNDS = {
    "Spirestalker": {"idle": (spire_idle, 4), "roar": (spire_roar, 3), "step": (spire_step, 3), "attack": (spire_attack, 2), "hit": (spire_hit, 3), "death": (spire_death, 2)},
    "Cindermane": {"idle": (cinder_idle, 4), "roar": (cinder_roar, 4), "step": (cinder_step, 3), "attack": (cinder_attack, 3), "hit": (cinder_hit, 3), "death": (cinder_death, 2)},
    "Boulderback": {"idle": (boulder_idle, 4), "roar": (boulder_roar, 4), "step": (boulder_step, 3), "attack": (boulder_attack, 3), "hit": (boulder_hit, 3), "death": (boulder_death, 2)},
    "Ironhorn": {"idle": (iron_idle, 4), "roar": (iron_roar, 3), "step": (iron_step, 3), "attack": (iron_attack, 3), "hit": (iron_hit, 3), "death": (iron_death, 2)},
}

# the active RMS each event is levelled to (the level of the earlier sound set); steps are short, so only the peak counts
TARGET_RMS = {"idle": 0.18, "roar": 0.18, "attack": 0.15, "hit": 0.17, "death": 0.15, "step": 0.20}
PEAK = 0.89


def level(x, event):
    """Highpass, fade and level to the event's RMS over the active part (frames within 20 dB of the loudest)."""
    x = highpass(x, 30.0, 2)
    h = int(0.02 * SR)
    n = len(x) // h
    rms = np.sqrt(np.mean(x[: n * h].reshape(n, h) ** 2, axis=1)) + 1e-12
    act = rms > rms.max() * 0.1
    cur = np.sqrt(np.mean(rms[act] ** 2))
    g = TARGET_RMS[event] / cur
    g = min(g, (PEAK if event != "step" else 0.85) / (np.max(np.abs(x)) + 1e-9))
    return fade(x * g, 0.002, 0.04)


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
                seed = (sum(ord(c) for c in name) * 131 + sum(ord(c) for c in event) * 17 + v * 7919) & 0x7FFFFFFF
                rng = np.random.default_rng(seed)
                x = level(fn(rng, v), event)
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
