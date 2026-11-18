#!/usr/bin/env python3
"""Synthesises the guardians' sounds (CastleStoryPlus/Assets/Sounds/Guardians/*.wav).

Everything is made from noise, oscillators, filters, envelopes and damped resonances (modal synthesis): no recordings,
no samples, no files of the game. Run it from anywhere:

    python3 tools/make_guardian_sounds.py                 # writes the WAVs next to the plugin's assets
    python3 tools/make_guardian_sounds.py --out DIR       # writes them somewhere else
    python3 tools/make_guardian_sounds.py --preview DIR   # also a spectrogram PNG per clip in DIR

Needs numpy and scipy (and Pillow for --preview). Output is deterministic (fixed seeds), 16-bit mono WAV at 32 kHz.

File names: <Guardian>_<event>_<n>.wav, with event one of idle, roar, step, attack, hit, death and n from 1. The game
picks one at random per event. To use other sounds (for example from a CC0 library) replace the files with the same
names, or add more numbered files: the game reads every <Guardian>_<event>_<n>.wav in the folder.

The sound of each guardian (the material is heard in every event):
    Spirestalker  giraffe, blue crystal  a high airy hum with glassy chimes (inharmonic bell partials)
    Cindermane    lion, fire and embers  a rough throaty roar with a bed of crackling embers and a fire whoosh
    Boulderback   gorilla, stone         deep chesty grunts, heavy thuds and grinding, crumbling stone
    Ironhorn      rhino, iron            snorts and bellows with clanking, ringing iron plates
"""
import argparse
import os
import sys
import wave

import numpy as np
from scipy import signal

SR = 32000
NYQ = SR / 2.0


# ---------------------------------------------------------------- building blocks

def tt(d):
    return np.arange(int(d * SR)) / SR


def white(n, rng):
    return rng.uniform(-1.0, 1.0, n)


def pink(n, rng):
    """Noise with a -3 dB per octave slope (filtered white noise)."""
    b = [0.049922035, -0.095993537, 0.050612699, -0.004408786]
    a = [1, -2.494956002, 2.017265875, -0.522189400]
    return signal.lfilter(b, a, white(n, rng))


def lowpass(x, f, order=2):
    return signal.sosfilt(signal.butter(order, min(f, NYQ * 0.98) / NYQ, "low", output="sos"), x)


def highpass(x, f, order=2):
    return signal.sosfilt(signal.butter(order, max(f, 20.0) / NYQ, "high", output="sos"), x)


def bandpass(x, lo, hi, order=2):
    return signal.sosfilt(signal.butter(order, [max(lo, 20.0) / NYQ, min(hi, NYQ * 0.98) / NYQ], "band", output="sos"), x)


def peak_filter(x, f, q):
    """A narrow resonance (a formant) at f."""
    b, a = signal.iirpeak(min(f, NYQ * 0.98) / NYQ, q)
    return signal.lfilter(b, a, x)


def sweep_filter(x, curve, q=2.0, kind="low"):
    """A state variable filter whose cutoff follows curve (Hz per sample)."""
    out = np.zeros_like(x)
    low = band = 0.0
    damp = 1.0 / q
    f = 2.0 * np.sin(np.pi * np.clip(curve, 30.0, NYQ * 0.45) / SR)
    for i in range(len(x)):
        low += f[i] * band
        high = x[i] - low - damp * band
        band += f[i] * high
        out[i] = low if kind == "low" else (band if kind == "band" else high)
    return out


def exp_env(n, attack=0.005, decay=0.2, curve=1.0):
    """Quick attack, exponential decay (decay: seconds to fall by 60 dB / 6.9)."""
    t = np.arange(n) / SR
    env = np.exp(-t / max(decay, 1e-4)) ** curve
    a = max(1, int(attack * SR))
    env[:a] *= np.linspace(0.0, 1.0, a) ** 1.5 if a <= n else 1.0
    return env


def shaped(n, points):
    """Piecewise linear envelope through (time fraction, level) points."""
    xs = [p[0] * (n - 1) for p in points]
    ys = [p[1] for p in points]
    return np.interp(np.arange(n), xs, ys)


def curve(n, points):
    """A smooth curve through (time fraction, value) points (pitch or filter contours)."""
    xs = np.array([p[0] * (n - 1) for p in points])
    ys = np.array([p[1] for p in points])
    c = np.interp(np.arange(n), xs, ys)
    return lowpass(c, 40.0, 1) if len(c) > 200 else c


