# Castle Story Plus

Castle Story Plus is a quality-of-life and gameplay mod for [Castle Story](https://store.steampowered.com/app/227860/Castle_Story/). It aims to make workers smarter, give the player more control over the army, and smooth out map loading and the UI.

The mod is a  [BepInEx 5](https://github.com/BepInEx/BepInEx) plugin that uses [HarmonyX](https://github.com/BepInEx/HarmonyX) to patch the game in memory at runtime:

- **No game files are changed.** To uninstall, delete the plugin folder.
- **No game code is in this repository.** It contains only the mod's own code. Castle Story is © Sauropod Studio.
- **Saves stay compatible.** Saves made with the mod can still be loaded by the unmodded game.

> **Status:** every feature below has been ported from the earlier decompiled-game prototype to this plugin and loads without errors. They still need play-testing in the plugin form.

| Area | Feature | Config switch (`[Features]`) |
|---|---|---|
| Economy | New workers arrive faster: brewed fireflies carry more energy (default +30%, `[Economy] EnergyMultiplier`) | `Economy` |
| Worker AI | Workers start work on their own, pick up their next task faster, and help other task groups when theirs is done | `WorkerAI` |
| Worker AI | A task can be paused instead of deleted: no bricktron works on it until it is resumed | `PauseTasks` |
| Worker AI | How many bricktrons work on a task at a time can be limited, e.g. a quarry with 4 | `WorkLimit` |
| Worker AI | A single blueprint (e.g. a warehouse) can get top priority from the context wheel over it | `BuildPriority` |
| Worker AI | No duplicate build jobs; workers carry a full armful for a blueprint and the ones near it, and deliver to them on one trip | `BuildJobs` |
| Worker AI | Faster task search in big task areas (mining, digging, many trees): no idle seconds after each block | `FasterTaskSearch` |
| Worker AI | Task reservation: a worker reserves the task it chooses, so idle workers no longer walk to the same task together | `TaskReservation` |
| Worker AI | Direct task: select bricktrons and right-click a blueprint, tree or block of an area to have them do that task now | `DirectTask` |
| Economy | Quarry limits per resource: a quarry digs stone, iron, brimstone, coal and blue crystal only until the team has the set amount of each (small + and - over each resource) | `QuarryLimit` |
| Economy | Quarry depth can be changed at any time, also while it is being dug, and quarries go 3 times as deep | `QuarryDepth` |
| Worker AI | Resource reservation: a worker reserves the items it fetches (a loose item, or its share of a stockpile), so others fetch elsewhere | `ResourceReservation` |
| Worker AI | Workers prefer the nearest stockpile | `StockpileChoice` |
| Worker AI | Idle workers consolidate small stockpiles into the fullest one | `StockpileConsolidation` |
| Worker AI | Mixed stockpiles: one pallet holds several resources, one resource type per column (four columns); raw stone (converted to bricks), bricks, logs, planks, iron, brimstone, crystals and other valuables (`[MixedStockpiles] NeverMix`) are never mixed | `MixedStockpiles` |
| Building | Large stockpile: 3 x 3 blocks, holds three times as much as the 2 x 2 stockpile | `LargeStockpile` |
| Worker AI | Idle workers pick up loose items near the base and store them, no cleanup zone needed; items on top of a building are taken from its foot; the range is set on the home crystal's task (blue ring around the crystal) | `AutoCleanup` |
| Worker AI | Idle builders repair damaged blocks near the base, no repair zone needed; the range is set on the home crystal's task (green ring around the crystal) | `AutoRepair` |
| Worker AI | Tree harvest areas also remove the stumps of felled trees: 3x the axe work of a tree, 1 log instead of 3 (`[TreeStumps]`) | `TreeStumps` |
| Building | Hovering a blueprint shows the resources it still needs, with icon and count; a block of a build task, and the menu of a selected build task, show what the whole task still needs (red when the stockpiles hold too little) | `BuildNeeds` |
| Pathfinding | Shorter routes, nearby stairs found from walls, bigger search budgets, no pause after picking something up, stairs usable under walkways, route searches limited to a time budget per frame | `Pathfinding` |
| Performance | Fixes memory leaks in the game itself (worker error store, highlights, pie menus, Lua menus, observers that outlive their game, stale reservations) | `GameLeakFixes` |
| Performance | Shorter garbage collection pauses: the Lua value heap (10 million slots, 280 MB, read by every collection) starts at 1 million slots and grows only when needed | `LuaHeapShrink` |
| Combat | Archers always hit visible targets, and arrows never hurt allies | `ArcherAccuracy` |
| Combat | Melee fighters low on health retreat to a healing ward or the home crystal and heal before fighting again (`[Retreat]`) | `Retreat` |
| Call to arms | Soldiers per class (limited by the sets you own), ranged and melee rally points, a role for each worker (for the selected workers, or per worker in a list of all workers) | `CallToArms` |
| Combat | Mixed weapon stands: one stand holds the weapons and armour of all soldier classes at once (as many weapons, shields/quivers and helmets/caps as before, any classes mixed); equipping takes one whole kit of a single class: the one a call to arms asks for, otherwise the class with the most kits on the stand (`[MixedRacks] PerSlot`) | `MixedRacks` |
| Experience | Separate work and combat XP, levels 1–10 with +5% per level, shown in the name tag with XP bars and a level-up effect | `Experience` |
| Workers | Giant bricktrons: sacrifice one worker and pay 50 dark crystals (or, without DarkCrystals, 1.5x the energy of a new bricktron) to upgrade another; 1.25x as big, 2x speed for everything it does, 2x health, carries 2.5x as much, still counts as one bricktron; one per 5 bricktrons; first researched at the research station (`[GiantBricktron]`) | `GiantBricktron` |
| Building | New blocks: stone bricks of 2 x 2, 2 x 4, 1 x 3 and 1 x 4 that stand on one supported block (the long ones bridge gaps between walls), a brick on end (1 x 1, 2 high), wooden slabs (half high) of 1 x 1, 2 x 1, 2 x 2 and 2 x 4 that hold on to the side of stone | `CustomBlocks` |
| Building | A drag build stops when the mouse button is let go, also over the interface or outside the window | `DragRelease` |
| Building | One build task per team: every blueprint goes into it, and other build tasks are merged into it | `GlobalBuildJob` |
| Building | Wood ladder: bricktrons climb straight up and down it, slower than stairs (`[Ladders] ClimbSpeed`, `RouteCost`), only with free hands or a bag | `Ladders` |
| Building | Warehouse: a 9 x 6 hall in the build menu that holds 4500 resources of any kind; workers walk inside, and its brick works turns stored stone into bricks (60 planks, 80 bricks, 20 iron, 10 rope) | `Warehouse` |
| Building | A demolished stockpile or warehouse drops what it holds instead of destroying it | `StockpileSpill` |
| Building | Market: a market hall in the build menu (crafting group, 8 planks, 6 bricks, 2 fabric) where workers trade any resource for any other; 15% fee, prices rise with trading and recover over time (a trade then asks more for the same lot, never refused), so trading never makes resources; coal and steel trade too (steel can only be given); several workers fetch for the same trade; workers walk inside (`[Market]`) | `Market` |
| Combat | Smithy, armoury and research station in the build menu (crafting group): workers research weapon upgrades (Sharpened Blades, Heavy Blades, Fletching, Steady Aim, Winch) and armour upgrades (Chainmail, Padded Gambeson, Reinforced Helmet, Shield Rims, Warded Plate) in three tiers (iron, steel, crystal), and at the research station colony upgrades paid in dark crystals (Housing: up to 15 more bricktrons, Work Methods, Stacking (+33% storage per tier, up to twice the room: 4 layers instead of 2, taller crates), Crystal Attunement, Light Boots, Firefly Lore, Giant Bricktrons, Scouting: more time between waves, Efficient Mining: more stone, ore and crystal from digging and mining; 5 minutes of work per tier, with a progress bar over the researching worker), for the whole team; kept in the save; workers walk inside | `Upgrades` |
| Economy | Coal and steel: coal is mined from veins in the deep rock of every map or burnt from logs in the furnace; the forge makes steel from iron and coal; the upgrades' steel tier costs steel | `Metallurgy` |
| Building | Hovering a stockpile lists what it holds next to the mouse (icon, name, count); a warehouse also shows how full it is in percent | `StockpileTooltip` |
| Economy | Dark crystals: slain enemies drop them (5 at 1 in 3, biftrons 15 at 1 in 2); the upgrades' crystal tier and giant bricktrons cost dark crystals | `DarkCrystals` |
| Economy | Blue crystal veins in the deep rock of every map, so blue crystal can be mined | `BlueCrystalDeposits` |
| Workers | A dead worker respawns for a flat 100 energy instead of the price of a new bricktron | `RespawnCost` |
| Economy | Workshops can loop their queue until a stock limit (5–100 of what they make), waiting while there is enough | `CraftLoopLimit` |
| Economy | A workshop's priority (e.g. the research station) can be set like a task's: locked, low, medium or high | `WorkshopPriority` |
| Workers | Dying bricktrons drop their gear instead of losing it | `DropGearOnDeath` |
| Combat | Artificers heal team mates in attack range with green healing bolts (10% health per bolt, one at a time) | `ArtificerHealing` |
| Combat | Critical hits from combat level 3: double damage at 5% (level 3), 10% (5), 15% (8) or 20% (10), with a red "2x damage" over the bricktron and a red glowing sword or shot | `CriticalHits` |
| Building | Eyedropper: middle-click a block or building to build a copy | `Eyedropper` |
| Building | Pending blueprints are always visible | `BlueprintsVisible` |
| Building | Blueprints marked for deconstruction stay dark orange, also while their build task is not selected | `DeconstructColour` |
| Building | Move buildings: hold **M** and click a building, then place it; workers demolish the old one and build the new one from its materials (`[Building] MoveStructureKey`) | `MoveStructure` |
| Building | Copy and paste: **Ctrl+C** and drag an area to copy its blocks, buildings and blueprints; **Ctrl+V** pastes them as blueprints (right-click rotates) (`[Building] CopyKey`, `PasteKey`) | `CopyPaste` |
| UI | Resource list (icon, name, count) in the top-right corner; a resource stays listed from the first time it is stocked (in red at 0, also after loading), plus storage used and free (with free as a percentage); a **Resources** button on the right-hand bar shows and hides it (`[ResourceList] Shown`) | `ResourceList` |
| UI | Minimap shows the island (terrain colours, height, slopes, walls and buildings) under the units | `MinimapTerrain` |
| UI | Invasion: big warning before the next wave at 30 and 15 seconds, countdown from 5 | `WaveWarning` |
| UI | Invasion: an arrow on the screen points where the next wave will come from, 30 seconds before it | `WaveDirection` |
| UI | Respawn status under a dead worker's firefly (time left, missing energy) | `RespawnStatus` |
| UI | Energy hint above your home crystal when the firefly of a killed enemy or worker arrives ("+N energy") | `EnergyHint` |
| UI | Castle Story Plus logo on the splash screen, main menu and loading screen | `PlusLogo` |
| UI | **Ctrl+F**: search the world; matching bricktrons (name, job), enemies, buildings, blueprints and resources get a yellow ring (several searches with commas) | `WorldSearch` |
| UI | Fast startup: `[Startup] FastStartup = true` in the config (or launch option `-faststartup`) skips the logo screens at startup | `FastStartup` |
| Menus | **Continue** on the title screen loads the latest save | `ContinueButton` |
| Menus | **Save and return to main menu** and **Save and quit game** (with a confirmation) in the quit dialog | `SaveAndLeave` |
| Menus | Exit closes the game at once (main menu or in a game), no long wait while everything is torn down | `FastQuit` |
| Menus | New Invasion worlds: choose how often enemy waves come (every 5, 10, 15, 20 or 30 minutes), saved with the world | `WaveInterval` |
| Menus | Game speed keys in single player: **1** normal, **2** 2x, **3** 3x (`[GameSpeed]`) | `SpeedKeys` |
| Saving | Autosave every 2 minutes of play (`[Saving] AutosaveMinutes`, 0 = off), one autosave slot per map | `AutoSave` |
| Menus | **Castle Story Plus settings** in the in-game Settings menu: autosave interval, firefly energy, giant bricktron values, keys and feature switches | (always on) |
| Loading | Faster map loading that keeps the window responsive | `FasterLoading` |
| Loading | Game menus drawn in C# instead of the game's slow-to-build Lua menus, one switch per menu in `[LuaUi]` (all off by default; being built up step by step) | `CsMenus` |
| Debug | F9: system log (what every bricktron does, problems, the mod's log) | `SystemLog` |
| Debug | F8: debug menu (host only): energy, builders, healing, resources, enemies, invasion waves and the time between them, route view, 100% critrate | `DebugMenu` |
| Debug | F7: performance window: frame rate, frame times (1% low, p95, p99), garbage collections, frame time and frame rate graphs, what takes the time each frame (every Update of the game and the mod, measured from the first time the window opens) and the last spikes; frames over 100 ms (`[Performance] SpikeMs`) are logged with what took their time | `Performance` |

See [CHANGELOG.md](CHANGELOG.md) for details.

## Roadmap

Castle Story Plus will keep growing beyond quality-of-life fixes. Planned next:

- **New maps:** new islands and scenarios to build on and defend.
- **More enemies:** new enemy types and invasion waves that call for different defences.
- **New creatures that work for you:** new units that take on tasks such as hauling, gathering or building, next to the bricktrons.

These plans can change. Ideas and wishes are welcome as [issues](https://github.com/Tricky12321/CastleStoryPlus/issues).

## Reporting bugs

This is a fan mod in early development, so bugs will happen. Please report them as [GitHub issues](https://github.com/Tricky12321/CastleStoryPlus/issues) and include:
- your system (Windows or Linux) and the mod version;
- what you did and what happened;
- the log file `BepInEx/LogOutput.log` from the Castle Story folder.

Please do not report problems caused by the mod to Sauropod Studio. Turn the mod off first to check whether a problem also happens in the normal game.

## Installation

Download the package for your system from the [latest release](https://github.com/Tricky12321/CastleStoryPlus/releases/latest). Each package is complete: installer, BepInEx 5.4.23.5 and Castle Story Plus.

- **Windows:** `CastleStoryPlus-<version>-windows.zip`. Unpack it and double-click `install.bat`. BepInEx loads by itself on Windows (`winhttp.dll`).
- **Linux:** `CastleStoryPlus-<version>-linux.zip`. Unpack it and run `bash install.sh`.

The installer finds Castle Story in your Steam libraries, installs BepInEx if it is missing, installs Castle Story Plus and points BepInEx at the bootstrap. Run it again at any time to update.

**Linux launch option:** the game needs the Steam launch option `./run_bepinex.sh %command%`. If Steam is closed while the installer runs, it sets the option itself, in every Steam user's `localconfig.vdf`, and keeps a `.castlestoryplus.bak` backup. Otherwise set it once yourself: right-click Castle Story > Properties > Launch Options.

Without downloading a package first, you can also run the installer straight from the repository. It then downloads the latest package itself.
- **Linux:** `curl -fsSL https://raw.githubusercontent.com/Tricky12321/CastleStoryPlus/main/installer/install.sh | bash`
- **Windows (PowerShell):** `& ([scriptblock]::Create((New-Object Net.WebClient).DownloadString('https://raw.githubusercontent.com/Tricky12321/CastleStoryPlus/main/installer/install.ps1')))`

Installer options (`install.sh` / `install.bat`):

| Linux | Windows | Effect |
|---|---|---|
| `--tag v0.3.0` | `-Tag v0.3.0` | install a specific release |
| `--game-dir DIR` | `-GameDir DIR` | game folder, if it is not found automatically |
| `--force` | `-Force` | reinstall even if up to date |
| `--uninstall` | `-Uninstall` | remove Castle Story Plus (BepInEx and its config stay) |

### Updates

Every release is a version tag (`v0.1.0`). At startup the plugin asks GitHub for the releases. It picks the newest published release (not a draft or pre-release) that has a package for your system. If that release is newer than the installed plugin, the main menu shows a notice with **Update and restart**.

Update and restart first downloads the installer from that tag's source (`installer/` in the repository), falling back to the one shipped in `BepInEx/plugins/CastleStoryPlus/installer/`. The installer waits for the game to close, installs exactly that tag and starts the game again through Steam. The installer's output goes to `BepInEx/CastleStoryPlus.Update.log`. Turn the check off with `[Features] UpdateCheck = false`.

### Manual installation

1. Unpack **BepInEx 5.4.23.5** (`BepInEx_linux_x64_5.4.23.5.zip` or `BepInEx_win_x64_5.4.23.5.zip` from the [BepInEx releases](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5)) into the Castle Story folder (the one that contains `Castle Story_Data`).
2. Copy the release's `files/BepInEx` folder into the Castle Story folder.
3. Point BepInEx at the bootstrap:
   - **Linux:** in `run_bepinex.sh`, set `target_assembly="BepInEx/core/CastleStoryPlus.Bootstrap.dll"` and use the launch option above.
   - **Windows:** in `doorstop_config.ini`, set `target_assembly = BepInEx\core\CastleStoryPlus.Bootstrap.dll`.
4. Start the game. `BepInEx/LogOutput.log` should contain `Castle Story Plus <version> loaded`.

### Why a bootstrap?

Castle Story runs Unity's very old **Mono 2.6**. On Linux, MonoMod (the library behind HarmonyX) cannot change memory protection on this runtime, because its `mprotect` platform needs `Environment.SystemPageSize`, which Mono 2.6 does not have. On top of that, BepInEx's terminal fix resets the platform to `null`. As a result, every Harmony patch fails with a `NullReferenceException` in `NativeDetour`.

`CastleStoryPlus.Bootstrap.dll` is a small doorstop entry point that fixes this before BepInEx starts:
- it installs a MonoMod native platform that calls `mprotect` directly;
- it keeps that platform when BepInEx tries to reset it;
- it then starts the normal BepInEx preloader.

On Windows the bootstrap only starts BepInEx and changes nothing else.

The full story (the failing call chain, how the platform and the setter hook work, doorstop 3 and 4, and the assembly loading trap on Mono 2.6) is in [docs/BepInEx-on-Mono-2.6.md](docs/BepInEx-on-Mono-2.6.md).

## Configuration

The first start creates `BepInEx/config/com.tricky12321.castlestoryplus.cfg`. Edit it while the game is closed, or use the in-game settings window.

- `[Features]`: one `true`/`false` switch per feature (see the table above).
- `[Economy] EnergyMultiplier` (default `1.3`): energy per brewed firefly, relative to the blue crystal it costs.
- `[Saving] AutosaveMinutes` (default `2`): minutes of play between autosaves, `0` = off.
- `[Building] MoveStructureKey`, `CopyKey`, `PasteKey`: keys for moving buildings and copy/paste.

Most settings can also be changed in the game: **Settings > Castle Story Plus settings**. Changes there are written to the config file at once; `[Features]` switches apply after a restart.

Call to arms settings (soldiers per class) are stored in the game's own player preferences. Rally points are set in-game and last for the current map.

## Building from source

Requirements:
- the [.NET SDK](https://dotnet.microsoft.com/download) (8 or newer; the plugin targets .NET Framework 3.5 like the game);
- an installed copy of Castle Story with BepInEx;
- a copy of the **original** game assemblies in `Castle Story_Data/Managed.orig/`. The plugin compiles against these, and the [BepInEx assembly publicizer](https://github.com/BepInEx/BepInEx.AssemblyPublicizer) makes their private members reachable.

```
dotnet build -c Release
```

The build copies `CastleStoryPlus.dll` into the game's `BepInEx/plugins/CastleStoryPlus/`. If the game is not in the default Steam location:

```
dotnet build -c Release -p:GameDir="/path/to/Castle Story/"
```

### Testing and the developer API

Three projects next to the plugin, never part of a release (see [docs/DevTools.md](docs/DevTools.md)):
- `CastleStoryPlus.Tests`: unit tests of the mod's pure logic, no game needed: `dotnet test CastleStoryPlus.Tests`.
- `CastleStoryPlus.DevTools`: a plugin of its own (`BepInEx/plugins/CastleStoryPlus.DevTools/`) with an HTTP API on `http://127.0.0.1:27860/api` into the running game, and in-game tests (pathfinding, ...) that run in the game that is loaded.
- `CastleStoryPlus.Mcp`: an MCP server that gives Claude Code the API's tools (game state, log, screenshots, route searches, running the in-game tests). Registered in `.mcp.json`.

### Releasing

1. Bump `Version` in `CastleStoryPlus/Plugin.cs`, update `CHANGELOG.md` and commit.
2. Run `tools/release.sh`. It builds the plugin once and packs one package per system with `tools/package.sh`:
   - `dist/CastleStoryPlus-v<version>-linux.zip`: `install.sh` and BepInEx linux_x64;
   - `dist/CastleStoryPlus-v<version>-windows.zip`: `install.bat`, `install.ps1` and BepInEx win_x64.

   It then tags `v<version>` and creates the GitHub release with the [GitHub CLI](https://cli.github.com/). The release gets just the two packages and the install instructions from `tools/release-notes.md`.

Releases are built locally, because the build needs the game's own assemblies, which cannot go into the repository or CI.

## Project layout

```
CastleStoryPlus.Bootstrap/   doorstop entry point that makes HarmonyX work on Mono 2.6 (see above)
CastleStoryPlus/
  Plugin.cs            entry point: config, feature switches and Harmony setup
  Core/                feature attribute, Lua injection, IL helpers
  Workers/             per-worker data (XP, call to arms role): stored, saved and synced by the mod
  <Area>/              one folder per feature area (Army, Building, Combat, Economy, Experience,
                       Giant, Loading, Menus, Pathfinding, Respawn, Saving, UI, Updates, WorkerAI)
  Diagnostics/         modding aids, off by default (SceneDump, WorkerTrace)
  Lua/                 Lua UI files added by the mod (the mod's own code only)
  Assets/              artwork (logo.png, plus.png), rendered by tools/make_logo.py
CastleStoryPlus.DevTools/    developer plugin (not released): HTTP API into the game, in-game test runner and tests
CastleStoryPlus.Mcp/         MCP server (stdio) for Claude Code, calls the DevTools API
CastleStoryPlus.Tests/       unit tests of the mod's pure logic (NUnit, dotnet test)
installer/             install.sh (Linux), install.ps1 + install.bat (Windows): install and update from GitHub releases
tools/                 package.sh (Linux + Windows packages), release.sh (tag + GitHub release), release-notes.md, make_logo.py
CHANGELOG.md           all changes, in English
Directory.Build.props  shared build settings (target framework, game path)
```

## How it works

- **Patches:** each change is a small Harmony patch class tagged with the feature it belongs to. A feature switched off in the config is simply not patched.
- **Lua UI:** the game's own Lua files are not shipped. When the game loads one, the mod inserts its code at fixed anchors. It adds its own Lua files from `BepInEx/plugins/CastleStoryPlus/Lua/`, and gives Lua a `CastleStoryPlus` table of C# functions.
- **Per-worker data** (XP, call to arms role):
  - It is saved as extra properties on the game's existing `CharacterState` component, so saves stay loadable by the unmodded game. Saves made with the earlier prototype keep their XP.
  - It is synced by appending it to that component's network data.

## Multiplayer

The mod adds network data and commands, so every player in a game must run the same version of the mod.

## Disclaimer

This is an unofficial fan mod and is not affiliated with Sauropod Studio. Use it at your own risk and back up your saves.
