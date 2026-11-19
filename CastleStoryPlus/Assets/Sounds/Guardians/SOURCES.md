# Sources of the guardian sounds

Every clip `<Guardian>_<event>_<n>.wav` is only the voice of the animal, a part of a recording below (the footsteps are
one recording of heavy stomps). Nothing is synthesised and nothing else is layered on. `tools/make_guardian_sounds.py`
cuts the parts, takes the background out (noise reduction, a low pass above the voice, a noise gate), pitches them
moderately for the size of the beast, levels and fades them. The trimmed, decoded source clips are in
`tools/guardian_sources/` (mono, 16-bit, 32 kHz, the start of the kept part is in the file name;
`tools/cut_guardian_sources.py` re-creates them).

Every recording is **CC0 1.0 (public domain dedication)**, checked on its own Freesound page (the licence link on the
page is `http://creativecommons.org/publicdomain/zero/1.0/`, checked 19-11-2026), and the page description was read to
rule out imitations, synthesised or effect-made sounds and human voices. CC0 needs no attribution; the authors are
named here anyway. The audio used is the high quality MP3 preview that Freesound serves to every visitor, decoded to
WAV. No recording under CC-BY, CC-BY-NC, CC-BY-SA or any other licence is used.

| Guardian | Source clip | Recording (Freesound page) | Author | Clips |
|---|---|---|---|---|
| Cindermane (lion) | `lion_bidone_69570_t11.2.wav` | https://freesound.org/people/Bidone/sounds/69570/ "Lion loud" (field recording of lions) | Bidone | roar 2 and 4, attack 2, hit 1, death 1 |
| Cindermane | `lion_bidone_69571_t1.6.wav` | https://freesound.org/people/Bidone/sounds/69571/ "Lions two" | Bidone | idle 1-3 |
| Cindermane | `lion_roman_cgr_223978_t0.0.wav` | https://freesound.org/people/roman_cgr/sounds/223978/ "Lion lament" (zoo, Sennheiser K6 + Zoom H4N) | roman_cgr | idle 4, roar 3, attack 1 and 3, hit 2 |
| Cindermane | `lion_felix_blume_405211_t11.0.wav` | https://freesound.org/people/felix.blume/sounds/405211/ "Lions screaming during the night" (zoo in Quilpue, Chile) | felix.blume | roar 1, hit 3, death 2 |
| Boulderback (gorilla) | `gorilla_j0ck0_397054_t0.1.wav` | https://freesound.org/people/J0ck0/sounds/397054/ "Gorillaschrei.wav" (roar of an alpha gorilla, recorded in a zoo) | J0ck0 | all clips except the steps |
| Ironhorn (rhino) | `rhino_breviceps_535810_t0.0.wav` | https://freesound.org/people/Breviceps/sounds/535810/ "Indian rhinoceros (Rhinoceros unicornis)" (phone recording) | Breviceps | all clips except the steps |
| Spirestalker (giraffe) | `camel_schots_693564_t3.0.wav` | https://freesound.org/people/schots/sounds/693564/ "Dromedaries mooing" (field recording) | schots | idle 1-2, roar 1-2, hit 2, death 1-2 |
| Spirestalker | `horse_black_trillium_397236_t0.3.wav` | https://freesound.org/people/black_trillium/sounds/397236/ "Horse Snort.wav" | black_trillium | idle 3, attack 1 |
| Spirestalker | `alpaca_thekingofgeeks360_852641_t0.0.wav` | https://freesound.org/people/TheKingOfGeeks360/sounds/852641/ "Camelids - Alpaca; High Moan" | TheKingOfGeeks360 | idle 4, attack 2, hit 1 |
| All (steps) | `stomp_oscaraudiogeek_334228_t1.5.wav` | https://freesound.org/people/oscaraudiogeek/sounds/334228/ "Stomping Ground Super Bassy" (stomps on cement, contact mic) | oscaraudiogeek | step 1-3 of every guardian, pitched per guardian |

Licence of every row: CC0 1.0.

## What is not an exact match (stand-ins and compromises)

- **Giraffe (Spirestalker):** giraffes are almost silent and no CC0 or public domain giraffe recording exists (the
  giraffe recordings on Wikimedia Commons are CC BY-SA, so not used). The voice is dromedary moans, an alpaca moan and a
  horse snort, standing in for the giraffe's humming, bleats and snorts.
- **Gorilla (Boulderback):** the only real gorilla recording under CC0 that was found is one roar (J0ck0, about 0.7
  seconds). Every Boulderback voice clip is that roar cut and pitched in different ways, so it has fewer variants (two
  idle, three roar, two attack, two hit, two death).
- **Rhinoceros (Ironhorn):** the only real rhinoceros recording found under CC0 is the phone recording of an Indian
  rhinoceros (noisy, the noise is reduced and the clips are low passed).
- **Footsteps:** one close recording of stomps, pitched for each guardian (higher for the Spirestalker, lower for the
  Boulderback).

## Rejected after reading the page

Human voices or imitations ("gorilla audio", "Big Lion Roar", "Monster Gorilla Imitation", a man beating his chest
is likely "Chest banging"), synthesised or effect-made sounds ("Rhinoceros Trumpeting Angry" is a French horn, "Flame
Burst" is CO2 and plug-in effects, "lion_mad", "Alien ..." files), mixes or processed sounds ("G-Gorilla_1-2",
"lion_growls", "Moose_Elk"), a circuit-bent toy, recordings with a lot of ambience (hippo, bison herd, bonobo chorus, a
sanctuary full of lions), and everything under another licence.
