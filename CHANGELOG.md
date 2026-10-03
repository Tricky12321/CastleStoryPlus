# Changelog

All changes compared to the original Castle Story game. Every entry has one tag:
`[ADD]` new feature, `[CHANGE]` changed behaviour, `[FIX]` bug fix, `[REMOVE]` removed feature.

## 0.2.1 — 2026-10-03

- [ADD] **Release packages:** separate packages per system, `CastleStoryPlus-<version>-linux.zip` (`install.sh`, BepInEx linux_x64) and `CastleStoryPlus-<version>-windows.zip` (`install.bat` + `install.ps1`, BepInEx win_x64). BepInEx is bundled, so a fresh install needs no second download; the Windows installer still downloads BepInEx x86 for a 32-bit game.
- [CHANGE] **Installer:** picks the package for its own system from a release, falling back to the single zip of 0.2.0. It installs the release first and then BepInEx from the package, and it reinstalls when BepInEx is missing even if the plugin is up to date.
- [ADD] **Installer:** one-line install from the latest release: `curl -fsSL .../releases/latest/download/install.sh | bash` on Linux, and on Windows a stand-alone `install.bat` (it fetches `install.ps1` from GitHub when it is not next to it) or a PowerShell one-liner. Every release carries `install.sh`, `install.ps1` and `install.bat` as separate files, plus install instructions (`tools/release-notes.md`).
- [ADD] **Installer:** on Linux, the installer sets the Steam launch option `./run_bepinex.sh %command%` itself when Steam is closed. It edits only `Software/Valve/Steam/apps/227860/LaunchOptions` in every Steam user's `localconfig.vdf`, keeps existing options around `%command%`, and writes a `.castlestoryplus.bak` backup first.
- [CHANGE] **UpdateCheck:** reads the release list and offers the newest published version tag (not a draft or pre-release) that has a package for this system, instead of only GitHub's "latest" release.
- [CHANGE] **UpdateCheck:** "Update and restart" downloads that release's own `install.sh`/`install.ps1` first, so installer fixes already apply to the update, and falls back to the installer shipped with the plugin. The button cannot start two updates.

## 0.2.0 — 2026-10-03 — CastleStoryPlus plugin (BepInEx 5 + HarmonyX)

The mods are moved out of the edited game DLLs into a BepInEx plugin, so the original game files stay untouched. Each feature has an on/off switch in `[Features]` of `BepInEx/config/com.tricky12321.castlestoryplus.cfg`. Installation: see README.md.

