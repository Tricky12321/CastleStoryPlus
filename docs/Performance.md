# Performance

How to find what takes a frame's time and the main thread's memory, and what was found and changed so far. The
numbers are from one big Invasion save (a base of 230 stockpiles and 76 lamps overrun: its crystal gone, 2 bricktrons
and 63 corruptrons left, 65 units in all) on the developer's machine, with autosave off so every run starts the same.
Only compare numbers measured the same way.

## Measuring

### In the game (F7 and DevTools)

With debug mode on, F7 opens the performance window. "Measure systems" (or `[Performance] MeasureAtStart`) puts a
timer on every `Update`, `FixedUpdate` and `LateUpdate` of the game and the mod, on every component the game's clock
updates (`fixed:`, `update:`, `late:`, `slow:`), and on a few heavy methods (`labor:`, `path:`, `knowledge:`,
`goals:`, `net:`, `lua:`, `ui:`). The time table shows per system:

| Column | Meaning |
|---|---|
| ms/call | one call's average time; the default order, so costly calls that run now and then come first |
| ms/frame | time per frame, all calls together; the "Sort" button orders by it |
| max ms | the longest frame |
| calls/frame | how often it runs per frame |

The same from DevTools (see [DevTools](DevTools.md)):

| Endpoint | What it gives |
|---|---|
| `GET /api/frames` | frame times (avg, 1% low, p50-p99), garbage collections and how the collector runs (`memory.gc`), the systems (`msPerCall`, `msPerFrame`; `sort=call`), the allocators, the last spikes |
| `GET /api/frames/phases` | each frame split into Unity's phases: fixed, update, before/at/after the main camera, present; "unmeasured" time in the systems table is found here |
| `GET /api/frames/loop` | Unity 6 only: each of Unity's own player loop steps timed (Update scripts, animation, canvases, renderers, physics, rendering, the time between frames), in a release player too and without the frame monitor's cost; the first call starts it |
| `GET /api/frames/markers` | Unity 6 only: Unity's own profiler markers (culling, drawing, shadows, skinning, animation, physics) per frame. A release player has only a few; the development player (`PlayerBuild.ProfilingLinux`, `Build/Profiling/Linux`) has all of them. One recorder per marker makes the game slower while it runs; `stop=true` takes them away |
| `GET /api/render` | renderers, lights, animators by culling mode, cameras, quality settings (shadow distance, frame rate cap) |
| `GET /api/ui/canvases` | the canvases that draw, grouped by path |
| `GET /api/ui/overheads` | the overhead icons by kind: how many show and how many of their canvases are on |
| `POST /api/heap/scan`, `GET /api/heap` | what fills the managed heap, per type, root and holder |

The frame monitor itself costs about 1.5 ms a frame in a big base (a timer around some 500 methods); it only runs
with debug mode on, while F7's "Measure systems" button is on, from the first time `/api/frames` asks (until
`POST /api/frames/stop`), or from the start with `[Performance] SpikeLog` or `MeasureAtStart`. Judge the frame rate without it: restart the game and use `/api/frames/loop` and `/api/frames/phases`,
which cost next to nothing.

### The benchmark

A fixed, busy base to compare builds and changes with, made only of the original game's things (its build menu's
blocks and structures, units, resources, recipes and tasks; nothing the mod adds), so the same save can be measured on
the original game too.

1. Build the save once: `POST /api/benchmark/build` (from any game; it starts a new sandbox world on Big Island and
   leaves the running game without saving). It picks the place near the home crystal with the least ground to move
   for a 120 x 80 platform and levels it as the world editor's plateau brush does (the columns' tops set to one
   height, the trees, plants, boulders and ore on it taken away). The platform is walled with stone (merlons, a tower
   per corner, a door per side, lantern wards along the inside) and gets a row of workshops and other structures, a
   row with every block of the build menu, 48 stockpiles filled with the game's resources and a 12 x 12 x 8 keep left
   as blueprints. Then 70 bricktrons (builders, knights, archers), loose stone, iron, fibre and clay to carry, every
   workshop on a loop, a harvest task in the forest west of the platform, a quarry east of it and the keep's
   builders. `enemies=N` adds a raid of N corruptrons. It runs 30 s and is saved as "CSP Benchmark".
   `GET /api/benchmark` follows it.
