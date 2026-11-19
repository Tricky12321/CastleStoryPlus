# Sources of the guardian sounds

Every sound in `<Guardian>_<event>_<n>.wav` is a part of a recording below: the animal voices and the material
(crystal, fire, stone, iron, footfalls). Nothing is synthesised. `tools/make_guardian_sounds.py` cuts the parts,
cleans them (noise reduction of the animal recordings, noise gate, rumble filter), pitches them, layers them, levels
and fades them. The trimmed, decoded source clips are in `tools/guardian_sources/` (mono, 16-bit, 32 kHz, the start
of the kept part is in the file name; `tools/cut_guardian_sources.py` re-creates them).

Every recording is **CC0 1.0 (public domain dedication)**, checked on its own Freesound page (the licence link on the
page is `http://creativecommons.org/publicdomain/zero/1.0/`, checked 19-11-2026), and the page description was read to
rule out imitations, synthesised or effect-made sounds and human voices. CC0 needs no attribution; the authors are
named here anyway. The audio used is the high quality MP3 preview that Freesound serves to every visitor, decoded to
WAV. No recording under CC-BY, CC-BY-NC, CC-BY-SA or any other licence is used.

## Animals

| Source clip | Recording (Freesound page) | Author | Used in |
|---|---|---|---|
| `lion_bidone_69570_t11.2.wav` | https://freesound.org/people/Bidone/sounds/69570/ "Lion loud" (field recording of lions) | Bidone | Cindermane roar 2 and 4, attack 2-3, death 1 |
| `lion_bidone_69571_t1.6.wav` | https://freesound.org/people/Bidone/sounds/69571/ "Lions two" | Bidone | Cindermane idle 1-3 |
| `lion_roman_cgr_223978_t0.0.wav` | https://freesound.org/people/roman_cgr/sounds/223978/ "Lion lament" (zoo, Sennheiser K6 + Zoom H4N) | roman_cgr | Cindermane idle 4, roar 3, attack 1, hit 3 |
| `lion_felix_blume_405211_t11.0.wav` | https://freesound.org/people/felix.blume/sounds/405211/ "Lions screaming during the night" (zoo in Quilpue, Chile) | felix.blume | Cindermane roar 1, hit 1-2, death 2 |
| `gorilla_j0ck0_397054_t0.1.wav` | https://freesound.org/people/J0ck0/sounds/397054/ "Gorillaschrei.wav" (roar of an alpha gorilla, recorded in a zoo) | J0ck0 | Boulderback idle 2 and 4, roar 1-4, attack 1-3, hit 1-3, death 1-2 |
| `gorilla_chest_shutup_outcast_367821_t3.4.wav` | https://freesound.org/people/shutup_outcast/sounds/367821/ "Chest banging.wav" (chest beats only) | shutup_outcast | Boulderback idle 1, 3 and 4, roar 1, 2, 3 and 4, attack 2 |
| `rhino_breviceps_535810_t0.0.wav` | https://freesound.org/people/Breviceps/sounds/535810/ "Indian rhinoceros (Rhinoceros unicornis)" (phone recording) | Breviceps | Ironhorn idle 1-4, roar 1-3, attack 1-3, hit 1-3, death 1-2 |
| `camel_schots_693564_t3.0.wav` | https://freesound.org/people/schots/sounds/693564/ "Dromedaries mooing" (field recording) | schots | Spirestalker idle 1-4, roar 1-3, hit 2-3, death 1-2 |
| `horse_black_trillium_397236_t0.3.wav` | https://freesound.org/people/black_trillium/sounds/397236/ "Horse Snort.wav" | black_trillium | Spirestalker idle 3, attack 1 |
| `alpaca_thekingofgeeks360_852641_t0.0.wav` | https://freesound.org/people/TheKingOfGeeks360/sounds/852641/ "Camelids - Alpaca; High Moan" | TheKingOfGeeks360 | Spirestalker idle 4, attack 2, hit 1 |

## Material (accents and footfalls)

