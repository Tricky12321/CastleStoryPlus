# Developer tools

Three projects for testing Castle Story Plus and for letting Claude Code look into the running game. None of them is part of a release: `tools/package.sh` only packs the `CastleStoryPlus` plugin.

| Project | What | Runs |
|---|---|---|
| `CastleStoryPlus.Tests` | Unit tests of the mod's pure logic (NUnit) | `dotnet test CastleStoryPlus.Tests`, no game needed |
| `CastleStoryPlus.DevTools` | BepInEx plugin: HTTP API into the game, in-game test runner and tests | in the game, installed by the build into `BepInEx/plugins/CastleStoryPlus.DevTools/` |
| `CastleStoryPlus.Mcp` | MCP server (stdio) with the API as tools for Claude Code | started by Claude Code from `.mcp.json` |

## Unit tests (`CastleStoryPlus.Tests`)

They test the mod's code that does not call into Unity's engine. Unity's native methods (GameObjects, physics, rendering) only exist inside the game, so such code is tested in the game instead. Code worth testing is moved into small classes of its own without game objects, e.g. `QuarryDepthSteps` and `WorldSearchQuery`. The mod lets the test project reach its `internal` classes (`Core/AssemblyInfo.cs`).

## In-game API (`CastleStoryPlus.DevTools`)

It listens on `http://127.0.0.1:27860` only, so other computers cannot reach it. The settings are in `BepInEx/config/com.tricky12321.castlestoryplus.devtools.cfg`:

```
[Api]
Enabled = true
Port = 27860
```

The environment variable `CSP_API_PORT` overrides the port, so two players of one install (a multiplayer host and a
client on one machine) each answer on a port of their own.

Answers are JSON. `GET /api` lists every endpoint with its parameters.