2. Run it: load the save (`CSP_AUTOLOAD="CSP Benchmark"`) and `POST /api/benchmark/run`, or start the game with
   `CSP_BENCHMARK=1` to run it once the save is in (`CSP_BENCHMARK=quit` closes the game afterwards). It sets normal
   speed, warms up 20 s, then measures four fixed camera views on the platform for 15 s each (overview,
   stockpiles, workshops, the keep): fps, 1% low, frame time percentiles, garbage collections and Unity's
   player loop steps. The report is `BepInEx/devtools/benchmarks/benchmark-<date>.json` and `GET /api/benchmark`.

Turn autosave off (`[Saving] AutosaveMinutes = 0`) where the benchmark is run often, so the save is never written
over by a later autosave of it, and keep the frame monitor off (`SpikeLog`), which costs time itself.

### With Unity's profiler

The systems table cannot see Unity's own work (rendering, animation, physics, UI batching). For that:
- **Port player:** `PlayerBuild.ProfilingLinux` builds a development player into `Build/Profiling/Linux` (see
  [Unity 6 port](Unity6Port.md)). Install into it, add BepInEx, start it, and pick it in the editor's Profiler window
  (target list). This is the closest to what a player runs.
- **Editor (Unity 6 reconstruction):** play mode with the Profiler window. The editor adds 30-40 ms a frame of its own
  (EditorLoop, the profiler), slows down to about 1 frame a second when it is not focused, and Deep Profile reloads
  the scripts in the middle of play mode (BepInEx is gone then: stop and start Play again). Use the editor to find
  what is slow, never to judge how fast the game is.

Measure for 10-15 seconds once the save has loaded and the first garbage collection has passed: the first seconds of
a game are full of one-off work (pools filling, menus being built).

## Frame rate work, 29-10-2026 and 30-10-2026

| Step | Port player | Editor |
|---|---:|---:|
| Start | 73 fps (13.6 ms) | 9 fps (105 ms) |
| AI ticks per frame capped | | 19 fps (52 ms) |
| Decoration animators, test scripts, sound objects | 103 fps (9.7 ms) | |
| Hidden overhead icons' canvases off | 125 fps (8.0 ms) | |
| Frame monitor off (it ran from the start: `SpikeLog`) | 180 fps (5.5 ms) | |
| Ore mine search share, stockpile icons' animators off | 199 fps (5.0 ms) | |
| Units standing still skip the motion pass | about 205 fps (4.8-4.9 ms) | |
| Faction vision at 60 steps a second | 216 fps (4.6 ms) | |

The 1% low went from 43 to 64 fps in the port player. All changes are part of `FrameSavers` unless named.

### What was changed

- **AI ticks per frame (`AiTickCap`):** the game runs its AI (behaviour trees of units and projects, the enemy watch,
  animation tracking) in the fixed update, 50 times a game second. Below 50 fps Unity runs several fixed updates per
  frame to catch up, at 10 fps five, so the AI did five times the work in each slow frame and made it slower still.
  The AI now runs in at most `[Performance] AiTicksPerFrame` (2) of a frame's fixed updates, times the game speed.
  Physics and movement run in all of them. Below 25 fps units decide a little later in game time.
- **Animation tracking (`AnimatorTrackerCache`):** a unit's AnimatorTracker follows about 66 animation states and
  asked the animator for its state once per state every fixed update (some 30 000 calls into Unity a frame). It now
  reads each layer once per update.
