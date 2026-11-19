# Sources of the guardian sounds

The animal voices in `<Guardian>_<event>_<n>.wav` are parts of the recordings below, cleaned (noise reduction, noise
gate, rumble filter), pitched down and layered by `tools/make_guardian_sounds.py`. The crystal chimes, stone thuds,
iron clanks and ember pops on top are made in that script (no recording). The trimmed, decoded source clips are in
`tools/guardian_sources/` (mono, 16-bit, 32 kHz; `tools/cut_guardian_sources.py` re-creates them).

Every recording is **CC0 1.0 (public domain dedication)**, checked on its own Freesound page (the licence link on the
page is `http://creativecommons.org/publicdomain/zero/1.0/`, checked 19-11-2026). CC0 needs no attribution; the
authors are named here anyway. The audio used is the high quality MP3 preview that Freesound serves to every visitor,
decoded to WAV. No recording under CC-BY, CC-BY-NC, CC-BY-SA or any other licence is used.

| Source clip | Recording (Freesound page) | Author | Licence | Used in |
|---|---|---|---|---|
| `lion_bidone_69570_t3.4.wav` | https://freesound.org/people/Bidone/sounds/69570/ "Lion loud" (field recording of lions) | Bidone | CC0 1.0 | Cindermane roar 1-3, attack 1-2, death 1 |
| `lion_bidone_69571_t1.5.wav` | https://freesound.org/people/Bidone/sounds/69571/ "Lions two" | Bidone | CC0 1.0 | Cindermane idle 1-3 |
| `lion_bidone_69572_t4.8.wav` | https://freesound.org/people/Bidone/sounds/69572/ "Lion roaring" | Bidone | CC0 1.0 | Cindermane idle 4, roar 4, hit 1, death 2 |
| `lion_stratcat322_270383_t0.3.wav` | https://freesound.org/people/stratcat322/sounds/270383/ "lion_growls.wav" (cut and rearranged from roman_cgr's field recording https://freesound.org/people/roman_cgr/sounds/223978/ "Lion lament", also CC0 1.0) | stratcat322 | CC0 1.0 | Cindermane attack 3, hit 2-3 |
| `gorilla_j0ck0_397054_t0.1.wav` | https://freesound.org/people/J0ck0/sounds/397054/ "Gorillaschrei.wav" (roar of an alpha gorilla, recorded in a zoo) | J0ck0 | CC0 1.0 | Boulderback idle 2 and 4, roar 1-4, attack 1-3, hit 1-3, death 1-2 |
| `gorilla_chest_shutup_outcast_367821_t3.4.wav` | https://freesound.org/people/shutup_outcast/sounds/367821/ "Chest banging.wav" (the chest beats) | shutup_outcast | CC0 1.0 | Boulderback idle 1, 3 and 4, roar 1, 2 and 4, attack 2 |
| `rhino_breviceps_535810_t0.0.wav` | https://freesound.org/people/Breviceps/sounds/535810/ "Indian rhinoceros (Rhinoceros unicornis)" | Breviceps | CC0 1.0 | Ironhorn idle 1-4, roar 1-3, attack 2-3, hit 1-3, death 1-2 |
| `horse_black_trillium_397236_t0.3.wav` | https://freesound.org/people/black_trillium/sounds/397236/ "Horse Snort.wav" | black_trillium | CC0 1.0 | Ironhorn idle 1 and 3, attack 1 and 3; Spirestalker idle 3, attack 1 |
| `camel_schots_693564_t2.5.wav` | https://freesound.org/people/schots/sounds/693564/ "Dromedaries mooing" (field recording) | schots | CC0 1.0 | Spirestalker idle 1-4, roar 1-3, hit 2-3, death 1-2 |
| `alpaca_thekingofgeeks360_852641_t0.0.wav` | https://freesound.org/people/TheKingOfGeeks360/sounds/852641/ "Camelids - Alpaca; High Moan" | TheKingOfGeeks360 | CC0 1.0 | Spirestalker idle 4, attack 2, hit 1 |

## What is not an exact match

- **Giraffe (Spirestalker):** giraffes are almost silent and no CC0 or public domain giraffe recording was found (the
  giraffe recordings on Wikimedia Commons are CC BY-SA and so not used). The voice is dromedary moans, an alpaca moan
  and horse snorts, used as stand-ins for the giraffe's night humming, bleats and snorts.
- **Gorilla (Boulderback):** the only real gorilla recording under CC0 that was found is one roar (J0ck0). It is used
  at several pitches and lengths, with the chest beats of the second recording. The chest beats are the sound of a
  chest being beaten, the page does not say whose.
- **Rhinoceros (Ironhorn):** the rhino calls are a phone recording (noisy, the noise is reduced); the snorts come from
  a horse.
- Not used although they are on Freesound under CC0: recordings that are human imitations, synthesised or a mix of other
  sounds (for example a "gorilla audio" that is a man saying the word, a "rhinoceros trumpeting" made with a French horn,
  a "Lion Growl" whose page says it is not real, a "LION ROAR" made with a filter).
