# Guardians

Rules for the beasts that guard the treasure chests, and for every new one. The four existing guardians follow them
and are the reference:

| Guardian | Animal | Material | Base unit |
|---|---|---|---|
| Spirestalker | giraffe | blue crystal spires along its neck | corruptron |
| Cindermane | lion | a mane of fire and embers | corruptron |
| Boulderback | gorilla | a back and fists of stone | biftron |
| Ironhorn | rhino | riveted iron plates and a steel horn | biftron |

The models are designed in `tools/beasts/beasts.py`. The game loads them through `CastleStoryPlus/Treasure/`: `BeastKinds`
defines each kind, `BeastMesh` loads the model, `BeastBody` animates it and `BeastHud` draws the icons and health bars.

## How a guardian looks

- **A real animal twisted by one material** of the game's world, such as crystal, fire, stone or iron. The animal must
  be easy to recognise from its silhouette. The material marks it as a guardian and hints at its strength.
- **Its own silhouette.** A new guardian must not be mistaken for another one, or for a bricktron, corruptron or
  biftron, from the game's usual camera height. Give it a different body shape (tall, long, broad or low) and a
  different gait.
- **Smooth, chunky and toy-like,** to fit Castle Story's style:
  - built from a few rounded shapes melted together (round cones, ellipsoids, rounded boxes), with no fine detail
    smaller than about 0.03 blocks;
  - legs, neck and head thick enough to read from far away.
- **Size in blocks:** at least as tall as a bricktron, and no more than about 3 blocks tall. It must fit the paths,
  the gates and the ruins. Ground level is y = 0.
- **Colours:**
  - **coat:** a base colour and a darker shade of it;
  - **material:** one or two colours that stand out against grass, rock and snow;
  - **hooves, claws or skin:** a dark colour;
  - **eyes:** a small, bright glowing colour, the same family as the material.

  Avoid the factions' team colours, so that a guardian is never taken for a player's unit.
- **One pattern** baked into the texture, for example spots, a silverback or plates (`PATTERNS` in `beasts.py`), with
  a little surface noise and ambient occlusion from the bake.

## Model rules

- Units are blocks: x forward, y up, z to the side. The beast faces +x.
- Every moving part is its own part with a pivot where it turns:
  - `Body`, `Head`, `Tail`, and `Arm` where there are arms;
  - legs as `LegF`/`LegB` with an `L`/`R` side.

  Build both sides with `mirror`.
- Bake with `bake.py` to `CastleStoryPlus/Assets/Beasts/<Name>.json` and `<Name>.png`. The texture atlas is
  2048x2048.
- Make the icon with `icons.py`: `<Name>_icon.png`, 64x64, the beast seen from the side (yaw 0.35, pitch 0.15), on a
  transparent background with a dark outline. It is the off-screen marker, so it must show the animal, not a
  corruptron.
- Check the model with `preview.py` (three angles) and `texview.py` (the baked texture) before adding it to the game.

## Behaviour that belongs to the look

- **A gait of its own,** worked out from the unit's speed in `BeastBody`, and a breathing or looking-about idle while
  standing. Set `StepsPerBlock` so that its feet do not slide.
- **An attack of its own** that shows the material or the animal. For example, the Ironhorn lowers its head and
  charges with its horn. The attack has a wind-up while the unit cranks and the blow when it swings.
- **A corpse that lies still,** tipped over.
- **A health bar over its head** (`BeastHud`). Set the bar height to fit the model: 4.6 for the Spirestalker, 3 for
  the others.

## In the game

- A guardian is drawn over an ordinary enemy unit (a corruptron or biftron), which does the walking, path finding and
  fighting.
- Each kind gets `Health`, `Damage` and `Speed` config entries under `[Treasure.<Name>]`.
- Do not add new serialized components: the model is rebuilt on every peer from the unit, and nothing of it is saved.
- Guardians roam round their chest: the guard raid always patrols, to random spots within 9 blocks of it. They never
  break objects or the ruin's blocks to get through (`TreasureChests`, `TreasureRuins`).
- Guardians are calm until a player's unit hits one. They fight only the players' units, never corruptrons or each
  other (`BeastTemper`).

## Sounds

Every guardian has sounds of its own (`GuardianSounds`, `BeastVoice`), so a new one is not finished without them.

- **The set per guardian:** six events, each with at least one clip (two or three for the ones heard often, so they
  do not repeat):
  - `idle`: a call now and then (every 14-35 seconds) while it is not fighting;
  - `roar`: when a player's unit comes within 16 blocks of it, when it is provoked (the host only, the temper is not synced) and when it goes into combat (at
    most one every 25 seconds, in real time);
  - `step`: one for every half stride of its gait, quieter when it walks slowly; the clip and `StepLevel` (in
    `GuardianSounds`) must fit its size and weight, so the Spirestalker paces lightly and the Boulderback thuds;
  - `attack`: as the blow starts (the wind-up is silent, the sound comes with the swing);
  - `hit`: when its health goes down;
  - `death`: when its corpse appears (only where it is seen, not from the fog).
- **Only real recordings, no synthesis:** every sound in every clip is cut from a CC0 / public domain recording: the
  voice of the animal (a lion's roars and growls, a gorilla's roar and chest beats, a rhino's calls; giraffes are
  almost silent, so the Spirestalker uses dromedary and elk moans, an alpaca moan and a horse snort) and the material
  as short accents on top: a struck crystal bowl (Spirestalker), a flame burst or torch pops (Cindermane), a rock hit
  or crumble (Boulderback), an anvil or chain clank (Ironhorn). Footsteps are recorded thuds and stomps. The voices
  are cleaned and pitched down for the guardian's size. There are no noise beds, no ambience and no reverb: the clips
  are silent between the sounds. A new guardian follows the same rule.
- **Built by a script, from recordings with a licence:** `tools/make_guardian_sounds.py` builds the clips into
  `CastleStoryPlus/Assets/Sounds/Guardians/<Guardian>_<event>_<n>.wav` (mono, 16-bit, 32 kHz, 0.15 - 6 s, levelled per
  event) from the trimmed recordings in `tools/guardian_sources/` (it only cuts, cleans, pitches, layers, levels and
  fades). Only CC0 or public domain recordings may be used, and the page of each one must be read (no imitations,
  synthesised or effect-made sounds, no human voices); every one is listed with its page, author and licence in
  `CastleStoryPlus/Assets/Sounds/Guardians/SOURCES.md` and has a cut in `tools/cut_guardian_sources.py`. Add the
  guardian's clip functions and its row in `SOUNDS`, run `python3 tools/make_guardian_sounds.py` (`--preview DIR` also
  writes a spectrogram and waveform PNG per clip) and commit the WAVs. The sound set can be swapped as a whole:
  replace the files with other ones of the same names (any 16-bit PCM or 32-bit float WAV), or add more numbered
  files, and the game picks one at random per event.
- **Played in the world:** as 3D sounds at the guardian, linear fall off from the event's full-volume distance to its
  furthest distance (the table `GuardianSounds.Settings`: a roar carries 90 blocks, a footstep 32), through
  `ModAudio`, which also keeps the game's mute and volume (master and effects), the one audio listener (only while a
  sound plays, following the camera, never two) and a pool of 16 voices, so a crowd of guardians cannot flood the
  mix: the quietest, least important sound is cut first. Nothing is played from a guardian that is not shown (fog).
  Nothing is allocated per frame.
- Add the new guardian to `StepLevel` and the tests in `CastleStoryPlus.DevTools/Tests/GuardianSoundTests.cs`.