- **Decoration animators:** some 640 animators on things that only show something (stockpile planks and logs, beacon
  handles, furnace fires, ward lanterns) animated every frame, also off the screen. They are set to `CullCompletely`
  and stop while none of their renderers is visible. The main camera's time fell from 1.0 to 0.2 ms.
- **Overhead icon test scripts:** every overhead icon (some 300) carried two of the developers' test components
  (`TestContainer`, `TestOrderQueue`) that Unity called every frame and that do nothing in the game. They switch
  themselves off.
- **Sound objects:** each of some 300 `AkGameObj` read its transform's position, forward and up up to a dozen times a
  frame to tell Wwise where it is. It reads position and rotation once now.
- **Hidden overhead icons' canvases:** a hidden icon is only made see-through by the game. Its own canvases were
  already switched off where they hold nothing but the hidden part; now the canvases nested in it are too (the
  content's, each line's arrow). A stockpile's hidden icon had three canvases on, about 700 in the save, that Unity
  sorted and batched every frame; now 230.
- **Ore mine:** the search for ore to dig takes 2% of a frame's time per mine (at most 0.3 ms), looks only at the part
  of each row inside the mine's sphere (about half the box) and rests 5 s between passes (a big mine's passes ran back
  to back). 0.43 ms a frame became 0.16 ms.
- **Stockpile icons' animators:** each stockpile's overhead icon (some 300) has an animator whose Show and Hide
  triggers nothing sets; it sits in a state without a clip. Unity still updated all of them every frame; they are
  switched off. Animation fell from 0.54 to 0.32 ms a frame.
- **Units standing still (`IdleUnitMotion`):** `Locomotion4` applied each unit's animator motion every frame: a
  helper transform moved, the facing levelled, the floor height looked up (route graph and surface lookups), rotation
  and position set, also for units standing still. When the last full pass changed nothing, the floor was exactly at
  the unit's feet and it has not moved, the pass is skipped for up to 0.25 s. About 0.2 ms a frame for 65 units.
- **Faction vision (`VisionRate`):** who sees whom is refreshed one small step per faction every frame, so the work
  per second grew with the frame rate. The steps now go at most at the rate of 60 frames a second.
- **Tunnel tasks (`TunnelGoalRefresh`):** a tunnel task looked over every voxel of its tunnel every 2 game seconds
  for blocks to dig and stairs to build in one go, up to 5.3 ms in one frame. The pass is spread over the next ticks,
  at most 0.25 ms a tick; the worst frame of a tunnel task fell to 1 ms.
- **Reach regions (`KnowledgeRegionsSpread`):** every 4 game seconds the game works out what each part of the map can
  reach. Its last step (2.3 ms) and the copy into the workers' knowledge (1.4 ms) ran in one tick; the copy now runs
  in the next fixed update, and the first step takes 40 roots a tick instead of 100 (0.9 to 0.4 ms). Worst frame 3.8 to
  2.4 ms.
- **Cleanup tasks (`GameLeakFixes`):** a cleanup task kept a list of every piece of litter ever picked up in its
  circle and went through it all on every refresh (every 0.6 s); it was never emptied. It is emptied after use.
- **Spike log off by default (`[Performance] SpikeLog`):** with debug mode on it started the frame monitor at once,
  about 1.3 ms a frame for everyone playing with debug mode. Config files made before keep their value.
- **Minimap:** columns that changed are drawn again every 2 s and the whole map once a minute, instead of the whole
  map every 15 s (about 4 ms a frame most of the time on a big map).
- **Shadows (`RenderTuning`):** the pass over the shadow casters is skipped while the camera stands still.
- **Route searches:** every slice of a search walked the map's corner column (about 512 node lookups) looking for the
  unit's own node in a list the game never fills; it is skipped.

### What was looked at and left

