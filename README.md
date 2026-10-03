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
| Worker AI | No duplicate build jobs, no carrying more material than a blueprint still needs | `BuildJobs` |
| Worker AI | Workers prefer the nearest stockpile | `StockpileChoice` |
| Worker AI | Idle workers consolidate small stockpiles into the fullest one | `StockpileConsolidation` |
| Pathfinding | Shorter routes, bigger search budgets, no pause after picking something up | `Pathfinding` |
| Combat | Archers always hit visible targets, and arrows never hurt allies | `ArcherAccuracy` |
| Call to arms | Soldiers per class (limited by the sets you own), ranged and melee rally points, a role for each worker | `CallToArms` |
| Experience | Separate work and combat XP, levels 1–10 with +5% per level, shown in the name tag with XP bars and a level-up effect | `Experience` |
| Building | Eyedropper: middle-click a block or building to build a copy | `Eyedropper` |
| Building | Pending blueprints are always visible | `BlueprintsVisible` |
| Building | Move buildings: hold **M** and click a building, then place it; workers demolish the old one and build the new one from its materials (`[Building] MoveStructureKey`) | `MoveStructure` |
| Building | Copy and paste: **Ctrl+C** and drag an area to copy its blocks, buildings and blueprints; **Ctrl+V** pastes them as blueprints (right-click rotates) (`[Building] CopyKey`, `PasteKey`) | `CopyPaste` |
| UI | Resource list (icon, name, count) in the top-right corner; used-up resources stay listed in red, plus storage used and free | `ResourceList` |
| UI | Respawn status under a dead worker's firefly (time left, missing energy) | `RespawnStatus` |
| UI | Castle Story Plus logo on the splash screen, main menu and loading screen | `PlusLogo` |
| Menus | **Continue** on the title screen loads the latest save | `ContinueButton` |
| Menus | **Save & Leave** in the quit dialog | `SaveAndLeave` |
| Loading | Faster map loading that keeps the window responsive | `FasterLoading` |

See [CHANGELOG.md](CHANGELOG.md) for details.

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

## Configuration

The first start creates `BepInEx/config/com.tricky12321.castlestoryplus.cfg`. Edit it while the game is closed.

- `[Features]`: one `true`/`false` switch per feature (see the table above).
- `[Economy] EnergyMultiplier` (default `1.3`): energy per brewed firefly, relative to the blue crystal it costs.

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
                       Loading, Menus, Pathfinding, Respawn, UI, Updates, WorkerAI)
  Diagnostics/         modding aids, off by default (SceneDump, WorkerTrace)
  Lua/                 Lua UI files added by the mod (the mod's own code only)
  Assets/              artwork (logo.png, plus.png), rendered by tools/make_logo.py
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