| Endpoint | What |
|---|---|
| `GET /api/status` | In a game, host or client, paused, speed, scene, fps |
| `GET /api/log?after=&count=&problems=` | BepInEx log lines, including the ones from before the plugin started. `after` gives only newer lines |
| `GET /api/features` | The mod's features and whether each is on |
| `GET /api/bricktrons?all=&q=` | Bricktrons: name, job, activity, task, position, what they carry (`healtron: true` marks a Healtron) |
| `GET /api/objects?q=&max=` | Things in the world by type name, with counts |
| `GET /api/storage?q=&empty=&max=` | Storages and what they hold: amounts per resource (type and the name the UI shows) and stored objects by type |
| `GET /api/selection` | What the player has selected, with ids, positions and whether it can be moved |
| `POST /api/move?id=&to=x,y,z&turn=&project=` | Moves a building like the mod's move key: a blueprint at the new place, the old one demolished |
| `GET /api/problems?count=&q=` | The system log's problems (F9): failed bricktron tasks with their reason, errors and warnings |
| `GET /api/cleanup?unit=&max=` | What a worker's automatic cleanup sees: loose items and why each is not picked up |
| `GET /api/reach?id=&unit=` | Whether a bricktron can get to a thing by the game's knowledge (regions, access cells with their route nodes, exclusions from failed walks) and by the mod's reachability map |
| `GET /api/workshops/why?unit=&id=&cells=` | Why a workshop has or has not work for a bricktron: every check of the game's HasWorkFor (room, reach, store, fetch, work) with its answer |
| `POST /api/screenshot?ui=&name=&maxWidth=&at=&yaw=&pitch=&distance=` | Saves a screenshot under `BepInEx/devtools/screenshots/` and answers its path. `ui=false`: the world only, without the menus, the build jobs' markers and the icons over things; with `at` the world's cameras are put there for the picture (and back after). `maxWidth`: also a copy at most that wide, `<name>_small.png` (`smallPath`), scaled on the graphics card |
| `GET /api/ui/rect?q=` | Where a shown menu part is on the screen, by its object's name (a station's window: `CS market`, `CS Research`, ...) |
| `POST /api/ui/click?text=&index=` | Clicks a shown button of the mod's windows by (part of) its label; `index` picks a match in reading order on the screen |
| `POST /api/ui/clickat?x=&y=` | Left-clicks the interface at a screen point (pixels from the top left, as screenshots count), as the mouse does: for the C# menus, whose buttons are not uGUI Buttons |
| `GET /api/ui/menus?q=` | The game's menu components (LegacyBaseMenu) in the loaded scenes, shown or not: path, type, and an enum menu's row, list and action containers |
| `GET /api/ui/containers?q=&max=` | The game's menu containers whose path holds q, with their items: operation, title, group, instance, and the objects they open or close |
| `GET /api/ui/tree?q=&depth=&index=` | An object's children (shown or not) with their components, active state, texts and rectangles |
| `GET /api/net` | Multiplayer: server and client running, connections, the lobby's players (name, host, ready, faction, ping), the map, LAN games heard (`lanGames`) |
| `POST /api/net/host?port=&name=` | Hosts a multiplayer game from the title screen, as the host button does (the lobby opens and picks an official map) |
| `POST /api/net/relations?value=allied\|neutral\|enemy` | Host: how the players' factions stand to each other in a game in progress (the game mode's rule); rebuilds who is sent what |
| `POST /api/net/join?address=&port=&name=` | Joins by address, as the custom server window's join does |
| `POST /api/net/ready?value=`, `POST /api/net/start?button=`, `POST /api/net/leave` | The lobby's ready, the host's start (`button=true`: as the start button does, the loading screen on every player first, then the host's ready), leaving |
| `POST /api/net/name?value=`, `GET`/`POST /api/net/nameedit?text=` | Renames this player as Enter in the lobby's name box does; the name box (shown, focused, text), and typing into it without Enter |
| `GET`/`POST /api/net/chat?text=` | The chat box of a game (text, open, shown); sends a chat message |
| `POST /api/net/lan` | Looks for games hosted on the LAN, as the LAN list does |
| `POST /api/net/online`, `POST /api/net/joinlobby?lobby=&name=` | Asks Steam for the online list's games (`onlineGames` in `GET /api/net`, which also shows `steam`: running, user, lobby); joins one by its lobby id as a click and join do |
| `GET /api/vision?path=&max=` | Fog of war per object: visible for the local faction and why (always visible, owned, allied, `factionsSeeing`, `carrierVisible` for what something carries), `hostVisibility`, renderers on, `trackedBy` |
| `GET /api/desync`, `POST /api/desync/check`, `POST /api/desync/break?at=&radius=&blocks=` | Desync protection: chunks hashed, rounds, chunks out of sync and corrected, the last vote, hashing time a frame; compare at once (host); take terrain or blocks away on this peer only (a desync on purpose) |
| `POST /api/quit` | Closes the game (its own Quit), half a second after answering; `tools/start-game.sh --restart` uses it |
| `POST /api/testworld?width=` | Builds the test world round the home crystal: every research done, one of every building and block, an armour stand per soldier class, and a full 2 x 2 and 3 x 3 stockpile of every resource at every Stacking tier. `GET /api/testworld` follows it, `GET /api/testworld/pieces` lists the pieces with their place |
| `GET /api/studio/plan` | The photo studio's pieces (the test world's plan) by index |
| `POST /api/studio?index=` | Shows that one piece alone on the studio's spot west of the home crystal, built and filled; `GET /api/studio` follows it and gives its drawn bounds. `tools/make_gallery.py` photographs every piece this way for [the gallery](Gallery.md) |
| `POST /api/camera?at=x,y,z&distance=&pitch=&yaw=` | Points the camera at a place |
| `POST /api/select?id=&q=` | Selects a building as a click does (its menu opens), by id or type (`MarketStation`) |
| `GET /api/path?to=x,y,z&from=&unit=&enemy=&nodes=&lods=` | Runs the game's route search for a bricktron (over the navigation mesh for the player's units in the Unity 6 port, as the game does) and compares it with the shortest route; `lods` limits the graph levels the search may use; `exhausted` says the search ran out of places |
| `GET /api/path/node?at=x,y,z` | The navigation node at a voxel and its links (kind, weight) |
| `GET /api/path/ground?x=&z=` | The highest voxel a bricktron can stand on in a column |
| `GET /api/path/straight?from=x,y,z&to=x,y,z&unit=` | Whether a bricktron may walk straight from one voxel to another (the `StraightPaths` line test), and where and why not; with the kinds of the steps of the route search's own route between them |
| `GET /api/path/stats?enemy=&top=&recent=` | Measurements of the route searches the game ran since the last reset |
| `POST /api/path/stats/reset` | Starts the route search measurements again from zero |
| `GET /api/navmesh`, `POST /api/navmesh/start`, `POST /api/navmesh/stop`, `GET /api/navmesh/path`, `GET /api/navmesh/bench` | Unity 6 port: the navigation mesh experiment, its build and update times, and routes over it against the game's A\* (see [NavMesh](NavMesh.md)) |
| `GET /api/speed?seconds=` | Game speed: chosen speed, `Time.timeScale`, paused, fixed timestep, `maximumDeltaTime`, and the speed actually reached over the last seconds (game seconds per real second), with fps and fixed steps per frame |
| `POST /api/speed?speed=&forSeconds=` | Sets the game speed for testing, 0.25 to 20 (single player or host only). `forSeconds`: back to 1x by itself after that many real seconds |
| `GET /api/worlds?q=` | The maps a new single-player game can start on (codename, name, size) with their modes and presets |
| `POST /api/world/new?map=&gamemode=&preset=&waves=` | Starts a new single-player world (leaves the current game without saving); answers at once, `GET /api/status` shows `worldStart` and `loading` |
| `GET /api/saves` | The single-player saves (name, map, last played mode, when), newest first |
| `POST /api/world/load?name=` | Loads a single-player save (by name or part of it) in its last played mode, as the title screen's Continue does; from a game it first leaves without saving. Answers at once; poll `GET /api/status` like for a new world |
| `GET /api/base` | The player's faction, home crystal (position, ground height, energy, bricktrons it can spawn), bricktrons by job, projects |
| `GET /api/ground?x=&z=&radius=&flat=` | Terrain heights round a point (default the home crystal), and flat squares to build on |
| `GET /api/blueprints/keys?all=` | The build menu's blueprints with their footprint |
| `POST /api/blueprints/place?key=&at=x,y,z&to=&turn=&project=&force=&commit=` | Places blueprints like the player, one or a box of them; answers per piece placed or refused (obstructed, unsupported, invalid) |
| `GET /api/projects?q=` | The player's projects: type, priority, crew and crew size, goals |
| `POST /api/projects/create?type=build\|quarry\|harvest\|treefarm&at=&from=&to=` | Makes a build project, a quarry digging out a box, a harvest project, or a tree farm around `at` |
| `POST /api/projects/priority?id=&priority=` | Sets a project's priority (locked, low, medium, high) |
| `POST /api/projects/crew?id=&size=` | Sets a project's crew size (0 to 8) |
| `GET /api/tests?filter=` | The in-game tests |
| `POST /api/tests/run?filter=&speed=&parallel=&clearUnits=` | Starts a run (answers at once), at `speed` if given (back to 1x when it ends). `parallel`: tests at the same time (1-8). `clearUnits=true`: the world's own units are taken away first |
| `GET /api/tests/results` | Progress, and each test's outcome, message, log and recorded values |
| `POST /api/tests/stop` | Stops the run after the current test's clean-up |

