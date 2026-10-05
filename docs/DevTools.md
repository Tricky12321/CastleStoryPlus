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
| `GET /api/bricktrons?all=&q=` | Bricktrons: name, job, activity, task, position, what they carry |
| `GET /api/objects?q=&max=` | Things in the world by type name, with counts |
| `GET /api/storage?q=&empty=&max=` | Storages and what they hold: amounts per resource (type and the name the UI shows) and stored objects by type |
| `GET /api/selection` | What the player has selected, with ids, positions and whether it can be moved |
| `POST /api/move?id=&to=x,y,z&turn=&project=` | Moves a building like the mod's move key: a blueprint at the new place, the old one demolished |
| `GET /api/problems?count=&q=` | The system log's problems (F9): failed bricktron tasks with their reason, errors and warnings |
| `GET /api/cleanup?unit=&max=` | What a worker's automatic cleanup sees: loose items and why each is not picked up |
| `POST /api/screenshot` | Saves a screenshot under `BepInEx/devtools/screenshots/` and answers its path |
| `GET /api/path?to=x,y,z&from=&unit=&enemy=&nodes=` | Runs the game's route search for a bricktron and compares it with the shortest route |
| `GET /api/path/node?at=x,y,z` | The navigation node at a voxel and its links (kind, weight) |
| `GET /api/path/ground?x=&z=` | The highest voxel a bricktron can stand on in a column |
| `GET /api/tests?filter=` | The in-game tests |
| `POST /api/tests/run?filter=` | Starts a run (answers at once) |
| `GET /api/tests/results` | Progress, and each test's outcome, message, log and recorded values |
| `POST /api/tests/stop` | Stops the run after the current test's clean-up |

The API takes calls on its own threads. Everything that touches the game runs on Unity's main thread, one frame at a time.

### Route searches

`/api/path` runs the same A\* search a bricktron runs, including the mod's patches (heuristic weight, kept improved paths). It runs it in one go, not spread over frames. It then runs a Dijkstra over the same navigation graph: the game's level-0 nodes and their links, with the same rules for what that bricktron may cross. That gives the shortest route.

The cost of a route is the sum of its links' weights, counted the way the game counts them:
- a flat step is 10;
- a step up or down costs more;
- climbing costs 240 or more.

`costRatio` is the A\* cost divided by the shortest cost, so 1 means a shortest route.

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
- `game_screenshot`, which shows the image
- `path_find`
- `path_node`
- `tests_list`
- `tests_run`, which waits for the results
- `tests_results`
- `tests_stop`
- `api_call`, for any endpoint

The game must be running with the DevTools plugin for them to answer. Set the environment variable `CSP_API_PORT` in `.mcp.json` if the port is changed.
