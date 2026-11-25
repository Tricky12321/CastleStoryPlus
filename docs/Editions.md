# Editions: Full and Essentials

Castle Story Plus comes in two editions, built from the same source and released together from the same tag, with the
same version number:

- **Castle Story Plus** (Full): everything in [Features](Features.md).
- **Castle Story Plus - Essentials**: only what makes the game do better what it already tries to do: bug fixes,
  leak, frame and loading work, crash and desync fixes, and the worker AI, task and pathfinding improvements. No new
  buildings, blocks, resources, items, units, enemies, weather or world content, and no balance or gameplay changes.
  For players who want vanilla Castle Story that works better (#183, #184).

What is left out of Essentials is not switched off by a setting: it is not compiled into it at all, and its Lua, icons,
textures and models are not in its package.

## What decides In or Out

- **In**: bug fixes; performance (leaks, frame time, loading, saving, quitting); crash and desync fixes; worker AI,
  tasks, pathfinding and getting unstuck; required infrastructure (the unnamed `[Feature]` classes, the C# menus that
  replace the game's Lua menus, the DevTools hooks, the updater); multiplayer fixes.
- **Out**: new content (buildings, blocks, resources, items, units, enemies, world generation, weather, achievements)
  and balance or gameplay changes (energy, accuracy, yields, costs, crew sizes, vision, speeds), and pure conveniences
  of the interface that fix nothing (copy and paste, move, speed keys, minimap zoom, autosave, extra information
  panels...).
- **(borderline)** marks a feature that could be argued either way; it was put where the rule above leans.

The unnamed infrastructure classes (`[Feature]` without a name: save, network and engine fixes the features rely on)
are in both editions, except the ones that only serve an Out feature (in the files Editions.targets leaves out).

## Building

```
dotnet build CastleStoryPlus -p:UnityTarget=player -p:Edition=Essentials
dotnet build CastleStoryPlus.UI -p:UnityTarget=player -p:Edition=Essentials
```

- `Edition` is an MSBuild property (Directory.Build.props): `Full` (the default, the build as it always was) or
  `Essentials`. Essentials defines the constant `ESSENTIALS` and builds into `obj/<target>/essentials/` and
  `bin/<target>/essentials/`, so the two editions never share build output.
- [Editions.targets](../Editions.targets) lists what Essentials leaves out: whole folders and files of Out features
  (`<Compile Remove>`) in the mod, the menu assembly, the developer tools and the local tests, and the Assets the
  package must not carry (Essentials ships only `logo_essentials.png` and `plus_essentials.png`; Full does not ship
  those two). Code of Out features inside files that stay is under `#if !ESSENTIALS`; namespaces of left-out folders
  are declared empty in `Core/EditionNamespaces.cs` so the using directives can stay.
- `tools/check-editions.py` checks, without the game, that every feature has a row below and that no `[Feature]`
  class of an Out feature is compiled into Essentials. CI runs it with a compile of the unit tests' logic in the
  Essentials edition (`.github/workflows/editions.yml`); the full compile needs the game's assemblies and is done
  locally.
- In-game tests (DevTools) of Out features are not in the Essentials build of the developer tools, so a test run in
  Essentials runs only the tests of what it has.

## Packages and updates

`tools/package-port.sh` (run by `tools/release.sh`) packs both editions from one player build per system:
`CastleStoryPlus-v<version>-linux.zip` / `-windows.zip` (Full, the names releases always had) and
`CastleStoryPlus-Essentials-v<version>-linux.zip` / `-windows.zip`. The two differ only in
`BepInEx/plugins/CastleStoryPlus`: the plugin, its menu assembly, its Assets and `edition.txt` (`Full` or
`Essentials`). The Essentials README says which edition it is. `release.sh` uploads the Full zips first and the
Essentials zips after them, because installers before 0.10.0 take the first `CastleStoryPlus-*-<os>.zip` of a release.

Updates stay within an edition: the in-game update check only offers a release that has this edition's zip
(`Edition.PackagePrefix`), and passes the edition to the installer (`--edition` / `-Edition`). The installer takes the
edition from that option, else from the installed `edition.txt` (a release before 0.10.0 has none: Full), and downloads
only that edition's zip. `install.sh --edition essentials` (`install.bat -Edition Essentials`) switches an install over.

## Identity and settings

- Essentials is called **Castle Story Plus - Essentials** in the log, the settings window, the update notice, the
  system report and BepInEx's plugin list, and shows its own logo and PLUS ESSENTIALS badge (the badge's width follows
  its texture's aspect, so the taller artwork is placed like the Full badge).
- Both editions have the same plugin GUID (`com.tricky12321.castlestoryplus`): Essentials replaces Full in the same
  folder and uses the same config file, `BepInEx/config/com.tricky12321.castlestoryplus.cfg`. Switches and settings
  of features Essentials does not have stay in the file untouched (BepInEx keeps entries it does not know) and apply
  again when Full is installed back. `[Economy] EnergyMultiplier` is not read by Essentials.

## Multiplayer

Full and Essentials players cannot play together: Essentials has none of Full's buildings, items and messages, so the
worlds would differ at once. The host sends its edition and version with the cheat setting it sends every few seconds
(CheatGuard's message 207; the fields come after the setting, so players of older versions read the setting and stop).
A client of the other edition leaves the lobby or game at once and shows a window saying which edition the host
plays and that every player needs the same one; it also tells the host (message 215), whose chat then says why the
player left. A host before 0.10.0 sends no edition, so nothing is checked against it. With the CheatGuard feature
switched off on the host no edition is sent either.

## Saves

- A save made with Essentials loads in Full.
- A save made with Full loads in Essentials. What Essentials does not have is left out of the world, with one log
  line per kind:
  - blocks of a type it does not know (the custom bricks, slabs, ladders, roads) are skipped by `UnknownBlocks`;
  - game objects whose factory key it does not know (market, warehouse, large stockpile, research station, smithy,
    armoury, saplings, coal, steel, dark crystals, kit bundles...) are skipped one by one by the game's loader, which
    loads every saved object on its own; `UnknownObjects` (Essentials only) writes which kind was left out and why;
  - the treasure chests' guardian beasts are ordinary enemy units in the save; Essentials takes them away once they
    are loaded (`FullSaveGuards`), so no guardians spawn (#179, #182). The ruins are ordinary stone blocks of the
    world and stay; the chests are not in the save;
  - the mod's own files next to a save (weather, treasure, research, market...) are ignored;
  - workers' stats the Full edition saves (experience, giant tier, soldier posts) stay on the units unused.
  What was stored in a left-out building is lost with it in that game; saving the game in Essentials makes that
  final. Nothing of it crashes the load.
- In Full, `SavedContent` adds these buildings, blocks and items to the game's factories even with their feature
  switched off, so a Full save keeps them (#185). Essentials does not compile that content, so it cannot add them;
  it skips them as above.

## Features

| Feature | Edition | Why |
|---|---|---|
| `Achievements` | Out | New content |
| `AllyWorkerIcons` | In | Multiplayer bug fix |
| `ArcherAccuracy` | Out | Balance: archers always hit |
| `ArcherRetarget` | In | Combat AI: archers no longer aim at a target they cannot hit |
| `ArtificerHealing` | Out | A new ability |
| `AssignNow` | In (borderline) | AI fix: assigned bricktrons start at once (with a reason when they cannot) |
| `AutoCleanup` | Out | A new chore for idle workers |
| `AutoRepair` | Out | A new chore for idle builders |
| `AutoSave` | Out | A new convenience |
| `BaseArea` | In | Worker AI fix: workers' own material and storage searches keep to the base (#181) |
| `BaseJobs` | Out | Builds on AutoCleanup and AutoRepair |
| `BlockAnchor` | Out | A new building control |
| `BlueCrystalDeposits` | Out | New resource deposits |
| `BlueprintsVisible` | Out (borderline) | A display preference, not a fix |
| `BuildBlocked` | Out | A new information display |
| `BuildJobs` | In | Worker AI: no duplicate build jobs, full loads, floor-by-floor building |
| `BuildNeeds` | Out | A new information display |
| `BuildPriority` | Out | A new building control |
| `BuildShortage` | Out | A new information display |
| `CallToArms` | Out | A new army control |
| `CameraMouseGuard` | In | Bug fix: the camera no longer scrolls off on its own |
| `CameraZoomOut` | Out | Camera range beyond the game's |
| `CheatGuard` | In | Multiplayer: cheats only when the host allows them |
| `ContinueButton` | Out | A new title screen button (convenience) |
| `CopyPaste` | Out | A new building tool |
| `CraftFinishFix` | In | Bug fix: finished orders leave the queue |
| `CraftLoopLimit` | Out | A new workshop control |
| `CraftRoundRobin` | In (borderline) | AI: a looping queue no longer stalls on one missing ingredient |
| `CriticalHits` | Out | A new combat mechanic |
| `CrowdPathing` | In | Pathfinding: units climb beside an ally instead of waiting below it |
| `CsMenus` | In | Infrastructure: menus drawn in C# (Lua UI port) |
| `CustomBlocks` | Out | New blocks |
| `DarkCrystals` | Out | A new resource |
| `DebugMenu` | In (borderline) | Diagnostics (debug mode only, cheats guarded in multiplayer); the in-game tests use it |
| `DeconstructColour` | In (borderline) | Fixes the deconstruct colour the game loses when the task is not selected |
| `Demolish` | Out | A new building task (demolition with the materials given back) |
| `DesyncProtection` | In | Multiplayer fix: worlds that drift apart are corrected |
| `DirectTask` | Out (borderline) | A new order (select bricktrons and right-click a task) |
| `DoorRepair` | In (borderline) | Bug fix: repair zones skipped damaged doors |
| `DragRelease` | In | Bug fix: a drag build stops when the mouse button is let go |
| `DropGearOnDeath` | Out (borderline) | Balance: gear is no longer lost |
| `Economy` | Out | Balance: changes the energy a firefly carries |
| `EmptyStockpile` | Out | A new storage control |
| `EnemyTreeClearing` | In (borderline) | Enemy AI: waves are no longer shut out by trees they cannot route through |
| `EnergyHint` | Out | A new information display |
| `Experience` | Out | A new progression system |
| `Eyedropper` | Out | A new building shortcut (convenience) |
| `FasterLoading` | In | Loading performance |
| `FasterTaskSearch` | In | Worker AI performance in big task areas |
| `FastQuit` | In | Performance: quitting no longer takes long |
| `FastSave` | In | Performance: saving no longer freezes the game |
| `FastStartup` | In (borderline) | Loading: a launch option that skips the logo screens |
| `FocusInputGuard` | In | Bug fix: alt+tab no longer opens the build menu |
| `FrameSavers` | In | Performance: less CPU time per frame |
| `GameChat` | Out | Changes the chat |
| `GameLeakFixes` | In | Bug fix: memory leaks in the game |
| `GiantBricktron` | Out | A new unit |
| `GlobalBuildJob` | In (borderline) | Bug fix: placing with the idle group or repair task selected no longer fails |
| `GuardianSounds` | Out | Sounds of the treasure guardians |
| `Healtron` | Out | A new enemy |
| `HiddenDeliveries` | In | Multiplayer bug fix |
| `IdleLog` | In | Diagnostics |
| `ItemHint` | Out | A new information display |
| `JobBoard` | In | Worker AI performance: idle workers skip tasks that just had nothing |
| `KitBundles` | Out | New items |
| `KitReservation` | In | AI: no two bricktrons run to the same last kit |
| `Ladders` | Out | A new building |
| `LargerTasks` | Out | Task areas larger than the game allows |
| `LargeStockpile` | Out | A new building |
| `LauncherOptions` | In (borderline) | Bug fix: the launcher forgot its resolution and fullscreen choice |
| `LeanCoal` | Out | Balance (and coal comes from Metallurgy) |
| `LoadRelief` | In | AI: workers no longer freeze with a load nothing can store |
| `LobbyStart` | In | Multiplayer bug fix: the lobby hung at 0 % loaded |
| `LuaHeapShrink` | In | Performance: shorter garbage collection pauses |
| `LuaStackShrink` | In | Performance: shorter garbage collection pauses |
| `Market` | Out | A new building |
| `MaxPlayers` | Out (borderline) | More players than the game allows |
| `MenuTextFields` | In | Port bug fix: the lobby's text boxes |
| `Metallurgy` | Out | New resources (coal, steel) |
| `MineShafts` | Out | New world content |
| `MinimapTerrain` | Out | A new minimap display |
| `MinimapZoom` | Out | A new minimap control |
| `MixedRacks` | Out | Changes how weapon stands work |
| `MixedStockpiles` | Out | Changes how storage works |
| `MoveStructure` | Out | A new building tool |
| `NoGoZones` | Out | A new player control: zones drawn for routes to keep out of (#200) |
| `OreMine` | Out | A new task |
| `PassThroughAllies` | In | Pathfinding: allies no longer block routes or doorways |
| `Pathfinding` | In | Route search fixes and budgets: units find the routes the game already looks for |
| `PauseTasks` | Out | A new task control |
| `Performance` | In | Diagnostics: the performance window (debug mode only) |
| `PickUpAround` | In (borderline) | AI: the game's own pick up order no longer sends everyone to one item |
| `PlayerOrdersFirst` | In | AI fix: a player's order is no longer overridden by instinct |
| `PlusLogo` | In | Identity: the edition's logo and version (Essentials uses its own artwork) |
| `PriorityLevels` | Out | New task levels |
| `QuarryDepth` | Out | Changes quarries |
| `QuarryLimit` | Out | A new task control |
| `QuarrySpeed` | Out | Balance: faster digging |
| `QuickNextTask` | In (borderline) | Worker AI: a shorter wait between tasks |
| `RackFilter` | Out | A new building setting |
| `Rain` | Out | New weather content |
| `RandomWorld` | Out | A new map generator |
| `RangedGuard` | Out | A new order |
| `RangedReposition` | In (borderline) | Combat AI: a blocked shooter steps to a clear shot instead of standing still |
| `RenderTuning` | In (borderline) | Performance: shorter shadow distance (a visible change) |
| `ResourceList` | Out | A new resource display |
| `ResourcePriority` | Out | A new gameplay control |
| `ResourceReservation` | In | Worker AI: no two workers fetch the same items |
| `RespawnCost` | Out | Balance: a flat respawn price |
| `RespawnStatus` | Out | A new information display |
| `Retreat` | Out | A new combat behaviour |
| `RichIron` | Out | Balance |
| `Roads` | Out | A new building |
| `RoofItems` | Out | Only for the mod's own buildings |
| `RouteOverlay` | In (borderline) | Diagnostics of pathfinding and getting unstuck: where a selected unit walks, climbs or is stuck (off until shown) |
| `Saplings` | Out | A new building and task |
| `SaveAndLeave` | Out | New dialog buttons (convenience) |
| `SavedTeams` | In (borderline) | Multiplayer fix: allies no longer start as enemies in a loaded save |
| `SidePlanks` | Out (borderline) | Changes the building support rules |
| `SoldierPosts` | Out | A new order |
| `SpawnDistance` | Out | Balance: where waves spawn |
| `SpeedKeys` | Out | New controls |
| `Sprint` | Out | A new ability |
| `SteamRelay` | In | Multiplayer fix: the game's NAT server is gone |
| `StockpileChoice` | In | Worker AI: the nearest stockpile instead of a far, fuller one |
| `StockpileConsolidation` | Out (borderline) | A new chore for idle workers |
| `StockpilePickup` | In | Bug fix: fetching no longer fails when the chosen item was taken |
| `StockpileSpill` | Out (borderline) | Balance: demolished storage keeps its contents |
| `StockpileTooltip` | Out | A new information display |
| `StuckEscape` | In | Worker AI: a bricktron shut in a hole digs its way out |
| `StuckReset` | In | AI failsafe: hanging workers start afresh |
| `SystemLog` | In | Diagnostics (debug mode only) |
| `TaskCrewSize` | Out (borderline) | Changes the game's crew size (8 to 15) |
| `TaskFlow` | In | Worker AI: shorter trips, the next task at once |
| `TaskPrediction` | In | Worker AI: the next task of the same area at once |
| `TaskReservation` | In | Worker AI: no two workers walk to one task |
| `Treasure` | Out | New world content (chests, ruins, beasts) |
| `TreasureGuardians` | Out | Part of Treasure (the chests' guardian packs) |
| `TreasureRuins` | Out | Part of Treasure (the castle ruin round each chest) |
| `TreeStumps` | Out | New harvest content |
| `UnarmedWorkers` | In (borderline) | AI: workers without gear no longer charge enemies on their own |
| `UnloadToWorkshops` | In (borderline) | Worker AI: a full bag goes to the workshop that needs it |
| `UnreachableTasks` | In | Worker AI: tasks nobody can reach are not walked to again and again |
| `UnreachableThought` | Out (borderline) | A more detailed thought text |
| `UpdateCheck` | In | Infrastructure: the updater (Essentials updates to the Essentials zips) |
| `Upgrades` | Out | New buildings and research |
| `WallLean` | Out (borderline) | Changes what archers can reach |
| `WardAnimations` | In (borderline) | Less on screen: optional switches (all off by default) that calm or thin out the wards' animations and particles, like RenderTuning's lighter picture |
| `WardRangeFade` | Out | A visual preference |
| `Warehouse` | Out | A new building |
| `WarlockSummons` | In (borderline) | Bug fix (#161): a wave is over only when its summons are dead |
| `WaveDirection` | Out | A new information display |
| `WaveInterval` | Out | A new game setting |
| `WaveWarning` | Out | New warnings |
| `WiderVision` | Out | Balance: units see further |
| `WildPlants` | Out | New world behaviour |
| `WoodOverhang` | Out | Changes the building support rules |
| `WorkerAI` | In | Worker AI: starts work, helps other groups, picks targets by walking distance |
| `WorkLimit` | Out | A new task control |
| `WorkshopFetchRange` | Out | A new workshop control |
| `WorkshopPriority` | Out | A new workshop control |
| `WorkshopSetup` | Out | A new workshop control |
| `WorkSpotReserve` | In | Bug fix: blueprints on a workshop's work spot were blocked for good |
| `WorldSearch` | Out | A new tool |
| `WornPaths` | Out | New world behaviour |

In: 65 features. Out: 100 features.