- **Route searches on a worker thread:** the search graph could be read from another thread, but the two tests a
  search makes on every node (target reached, can this voxel be crossed) call Unity (`transform`, `GetComponent`),
  which only works on the main thread. It would take snapshots of targets and crossings per search and thread-safe
  versions of several patches (500+ lines) for at most about 1.5 ms a frame. Not done.
- **Culling the units' animators completely off the screen:** the game waits on their animation states (a dig or a
  strike ends when its state ends), so stopped animators would leave workers waiting. Units cull only their bones
  (`CullUpdateTransforms`).
- **UNet:** in the editor `NetworkCallbacks.LateUpdate` cost 4.5 ms a frame; in the port player it costs nothing
  measurable (`net: UpdateServerObjects`). It was the editor.
- **Lights:** 76 point lights in view; after the changes above the main camera takes 0.2 ms on the main thread, so
  there was nothing to gain.
- **Incremental garbage collection:** on in Unity 6. It does not shorten the pauses of a heap this size (see below)
  and puts a write barrier on every reference store; worth a test with it off.

### What is left

- **The game's AI (fixed update), about 0.4 ms a frame on average:** the tasks' goal searches are already spread over
  ticks by the game. What still shows as single slow frames: a worker's work search (`fixed: Labor`, up to 4-5 ms when
  a frame runs two AI ticks after a slow one; `BestRecepteurToStore` about 0.75 ms a call, cached) and the reach
  regions' last step (`ResultStep`, 2.3 ms in one tick every 4 s). The monitor's `ai:` rows measure inside them.
- **Frame rate cap:** the game's graphics setting caps the frame rate (`Application.targetFrameRate`, 240 in the
  last measurements).
