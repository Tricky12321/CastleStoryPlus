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
    ("lion_bidone", 69570, "706955", 11.2, 26.2),
    ("lion_bidone", 69571, "706955", 1.6, 8.7),
    ("lion_roman_cgr", 223978, "639300", 0.0, 15.5),
    ("lion_felix_blume", 405211, "1661766", 11.0, 19.0),
    ("lion_stratcat322", 270383, "1808829", 0.3, 4.6),
    ("gorilla_j0ck0", 397054, "2884295", 0.1, 1.6),
    ("gorilla_chest_shutup_outcast", 367821, "6104883", 3.4, 9.5),
    ("rhino_breviceps", 535810, "9159316", 0.0, 9.2),
    ("camel_schots", 693564, "954919", 3.0, 12.0),
    ("moose_elk_gleith", 486554, "2795485", 0.7, 13.0),
    ("horse_black_trillium", 397236, "4889106", 0.3, 2.7),
    ("alpaca_thekingofgeeks360", 852641, "15895934", 0.0, 0.6),
    ("crystal_caiogracco", 150453, "2703701", 0.0, 1.8),
    ("crystal_caiogracco", 150454, "2703701", 0.0, 1.8),
    ("crystal_caiogracco", 150456, "2703701", 0.0, 0.3),
    ("fire_nic3_one", 166553, "1358210", 0.7, 7.6),
    ("fire_j1987", 111331, "367313", 0.0, 0.9),
    ("fire_craigsmith", 675771, "2524442", 0.1, 5.3),
    ("stone_lolamadeus", 179365, "544580", 0.3, 5.2),
    ("stone_northern87", 369981, "1196518", 4.3, 10.6),
    ("stone_kastenfrosch", 521956, "311243", 0.0, 2.3),
    ("iron_ldezem", 386129, "1655965", 0.0, 1.6),
    ("iron_alan3809", 103071, "1703470", 3.0, 7.0),
    ("iron_michorvath", 270589, "3094998", 0.1, 1.0),
    ("iron_rsellick", 545547, "6233742", 6.9, 12.0),
    ("iron_roxis_boy", 401622, "7516983", 2.6, 7.0),
    ("thud_sgrowe", 342535, "6148875", 0.0, 1.0),
    ("thud_7of9designs", 640204, "12131847", 0.0, 0.7),
    ("stomp_oscaraudiogeek", 334228, "5117035", 1.5, 6.5),
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