The API takes calls on its own threads. Everything that touches the game runs on Unity's main thread, one frame at a time.

### Playing through the API

Cheats (host only): `instant=true` on `POST /api/blueprints/place` or `POST /api/blueprints/complete` builds blueprints at once; `POST /api/cheat/bricktrons?count=&job=` spawns bricktrons; `POST /api/cheat/items?key=&count=` spawns items; `POST /api/cheat/enemies?type=&count=&at=&level=&raid=` spawns enemies (corruptron, biftron, minitron, crawler, magitron or healtron), by default about 20 voxels from the home crystal as a new raid aimed at it. `POST /api/projects/hire?id=&count=` puts idle bricktrons on a project (a project's crew size alone does not hire anyone). Workshops: `GET /api/workshops`, `POST /api/workshops/worker?id=`, `POST /api/workshops/queue?id=&recipe=Planks&loop=true`. `POST /api/blueprints/remove?from=&to=` takes away unbuilt blueprints; `POST /api/demolish?from=&to=` takes down placed buildings (`instant=true` at once). Leave walkways between stockpiles: workers cannot reach a stockpile surrounded by others. Workers cannot dig a quarry under trees: `POST /api/projects/create?type=quarry` refuses it unless `clear=true` (a harvest project cuts them first); `GET /api/ground?flat=6&noTrees=true&heights=false` lists places without trees. Stone walls need interlocking bricks (joints shifted per layer), or the upper layers never get support. `game_start` (MCP) starts the game through the launcher: it leaves `BepInEx/devtools/autoplay`, and the launcher (patched by the mod) presses Play once when it finds it.

So Claude can play a game by itself: start a world without enemies, find flat ground and build.

- `POST /api/world/new` starts a world the way the new game screen does (`GameParam.StartGame` with the chosen map, mode and preset); from a game it first leaves without saving, like "leave game". Without `map` it takes the first official map that has the mode. The default mode is `sandbox`: no enemies, an invincible home crystal and 5 bricktrons. `waves` sets Invasion's minutes between waves (the "Waves every" choice); Invasion cannot be without waves, use Sandbox. Poll `GET /api/status` until `inGame` is true and `worldStart` is null; `worldStartResult` says how it went. `gamemode=world_editor` opens the map (default: the first official one) in the world editor instead.
- `GET /api/ground` gives the top solid voxel per column; a block goes in the empty voxel above (`y + 1`). `flat=6` lists 6 x 6 squares of the same height nearest the center, with the `from`/`to` corners to build on.
- `POST /api/blueprints/place` holds a copy of the blueprint at the voxel like the player's cursor (its own fitter turns and offsets it, workshops stand on the ground), validates it, and the host spawns the real blueprint into the build project, then commits the project so it gets built. `to` fills the box between `at` and `to`, stepping by the blueprint's footprint, bottom layer first. Refused pieces: `obstructed` (something in the way), `unsupported` (nothing holds it; `force=true` places it anyway and it waits for support, as the player may), `invalid`. Rope bridges are not placed.
- The build project: `project`, else the one the API used last, else the last selected build project, else a new one.
- Not yet: bricktron jobs (kits), spawning bricktrons, workshop recipes, research, harvest projects, saving.

### Game speed

`POST /api/speed` fast-forwards the game for testing, up to 20x, so walks, building, waves and tests play out much faster than real time.
- The speed goes through the game's own clock (`ClockConfig`) as a clock speed of its own, like the mod's speed keys. Pausing keeps it, and unpausing returns to it.
- Unity advances game time by at most `Time.maximumDeltaTime` per frame (1/3 s by default). At 20x and 30 fps a frame needs 0.66 s, so without a change the game would silently run at about 10x. While fast, `maximumDeltaTime` is raised to speed / 10 (the speed holds down to 10 fps) and put back at 1x. The fixed timestep is not changed, so a 20x game runs 20 times as many fixed steps per real second.
- `GET /api/speed` measures the speed actually reached. It is lower than asked when the computer cannot run that many fixed steps per second; `framesAtMaxDelta` is the share of frames that hit the cap.
- Back to 1x: when `forSeconds` runs out (kept in the game, so it also happens when nobody resets it), when a map loads, on leaving to the menu, and in multiplayer. If the player or the game picks another speed (speed keys, the game's speed buttons), DevTools lets go and keeps that speed. Every change is logged.
- Only in single player or as host without other players; the game keeps multiplayer at normal speed.
- While the game runs faster than 5x, frame spikes are still counted (F7, `/api/frames`) but not logged as "Performance: frame took X ms" warnings.

Things to keep in mind at 10-20x:
- Game logic that runs in `Update` with `Time.deltaTime` takes big steps: bricktron movement and animation (`Locomotion4`), projectiles flying (`KinematicProjectile`, which sweeps its collision over the step), the day/night cycle, boulder regrowth, drawbridges. Most AI runs in fixed steps and is not affected.
- The mod's per-frame budgets are real time per frame, not per game second: route searches (`[Pathfinding] SearchFrameBudgetMs`) and the task search (3 ms) get the same time per frame while a frame holds many more game seconds. Bricktrons may stand a bit longer waiting for a route or a task, in game time.
- The units' AI runs in at most `[Performance] AiTicksPerFrame` (2) fixed steps a frame, times the speed: at 10x up to 20.
- Timers of the mod that use real time (autosave, refresh intervals of panels, wave warnings) run at real-time pace.

### Route searches

`/api/path` runs the same A\* search a bricktron runs, including the mod's patches (heuristic weight, kept improved paths). It runs it in one go, not spread over frames. It then runs a Dijkstra over the same navigation graph: the game's level-0 nodes and their links, with the same rules for what that bricktron may cross. That gives the shortest route.

The cost of a route is the sum of its links' weights, counted the way the game counts them:
- a flat step is 10;
- a step up or down costs more;
- climbing costs 240 or more.

`costRatio` is the A\* cost divided by the shortest cost, so 1 means a shortest route.

`/api/path/stats` measures the searches the game itself runs while it plays (the API's own searches are not counted). The game first searches over coarse terrain blocks, then a fine search straightens the route near the start. The answer gives:
- searches per minute and how each ended (`improved`, `kept coarse` when the fine search ran out, `not precise`, `failed`, `abandoned`);
- CPU per search, per busy frame and the worst frame, and frames until a search is done;
- nodes visited by the coarse and the fine search, and whether the fine search needed a higher level;
- route length per straight distance;
- `repeats`: how many searches went to a target cell (4 voxels) an earlier search already went to, or came from and went to the same cells, which is what distance maps per target or a route cache could answer;
- the most common targets, target cells and origin/target pairs, and the most recent searches.

### Allocations

Every garbage collection stops the game for as long as it takes to scan the whole heap. `/api/frames` shows how much is allocated per second, but only for the Updates it measures. `/api/allocs` shows who allocates the rest. It reads Mono's own profiler interface (`AllocProfiler` in `CastleStoryPlus.Bootstrap`):
- bytes and objects per type, for every thread;
- the main thread's call stack, sampled every 32 KB allocated (`sampleKb`) and counted with the bytes it stands for;
- per site, the stacks summed by their first frame outside System, UnityEngine, Mono and MoonSharp: the game or mod method that allocates.

Start it with the game, in the doorstop entry point, so it sees every allocation: `CSP_ALLOC_PROFILER=1 tools/start-game.sh`. Started later (`POST /api/allocs/start`) it only sees arrays, strings and code compiled since, because Mono picks an allocator without the profiler call when it compiles a method. `POST /api/allocs/stop` and `/reset` stop and clear it. While it runs, every allocation costs a call into managed code, so the game runs a little slower. Linux x86-64 only.

### Benchmark

`POST /api/benchmark/build` makes a fixed benchmark save ("CSP Benchmark") from the original game's things only, and
`POST /api/benchmark/run` measures it (fps, 1% low, frame time percentiles, Unity's player loop steps, from four fixed
camera views) into `BepInEx/devtools/benchmarks/benchmark-<date>.json`. `CSP_AUTOLOAD="CSP Benchmark" CSP_BENCHMARK=quit`
loads it, measures it and closes the game. See [Performance](Performance.md#the-benchmark).

## In-game tests

A test is a static coroutine method with `[GameTest]` in `CastleStoryPlus.DevTools/Tests/`:

```csharp
[GameTest("Pathfinding", "What it checks", TimeoutSeconds = 60f)]
private static IEnumerator MyTest(TestContext t)
{
	t.True(condition, "message");          // a failed assertion ends the test
	yield return t.Seconds(2f);            // wait in game time
	yield return t.Until(() => done, 10f, "what it waits for");
	t.Cleanup(() => { /* always runs, also after a failure */ });
	t.Record("value", 42);                 // kept with the result
}
```

- Tests marked `NeedsHostGame` (the default) are skipped unless a game is in progress on the host. Single player counts as host.
- `TestWorld` builds scenery for a test:
  - `FindFlatArea` finds a flat open spot;
  - `PlaceBlocks` places stone bricks and removes them again in the clean-up;
  - `WaitForGraph` waits until the navigation graph has taken the change in.
- `t.Seconds` waits in game time; `t.Until` and the test's timeout count real seconds. A run with `speed` (e.g. 10 or 20) therefore finishes its waits sooner, while the timeouts stay as long.
- `t.UntilGame` waits like `t.Until`, but its limit counts game seconds, for gameplay that takes game time (a tree to fall, a fight).
- `t.UntilGame(condition, seconds, what, noteSeconds, note)` also runs `note` every few game seconds, to log or record what the game was doing, so a failed run tells why.
- `Scenario` sets up a test's own scene and takes it away again in the clean-up:
  - `Site` finds flat open ground near the home crystal (from the terrain, without trees, boulders or ore), away from the bricktrons standing round it and from the ground earlier tests used;
  - `Bricktron`, `Corruptron` and `Spawn` make units (any faction and job, with their gear) on the nearest voxel of the route graph (a unit off it cannot walk), `Items` loose items;
  - `NewProject`, `BuildProject` and `Hire` make projects and put bricktrons on them;
  - `Building` places a stockpile or workshop and finishes it at once;
  - `Count` and `Stored` count items of a kind (everywhere, or in storages), `Hp` and `Alive` read a unit's health.
  - each test gets ground of its own while it runs; it is given back afterwards unless the test left something there (a unit that died leaves a corpse; `LeavesMess` for dug ground). After every test the runner waits a few physics steps, so blocks its clean-up took away are gone before the next test looks at the ground.
- Before a run with `clearUnits` (the script's default) every unit the world has is taken away: the player's starting bricktrons and the map's roaming enemies and beasts, so the tests meet only their own units. The base's standing repair and cleanup tasks get no crew during the run (they would take the tests' workers and material). The runner adds one builder of its own for the older tests that use "the player's builder", and takes it away afterwards.
- Parallel runs: the tests of a class marked `[ParallelTests]` may run next to others, each on ground of its own at least 10 voxels from the others'. Tests of one group never overlap: `[ParallelTests("Economy")]` (tasks and crafting: workers fetch from and store into any stockpile, so their counts would mix) and `[ParallelTests("Combat")]` (a fight draws in every enemy within sight). `[GameTest(..., Exclusive = true)]` and classes without the attribute run alone. A test that finds no free ground while others hold it (`TestNoRoom`) runs again once nothing else runs.
- A test that expects something not to happen first shows that it could (a route to the spot before the wall is built), so it cannot pass for the wrong reason.
- The tests change the game they run in. They walk bricktrons around and place blocks for a while, so run them in a test save, or with `tools/run-game-tests.py`:

```sh
tools/run-game-tests.py                    # every test, 4x speed, a new sandbox on Tortuga
tools/run-game-tests.py --filter Combat    # one category or test
tools/run-game-tests.py --parallel 1       # one test after the other
```

Options: `--parallel` (tests at the same time, default 4), `--keep-units` (leave the world's own units in it), `--speed` (default 4), `--map` (default Tortuga, which has trees), `--settle` (game seconds the new world gets before the tests, default 60), `--hold` (keep the player running for a look into it), `--trace` (the worker trace on, `BepInEx/workertrace.log` in the player).

It starts a player of its own (default: the Unity 6 port's `CastleStoryPlus.Player/Build/Linux`) on a copy of a game root without saves (`--root-src`, default `~/.cache/csp-bench/root-src`), starts the sandbox, runs the tests, prints each outcome, saves and closes the player. The results go to `~/.cache/csp-tests/game-tests-<time>.json`, and the exit code is 0 when no test failed. No other game may answer on the API port while it runs.

| Test | Checks |
|---|---|
| `PathfindingTests.RandomRoutesNearShortest` | 40 random routes around a builder: every route that exists is found. No route costs over 1.3 times the shortest, and they average at most 1.1 |
| `PathfindingTests.DetourAroundWall` | Builds a wall 9 wide and 3 high on flat ground. The route goes round it, not through it, close to the shortest way |
| `PathfindingTests.BuilderWalksToSpot` | A builder ordered to a spot 10 voxels away gets there |
| `PathfindingTests.EnemyRoutesNearShortest` | The same check as the random routes, for an enemy unit, which uses its own heuristic weight and budget |
| `ModTests.FeaturesStarted` | Every feature that is switched on started without errors |
| `TaskTests.BuilderFellsTreeOnOrder` | A builder ordered to cut the nearest tree fells it: the tree leaves the tree registry and its logs lie there |
| `TaskTests.HarvestProjectFellsTrees` | A tree harvest project with two builders fells a tree in its circle |
| `TaskTests.LooseItemsAreHauled` | A builder told to clean up carries 4 loose stones into an empty stockpile |
| `TaskTests.QuarryIsDug` | A 3 x 3 quarry with two builders and a stockpile: at least 3 voxels of ground are dug out |
| `TaskTests.WorkersBuildBlueprints` | Two builders build 3 stone brick blueprints with stone blocks (`Blocks.Stone_DoubleBrick`, what a stone brick takes; raw stone does not do) from a stockpile |
| `CraftingTests.ChoppingBlockMakesPlanks` | A chopping block with a worker and logs in a stockpile: one Planks order makes 3 planks from one log |
| `CombatTests.KnightKillsCorruptron` | A knight attacks a corruptron 4 voxels away on its own and kills it, and survives |
| `CombatTests.ArcherShootsCorruptron` | An archer 9 voxels away shoots a corruptron |
| `CombatTests.OrderedAttack` | A knight ordered to attack a corruptron 14 voxels away walks to it and hits it |
| `CombatTests.CorruptronAttacksBricktron` | A corruptron attacks a builder next to it |
| `CombatTests.CorruptronDeath` | A killed corruptron is gone, the game's `UnitDied` signal names it and a firefly with its energy is made |
| `CombatTests.RaidDamagesHomeCrystal` | Three corruptrons in a raid march on the home crystal and damage it; in a sandbox, where the crystal is invincible, they have to reach it (its hit points are put back afterwards) |
| `CombatTests.RivalFactionsFight` | A Blue and a Red knight, the factions made enemies, fight until one dies. A faction without a player does not start a fight by instinct (recorded as `startedByInstinct`), so after 15 game seconds both are ordered to attack |
| `StructureTests.SupportedStructureStands` | A column of 6 stone bricks and a 5 x 3 wall on the ground stand, no block falls |
| `StructureTests.FloatingBlockFalls` | A stone brick 3 voxels above the ground falls (becomes a falling block) |
| `StructureTests.ColumnCollapsesWhenBaseDestroyed` | A column of 5 whose bottom block is destroyed as by an attack comes down: the 4 above fall |
| `StructureTests.OverhangFalls` | A block beside the top of a column, with nothing under it, falls; the column stands |
| `StructureTests.RemovedMiddleDropsTheTop` | A column with its middle block destroyed: the blocks above fall, those below stay |
| `StructureTests.BlueprintNeedsSupport` | A stone brick blueprint on the ground is buildable, one in the air is not |
| `StructureTests.TallTowerStands` | A tower of 15 stone bricks stands |
| `StructureTests.TenHighColumnCascades` | A column of 10 loses its base: all 9 above it come down |
| `StructureTests.NeighbourColumnStands` | Of two columns side by side, the one that loses its base falls, the other stands |
| `StructureTests.BlockFallsWhenGroundRemoved` | The ground under a stone brick is dug away (its damage receiver killed, as digging does): the brick falls |
| `BlockTests.BlueprintIntoBlockIsObstructed` | A blueprint where a stone brick already is: obstructed |
| `BlockTests.BlueprintOnBlueprintWaits` | A blueprint resting on another blueprint is valid but not buildable |
| `BlockTests.FinishedBlueprintBecomesBlock` | A finished stone brick blueprint becomes a stone brick |
| `BlockTests.DemolishedBlockIsGone` | A stone brick demolished at once with the remove tool's command is gone |
| `BlockTests.DamagedBlockStays` | A stone brick that loses a third of its hit points stays, damaged |
| `BlockTests.BlockDestroyedByDamage` | A stone brick out of hit points is destroyed |
| `RouteLimitTests.NoRouteIntoWalledSpot` | A spot inside a ring of stone 12 high: no route into it (the goal could be reached before the ring) |
| `RouteLimitTests.NoRouteOutOfWalledSpot` | From inside the ring no route leads out |
| `RouteLimitTests.LowWallIsNotClimbed` | A ring only 3 high already shuts the spot off: stone walls are not climbed |
| `RouteLimitTests.RouteThroughGapInTallWall` | The ring with a gap one column wide: the route goes in through the gap |
| `RouteLimitTests.BuilderWalksInThroughGap` | A builder ordered into the ring walks in through the gap |
| `RouteLimitTests.RouteOpensWhenWallRemoved` | No route into the closed ring; one column taken down, a route |
| `RouteLimitTests.RouteUpOneStep` | A platform one block high: the route goes up onto it |
| `RouteLimitTests.GoalInTheAirEndsOnGround` | A goal 3 voxels in the air: the search takes the nearest place to stand, the route ends on the ground |
| `RouteLimitTests.GoalInsideGroundEndsOnSurface` | A goal inside solid ground: the route ends on open ground |
| `RouteLimitTests.OrderIntoWalledSpotNeverArrives` | A builder ordered into the closed ring never gets in, nor up the wall |
| `CombatTests.HigherLevelHasMoreHp` | A level 5 corruptron has more hit points than a level 1 one |
| `CombatTests.KnightHasMoreHpThanBuilder` | A knight has more hit points than a builder |
| `CombatTests.ArchersKillCorruptron` | Three archers 8 voxels away shoot a corruptron dead |
| `CombatTests.KnightsBeatCorruptrons` | Two knights against two corruptrons: both corruptrons die |
| `CombatTests.SameFactionDoesNotFight` | Two knights of the same faction side by side do not hurt each other |
| `CombatTests.FactionsNotEnemiesDoNotFight` | A Blue and a Green knight (not enemies, as the game sets them) do not fight |
| `CombatTests.KilledCorruptronLeavesCorpse` | A corruptron killed by a knight leaves a corpse |
| `CombatTests.KnightKillsMinitron` | A knight kills a minitron and survives |
| `CraftingTests.TwoOrdersMakeSixPlanks` | Two Planks orders with two logs make 6 planks and use both logs |
| `CraftingTests.NoLogsNoPlanks` | A Planks order with no logs anywhere makes nothing and stays queued |
| `CraftingTests.NoWorkerNoPlanks` | A chopping block without a worker, and no other bricktron about, makes nothing (`Scenario.SidelineOthers` switches the bricktrons that are not the test's off for the test) |
| `CraftingTests.IdleBuilderTakesWorkshopOrder` | An idle builder takes a chopping block's order by itself, no worker assigned, and makes the planks (a builder with nothing to do, even one on a project without work, does so) |
| `CraftingTests.LoopUsesAllLogs` | A looping Planks order goes on while there are logs: 2 logs make 6 planks |
| `TaskTests.FelledTreeDropsThreeLogs` | A felled tree drops its 3 logs next to its stump |
| `TaskTests.LooseIronIsHauled` | A builder told to clean up carries 3 lumps of iron ore into a stockpile |
| `TaskTests.DeletedProjectReleasesCrew` | A deleted project's bricktrons are on no project afterwards |
| `TaskTests.HiringGrowsFullCrew` | Hiring three onto a project with room for one grows its crew size to take them all |
| `TaskTests.PriorityRaisedAndLowered` | A project's priority goes up and back down with the task bar's buttons |
| `WorkerOrderTests.ReassignedWorkerStoresThenSwitches` | A quarry worker with a part-full bag moved to a tree harvest takes no more quarry work: everything in its bag goes into a storage, then it starts on the harvest |
| `WorkerOrderTests.ManualDigWaitsForNextOrder` | A worker told to dig a voxel takes no task for 6 game seconds afterwards (also with an empty bag), digs the next ordered voxel, and takes up something again once the wait is over |
| `WorkerOrderTests.AssignedWorkerStaysOnTask` | A worker put on a tree harvest fells two of its trees and never works in a quarry of higher priority next to it |
| `WorkerOrderTests.RightClickedWorkerStaysOnTask` | The same for a free worker right-clicked onto a tree of the harvest (`DirectTask`): it joins the harvest's crew and stays on it |

## Claude Code (`CastleStoryPlus.Mcp`)

`.mcp.json` at the repository root starts the built server:

```
dotnet build CastleStoryPlus.Mcp -c Release
```

Claude Code then has these tools:
- `game_status`
- `game_log`
- `game_features`
- `game_bricktrons`
- `game_objects`
- `game_storage`
- `game_selection`
- `game_move`
- `game_problems`
- `game_cleanup`
- `game_screenshot`, which shows the image at most 1280 pixels wide (`maxWidth`), so it fits in one MCP message; the full-size file stays at the path given
- `path_find`
- `path_node`
- `path_stats` and `path_stats_reset`
- `tests_list`
- `game_spawn_enemies`, which spawns enemies (also Healtrons) as a raid on the player's base
- `game_speed`, which reads or sets the game speed (up to 20x, with `forSeconds` to return to 1x by itself) and answers the speed reached
- `tests_run`, which waits for the results; `speed` runs the tests faster
- `tests_results`
- `tests_stop`
- `api_call`, for any endpoint

The game must be running with the DevTools plugin for them to answer. Set the environment variable `CSP_API_PORT` in `.mcp.json` if the port is changed.
