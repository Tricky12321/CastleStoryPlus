# Development

- [Building from source](#building-from-source) · [Testing and the developer API](#testing-and-the-developer-api) · [Releasing](#releasing)
- [Project layout](#project-layout) · [How it works](#how-it-works)

## Building from source

Requirements:
- the [.NET SDK](https://dotnet.microsoft.com/download) (8 or newer; the plugin targets .NET Framework 3.5 like the game);
- an installed copy of Castle Story with BepInEx;
- a copy of the **original** game assemblies in `Castle Story_Data/Managed.orig/`. The plugin compiles against these, and the [BepInEx assembly publicizer](https://github.com/BepInEx/BepInEx.AssemblyPublicizer) makes their private members reachable.

```sh
dotnet build -c Release
```

The build copies `CastleStoryPlus.dll` into the game's `BepInEx/plugins/CastleStoryPlus/`. If the game is not in the default Steam location:

```sh
dotnet build -c Release -p:GameDir="/path/to/Castle Story/"
```

How to add a building with its own model and textures is described in [Custom buildings](CustomBuildings.md).

### Testing and the developer API

Three projects next to the plugin, never part of a release (see [DevTools](DevTools.md)):
- `CastleStoryPlus.UnitTests`: unit tests of the mod's pure logic (the `Logic` folders), no game needed, also run in CI: `dotnet test CastleStoryPlus.UnitTests`. `CastleStoryPlus.Tests`: the local-only tests that need the game's assemblies (Harmony patch audit). See [Testing](Testing.md).
- `CastleStoryPlus.DevTools`: a plugin of its own (`BepInEx/plugins/CastleStoryPlus.DevTools/`) with an HTTP API on `http://127.0.0.1:27860/api` into the running game, and in-game tests (pathfinding, ...) that run in the game that is loaded.
- `CastleStoryPlus.Mcp`: an MCP server that offers the API as tools to MCP clients (game state, log, screenshots, route searches, running the in-game tests, fast-forwarding the game up to 20x with `game_speed`). Registered in `.mcp.json`.

Code changes without a restart:
- the in-game menu (`CastleStoryPlus.UI`): build it and `POST /api/ui/reload`;
- the plugin's patches: build it and `POST /api/mod/reload`. The newest `CastleStoryPlus.dll` is loaded as a new copy, the running copy's Harmony patches are removed, its features' static `Disable()` run and its components on the plugin object are destroyed, and the new copy patches its features as at startup; the old copy's components on the game's objects are swapped for the new copy's type with their fields copied. What the old copy set up otherwise (event and signal handlers without a `Disable()`, static data, content registered once such as new resources) stays, and DevTools and the tests stay bound to the startup copy, so restart the game for anything beyond patch changes.

Finding what is slow or what fills memory: see [Performance](Performance.md) and [Lua memory leaks](LuaMemoryLeaks.md).

### Releasing

1. Bump `Version` in `CastleStoryPlus/Plugin.cs`, update [`CHANGELOG.md`](../CHANGELOG.md) and commit.
2. Run `tools/release.sh`. It builds the plugin once and packs one package per system with `tools/package-port.sh`:
   - `dist/CastleStoryPlus-v<version>-linux.zip`: `install.sh` and BepInEx linux_x64;
   - `dist/CastleStoryPlus-v<version>-windows.zip`: `install.bat`, `install.ps1` and BepInEx win_x64.

   It then tags `v<version>` and creates the GitHub release with the [GitHub CLI](https://cli.github.com/). The release gets just the two packages and the install instructions from `tools/release-notes.md`.

Releases are built locally, because the build needs the game's own assemblies, which cannot go into the repository or CI.

## Project layout

```
CastleStoryPlus.Bootstrap/   doorstop entry point that makes HarmonyX work on Mono 2.6 (see docs/Installation.md)
CastleStoryPlus/
  Plugin.cs            entry point: config, feature switches and Harmony setup
  Core/                feature attribute, Lua injection, IL helpers
  Workers/             per-worker data (XP, call to arms role): stored, saved and synced by the mod
  <Area>/              one folder per feature area (Army, Building, Combat, Economy, Experience,
                       Giant, Loading, Market, Menus, Metallurgy, Nature, Pathfinding, Performance,
                       Respawn, Saving, UI, Updates, Upgrades, WorkerAI, ...)
  Diagnostics/         modding aids and debug windows (debug menu, X-ray, SceneDump, WorkerTrace)
  Lua/                 Lua UI files added by the mod (the mod's own code only)
  Assets/              artwork (logo.png, plus.png), rendered by tools/make_logo.py
CastleStoryPlus.UI/          the C# in-game menu's builders and views (CastleStoryPlus.UI.dll, reloadable in game)
CastleStoryPlus.DevTools/    developer plugin (not released): HTTP API into the game, in-game test runner and tests
CastleStoryPlus.Mcp/         MCP server (stdio), calls the DevTools API
CastleStoryPlus.UnitTests/   unit tests of the mod's pure logic (NUnit, no game, also in CI)
CastleStoryPlus.Tests/       local tests that need the game's assemblies (Harmony patch audit)
installer/             install.sh (Linux), install.ps1 + install.bat (Windows): install and update from GitHub releases
tools/                 package.sh (Linux + Windows packages), release.sh (tag + GitHub release), release-notes.md, make_logo.py
docs/                  technical notes and the gallery
CHANGELOG.md           all changes, in English
Directory.Build.props  shared build settings (target framework, game path)
```

## How it works

- **Patches:** each change is a small Harmony patch class tagged with the feature it belongs to. A feature switched off in the config is simply not patched.
- **Lua UI:** the game's own Lua files are not shipped. When the game loads one, the mod inserts its code at fixed anchors. It adds its own Lua files from `BepInEx/plugins/CastleStoryPlus/Lua/`, and gives Lua a `CastleStoryPlus` table of C# functions.
- **Per-worker data** (XP, call to arms role):
  - It is saved as extra properties on the game's existing `CharacterState` component, so saves stay loadable by the unmodded game. Saves made with the earlier prototype keep their XP.
  - It is synced by appending it to that component's network data.
