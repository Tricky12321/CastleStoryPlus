#!/usr/bin/env python3
"""Rebuilds tools/guardian_sources/*.wav from the public CC0 recordings listed in
CastleStoryPlus/Assets/Sounds/Guardians/SOURCES.md. Only needed to re-create the trimmed source clips; the normal
build (make_guardian_sounds.py) only reads the clips that are already in tools/guardian_sources.

Downloads the Freesound preview (the file the site itself serves to anonymous visitors, MP3) of each sound, decodes
it with ffmpeg to mono 32 kHz 16-bit WAV and keeps only the part that is used. The start of the kept part is in the
file name (..._t<seconds>.wav), so times in make_guardian_sounds.py refer to the original recording.

Downloaded files are only decoded by ffmpeg, never executed. Needs ffmpeg and network access.
    python3 tools/cut_guardian_sources.py
"""
import os
import subprocess
import sys
import tempfile
import urllib.request

# name, freesound id, preview path token, start, end (seconds in the original recording)
CUTS = [
    ("lion_bidone", 69570, "706955", 3.4, 27.6),
    ("lion_bidone", 69571, "706955", 1.5, 9.0),
    ("lion_bidone", 69572, "706955", 4.8, 15.5),
    ("lion_stratcat322", 270383, "1808829", 0.3, 4.6),
    ("gorilla_j0ck0", 397054, "2884295", 0.1, 1.6),
    ("gorilla_chest_shutup_outcast", 367821, "6104883", 3.4, 11.5),
    ("rhino_breviceps", 535810, "9159316", 0.0, 9.2),
    ("camel_schots", 693564, "954919", 2.5, 20.0),
    ("horse_black_trillium", 397236, "4889106", 0.3, 2.7),
    ("alpaca_thekingofgeeks360", 852641, "15895934", 0.0, 0.6),
]


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    out = os.path.join(here, "guardian_sources")
    os.makedirs(out, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        for name, fid, token, a, b in CUTS:
            sid = str(fid)
            url = "https://cdn.freesound.org/previews/%s/%s_%s-hq.mp3" % (sid[:-3], sid, token)
            src = os.path.join(tmp, sid + ".mp3")
            req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
            with urllib.request.urlopen(req, timeout=60) as r, open(src, "wb") as f:
                f.write(r.read())
            dst = os.path.join(out, "%s_%d_t%.1f.wav" % (name, fid, a))
            subprocess.run(["ffmpeg", "-v", "error", "-y", "-ss", "%.3f" % a, "-t", "%.3f" % (b - a), "-i", src,
                            "-ac", "1", "-ar", "32000", "-c:a", "pcm_s16le", dst], check=True)
            print("wrote", dst)


if __name__ == "__main__":
    sys.exit(main())