| Source clip | Recording (Freesound page) | Author | Used in |
|---|---|---|---|
| `crystal_caiogracco_150453_t0.0.wav` | https://freesound.org/people/caiogracco/sounds/150453/ "Crystal bowl hit F3 1.wav" | caiogracco | Spirestalker idle, roar, attack, death |
| `crystal_caiogracco_150454_t0.0.wav` | https://freesound.org/people/caiogracco/sounds/150454/ "Crystal bowl F#3 1.wav" | caiogracco | Spirestalker idle, roar, attack, death |
| `crystal_caiogracco_150456_t0.0.wav` | https://freesound.org/people/caiogracco/sounds/150456/ "Crystal bowl nail E4 1.wav" | caiogracco | Spirestalker step, hit |
| `fire_nic3_one_166553_t0.7.wav` | https://freesound.org/people/Nic3_one/sounds/166553/ "spray fire bursts 1.wav" (field recording of flame bursts) | Nic3_one | Cindermane attack 1 and 3, death |
| `fire_j1987_111331_t0.0.wav` | https://freesound.org/people/j1987/sounds/111331/ "flameclic.wav" (a fireplace lighter) | j1987 | Cindermane attack 2 |
| `fire_craigsmith_675771_t0.1.wav` | https://freesound.org/people/craigsmith/sounds/675771/ "S31-08 Acetylene torch; multiple pops.wav" | craigsmith | Cindermane hit, death |
| `stone_lolamadeus_179365_t0.3.wav` | https://freesound.org/people/lolamadeus/sounds/179365/ "Small Rock and Stone Hits.wav" | lolamadeus | Boulderback step, idle, attack, hit; Spirestalker step, attack |
| `stone_northern87_369981_t4.3.wav` | https://freesound.org/people/northern87/sounds/369981/ "Stone dropping to ground_northern87" | northern87 | Boulderback roar, attack, death |
| `stone_kastenfrosch_521956_t0.0.wav` | https://freesound.org/people/Kastenfrosch/sounds/521956/ "Stone Crumble 2" | Kastenfrosch | Boulderback attack, death |
| `iron_ldezem_386129_t0.0.wav` | https://freesound.org/people/ldezem/sounds/386129/ "Anvil - Lokomo 125 kg - Hammer on 6mm steel 1 time" | ldezem | Ironhorn roar 2, attack 1 |
| `iron_alan3809_103071_t3.0.wav` | https://freesound.org/people/alan3809/sounds/103071/ "Anvil Strikes_5.wav" | alan3809 | Ironhorn attack 2-3, hit 3 |
| `iron_michorvath_270589_t0.1.wav` | https://freesound.org/people/michorvath/sounds/270589/ "Anvil Hit 1" | michorvath | Ironhorn roar 1, hit 1 |
| `iron_rsellick_545547_t6.9.wav` | https://freesound.org/people/rsellick/sounds/545547/ "metal clanks.wav" | rsellick | Ironhorn step, roar 3, hit 2, death |
| `iron_roxis_boy_401622_t2.6.wav` | https://freesound.org/people/Roxis_Boy/sounds/401622/ "Chain Rattling - 1" | Roxis_Boy | Ironhorn death |
| `thud_sgrowe_342535_t0.0.wav` | https://freesound.org/people/sgrowe/sounds/342535/ "Medium Thud 1" | sgrowe | Cindermane step 1 |
| `thud_7of9designs_640204_t0.0.wav` | https://freesound.org/people/7of9Designs/sounds/640204/ "Heavy Metal Thud on Ground" | 7of9Designs | Ironhorn step, attack |
| `stomp_oscaraudiogeek_334228_t1.5.wav` | https://freesound.org/people/oscaraudiogeek/sounds/334228/ "Stomping Ground Super Bassy" (stomps on cement, contact mic) | oscaraudiogeek | Cindermane step 2-3, Boulderback step 1-3, Spirestalker step 1-3 |

Licence of every row: CC0 1.0.

## What is not an exact match (stand-ins)

- **Giraffe (Spirestalker):** giraffes are almost silent and no CC0 or public domain giraffe recording exists (the
  giraffe recordings on Wikimedia Commons are CC BY-SA, so not used). The voice is dromedary moans and grunts, an
  alpaca moan and a horse snort, standing in for the giraffe's humming, grunts, bleats and snorts.
- **Gorilla (Boulderback):** the only real gorilla recording under CC0 that was found is one roar (J0ck0). It is used
  at several pitches and lengths with real chest beats. The chest beats are the sound of a chest being beaten; the
  page does not say whose.
- **Rhinoceros (Ironhorn):** the only real rhinoceros recording found under CC0 is the phone recording of an Indian
  rhinoceros (noisy, the noise is reduced). Horse snorts are not used for the Ironhorn.
- **Material:** the "spray fire bursts" are flames from a spray can, the "acetylene torch" pops come from an
  old film effects archive (digitised, some noise), the "stone drop" and "rock hits" are close recordings of stones.

## Rejected after reading the page

Human voices or imitations ("gorilla audio", "Big Lion Roar", "Monster Gorilla Imitation"), recordings whose origin
cannot be told ("Moose_Elk.mp3" by gleith: no description, and the uploader's other sounds are human imitations),
edited derivatives ("lion_growls.wav" by stratcat322 is "Lion lament" rearranged and given "more depth/power"; the
original is used instead), synthesised or effect-made sounds ("Rhinoceros Trumpeting Angry" is a French horn, "Flame
Burst" is CO2 and plug-in effects, "lion_mad", "Alien ..." files), mixes of other sounds ("G-Gorilla_1-2"), a
circuit-bent toy, recordings with a lot of ambience (hippo, bison herd, bonobo chorus, a sanctuary full of lions), and
everything under another licence.