def osc(freq, shape="sine", rng=None, jitter=0.0):
    """An oscillator following freq (Hz per sample). Band-limited saw/pulse via harmonics up to Nyquist."""
    f = freq.copy()
    if jitter and rng is not None:
        f = f * (1.0 + jitter * lowpass(white(len(f), rng), 60.0, 1) * 6.0)
    phase = np.cumsum(f) / SR
    if shape == "sine":
        return np.sin(2 * np.pi * phase)
    top = max(float(np.max(f)), 20.0)
    count = int(min(60, (NYQ * 0.9) // top))
    out = np.zeros(len(f))
    for k in range(1, count + 1):
        if shape == "saw":
            out += np.sin(2 * np.pi * k * phase) / k
        elif shape == "odd":
            if k % 2 == 1:
                out += np.sin(2 * np.pi * k * phase) / k
        elif shape == "buzz":
            out += np.sin(2 * np.pi * k * phase) / (k ** 0.6)
    return out * (2.0 / np.pi if shape == "saw" else 1.0)


def modal(duration, partials, rng, strike=0.002):
    """Damped resonances: (frequency, amplitude, decay seconds). A struck bell, plate or stone."""
    n = int(duration * SR)
    t = np.arange(n) / SR
    out = np.zeros(n)
    for freq, amp, decay in partials:
        if freq >= NYQ * 0.95:
            continue
        ph = rng.uniform(0, 2 * np.pi)
        out += amp * np.sin(2 * np.pi * freq * t + ph) * np.exp(-t / decay)
    if strike > 0:
        k = int(strike * SR * 4)
        click = white(n, rng) * np.exp(-t / strike)
        out += 0.5 * click * (np.arange(n) < k)
    return out


def place(buf, snippet, at, gain=1.0):
    i = int(at * SR)
    if i >= len(buf) or i < 0:
        return
    j = min(len(buf), i + len(snippet))
    buf[i:j] += snippet[: j - i] * gain


def crackle(duration, rng, rate, band=(900, 5200), power=2.2, decay=0.004, rate_curve=None):
    """Sparse random pops (embers, fire): Poisson times, power law loudness, each a short bandpassed burst."""
    n = int(duration * SR)
    out = np.zeros(n)
    t = 0.0
    while t < duration:
        r = rate if rate_curve is None else rate * float(np.interp(t / duration, rate_curve[0], rate_curve[1]))
        t += rng.exponential(1.0 / max(r, 0.01))
        if t >= duration:
            break
        amp = rng.uniform(0.05, 1.0) ** power
        d = decay * rng.uniform(0.5, 2.5)
        m = int(d * SR * 6)
        burst = white(m, rng) * np.exp(-np.arange(m) / SR / d)
        centre = rng.uniform(*band)
        burst = bandpass(burst, centre * 0.6, min(centre * 1.6, NYQ * 0.95), 1)
        place(out, burst, t, amp)
    return out


def grains(duration, rng, rate, freq_range, decay_range, rate_curve=None, noise=0.4):
    """Falling stones: many short damped resonances at random pitches."""
    n = int(duration * SR)
    out = np.zeros(n)
    t = 0.0
    while t < duration:
        r = rate if rate_curve is None else rate * float(np.interp(t / duration, rate_curve[0], rate_curve[1]))
        t += rng.exponential(1.0 / max(r, 0.01))
        if t >= duration:
            break
        f = np.exp(rng.uniform(np.log(freq_range[0]), np.log(freq_range[1])))
        d = rng.uniform(*decay_range)
        m = int(d * SR * 5)
        tm = np.arange(m) / SR
        g = np.sin(2 * np.pi * f * tm * (1 + 0.1 * np.exp(-tm / 0.01))) * np.exp(-tm / d)
        g += noise * white(m, rng) * np.exp(-tm / (d * 0.3))
        place(out, g, t, rng.uniform(0.15, 1.0) ** 2)
    return out


def room(x, rng, length=0.35, wet=0.12, damp=2500.0):
    """A little air round the sound: convolution with a decaying, darkened noise tail."""
    n = int(length * SR)
    ir = white(n, rng) * np.exp(-np.arange(n) / SR / (length / 5.0))
    ir = lowpass(ir, damp, 1)
    ir /= np.sqrt(np.sum(ir ** 2)) + 1e-9
    wetsig = signal.fftconvolve(x, ir)[: len(x)]
    return x * (1.0 - wet) + wetsig * wet * 1.5


def mix(*layers, length=None):
    n = length or max(len(l) for l in layers)
    out = np.zeros(n)
    for l in layers:
        m = min(n, len(l))
        out[:m] += l[:m]
    return out


def pad(x, n):
    return np.concatenate([x, np.zeros(max(0, n - len(x)))])[:n]


def vowel(source, a, b, mix_curve, q=6.0):
    """Formant filter (parallel resonances) that moves from vowel a to vowel b: a, b are lists of (freq, gain)."""
    out_a = sum(g * peak_filter(source, f, q) for f, g in a)
    out_b = sum(g * peak_filter(source, f, q) for f, g in b)
    return out_a * (1.0 - mix_curve) + out_b * mix_curve


def drive(x, amount):
    return np.tanh(x * amount) / np.tanh(amount)


def finish(x, level=0.89):
    """Remove DC and rumble, fade the ends, normalise to the level."""
    x = highpass(x, 25.0, 2)
    f = int(0.003 * SR)
    x[:f] *= np.linspace(0, 1, f)
    e = int(min(0.04, len(x) / SR * 0.3) * SR)
    x[-e:] *= np.linspace(1, 0, e) ** 1.5
    return x / (np.max(np.abs(x)) + 1e-9) * level


# vowel formants (Hz, gain): a rough "ah", "oh", "oo", "eh"
AH = [(700, 1.0), (1150, 0.7), (2600, 0.35)]
OH = [(450, 1.0), (800, 0.6), (2500, 0.2)]
OO = [(300, 1.0), (700, 0.35), (2400, 0.1)]
EH = [(530, 1.0), (1840, 0.5), (2480, 0.3)]
UH = [(520, 1.0), (1190, 0.5), (2390, 0.25)]


# ---------------------------------------------------------------- shared voices

def growl(dur, f0, rng, vowels=(UH, AH), rough=30.0, rough_depth=0.6, jitter=0.5, breath=0.25, saw="buzz", amp=None, pitch_noise=1.0):
    """A throaty animal voice: a harmonic source (pitch contour f0, a fraction-of-duration curve), amplitude roughness
    (the vocal folds flapping), pitch jitter, formants moving between two vowels and some breath."""
    n = int(dur * SR)
    f = f0 if isinstance(f0, np.ndarray) else curve(n, f0)
    src = osc(f, saw, rng, jitter * pitch_noise * 0.02)
    flutter = 1.0 - rough_depth * (0.5 + 0.5 * np.sin(2 * np.pi * np.cumsum(rough * (1 + 0.1 * lowpass(white(n, rng), 20, 1) * 4)) / SR))
    src = src * flutter
    src += breath * lowpass(white(n, rng), 3000, 1) * 0.6
    morph = np.linspace(0, 1, n) ** 1.3
    v = vowel(src, vowels[0], vowels[1], morph)
    if amp is not None:
        v = v * (amp if isinstance(amp, np.ndarray) else shaped(n, amp))
    return v


def thump(dur, f_start, f_end, decay, rng, click=0.2, click_hz=900):
    """A body hitting the ground: a falling sine, with a short dull click on top."""
    n = int(dur * SR)
    t = np.arange(n) / SR
    f = f_end + (f_start - f_end) * np.exp(-t / (decay * 0.35))
    body = np.sin(2 * np.pi * np.cumsum(f) / SR) * np.exp(-t / decay)
    knock = lowpass(white(n, rng), click_hz, 2) * np.exp(-t / 0.012) * click
    return body + knock


def whoosh(dur, rng, f_from, f_to, q=1.5, peak=0.5):
    n = int(dur * SR)
    c = curve(n, [(0, f_from), (peak, f_to), (1, f_from * 0.8)])
    x = sweep_filter(white(n, rng), c, q, "band")
    return x * shaped(n, [(0, 0), (peak, 1), (1, 0)]) * 2.0


def chime_partials(f0, decay, bright=1.0):
    """Glass or crystal: strongly inharmonic partials (a struck bell / tube) which decay faster the higher they are."""
    ratios = [1.0, 2.76, 5.40, 8.93, 13.34]
    amps = [1.0, 0.55 * bright, 0.32 * bright, 0.2 * bright, 0.1 * bright]
    return [(f0 * r, a, decay / (1.0 + 0.9 * i)) for i, (r, a) in enumerate(zip(ratios, amps))]


def chime(f0, decay, rng, dur=None, bright=1.0):
    dur = dur or decay * 3
    x = modal(dur, chime_partials(f0, decay, bright), rng, strike=0.0015)
    # slight beating: a detuned twin
    y = modal(dur, chime_partials(f0 * 1.004, decay * 0.9, bright), rng, strike=0.0)
    return (x + 0.5 * y) * 0.6


def iron_ring(f0, decay, rng, dur=None):
    """Struck iron plate: dense inharmonic partials with a metallic clank on top."""
    dur = dur or decay * 3
    ratios = [1.0, 1.59, 2.14, 2.65, 3.33, 4.1, 5.3, 6.7]
    amps = [1.0, 0.8, 0.7, 0.55, 0.45, 0.3, 0.22, 0.15]
    parts = [(f0 * r * rng.uniform(0.985, 1.015), a, decay / (1.0 + 0.45 * i)) for i, (r, a) in enumerate(zip(ratios, amps))]
    return modal(dur, parts, rng, strike=0.003)


def stone_knock(f0, decay, rng, dur=None):
    """Struck stone: a few low, quickly dying resonances and a dull crack."""
    dur = dur or decay * 4
    ratios = [1.0, 1.7, 2.6, 3.9, 5.6]
    amps = [1.0, 0.7, 0.45, 0.3, 0.18]
    parts = [(f0 * r * rng.uniform(0.97, 1.03), a, decay / (1.0 + 0.8 * i)) for i, (r, a) in enumerate(zip(ratios, amps))]
    return modal(dur, parts, rng, strike=0.004)


# ---------------------------------------------------------------- Spirestalker (giraffe, crystal)

def spire_idle(rng, v):
    # A soft, high hum rising and falling (a giraffe's low calls are almost infrasound; this is a singing crystal hum),
    # with a bell glint that sings on the pitch.
    d = 1.6 + 0.2 * v
    base = 300 + 40 * v
    f0 = [(0, base * 0.9), (0.3, base * 1.05), (0.7, base * 1.12), (1, base * 0.8)]
    h = growl(d, f0, rng, vowels=(OO, OH), rough=9, rough_depth=0.15, jitter=0.25, breath=0.35, saw="odd", amp=[(0, 0), (0.2, 1), (0.7, 0.8), (1, 0)])
    n = len(h)
    sparkle = highpass(chime(base * 4.0, 0.5, rng, d), 1200, 1) * 0.12
    c = np.zeros(n)
    place(c, chime(base * 2.0, 0.6, rng, 1.2), 0.35, 0.28)
    place(c, chime(base * 3.0, 0.5, rng, 1.0), 0.75, 0.2)
    return mix(h * 0.9, pad(sparkle, n), c)


def spire_roar(rng, v):
    # A long, rising keening cry, glass-like and harsh, over a chime cascade that rings out.
    d = 2.0
    n = int(d * SR)
    base = 330 + 30 * v
    f0 = [(0, base * 0.8), (0.18, base * 1.35), (0.55, base * 1.5), (1, base * 1.05)]
    cry = growl(d, f0, rng, vowels=(AH, EH), rough=45, rough_depth=0.35, jitter=1.0, breath=0.3, saw="buzz", amp=[(0, 0), (0.08, 1), (0.6, 0.9), (1, 0)])
    cry = drive(cry, 1.8) * 0.8
    cas = np.zeros(n)
    for i in range(9):
        place(cas, chime(rng.choice([660, 880, 990, 1320, 1760]) * rng.uniform(0.97, 1.03), rng.uniform(0.3, 0.7), rng, 1.2), 0.06 + i * 0.16 + rng.uniform(0, 0.05), 0.22 * (1 - i / 14))
    return mix(cry, cas, whoosh(d, rng, 1500, 4500, 1.0, 0.2) * 0.12)


def spire_step(rng, v):
    # Long legs: a light, dry hoof tick and a tiny crystal glint (a pace, so little weight).
    d = 0.5
    n = int(d * SR)
    hoof = thump(0.18, 150 + 15 * v, 75, 0.05, rng, click=0.5, click_hz=1800)
    out = np.zeros(n)
    place(out, hoof, 0.0, 0.8)
    place(out, chime(rng.choice([1980, 2480, 2960]) * rng.uniform(0.96, 1.04), 0.12, rng, 0.4), 0.012, 0.18)
    return out


def spire_attack(rng, v):
    # The neck whips down: a rising whoosh, a crack at the end and a bright chord.
    d = 0.9
    n = int(d * SR)
    sw = whoosh(0.4, rng, 600, 5200, 2.0, 0.85)
    out = np.zeros(n)
    place(out, sw, 0.0, 0.9)
    place(out, thump(0.3, 220, 80, 0.07, rng, 0.8, 2500), 0.36, 0.7)
    for i, f in enumerate([880, 1318, 1760]):
        place(out, chime(f * rng.uniform(0.98, 1.02), 0.4, rng, 0.9), 0.34 + i * 0.015, 0.3)
    voice = growl(0.5, [(0, 380), (0.3, 520), (1, 330)], rng, vowels=(AH, EH), rough=40, rough_depth=0.3, breath=0.3, amp=[(0, 0), (0.1, 1), (1, 0)])
    place(out, voice * 0.5, 0.05)
    return out


def spire_hit(rng, v):
    # Glass cracking: a handful of tinkles and a short pained cry.
    d = 0.7
    n = int(d * SR)
    out = np.zeros(n)
    for i in range(7):
        place(out, chime(rng.uniform(1500, 4200), rng.uniform(0.06, 0.2), rng, 0.4), rng.uniform(0, 0.18), rng.uniform(0.15, 0.4))
    place(out, thump(0.2, 260, 110, 0.04, rng, 0.8, 3000), 0, 0.5)
    cry = growl(0.45, [(0, 520), (0.2, 620 + 30 * v), (1, 380)], rng, vowels=(EH, AH), rough=55, rough_depth=0.3, jitter=0.8, breath=0.3, amp=[(0, 0), (0.06, 1), (1, 0)])
    place(out, drive(cry, 2) * 0.55, 0.0)
    return out


def spire_death(rng, v):
    # A falling cry that dies to a breath, crystals shattering and ringing out, a last descending chime run.
    d = 3.2
    n = int(d * SR)
    cry = growl(1.8, [(0, 560), (0.25, 600), (1, 160)], rng, vowels=(AH, OO), rough=25, rough_depth=0.4, jitter=0.9, breath=0.4, amp=[(0, 0), (0.05, 1), (0.6, 0.7), (1, 0)])
    out = np.zeros(n)
    place(out, drive(cry, 1.6) * 0.7, 0.0)
    for i in range(24):
        place(out, chime(rng.uniform(1100, 5200), rng.uniform(0.08, 0.35), rng, 0.8), 0.3 + rng.exponential(0.5) * (1 + i * 0.03) , rng.uniform(0.08, 0.3))
    for i, f in enumerate([1980, 1760, 1480, 1318, 990, 880, 660]):
        place(out, chime(f, 0.9, rng, 1.8), 0.9 + i * 0.28, 0.24)
    place(out, thump(0.5, 110, 40, 0.15, rng, 0.2, 700), 1.7, 0.5)
    return out


# ---------------------------------------------------------------- Cindermane (lion, fire and embers)

def cinder_idle(rng, v):
    # A low, throaty rumble that wobbles like a big cat's purr-growl, with embers crackling under it.
    d = 1.8
    n = int(d * SR)
    f0 = [(0, 78 + 6 * v), (0.4, 92), (1, 70)]
    g = growl(d, f0, rng, vowels=(UH, OH), rough=26, rough_depth=0.75, jitter=0.9, breath=0.5, amp=[(0, 0), (0.15, 0.9), (0.75, 0.8), (1, 0)])
    emb = crackle(d, rng, 55, rate_curve=([0, 0.5, 1], [0.6, 1.0, 0.4])) * 0.4
    return mix(drive(g, 1.5) * 0.85, emb)


def cinder_roar(rng, v):
    # The lion's roar: a few rising grunts that open into a long, rough, roaring bellow that tails off in huffs,
    # with a bed of crackle and a fire rush.
    d = 2.6
    n = int(d * SR)
    f0 = [(0, 90), (0.1, 150), (0.25, 190 + 15 * v), (0.55, 170), (0.8, 120), (1, 70)]
    r = growl(d, f0, rng, vowels=(UH, AH), rough=34, rough_depth=0.8, jitter=1.4, breath=0.45, saw="buzz", amp=[(0, 0), (0.06, 1), (0.55, 1.0), (0.85, 0.5), (1, 0)])
    r = drive(r, 2.6) * 0.75
    rush = bandpass(white(n, rng), 600, 4000, 1) * shaped(n, [(0, 0), (0.1, 0.5), (0.5, 0.6), (1, 0)]) * 0.35
    emb = crackle(d, rng, 90, band=(700, 6000), rate_curve=([0, 0.2, 1], [0.3, 1.0, 0.6])) * 0.55
    return mix(r, rush, emb)


def cinder_step(rng, v):
    # A big cat's padded paw: a soft low thud and a rustle of embers, quick and light.
    d = 0.4
    n = int(d * SR)
    paw = thump(0.25, 130 + 10 * v, 55, 0.06, rng, click=0.35, click_hz=700) * 0.8
    emb = crackle(0.3, rng, 120, band=(1000, 5000), decay=0.003) * 0.35
    out = np.zeros(n)
    place(out, paw, 0)
    place(out, emb, 0.01)
    return out


def cinder_attack(rng, v):
    # The pounce: a rush of flame, a short snarling roar at the moment of the bite.
    d = 1.0
    n = int(d * SR)
    fire = whoosh(0.5, rng, 400, 3500, 0.9, 0.7)
    snarl = growl(0.55, [(0, 140), (0.3, 230), (1, 120)], rng, vowels=(AH, UH), rough=40, rough_depth=0.8, jitter=1.4, breath=0.5, amp=[(0, 0), (0.08, 1), (0.6, 0.8), (1, 0)])
    out = np.zeros(n)
    place(out, fire, 0, 0.7)
    place(out, drive(snarl, 2.4) * 0.8, 0.2)
    place(out, thump(0.3, 120, 50, 0.08, rng, 0.5, 900), 0.28, 0.6)
    place(out, crackle(0.6, rng, 160) * 0.5, 0.25)
    return out


def cinder_hit(rng, v):
    # A yelp with a burst of sparks.
    d = 0.7
    n = int(d * SR)
    y = growl(0.45, [(0, 220), (0.15, 380 + 25 * v), (1, 180)], rng, vowels=(EH, AH), rough=50, rough_depth=0.6, jitter=1.2, breath=0.4, amp=[(0, 0), (0.05, 1), (1, 0)])
    out = np.zeros(n)
    place(out, drive(y, 2.2) * 0.75, 0)
    place(out, crackle(0.5, rng, 260, band=(1500, 7000), decay=0.002) * 0.8, 0)
    return out


def cinder_death(rng, v):
    # A long bellow that sinks to a rattle, the fire dying away to a few last pops.
    d = 3.4
    n = int(d * SR)
    f0 = [(0, 170), (0.2, 160), (0.6, 100), (1, 45)]
    r = growl(2.2, f0, rng, vowels=(AH, OO), rough=28, rough_depth=0.8, jitter=1.4, breath=0.5, amp=[(0, 0), (0.05, 1), (0.5, 0.8), (1, 0)])
    out = np.zeros(n)
    place(out, drive(r, 2.2) * 0.75, 0)
    place(out, crackle(d, rng, 110, rate_curve=([0, 0.4, 1], [1.2, 0.8, 0.05])) * 0.6, 0)
    place(out, thump(0.5, 100, 38, 0.16, rng, 0.2, 500), 1.9, 0.6)
    place(out, lowpass(white(int(1.2 * SR), rng), 900, 1) * shaped(int(1.2 * SR), [(0, 0), (0.1, 1), (1, 0)]) * 0.25, 1.9)
    return out


# ---------------------------------------------------------------- Boulderback (gorilla, stone)

def boulder_idle(rng, v):
    # Deep chesty "hoo-hoo" grunts, each with a dull stone scrape.
    d = 1.7
    n = int(d * SR)
    out = np.zeros(n)
    for i, at in enumerate([0.0, 0.5, 0.98]):
        g = growl(0.42, [(0, 85 + 5 * v), (0.4, 100), (1, 62)], rng, vowels=(OO, UH), rough=18, rough_depth=0.35, jitter=0.6, breath=0.35, amp=[(0, 0), (0.15, 1), (1, 0)])
        place(out, g, at, 0.9 - i * 0.12)
    grind = lowpass(pink(n, rng), 600, 2) * shaped(n, [(0, 0), (0.2, 1), (0.7, 0.6), (1, 0)])
    grind *= 0.5 + 0.5 * np.sin(2 * np.pi * np.cumsum(np.full(n, 9.0)) / SR) ** 2
    return mix(out, grind * 0.3)


def boulder_roar(rng, v):
    # Chest drumming (hollow thumps, quick) into a deep, rough roar with rocks grinding underneath.
    d = 2.8
    n = int(d * SR)
    out = np.zeros(n)
    for i in range(6):
        place(out, thump(0.22, 120 - 8 * (i % 2), 55, 0.06, rng, 0.35, 500), 0.05 + i * 0.13, 0.65)
    f0 = [(0, 80), (0.15, 130), (0.35, 150 + 10 * v), (0.7, 120), (1, 60)]
    r = growl(2.0, f0, rng, vowels=(UH, AH), rough=22, rough_depth=0.7, jitter=1.0, breath=0.45, amp=[(0, 0), (0.1, 0.7), (0.5, 1), (1, 0)])
    place(out, drive(r, 2.4) * 0.8, 0.75)
    gr = lowpass(pink(n, rng), 800, 2) * shaped(n, [(0, 0), (0.3, 0.7), (0.8, 0.6), (1, 0)]) * (0.6 + 0.4 * np.sin(2 * np.pi * np.cumsum(np.full(n, 6.5)) / SR))
    return mix(out, gr * 0.4, grains(d, rng, 22, (120, 700), (0.03, 0.09)) * 0.3)


def boulder_step(rng, v):
    # A heavy knuckle or foot: a deep thud that lingers, a crunch of stone on stone.
    d = 0.6
    n = int(d * SR)
    out = np.zeros(n)
    place(out, thump(0.5, 95 + 8 * v, 38, 0.13, rng, 0.25, 500), 0, 1.0)
    place(out, stone_knock(210 * rng.uniform(0.9, 1.1), 0.06, rng, 0.4), 0.005, 0.28)
    place(out, grains(0.25, rng, 60, (200, 1400), (0.01, 0.03)), 0.02, 0.3)
    return out


def boulder_attack(rng, v):
    # Both fists up, a grunt, then the slam: a huge thud, a crack and falling rubble.
    d = 1.4
    n = int(d * SR)
    out = np.zeros(n)
    g = growl(0.5, [(0, 100), (0.4, 140), (1, 90)], rng, vowels=(UH, AH), rough=24, rough_depth=0.5, jitter=0.8, breath=0.45, amp=[(0, 0), (0.2, 1), (1, 0.2)])
    place(out, drive(g, 2) * 0.6, 0)
    place(out, thump(0.9, 90, 32, 0.22, rng, 0.45, 600), 0.4, 1.0)
    place(out, stone_knock(180, 0.14, rng, 0.8), 0.4, 0.5)
    place(out, grains(0.8, rng, 70, (150, 2200), (0.02, 0.07), rate_curve=([0, 1], [1.0, 0.1])), 0.42, 0.45)
    return out


def boulder_hit(rng, v):
    # Stone cracking and chipping, and a short pained grunt.
    d = 0.8
    n = int(d * SR)
    out = np.zeros(n)
    place(out, stone_knock(260 * rng.uniform(0.9, 1.1), 0.09, rng, 0.5), 0, 0.8)
    place(out, grains(0.5, rng, 90, (300, 3000), (0.008, 0.03)), 0.0, 0.45)
    g = growl(0.4, [(0, 130), (0.2, 160 + 10 * v), (1, 95)], rng, vowels=(AH, UH), rough=30, rough_depth=0.5, breath=0.4, amp=[(0, 0), (0.08, 1), (1, 0)])
    place(out, drive(g, 2) * 0.6, 0.02)
    return out


def boulder_death(rng, v):
    # A long groan, then the body comes apart: a rockslide of tumbling stones fading away with a final slump.
    d = 3.8
    n = int(d * SR)
    out = np.zeros(n)
    g = growl(1.9, [(0, 110), (0.3, 120), (1, 40)], rng, vowels=(UH, OO), rough=17, rough_depth=0.6, jitter=0.9, breath=0.5, amp=[(0, 0), (0.08, 1), (0.6, 0.8), (1, 0)])
    place(out, drive(g, 2) * 0.75, 0)
    place(out, grains(3.0, rng, 55, (90, 2400), (0.02, 0.1), rate_curve=([0, 0.3, 1], [0.3, 1.0, 0.05])), 0.8, 0.55)
    for i, at in enumerate([0.9, 1.4, 1.9, 2.5]):
        place(out, thump(0.6, 80, 32, 0.18, rng, 0.2, 400), at, 0.8 - i * 0.12)
        place(out, stone_knock(rng.uniform(120, 260), 0.1, rng, 0.6), at, 0.35)
    gr = lowpass(pink(int(2.0 * SR), rng), 500, 2) * shaped(int(2.0 * SR), [(0, 0), (0.2, 1), (1, 0)])
    place(out, gr, 0.8, 0.35)
    return out


# ---------------------------------------------------------------- Ironhorn (rhino, iron)

def iron_idle(rng, v):
    # A heavy snort and huff with a deep grunt, a faint ring of plates shifting.
    d = 1.5
    n = int(d * SR)
    out = np.zeros(n)
    for at, ln in [(0.0, 0.38), (0.55, 0.3)]:
        m = int(ln * SR)
        breath = bandpass(white(m, rng), 300, 2400, 2) * shaped(m, [(0, 0), (0.12, 1), (0.6, 0.5), (1, 0)])
        breath = sweep_filter(breath * 2, curve(m, [(0, 900), (0.4, 1800), (1, 700)]), 1.5, "band")
        place(out, breath, at, 0.8)
    g = growl(0.9, [(0, 70 + 4 * v), (0.4, 85), (1, 60)], rng, vowels=(UH, OH), rough=20, rough_depth=0.5, breath=0.3, amp=[(0, 0), (0.2, 1), (1, 0)])
    place(out, drive(g, 1.8) * 0.6, 0.25)
    place(out, iron_ring(520 * rng.uniform(0.95, 1.05), 0.25, rng, 0.8), 0.7, 0.12)
    return out


def iron_roar(rng, v):
    # A bellowing, brassy roar (rough, low, with harmonic bite) over a ringing of iron plates and clanks.
    d = 2.4
    n = int(d * SR)
    f0 = [(0, 75), (0.12, 110), (0.35, 135 + 8 * v), (0.7, 110), (1, 60)]
    r = growl(d, f0, rng, vowels=(AH, UH), rough=15, rough_depth=0.55, jitter=0.7, breath=0.3, saw="saw", amp=[(0, 0), (0.08, 1), (0.6, 1), (1, 0)])
    r = drive(r, 3.0) * 0.7
    out = np.zeros(n)
    place(out, r, 0)
    for i in range(7):
        place(out, iron_ring(rng.uniform(380, 900), rng.uniform(0.15, 0.4), rng, 1.0), 0.15 + i * 0.25 + rng.uniform(0, 0.08), 0.16)
    # the horn shrieking on the plates
    place(out, sweep_filter(white(int(0.9 * SR), rng) * 1.0, curve(int(0.9 * SR), [(0, 1800), (1, 2600)]), 8.0, "band") * shaped(int(0.9 * SR), [(0, 0), (0.3, 1), (1, 0)]) * 0.25, 0.4)
    return out


def iron_step(rng, v):
    # A heavy trot: a deep thump and a clank of plates settling (ringing metal, quick to die).
    d = 0.6
    n = int(d * SR)
    out = np.zeros(n)
    place(out, thump(0.4, 100 + 8 * v, 45, 0.09, rng, 0.3, 700), 0, 0.95)
    place(out, iron_ring(rng.choice([430, 560, 690]) * rng.uniform(0.95, 1.05), 0.09, rng, 0.45), 0.012, 0.28)
    place(out, highpass(white(int(0.1 * SR), rng), 2500, 1) * exp_env(int(0.1 * SR), 0.001, 0.015), 0.02, 0.15)
    return out


def iron_attack(rng, v):
    # The charge: a snort, thundering steps and the horn striking with a loud clang.
    d = 1.2
    n = int(d * SR)
    out = np.zeros(n)
    sn = bandpass(white(int(0.35 * SR), rng), 250, 2500, 2) * shaped(int(0.35 * SR), [(0, 0), (0.15, 1), (1, 0)])
    place(out, sn, 0, 0.7)
    for i, at in enumerate([0.1, 0.27, 0.43]):
        place(out, thump(0.3, 95, 42, 0.07, rng, 0.3, 700), at, 0.75)
        place(out, iron_ring(rng.uniform(400, 600), 0.06, rng, 0.3), at, 0.2)
    place(out, iron_ring(rng.choice([700, 820, 940]), 0.55, rng, 1.2), 0.5, 0.65)
    place(out, thump(0.4, 110, 45, 0.1, rng, 0.8, 1200), 0.5, 0.8)
    g = growl(0.5, [(0, 90), (0.4, 130), (1, 85)], rng, vowels=(AH, UH), rough=18, rough_depth=0.5, saw="saw", amp=[(0, 0), (0.15, 1), (1, 0)])
    place(out, drive(g, 2.5) * 0.55, 0.28)
    return out


def iron_hit(rng, v):
    # A clang: iron struck, ringing out, with a short bellow of pain.
    d = 0.9
    n = int(d * SR)
    out = np.zeros(n)
    place(out, iron_ring(rng.choice([500, 640, 780]) * rng.uniform(0.95, 1.05), 0.35, rng, 0.85), 0, 0.8)
    place(out, thump(0.2, 150, 60, 0.05, rng, 0.9, 2000), 0, 0.6)
    g = growl(0.45, [(0, 110), (0.2, 150 + 8 * v), (1, 90)], rng, vowels=(AH, UH), rough=20, rough_depth=0.5, saw="saw", amp=[(0, 0), (0.06, 1), (1, 0)])
    place(out, drive(g, 2.5) * 0.6, 0.02)
    return out


def iron_death(rng, v):
    # A long falling bellow, then the plates clatter to the ground one after another, ringing and settling.
    d = 3.6
    n = int(d * SR)
    out = np.zeros(n)
    g = growl(1.8, [(0, 120), (0.3, 125), (1, 42)], rng, vowels=(AH, OO), rough=16, rough_depth=0.55, jitter=0.8, saw="saw", breath=0.3, amp=[(0, 0), (0.06, 1), (0.6, 0.8), (1, 0)])
    place(out, drive(g, 2.4) * 0.7, 0)
    at = 0.7
    gap = 0.3
    for i in range(14):
        place(out, iron_ring(rng.uniform(300, 1100), rng.uniform(0.1, 0.45), rng, 1.0), at, 0.5 * (1 - i / 18))
        place(out, thump(0.25, 90, 45, 0.06, rng, 0.5, 1500), at, 0.4 * (1 - i / 18))
        gap *= 0.86
        at += gap * rng.uniform(0.7, 1.3)
    place(out, thump(0.7, 80, 30, 0.2, rng, 0.2, 400), 1.9, 0.9)
    place(out, iron_ring(210, 0.9, rng, 1.5), 1.9, 0.4)
    return out


# ---------------------------------------------------------------- the set

# name -> {event: (function, variants)}; the level is how loud the clip is before the game's per-event volume.
SOUNDS = {
    "Spirestalker": {"idle": (spire_idle, 2), "roar": (spire_roar, 2), "step": (spire_step, 3), "attack": (spire_attack, 2), "hit": (spire_hit, 2), "death": (spire_death, 1)},
    "Cindermane": {"idle": (cinder_idle, 2), "roar": (cinder_roar, 2), "step": (cinder_step, 3), "attack": (cinder_attack, 2), "hit": (cinder_hit, 2), "death": (cinder_death, 1)},
    "Boulderback": {"idle": (boulder_idle, 2), "roar": (boulder_roar, 2), "step": (boulder_step, 3), "attack": (boulder_attack, 2), "hit": (boulder_hit, 2), "death": (boulder_death, 1)},
    "Ironhorn": {"idle": (iron_idle, 2), "roar": (iron_roar, 2), "step": (iron_step, 3), "attack": (iron_attack, 2), "hit": (iron_hit, 2), "death": (iron_death, 1)},
}


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
    # a simple heat colour map
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
    here = os.path.dirname(os.path.abspath(__file__))
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", default=os.path.join(here, "..", "CastleStoryPlus", "Assets", "Sounds", "Guardians"))
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
                x = fn(rng, v)
                x = room(x, rng, 0.3, 0.1 if event == "step" else 0.16)
                x = finish(x, 0.89 if event != "step" else 0.85)
                base = "%s_%s_%d" % (name, event, v + 1)
                write_wav(os.path.join(args.out, base + ".wav"), x)
                if args.preview:
                    spectrogram_png(os.path.join(args.preview, base + ".png"), x, base)
                total += 1
    print("wrote %d clips to %s" % (total, os.path.abspath(args.out)))


if __name__ == "__main__":
    sys.exit(main())
