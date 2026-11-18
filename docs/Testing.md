# Testing

Most of the mod's logic is tested by fast unit tests that need neither the game nor Unity. The slow in-game tests
([DevTools](DevTools.md), `tools/run-game-tests.py`) stay for what only the running game can show; run them rarely.

| Where | What | Needs the game | Command | In CI |
|---|---|---|---|---|
| `CastleStoryPlus.UnitTests` | Unit tests of the pure logic (NUnit) | no | `dotnet test CastleStoryPlus.UnitTests` | yes, "Unit tests" |
| `CastleStoryPlus.Tests` | Harmony patch audit, tests of mod types that touch game types | its assemblies (the port player's) | `dotnet test CastleStoryPlus.Tests` | no |
| `CastleStoryPlus.DevTools/Tests` | In-game integration tests | the running game | `tools/run-game-tests.py` | no |

## Architecture: patches and components are thin, decisions are pure

A feature has three kinds of code:

1. **Adapters** (Harmony patches, MonoBehaviours, `Plugin`/`Startup` code): read the game (clock, settings, the world,
   network), call the logic, and write the answer back (set `__result`, change a field, send a message). No arithmetic
   or branching worth testing.
2. **Logic** (pure classes): the decisions and calculations. Inputs and outputs are numbers, bools, enums, strings,
   bytes and small classes of their own. Time, random numbers and settings come in as arguments (`float clock`,
   `Func<double> next`, `int? setting`), never read from `Time`, `UnityEngine.Random` or `ConfigEntry`.
3. **Game state** stays in the adapter's statics (the loaded world, who is host); the logic's own state (e.g.
   `RainWeather`) is a plain object the adapter keeps.

### The folder convention

Pure code lives in a folder named `Logic` inside the feature's area folder, one file per class:

```
CastleStoryPlus/Weather/Logic/RainRules.cs      static functions: numbers and decisions
CastleStoryPlus/Weather/Logic/RainWeather.cs    the weather's state and hourly roll
CastleStoryPlus/Weather/Logic/RainWire.cs       save text and message bytes, with their checks
CastleStoryPlus/Pathfinding/Logic/RoadRules.cs  road speed factor, route weights, navigation area costs
```

Every file under `CastleStoryPlus/**/Logic/**/*.cs` is part of the mod as usual (same namespace as the area, e.g.
`CastleStoryPlus.Weather`) **and** is compiled a second time, straight from the source, into
`CastleStoryPlus.UnitTests`, which has no reference to any game or Unity assembly. The compiler enforces the purity:
a `using UnityEngine;` or a game type in a `Logic` file does not compile in the test project (and CI), so it cannot
slip in.

Why this and not a separate class library or mocking:

- A source link needs no extra assembly to ship, load or reload (the mod is one DLL, `ModReload` swaps it as before),
  and the code is built for the port (net48) exactly as the rest.
- The tests run on .NET 8 with no game DLL, so GitHub Actions can run them. The game's assemblies are not in the
  repository and must never be.
- No interfaces or mocks are needed (the project avoids interfaces): the logic takes plain values.

### Rules for `Logic` files

- Allowed: `System`, `System.Collections.Generic`, `System.Linq`, `System.Text`, and `Newtonsoft.Json` (the game ships
  it; the test project takes it from NuGet).
- Not allowed: `UnityEngine` (also structs such as `Vector3` and `Mathf`: use `System.Math`, floats, or a small value
  type of your own), `Brix.*`, `BepInEx`, `HarmonyLib`, `GameObject`, `Time`, `Physics`, networking.
- The mod targets the Unity 6 port (net48, `UNITY6`); the old net35 build is dropped (releases ship the port since 0.8.1), so
  net48 APIs are fine. Keep the maths identical to Unity's where it was Unity's (`RainRules.Repeat` is `Mathf.Repeat`, `RoadRules.RoundToInt`
  is `Mathf.RoundToInt`: halves round to even).
- Classes are `internal`; the test project compiles them itself. Test classes must be public, so keep the pure types
  out of the signatures of test methods (use `int` and cast to an enum inside the test).
- Named constants for the numbers the behaviour depends on (`RainRules.RestHours`), so tests and adapters share them.

## Writing unit tests

Put them in `CastleStoryPlus.UnitTests/<Area>/`, named after the class (`RainRulesTests`). Test the edges: zero,
negative, NaN, the exact limit and one past it, empty or broken input, and the order things happen in. For anything with
random numbers pass a fixed `Func<double>` (`() => 0.0`) or a seeded `System.Random`; for a long-run property (the
average wait between rains) use a seeded loop. A test of a state machine (`RainWeather`) runs whole cycles.

```sh
dotnet test CastleStoryPlus.UnitTests        # about a second
```

## Converting a feature (a short guide)

1. Read the patch or component and mark every line that is a **decision** or a **calculation**: comparisons, clamps,
   scaling, counting, parsing, validation, state changes that follow from inputs.
2. Move them into `<Area>/Logic/<Feature>Rules.cs` (static functions) and, when there is state, a small class
   (`<Feature>State`). Replace Unity calls with plain types: `Mathf.Max` becomes `Math.Max`, a `ConfigEntry<int>` value
   becomes an `int` (or `int?` for "not set"), `Time.time` becomes a `float now` argument, a `Vector3` a few floats.
3. Make the adapter call the logic. Do not change behaviour in the move: first tests that describe what the code does
   now, then (separately) fixes.
4. Write the tests (edge cases first), run `dotnet test CastleStoryPlus.UnitTests`.
5. Keep what cannot be pure (finding the block, reading the game's field) in the adapter, and keep it a few lines.
6. Existing pure helpers that sit next to game code (for example `QuarryDepthSteps`, `Upgrades/SprintRules.cs`) are
   converted by moving the file into the area's `Logic` folder (the namespace stays) and its test from
   `CastleStoryPlus.Tests` into `CastleStoryPlus.UnitTests`; if the helper uses a game type, replace it by values first.

Worked examples: Rain (`Weather/Logic`, `CastleStoryPlus.UnitTests/Weather`) and Roads (`Pathfinding/Logic`,
`CastleStoryPlus.UnitTests/Pathfinding`).

## Local tests with the game's assemblies (`CastleStoryPlus.Tests`)

They load the mod and the game's assemblies on .NET 8, without starting the game. The project always builds the mod for
the Unity 6 port's player (`CastleStoryPlus.Player/Build/Linux` after an install; or `-p:PlayerDir=<player>/`) and does
not install it anywhere (`SkipInstall`). The assemblies are found through `gamefolders.txt`, written by the build.

```sh
dotnet test CastleStoryPlus.Tests
dotnet test CastleStoryPlus.Tests -p:PlayerDir=$HOME/.cache/csp-tests/players/agentE/
```

### The Harmony patch audit (`HarmonyPatchAuditTests`)

Reads the patches' attributes (class level and method level, `TargetMethod(s)`) and resolves them against the game's
assemblies, without patching anything:

- **Targets exist:** every patch's target method is found and not ambiguous (name the parameter types).
- **Parameters can be filled:** every parameter of a prefix, postfix, finalizer is `__instance`, `__result`, `__state`,
  `___field` (the field exists), `__n`, or the name of one of the target's parameters, with a compatible type; a prefix
  returns void or bool, a transpiler an `IEnumerable`.
- **Skipping prefixes:** where several patch classes have a prefix on the same target (listed in the test output), a
  prefix that can skip the original (a bool prefix that is not just `return true`; a heuristic on its IL, it may flag a little too much) before one that does not take `__runOriginal` fails. HarmonyX orders by priority, then `[HarmonyBefore]`/`[HarmonyAfter]`, then registration order, so for equal priority both orders are checked.
  Later prefixes still run and may overwrite `__result`; either the later one takes `__runOriginal` and respects it, or
  the pair is added to `SkipWhitelist` with the reason it is fine. The first run listed 27 pairs that are on the list
  as "baseline, not reviewed": review one, write its reason or fix it, and the list shrinks. A new pair fails.
- **Message ids:** every `const short` of 200 or more is unique, and none is written as a bare number in a
  `RegisterHandler`/`SendToAll` call (so this test can see it).

Patches made by code (`harmony.Patch(...)` in `FrameMonitor`, `LazySignalLists`) are not seen by the audit. The test
uses the `Lib.Harmony` package, because the game's HarmonyX does not start on .NET 8.

## In-game tests

See [DevTools](DevTools.md). `tools/run-game-tests.py --headless` and `tools/run-multiplayer-tests.py --headless` start
the players with `-batchmode -nographics`. A test that needs rendering (drops, lights, screenshots) declares
`[GameTest(..., NeedsGraphics = true)]` and is left out of a headless run.