- [ADD] **CopyPaste:** Ctrl+C (`[Building] CopyKey`) and drag an area with the left mouse button (yellow box) to copy everything in those columns, from 8 voxels below the lowest corner and up: placed blocks, your own buildings and your own pending blueprints. Rope bridges, loose blocks and carried or stored items are skipped. Right-click or Esc cancels; while dragging, the game's pickers stand by so no units get selected.
- [ADD] **CopyPaste:** Ctrl+V (`[Building] PasteKey`) shows the copy at the cursor as the game's compound blueprint (the one used for workshop blueprints, with support validation). Right-click rotates 90°, left-click places it into the selected build project (one is selected or created if needed). Obstructed pieces are skipped. The copy stays on the clipboard until the map changes.
- [ADD] **MoveStructure:** hold **M** (`[Building] MoveStructureKey`) and left-click one of your buildings (workshops, stockpiles, tool racks, nests; not wall blocks). Its blueprint follows the cursor like a normal blueprint; click to place, Esc cancels. The target cannot overlap the old building.
- [ADD] **MoveStructure:** the new blueprint and an anti-blueprint on the old building go into the selected build project. Workers demolish the old building, which drops its contents and the materials it cost, and build the new one, so a move costs work but no materials. Known limit: after a save and reload before the demolition, the materials are not dropped.
- [ADD] **MoveStructure:** new network command `CmdMoveStructure` (hand-registered, hash 1129595220).
- [ADD] **Installer:** `installer/install.sh` (Linux) and `installer/install.ps1` + `install.bat` (Windows) find Castle Story in the Steam libraries, install BepInEx 5.4.23.5 if missing, install the latest GitHub release (or `--tag`/`-Tag`) and point BepInEx at the bootstrap. Running them again updates; `--uninstall`/`-Uninstall` removes the mod. The installed version is written to `plugins/CastleStoryPlus/version.txt`.
- [ADD] **UpdateCheck:** at startup the plugin asks the GitHub API for the latest release (through Unity's `WWW`, since Mono 2.6 cannot do TLS 1.2). If it is newer, the main menu shows a notice with **Update and restart**, which runs the shipped installer: it waits for the game to close, installs the release and starts the game again through Steam (log in `BepInEx/CastleStoryPlus.Update.log`).
- [ADD] `tools/package.sh` packs `dist/CastleStoryPlus-v<version>.zip` (installers plus `files/BepInEx/...`); `tools/release.sh` tags the version and creates the GitHub release.
- [ADD] **PlusLogo:** a yellow "PLUS" badge next to every copy of the game logo in the main menu, in the style of the game logo (Luckiest Guy font, Apache 2.0, rendered by `tools/make_logo.py`).
- [ADD] **PlusLogo:** the full Castle Story Plus logo under the Unity logo on the splash screen, fading with it (`Assets/logo.png`, since the game logo is not loaded yet then).
- [ADD] **PlusLogo:** the game logo with the badge on the loading screen's top curtain, above "Loading", sliding with the curtain. The game logo is not redistributed: its texture is reused at runtime. The artwork uses mipmaps and trilinear filtering, so it stays smooth when scaled down.
- [ADD] **ResourceList:** resources that have been stocked once stay in the list for the rest of the session; name and count turn red at 0.
- [ADD] **ResourceList:** two rows above the resources, **Storage used** (`used / total`) and **Storage free**, in the game's encumbrance units summed over all your stockpiles. Free room turns orange at 90% full and red when full. New Lua functions `CastleStoryPlus.StorageUsed()` and `CastleStoryPlus.StorageCapacity()`.
- [ADD] **WorkerTrace** (`[Debug]`, off by default): logs every worker state change with timestamps to `BepInEx/workertrace.log`, to find where workers lose time between tasks.
- [ADD] **SceneDump** (`[Debug]`, off by default): writes the object hierarchy to `BepInEx/scenedump/` on every scene load, as a modding aid.
- [ADD] **Lua injection:** text patches at anchors in the game's Lua files, the mod's own Lua files from the plugin folder, and a `CastleStoryPlus` Lua table for C# callbacks.
- [ADD] **Per-worker data without new fields:** XP and call to arms role live in a side table keyed by `CharacterState`, are saved as extra JSON properties on that component (`workXp`, `combatXp`, `callToArmsRole`, same names as the prototype) and are synced by appending to `CharacterState.OnSerialize`/`OnDeserialize`.
- [ADD] New project `CastleStoryPlus/`, built against the original assemblies (`Managed.orig`) with the BepInEx assembly publicizer for access to private members. A build copies the plugin into the game.
- [ADD] New project `CastleStoryPlus.Bootstrap`, the doorstop target, which makes HarmonyX work on Castle Story's Mono 2.6. Without it every patch failed with a `NullReferenceException` in `NativeDetour`: MonoMod's libc platform needs `Environment.SystemPageSize`, which Mono 2.6 lacks, and BepInEx's XTermFix resets `DetourHelper.Native` to `null`. The bootstrap installs an `mprotect`-based native platform, keeps it through a hook on the setter and then starts the BepInEx preloader. Works with doorstop 3 and 4.
- [CHANGE] The bootstrap only installs its libc detour platform on Linux/macOS, so it can also run on Windows.
- [CHANGE] Plugin GUID is now `com.tricky12321.castlestoryplus` (config file renamed to match).
- [CHANGE] The game runs on its original DLLs and Lua files again; all prototype features are ported to the plugin (next entries).
- [CHANGE] **Economy:** `EnergyMultiplier` (default 1.3).
- [CHANGE] **WorkerAI:** LookForWork every 2 ticks instead of 10 with 2-tick waits, tick offset 1, a worker becomes a free agent after a direct order, and locked workers help other task groups (raids excluded) and return to their own group.
- [CHANGE] **BuildJobs:** covered build goals are skipped; the pick-up amount is the remaining need minus what other workers carry (1–20).
- [CHANGE] **StockpileChoice:** smaller pickup/store bonuses, and stores are scored by the stored resource.
- [ADD] **StockpileConsolidation:** idle workers consolidate stockpiles.
- [CHANGE] **Pathfinding:** A* weight 33 instead of 45, the improved path is kept, and search budgets for free agents and build projects are 100.
- [CHANGE] **ArcherAccuracy:** homing bricktron arrows with line of sight, and no friendly fire from arrows.
- [ADD] **CallToArms:** quotas per class, rally points, per-worker roles and the settings window, with the settings button injected into the right-hand bar and two new network commands.
- [ADD] **Experience:** work and combat XP, levels, damage and work speed bonuses, level in the name tag, XP bars and level-up effect.
- [ADD] **Eyedropper**, **RespawnStatus**, **ContinueButton** and **SaveAndLeave** (including extra dialog buttons).
- [CHANGE] **BlueprintsVisible:** pending blueprints are always visible to their own faction.
- [ADD] **ResourceList:** the Lua resource list is injected into `GameMenu.lua`.
- [REMOVE] **ResourceList:** the old resource grid is removed from `Panel_Storage.lua`.
- [CHANGE] **FasterLoading:** per-frame polling instead of 1-second waits, uncapped progress bar, saved blocks, game objects and nature read on a background thread, time-budgeted object creation, tree loading and terrain gravity, 1500 blocks per yield, and terrain chunks no longer wait for colliders (plus the matching worker wake-up fix). Not ported: the time budgets for pathfinding init (`PathHigherLod`, `TestPathfinding`) and the `[LoadTiming]` log.

## 2026-10-03 — prototype: edits in the decompiled DLLs (replaced by the plugin)

- [ADD] `CastleStory.sln` with all six projects.
- [ADD] `Directory.Build.props`: targets net35 with C# 14, enables unsafe code, and points `GameManagedDir` at the Steam install.
- [CHANGE] The `.csproj` files reference each other through `ProjectReference` and the game DLLs through `$(GameManagedDir)`.
- [FIX] `GeneratedNetworkCode` compiles: duplicate array reader/writer methods removed, `TrackingPair` construction fixed, calls go through new `SerializeItemInternal` / `DeserializeItemInternal` wrappers.
- [FIX] `BeaconDriver` and `TunnelNetworkDeserialization`: `protected override` access fixed and `internal` wrappers added.
- [FIX] `BearTrapTrigger.TrapState` is public.
- [FIX] `BitsEnumerator`: field initialisers moved into the constructor.
- [FIX] Rebuilt from IL the methods ILSpy could not decompile: the `AutonomyStatus` and `CatapultShootStatus` static constructors, `CursorController.SetUpStates` and `SetUpAttackState`, `CraftingInstructions.CookRecipeNode` and `ObjectActivator.Operate`.
- [FIX] Crash when starting an invasion map: Unity's old Mono JIT asserted (`local-propagation.c:102`) on the `fixed (char*)` code in `LuaTimer.AssignTimeToString`, which is rewritten without unsafe code. Builds are done in Release.
- [FIX] Race in `ThreadedHeavyWorker.MainThreadWork`: the worker thread is only woken after it has reserved a megavoxel.
- [CHANGE] Workers start work on their own: after a direct order they become free agents (previously InstinctOnly), and when their own project group has no work left they help other projects in priority order (raid projects excluded).
- [CHANGE] Workers start their next task faster: LookForWork interval 10 → 2 ticks, inner waits 10 → 2 ticks, labor tick offset 2 → 1.
- [CHANGE] Several workers no longer take the same build job: a build goal counts as covered once other workers already carry enough material for it.
- [CHANGE] Workers no longer carry more material than needed: the pick-up amount is the remaining need minus what others carry, clamped to 1–20.
- [CHANGE] Stockpile choice favours the nearest stockpile, through new `PickupBonuses` weights.
- [FIX] `StoreDistanceBonusFor` scored distance wrongly.
- [CHANGE] A* heuristic weight 45 → 33, which gives fewer detours.
- [FIX] The pause after picking something up: the improved path is kept instead of being re-improved after the search (`SearchPathRequest.WrapUpSearch`).
- [CHANGE] Search budgets for free agents and build projects 40 → 100.
- [CHANGE] Energy for new workers arrives 30% faster (`Brewery.EnergyMultiplier = 1.3`).
- [CHANGE] Pending blueprints are always visible to their own faction, not only in build mode.
- [ADD] **SAVE & LEAVE** button in the "Quit Game" dialog: overwrites the loaded manual save, otherwise creates a new manual save named `<map> yyyy-MM-dd HH-mm`. Only shown to the host in multiplayer.
- [ADD] **CONTINUE** button below Play on the title screen: loads the newest single-player save (manual, quick or autosave) that matches the game version, like picking it in Load Game; a dialog says so if no save exists. It is a runtime clone of the Play button (`ContainerGenericPopulator.AddContinueButton`, new `Operation.ContinueGame` appended to `LegacyBaseMenu.Operation`).
- [CHANGE] Generic dialogs duplicate their button template when a dialog needs more buttons than the prefab has.
- [ADD] Eyedropper: a middle-click (short, without dragging) on a placed block, structure or blueprint selects that item for building (`Brix.Input/Eyedropper.cs`). Dragging the middle button still rotates the camera. Column and ramp pieces map to the item the build menu offers. Selects the last build project, or creates one. Natural terrain, rope bridge planks and shared multi-block voxels are ignored.
- [CHANGE] Bricktron archers always hit a target in line of sight: no inaccuracy, and the arrow homes in on the target (steered on the server).
- [CHANGE] Arrows never damage allied units, and homing arrows ignore anything other than their target.
- [ADD] **Call to arms settings** window, opened from a flag button next to the call to arms button in the right-hand bar (`Brix.UI.CallToArmsUI/CallToArmsPanel.cs`).
- [ADD] Call to arms soldiers per class (Knights, Halberdiers, Archers, Arbalists, Alchemists, Artificers): only that many workers are called, soldiers already in a kit count towards it, each takes its class kit from the nearest rack that holds one (`ToolNode.GoEquipKitFromRack`), a number never exceeds the sets owned, all 0 works as before, stored in `PlayerPrefs`.
- [ADD] Call to arms rally points: one for ranged and one for melee soldiers, placed with a left click (right click or Esc cancels), shown as a blue or red flag for the current map. On the alarm soldiers run to their point, otherwise to the crystal as before.
- [ADD] Per-worker call to arms role (Auto, Stay at work, or one of the six classes): fixed classes go first and count towards the number, Auto fills the rest, Stay at work is never called; a class is never given to more workers than there are sets. Saved as an extra `callToArmsRole` property on `CharacterState` and synced to clients.
- [ADD] Network commands `CmdCallToArmsRally` and `CmdSetCallToArmsRole`.
- [ADD] Respawn status under a dead bricktron's firefly (`Firefly.RespawnStatus`, shown by `FireflyOverheadDisplayDriver`): `flying home, ~N s`, `respawn in N s` (25-second absorption), `needs N energy`, `respawning`. Clients run their own absorption timer, so their countdown can be slightly off.
- [ADD] Idle workers consolidate stockpiles: when no project has work, they move a resource from its least-filled stockpile into the one holding the same resource with the least free room, only when the target holds at least as much. One worker per stockpile, claims expire after 60 seconds, and the chore can be interrupted until something is picked up (`StockpileConsolidation`, hooked into `IdleProject`).
- [ADD] Resource list in the top-right corner, left of the right-hand icon bar: icon, name and count per stocked resource (`LUI/Panels/Gamemodes/Panel_StorageList.lua`, mounted in `LUI/Menus/GameMenu.lua`). Modded Lua lives in `GameLua/`, originals in `GameLua.orig/`.
- [REMOVE] Old resource icon grid under the minimap (`Panel_Storage.lua`); the "build more stockpiles" alert is kept.
- [ADD] Separate **work** and **combat** experience: work XP 1 per finished task, combat XP 1 per hit and 5 per kill. Levels 1–10 at total XP 20 / 50 / 90 / 140 / 200 / 270 / 350 / 440 / 540, each +5% work animation speed or melee and ranged damage. The game's own `CharacterLevel` is untouched.
- [ADD] Name tag shows the levels (e.g. `Bob [Lv 3/2]`) with two thin XP bars below: work (blue) and combat (red).
- [ADD] Level-up effect: "LEVEL UP!" rises from the name tag, which pulses and flashes gold, with the task-completed sound. Runs on every peer; large jumps such as loading a save do not trigger it.
- [ADD] XP saved as `workXp` and `combatXp` on `CharacterState` (saves stay loadable by the unmodded game; old saves load with 0 XP) and synced to clients, so modded and unmodded clients cannot play together.
- [CHANGE] Faster map loading: no 1-second polling waits, no cap on progress steps, terrain chunks do not wait a frame for their collider, more chunks synced per frame, more blocks per yield.
- [FIX] Loading froze the window ("application not responding"): loading loops yield on a 30 ms frame budget (`FrameBudget`) for terrain gravity, trees, pathfinding init and placed blocks; nature data, placed blocks and saved game objects are read and parsed on a background thread (`BackgroundTask`); saved objects are created a few per frame with game time frozen.
- [ADD] Each loading step logs its duration and longest frame to `Player.log` with the prefix `[LoadTiming]`.
