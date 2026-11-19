#!/usr/bin/env python3
"""Builds the guardians' sounds (CastleStoryPlus/Assets/Sounds/Guardians/*.wav) from real animal recordings only.

Each clip is only the voice of the animal: a part of a CC0 / public domain recording (tools/guardian_sources, listed
with their licences in CastleStoryPlus/Assets/Sounds/Guardians/SOURCES.md). The footsteps are one recording of heavy
stomps. Nothing is synthesised and nothing else is layered on: the script cuts the parts, takes the background out
(spectral noise reduction, a low pass above the voice, a noise gate on the tails), pitch-shifts them moderately for
the size of the beast, levels and fades. Between the sounds the clips are silent. Run it from anywhere:

    python3 tools/make_guardian_sounds.py                 # writes the WAVs next to the plugin's assets
    python3 tools/make_guardian_sounds.py --out DIR       # writes them somewhere else
    python3 tools/make_guardian_sounds.py --preview DIR   # also a spectrogram PNG per clip in DIR
    python3 tools/make_guardian_sounds.py --only Ironhorn

Needs numpy and scipy (and Pillow for --preview). Output is deterministic, 16-bit mono WAV at 32 kHz, levelled per
event to the same active RMS (a static soft clip keeps the peaks under 0.89, no pumping). The source clips are read
with the standard wave module; tools/cut_guardian_sources.py re-creates them from the original recordings. Numbered
clips of a guardian and event that are not made any more are deleted from the output folder.

File names: <Guardian>_<event>_<n>.wav, with event one of idle, roar, step, attack, hit, death and n from 1. The game
picks one at random per event (every numbered file in the folder is read), clips must be 0.15 - 6 s.

The voice of each guardian (stand-ins are named in SOURCES.md):
    Spirestalker  giraffe   dromedary moans, a horse snort and an alpaca moan (no CC0 giraffe recording exists)
    Cindermane    lion      real lion roars, grunts and growls
    Boulderback   gorilla   one real gorilla roar, cut and pitched in several ways
    Ironhorn      rhino     real Indian rhinoceros calls
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

# the stomps are a clean close recording, only the animals get the noise reduction
MATERIAL = {334228}
# per recording: where the voice stops (a low pass takes birds, insects and hiss above it away) and how hard the
# noise reduction works (a noisy recording gets more)
LOWPASS = {69570: 3500, 69571: 2800, 223978: 5000, 405211: 3000, 693564: 3500, 535810: 4500, 397054: 6000}
REDUCE = {693564: 3.0, 535810: 3.0}


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
        _CLEAN[fid] = (x if fid in MATERIAL else denoise(x, over=REDUCE.get(fid, 2.4)), off)
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


def cut(fid, t0, t1, st=0.0):
    """One part of a source recording (times in the original recording), cleaned, pitched by st semitones."""
    x, off = clean_source(fid)
    i0 = max(0, int(round((t0 - off) * SR)))
    i1 = min(len(x), int(round((t1 - off) * SR)))
    seg = x[i0:i1]
    if fid in LOWPASS:
        seg = lowpass(seg, LOWPASS[fid], 4)
    # the fades come after the high pass, so the part ends at zero (the filter's tail would end in a step: a click)
    out = fade(highpass(pitch(gate(seg), st), 30.0, 2), 0.008, 0.02)
    return out * (0.8 / (np.max(np.abs(out)) + 1e-9))


def seq(fid, parts, st=0.0, gap=0.14):
    """Several parts of one recording one after the other (true silence of `gap` seconds between them)."""
    out = np.zeros(0)
    for t0, t1 in parts:
        out = np.concatenate([out, np.zeros(int(gap * SR)) if len(out) else out, cut(fid, t0, t1, st)])
    return out


# ---------------------------------------------------------------- the clips
# Freesound ids, see SOURCES.md. Times are in the original recording. A clip is one voice (or a few parts of it in a
# row), followed by silence.

LION_A, LION_B, LION_R, LION_N = 69570, 69571, 223978, 405211
GORILLA, RHINO = 397054, 535810
CAMEL, HORSE, ALPACA = 693564, 397236, 852641
STOMP = 334228
# each starts 20 ms before the stomp (onsets at 1.65, 3.92 and 6.30 s), so the sound is not late on the footfall
STOMPS = [(1.63, 2.13), (3.90, 4.36), (6.28, 6.50)]


def one(x, tail=0.15):
    """The voice, then `tail` seconds of silence."""
    return np.concatenate([x, np.zeros(int(tail * SR))])


def step(semitones, v):
    """A footfall: one of the heavy stomps, pitched for the size of the guardian."""
    return one(cut(STOMP, STOMPS[v][0], STOMPS[v][1], semitones), 0.05)


# --- Spirestalker (giraffe): moans, a snort and a bleat ---

def spire_idle(v):
    return one([cut(CAMEL, 3.2, 5.6, -1.0), cut(CAMEL, 6.8, 8.8, 0.0), cut(HORSE, 0.5, 0.9, 0.0), cut(ALPACA, 0.0, 0.6, -1.0)][v])


def spire_roar(v):
    return one([cut(CAMEL, 3.2, 5.9, 1.0), cut(CAMEL, 9.0, 11.2, 2.0)][v])


def spire_step(v):
    return step(5.0, v)


def spire_attack(v):
    return one([cut(HORSE, 2.0, 2.4, 0.0), cut(ALPACA, 0.0, 0.6, 0.0)][v])


def spire_hit(v):
    return one([cut(ALPACA, 0.0, 0.6, -1.0), cut(CAMEL, 10.0, 10.8, 1.0)][v])


def spire_death(v):
    return one([cut(CAMEL, 3.2, 5.9, -4.0), cut(CAMEL, 6.8, 8.8, -4.0)][v])


# --- Cindermane (lion) ---

def cinder_idle(v):
    src = [(LION_B, 1.8, 2.5), (LION_B, 4.8, 5.7), (LION_B, 7.5, 8.6), (LION_R, 0.1, 1.9)][v]
    return one(cut(src[0], src[1], src[2], -2.0))


def cinder_roar(v):
    return one([cut(LION_N, 11.1, 14.2, -2.0),
                seq(LION_A, [(14.0, 15.4), (16.2, 17.5)], -2.0, 0.1),
                seq(LION_R, [(10.6, 12.2), (13.9, 15.2)], -2.5, 0.1),
                seq(LION_A, [(20.0, 21.4), (21.9, 23.1), (23.6, 24.7)], -2.0, 0.08)][v])


def cinder_step(v):
    return step(0.0, v)


def cinder_attack(v):
    src = [(LION_R, 7.2, 8.8), (LION_A, 18.2, 19.4), (LION_R, 4.0, 5.5)][v]
    return one(cut(src[0], src[1], src[2], -2.0))


def cinder_hit(v):
    src = [(LION_A, 20.0, 20.8), (LION_R, 13.9, 14.7), (LION_N, 16.9, 17.7)][v]
    return one(cut(src[0], src[1], src[2], -2.0))


def cinder_death(v):
    return one([seq(LION_A, [(21.9, 23.1), (23.6, 24.7), (25.1, 26.1)], -4.0, 0.12), cut(LION_N, 16.9, 18.9, -5.0)][v])


# --- Boulderback (gorilla): the one real roar (0.2 - 0.9 s of the recording), cut and pitched in several ways ---

def boulder_idle(v):
    return one([cut(GORILLA, 0.2, 0.9, -7.0), cut(GORILLA, 0.2, 0.6, -9.0)][v])


def boulder_roar(v):
    return one([cut(GORILLA, 0.2, 0.9, -4.0), cut(GORILLA, 0.2, 0.9, -6.0), seq(GORILLA, [(0.2, 0.6), (0.5, 0.9)], -5.0, 0.2)][v])


def boulder_step(v):
    return step(-5.0, v)


def boulder_attack(v):
    return one([cut(GORILLA, 0.2, 0.9, -3.0), cut(GORILLA, 0.2, 0.7, -5.0)][v])


def boulder_hit(v):
    return one([cut(GORILLA, 0.2, 0.6, -4.0), cut(GORILLA, 0.3, 0.8, -6.0)][v])


def boulder_death(v):
    return one([cut(GORILLA, 0.2, 0.9, -8.0), cut(GORILLA, 0.2, 0.8, -7.0)][v])


# --- Ironhorn (rhino) ---

def iron_idle(v):
    return one([cut(RHINO, 1.2, 1.9, -3.0), cut(RHINO, 0.1, 0.7, -4.0), seq(RHINO, [(2.1, 2.6), (2.8, 3.1)], -4.0, 0.2), seq(RHINO, [(3.7, 3.9), (4.2, 4.8)], -3.0, 0.2)][v])


def iron_roar(v):
    return one([cut(RHINO, 7.9, 9.0, -5.0), cut(RHINO, 4.2, 5.0, -5.0), seq(RHINO, [(1.2, 1.9), (2.1, 2.6)], -4.0, 0.12)][v])


def iron_step(v):
    return step(-2.0, v)


def iron_attack(v):
    return one([cut(RHINO, 0.1, 0.7, -3.0), cut(RHINO, 1.2, 1.9, -4.0), seq(RHINO, [(3.7, 3.9), (4.2, 4.7)], -4.0, 0.1)][v])


def iron_hit(v):
    src = [(7.9, 8.5, -4.0), (2.8, 3.1, -4.0), (4.2, 4.7, -4.0)][v]
    return one(cut(RHINO, src[0], src[1], src[2]))


def iron_death(v):
    return one([cut(RHINO, 7.9, 9.0, -7.0), seq(RHINO, [(1.2, 1.9), (2.1, 2.6)], -6.0, 0.12)][v])


# ---------------------------------------------------------------- the set

# name -> {event: (function, variants)}
SOUNDS = {
    "Spirestalker": {"idle": (spire_idle, 4), "roar": (spire_roar, 2), "step": (spire_step, 3), "attack": (spire_attack, 2), "hit": (spire_hit, 2), "death": (spire_death, 2)},
    "Cindermane": {"idle": (cinder_idle, 4), "roar": (cinder_roar, 4), "step": (cinder_step, 3), "attack": (cinder_attack, 3), "hit": (cinder_hit, 3), "death": (cinder_death, 2)},
    "Boulderback": {"idle": (boulder_idle, 2), "roar": (boulder_roar, 3), "step": (boulder_step, 3), "attack": (boulder_attack, 2), "hit": (boulder_hit, 2), "death": (boulder_death, 2)},
    "Ironhorn": {"idle": (iron_idle, 4), "roar": (iron_roar, 3), "step": (iron_step, 3), "attack": (iron_attack, 3), "hit": (iron_hit, 3), "death": (iron_death, 2)},
}

# the active RMS each event is levelled to (the level of the earlier sound set)
TARGET_RMS = {"idle": 0.18, "roar": 0.18, "attack": 0.15, "hit": 0.17, "death": 0.15, "step": 0.20}
PEAK = 0.89
# the highest peak the levelling gain may produce before the soft clip (above it the clip would audibly distort)
MAX_DRIVE = 1.2


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
    the gain is corrected for what the clip took away. The gain never drives a peak above MAX_DRIVE: a clip with very
    sharp transients ends up a little quieter instead of audibly distorted."""
    x = highpass(x, 30.0, 2)
    g = TARGET_RMS[event] / active_rms(x)
    for _ in range(4):
        g *= TARGET_RMS[event] / active_rms(soft(x * g))
    g = min(g, MAX_DRIVE / (np.max(np.abs(x)) + 1e-9))
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
            for old in glob.glob(os.path.join(args.out, "%s_%s_*.wav" % (name, event))):
                if int(re.search(r"_(\d+)\.wav$", old).group(1)) > variants:
                    os.remove(old)
    print("wrote %d clips to %s" % (total, os.path.abspath(args.out)))


if __name__ == "__main__":
    sys.exit(main())
