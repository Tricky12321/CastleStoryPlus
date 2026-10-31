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
| `POST /api/quit` | Closes the game (its own Quit), half a second after answering; `tools/start-game.sh --restart` uses it |
| `POST /api/testworld?width=` | Builds the test world round the home crystal: every research done, one of every building and block, an armour stand per soldier class, and a full 2 x 2 and 3 x 3 stockpile of every resource at every Stacking tier. `GET /api/testworld` follows it, `GET /api/testworld/pieces` lists the pieces with their place |
| `GET /api/studio/plan` | The photo studio's pieces (the test world's plan) by index |
| `POST /api/studio?index=` | Shows that one piece alone on the studio's spot west of the home crystal, built and filled; `GET /api/studio` follows it and gives its drawn bounds. `tools/make_gallery.py` photographs every piece this way for [the gallery](Gallery.md) |
| `POST /api/camera?at=x,y,z&distance=&pitch=&yaw=` | Points the camera at a place |
| `POST /api/select?id=&q=` | Selects a building as a click does (its menu opens), by id or type (`MarketStation`) |
| `GET /api/path?to=x,y,z&from=&unit=&enemy=&nodes=` | Runs the game's route search for a bricktron and compares it with the shortest route |
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
| `GET /api/base` | The player's faction, home crystal (position, ground height, energy, bricktrons it can spawn), bricktrons by job, projects |
| `GET /api/ground?x=&z=&radius=&flat=` | Terrain heights round a point (default the home crystal), and flat squares to build on |
| `GET /api/blueprints/keys?all=` | The build menu's blueprints with their footprint |
| `POST /api/blueprints/place?key=&at=x,y,z&to=&turn=&project=&force=&commit=` | Places blueprints like the player, one or a box of them; answers per piece placed or refused (obstructed, unsupported, invalid) |
| `GET /api/projects?q=` | The player's projects: type, priority, crew and crew size, goals |
| `POST /api/projects/create?type=build\|quarry&at=&from=&to=` | Makes a build project, or a quarry digging out a box |
| `POST /api/projects/priority?id=&priority=` | Sets a project's priority (locked, low, medium, high) |
| `POST /api/projects/crew?id=&size=` | Sets a project's crew size (0 to 8) |
| `GET /api/tests?filter=` | The in-game tests |
| `POST /api/tests/run?filter=&speed=` | Starts a run (answers at once), at `speed` if given (back to 1x when it ends) |
| `GET /api/tests/results` | Progress, and each test's outcome, message, log and recorded values |
| `POST /api/tests/stop` | Stops the run after the current test's clean-up |

The API takes calls on its own threads. Everything that touches the game runs on Unity's main thread, one frame at a time.

### Playing through the API

Cheats (host only): `instant=true` on `POST /api/blueprints/place` or `POST /api/blueprints/complete` builds blueprints at once; `POST /api/cheat/bricktrons?count=&job=` spawns bricktrons; `POST /api/cheat/items?key=&count=` spawns items; `POST /api/cheat/enemies?type=&count=&at=&level=&raid=` spawns enemies (corruptron, biftron, minitron, crawler, magitron or healtron), by default about 20 voxels from the home crystal as a new raid aimed at it. `POST /api/projects/hire?id=&count=` puts idle bricktrons on a project (a project's crew size alone does not hire anyone). Workshops: `GET /api/workshops`, `POST /api/workshops/worker?id=`, `POST /api/workshops/queue?id=&recipe=Planks&loop=true`. `POST /api/blueprints/remove?from=&to=` takes away unbuilt blueprints; `POST /api/demolish?from=&to=` takes down placed buildings (`instant=true` at once). Leave walkways between stockpiles: workers cannot reach a stockpile surrounded by others. Workers cannot dig a quarry under trees: `POST /api/projects/create?type=quarry` refuses it unless `clear=true` (a harvest project cuts them first); `GET /api/ground?flat=6&noTrees=true&heights=false` lists places without trees. Stone walls need interlocking bricks (joints shifted per layer), or the upper layers never get support. `game_start` (MCP) starts the game through the launcher: it leaves `BepInEx/devtools/autoplay`, and the launcher (patched by the mod) presses Play once when it finds it.

So Claude can play a game by itself: start a world without enemies, find flat ground and build.

- `POST /api/world/new` starts a world the way the new game screen does (`GameParam.StartGame` with the chosen map, mode and preset); from a game it first leaves without saving, like "leave game". Without `map` it takes the first official map that has the mode. The default mode is `sandbox`: no enemies, an invincible home crystal and 5 bricktrons. `waves` sets Invasion's minutes between waves (the "Waves every" choice); Invasion cannot be without waves, use Sandbox. Poll `GET /api/status` until `inGame` is true and `worldStart` is null; `worldStartResult` says how it went.
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
- The tests change the game they run in. They walk bricktrons around and place blocks for a while, so run them in a test save.

| Test | Checks |
|---|---|
| `PathfindingTests.RandomRoutesNearShortest` | 40 random routes around a builder: every route that exists is found. No route costs over 1.3 times the shortest, and they average at most 1.1 |
| `PathfindingTests.DetourAroundWall` | Builds a wall 9 wide and 3 high on flat ground. The route goes round it, not through it, close to the shortest way |
| `PathfindingTests.BuilderWalksToSpot` | A builder ordered to a spot 10 voxels away gets there |
| `PathfindingTests.EnemyRoutesNearShortest` | The same check as the random routes, for an enemy unit, which uses its own heuristic weight and budget |
| `ModTests.FeaturesStarted` | Every feature that is switched on started without errors |

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