- **Update scripts, about 2.0 ms:** spread over many systems; the biggest are `Locomotion4` (about 0.3 ms for 65
  units, some 5 microseconds each: the grounded check, the state's update, the layer weights, a dozen small calls
  into Unity; indexing the state machine's handlers by signal was tried and made no measurable difference), the overhead icons' small updates (some 1000 calls,
  about 0.3 ms together). The frame monitor measures inside `Locomotion4` (`loco:` rows); its ten or so timers per unit
  add about 0.3 ms there themselves.
- **Rendering:** about 0.6 ms on the main thread in these measurements, where the camera looked at little; a view over
  the base costs more.
- **Garbage collection pauses:** about 80 ms in the port player (0.3-2.3 s in the editor) with 1.1 GB of managed heap.
  Unity's collector does not move objects; every collection reads the whole heap. The known big holders are the
  terrain's colour buffers (`Color32[]` in pools, about 250 MB). See also [Lua memory leaks](LuaMemoryLeaks.md).

## Jobs of the player's own (Unity 6 port), 31-10-2026

The port's player is built by us, so it can carry jobs of our own: `CastleStoryPlus.Compute`
(`CastleStoryPlus.Player/Assets/Compute`), compiled by Burst when the player is built (`Plugins/lib_burst_generated.so`;
Burst also compiles ahead of time for a Mono player). The mod takes snapshots on the main thread into native arrays,
schedules the job on Unity's worker threads and applies the results later in the frame or in the next one
(`PlayerJobs`, `[Performance] PlayerJobs`). See [Unity 6 port](Unity6Port.md) for the build.

**Method.** The test player (`Build/VulkanFix`, OpenGL Core), the "Benchmark" save from a fresh copy each run, the camera
over the base (`at=515,100,555`), 60 s after loading: every frame for 30 s (`/api/frames/raw`), Unity's player loop for
15 s (`/api/frames/loop`), then the systems table for 30 s (`/api/frames`, with the frame monitor's own cost). Runs
with the changes on and off alternate, four of each (two more runs with the vision job alone agree).

| | Off (4 runs) | On (4 runs) |
|---|---:|---:|
| Frame time, monitor off (`/api/frames/raw`) | 10.9 ms | 9.4 ms |
| Player loop fps | 91.3 | 100.7 |
| Update scripts (player loop step) | 5.15 ms | 4.44 ms |
| `update: SharedFactionVision` | 0.56 ms/frame | 0.55 ms/frame |
| `AnimatedProgression.Update` | 0.40 ms/call, 0.46 ms/frame | 0.08 ms/call, about 0.09 ms/frame (see below) |
| Animation (`DirectorUpdateAnimationBegin`) | 0.85 ms | 0.83 ms |

Runs of the same setup differ by up to 30 % in frame rate (the game's own course, see the Vulkan benchmark below), so the
frame rate rows say little; the per-system rows are what changed.

- **Faction vision, detection step as a Burst job (`VisionJobs`, `CastleStoryPlus.Compute.VisionDetect`):** which
  tracking groups each faction sees fully or partly is worked out by a parallel job over the groups, with VisionScan's
  buckets, and applied at the pass's next step. Checked in game against the game's own sums for every vision
  (`CSP_COMPUTE_VERIFY=1`): 400 passes, no group differed, with Burst on. It changes nothing measurable here: after
  VisionScan this step already took next to nothing (`vision: AssignDetectionStatusFromAllVisions` below 0.001 ms a
  call, both ways). The frame monitor now times each step of the pass (`vision:` rows), and they show where the 0.6 ms
  goes: `AssignCollisionsFromFullGroup`, 0.43-0.62 ms a frame and up to 15 ms in one call. It goes over every entry of
  ten fully seen groups a step; the stored items of a base have a vision each too (about 250 entries with a vision per
  call, measured), and each entry is bookkeeping on the game's objects (`CollisionTrackingId`, `IsResponsibleForVision`,
  the faction's lists), which a job cannot reach. A list of the entries with a vision per group was tried and removed:
  it skipped almost nothing.
- **Pallet animation on Unity's worker threads (`ProgressionOnWorkers`, `[Performance] ProgressionOnWorkers`):** the
  game stepped a storage's animator by hand (`Animator.Update`) every frame for a second after its content changed,
  0.41 ms a call on the main thread. The animator is now switched on for that time and Unity's animation pass works it
  out with the same time steps on its worker threads; it is switched off again when the game would stop stepping it.
  The main thread's call fell from 0.40 to 0.08 ms (what is left are the mod's patches that look at the pallet after
  the step), about 0.36 ms a frame; the animation step of the player loop did not grow. (The frame monitor counts
  fewer calls with it on, 0.1-0.3 a frame instead of 1.1, though the runs are as many and as long; the frame figure
  above takes the off runs' count.) Checked with
  `CSP_PROGRESSION_LOG=1` (each run's frames and end state): 267 runs either way in one session, all ending out of
  transition in the same states.
- **A vertex shader for the pallet animation:** not done. The clips switch objects on and off (the crate, each
  resource's heap), the pieces are the game's meshes with the game's converted materials (a shader of ours would have to
  stand in for each of them), and the mod's stacking and large stockpile code read and copy the pieces' transforms on
  the CPU.
- **ReachMap's flood as a job:** not done. A job needs the fine route graph in native arrays: some 150 000 nodes and
  their branches, and whether each can be crossed, which asks the game's volume registry (doors, blockers) per node.
  Making that snapshot walks the same nodes and branches the flood walks, on the main thread, so it costs what the flood
  costs (at most 0.4 ms a frame, spread). It would pay only with a graph kept up to date as the world changes.

## Vulkan: initial benchmark, 31-10-2026

The Unity 6 port on Linux with OpenGL Core against Vulkan (`CSP_VULKAN=1`, `-force-vulkan`), modded, on the
"Benchmark" save (a large base, some 65 units). Radeon RX 9070 (RADV), 16 threads, frame cap 240.

**Method.** Every run starts the player on a fresh copy of the save (so each starts from the same point; it is saved
on quit, into the copy), puts the camera over the base, waits 60 s, then records every frame time for 30 s at 1x
speed, 15 s of the main thread's player loop, and 30 s at 3x after 20 s. The per-system measuring is off
(`GET /api/frames/raw`). CPU time of the process and the GPU's busy share are sampled over the windows. OpenGL and
Vulkan runs alternate.

**Graphics threading modes.** Unity 6 on Vulkan takes only two of the modes in `boot.config`
(`gfx-threading-mode`): 4, client and worker threads with native graphics jobs (the default here), and 6, split
jobs. Other values (2, 5) start as split jobs. OpenGL Core runs 3 (client and worker jobs; native jobs need Vulkan).

**Results at 1x** (frame times in ms):

| Run | API, mode | fps | p50 | p95 | p99 | 1% low fps | Max | GCs in window |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| 1 | OpenGL | 66.8 | 14.5 | 22.1 | 26.9 | 31.9 | 42.8 | 0 |
| 7 | OpenGL | 75.9 | 12.8 | 20.5 | 25.8 | 32.2 | 38.9 | 0 |
| 11 | OpenGL | 71.9 | 12.9 | 22.1 | 31.5 | 15.3 | 548.1 | 1 |
| 2 | Vulkan, native jobs | 64.4 | 14.6 | 22.9 | 32.0 | 17.4 | 343.3 | 1 |
| 8 | Vulkan, native jobs | 48.9 | 18.8 | 31.5 | 41.1 | 16.0 | 220.8 | 1 |
| 12 | Vulkan, native jobs | 69.5 | 13.2 | 23.1 | 35.9 | 20.1 | 126.8 | 1 |
| 13 | Vulkan, split jobs | 64.2 | 14.9 | 23.4 | 31.8 | 24.1 | 79.0 | 0 |
| 14 | Vulkan, split jobs | 65.0 | 14.9 | 22.6 | 26.5 | 31.5 | 44.4 | 0 |
| 15 | Vulkan, split jobs | 49.3 | 19.1 | 28.2 | 34.8 | 22.1 | 107.0 | 1 |

At 3x all of them run 34-61 fps, in the same order.

**What it shows.**
- Runs of the same setup differ by up to 15 % (split jobs: 64, 65 and 49 fps): the game itself differs from run to
  run (the AI's choices, when a garbage collection falls). One run per setup decides nothing.
- Vulkan with native graphics jobs is about even with OpenGL Core; split jobs a little lower and smoother. The GPU is
  30-40 % busy in all of them: the frame is bound by the main thread, which the graphics API changes little.
- The worst hitches (100-550 ms) are garbage collections, with both APIs. A window without one has its 1% low at
  24-32 fps on either API.
- Vulkan is the API to build on: it is the one with native graphics jobs and room to grow (GPU compute, async work);
  OpenGL Core stays the default until Vulkan is ahead and its open issues are fixed (see
  [Unity 6 port](Unity6Port.md)).

## Invasion raids without a crystal

While measuring, the frame rate fell to a tenth over a few runs: the save had grown from 9 to some 1000 enemy raids,
each with its beacon, timers and overhead. With the players' crystal gone, the gamemode's wave spawn made its raid and
then threw on the missing crystal (`invasion.lua`: attempt to index a nil value) before the next wave's timer started,
so it ran again on every slow update, and the autosave kept them. `GameLeakFixes` now skips the wave without a crystal.
A save that already holds such raids keeps them.

## Memory

The heap scan (`POST /api/heap/scan`) walks everything reachable from static fields and live components, a few ms per
frame, and reports MB per type, per root and per holding field. In the save above (29-10-2026) the reachable heap grew
from 810 to 870 MB in 11 minutes, with no single leak; Mono reported about 1450 MB in use after a collection, the rest
being gaps the non-moving collector cannot close. What was fixed before is in [Lua memory leaks](LuaMemoryLeaks.md)
and the `GameLeakFixes` entries of the [changelog](../CHANGELOG.md).
