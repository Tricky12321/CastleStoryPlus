# Features

Each feature has a switch in the `[Features]` section of the config file (see [Configuration](Configuration.md)). Settings in other sections are named next to the feature, e.g. `[Economy] EnergyMultiplier`. See [CHANGELOG.md](../CHANGELOG.md) for every detail, and the [gallery](Gallery.md) for pictures of every building, block, armour stand and stockpile.

> **Status:** every feature has been ported from the earlier decompiled-game prototype to this plugin and loads without errors. They still need play-testing in the plugin form.

## Contents

- [Worker AI](#worker-ai) · [Economy and resources](#economy-and-resources) · [Mining and metallurgy](#mining-and-metallurgy) · [Nature](#nature)
- [Building](#building) · [Storage](#storage) · [Workshops, market and research](#workshops-market-and-research)
- [Army and combat](#army-and-combat) · [Workers and experience](#workers-and-experience) · [Pathfinding](#pathfinding)
- [UI](#ui) · [Menus, saving and loading](#menus-saving-and-loading) · [Performance](#performance) · [Debug tools](#debug-tools)

## Worker AI

Workers find, share and finish their work on their own.

| Feature | Switch |
|---|---|
| Workers start work on their own, pick up their next task faster, help other task groups when theirs is done, and only dig, harvest or clean up what they can store all of. | `WorkerAI` |
| Workers start their next task right away, fetch materials from the stockpile that makes the shortest trip, and store a full bag straight after digging, felling or harvesting (after digging also when none of the area's blocks fits in the bag any more). | `TaskFlow` |
| A task can be paused instead of deleted: no bricktron works on it until it is resumed. | `PauseTasks` |
| Limit how many bricktrons work on a task at a time, e.g. a quarry with 4. | `WorkLimit` |
| Give a single blueprint (e.g. a warehouse) top priority from the context wheel over it. | `BuildPriority` |
| Cleanup and repair round the base are two fixed tasks with their own priority and crew, instead of chores for every idle worker. | `BaseJobs` |
| Tasks and things workers cannot walk to (blueprints, dig voxels, harvest, repairs, loose items) are shown in red and go 10 places down the list a worker picks tasks from (taken only when none before them can be done) until something changes within 3 blocks of them. | `UnreachableTasks` |
| "Can't reach my destination" says what the worker could not reach and how far ("Log, 12 blocks away, 3 up"); host and single player. | `UnreachableThought` |
| **New:** a bricktron shut in a hole (a pit dug too deep, a closed-off pocket) digs its own way out to the nearest place on its team's routes (host only; uses the route map of `UnreachableTasks`). | `StuckEscape` |
| No duplicate build jobs; workers carry a full armful for a blueprint and the ones near it and deliver to them on one trip; builders also build blueprints that cost nothing. | `BuildJobs` |
| Faster task search in big task areas (mining, digging, many trees): no idle seconds after each block. No waits before asking each project or per kind of resource checked, so every kind of task (building, workshops, storing) is found sooner. | `FasterTaskSearch` |
| Task reservation: a worker reserves the task it chooses, so idle workers no longer walk to the same task together. | `TaskReservation` |
| Job board: an idle worker remembers which tasks just had nothing for it and does not ask them again until something they depend on changes (goals, crew, its own state or bag, routes, stockpiles) or 1-5 seconds pass; same tasks, same order (`[JobBoard] JobBoardMode`: Off, Shadow, On). | `JobBoard` |
| A worker that finishes a task of a quarry, tunnel, dig, flatten, harvest, cleanup or build area, or a step at its workshop, or takes what it dug or felled there to storage, goes on to the area's next task at once (chosen by the game's own choice), instead of standing 1 to 1.5 seconds while the game searches every project. | `TaskPrediction` |
| Resource reservation: a worker reserves the items it fetches (a loose item, or its share of a stockpile), so others fetch elsewhere. | `ResourceReservation` |
| A worker fetching from a stockpile takes any item of the kind there, instead of failing when the item the game picked was taken first. | `StockpilePickup` |
| Direct task: select bricktrons and right-click a blueprint, tree or block of an area to have them do that task now. | `DirectTask` |
| **New:** select bricktrons and right-click a task (in the task bar or its area): they start on it at once instead of finishing what they do; one that has not started within 4 seconds is reported on screen with the reason (cannot reach it, no storage room, nothing to do, paused...). Host only. | `AssignNow` |
| The pick up order on a loose item: each selected bricktron takes an item of that kind no other one is after (not all the same one), then picks up more of that kind lying round it until its hands are full. | `PickUpAround` |
| Idle workers pick up loose items near the base and store them, no cleanup zone needed; items on top of a building are taken from its foot. The range is the home crystal's blue ring. With the base's "Auto cleanup" task they leave the items inside its zone to its crew and still take the ones outside it. | `AutoCleanup` |
| Idle builders repair damaged blocks near the base, no repair zone needed. The range is the home crystal's green ring. | `AutoRepair` |

The ranges of `AutoCleanup` and `AutoRepair` are set on the home crystal's task; "Show ranges" / "Hide ranges" on the crystal's context wheel shows or hides the rings.

## Economy and resources

More energy, more resources, and control over how much is dug.

| Feature | Switch |
|---|---|
| New workers arrive faster: brewed fireflies carry more energy (default +30%, `[Economy] EnergyMultiplier`). | `Economy` |
| Quarry limits per resource: a quarry digs stone, iron, brimstone, coal and blue crystal only until the team has the set amount of each (small + and - over each resource). | `QuarryLimit` |
| Quarry depth can be changed at any time, also while it is being dug, and quarries go 3 times as deep. | `QuarryDepth` |
| Dark crystals: slain enemies drop them (5 at 1 in 3, biftrons 15 at 1 in 2); the upgrades' crystal tier and giant bricktrons cost dark crystals. | `DarkCrystals` |
| Blue crystal veins in the deep rock of every map, so blue crystal can be mined. | `BlueCrystalDeposits` |

## Mining and metallurgy

Coal, steel and new ways to get at the ore under the ground.

| Feature | Switch |
|---|---|
| Coal and steel: coal is mined from veins in the deep rock of every map or burnt from logs in the furnace; the forge makes steel from iron and coal; the furnace burns coal as well as orange crystal (coal first); the upgrades' steel tier costs steel. | `Metallurgy` |
| **New:** mine shafts, 2 to 4 big lodes of orange crystal, iron, blue crystal and coal (no stone) in the deep rock of every map, showing on the cliff face, whose blocks take 3 times as long to dig; old maps and saves get them too. | `MineShafts` |
| **New:** "Ore mine" in the new task menu (harvest tasks, after Brimstone) is placed like a harvest task, and its bricktrons dig only the ore they can safely get at, following a vein into the rock, without digging stone. | `OreMine` |
| Iron in the ground gives 25% more raw iron on average, from iron ore dug out and iron deposits mined (`[RichIron] Bonus`). | `RichIron` |
| Mining gives 30% less coal on average: each coal from a coal vein dug out or a coal deposit mined is lost with a 30% chance (`[LeanCoal] Reduction`). | `LeanCoal` |
| Bricktrons with a job that stand without a task for 15 seconds or more are written to `BepInEx/idle.log`: how long, where, their job, their last task and why they turned tasks down (`[Debug] IdleLogSeconds`). | `IdleLog` |

## Nature

| Feature | Switch |
|---|---|
| Tree harvest areas also remove the stumps of felled trees: 3x the axe work of a tree, 1 log instead of 3 (`[TreeStumps]`). | `TreeStumps` |
| Plant trees: a free tree sapling (build menu, structures) grows from a tiny tree to a full tree over 30 minutes of game time, then is felled like any tree. A tree farm (new task menu) is a box dragged out like a landscape task, where builders plant saplings, fell the full-grown trees and replant the spots (`[Saplings]`). | `Saplings` |

## Building

New blocks, new buildings and faster ways to build.

| Feature | Switch |
|---|---|
| New blocks: stone bricks of 2 x 2, 2 x 4, 1 x 3 and 1 x 4 that stand on one supported block (the long ones bridge gaps between walls), a brick on end (1 x 1, 2 high), and wooden slabs (half high) of 1 x 1, 2 x 1, 2 x 2, 2 x 4, 1 x 3, 1 x 5 and 2 x 3 that hold on to the side of stone. | `CustomBlocks` |
| Wood ladder: bricktrons climb straight up and down it, and step onto or off it from the side halfway up; slower than stairs (`[Ladders] ClimbSpeed`, `RouteCost`), only with free hands or a bag. | `Ladders` |
| Wood blocks and wooden slabs can be built out sideways from other wood (a slab in line with a slab walkway holds): up to 5 blocks may hang into the air from a supported wood block (`[WoodOverhang] MaxOverhang`). | `WoodOverhang` |
| A drag build stops when the mouse button is let go, also over the interface or outside the window. | `DragRelease` |
| In build mode X picks which cell of the held block goes where the mouse points (Shift+X the other way), through all of its cells; choosing another block starts again. | `BlockAnchor` |
| One build task per team: every blueprint goes into it, and other build tasks are merged into it. | `GlobalBuildJob` |
| Hovering a blueprint shows the resources it still needs, with icon and count; a block of a build task, and the menu of a selected build task, show what the whole task still needs (red when the stockpiles hold too little). | `BuildNeeds` |
| Hovering a blueprint that is not being built says why, in a small panel over the cursor: its task is not committed, paused or has no workers, something is in the way (named, with its cells marked red in the world), it waits for what it rests on, or no bricktron can get to a place to build it from (with how many of the task's blueprints are stuck so). | `BuildBlocked` |
| Building that waits for material says so: a table at the bottom right (foldable to a small strip of icons and missing counts) lists each resource the build tasks need more of than the stockpiles hold (with what it is made from, e.g. logs for planks), and every blueprint still waiting for material shows the resource on red (not enough in stock), green (enough in stock) or blue (a worker is bringing it). | `BuildShortage` |
| A wooden slab is also held from the side, by a plank right next to it that the game's own rule holds (one step out, no endless chains), so a slab laid beside a finished walkway can be built. | `SidePlanks` |
| The mouse over a resource lying on the ground shows a small label at the cursor with its icon and name. | `ItemHint` |
| Eyedropper: middle-click a block or building to build a copy. | `Eyedropper` |
| Pending blueprints are always visible. | `BlueprintsVisible` |
| Blueprints marked for deconstruction stay dark orange, also while their build task is not selected. | `DeconstructColour` |
| Move buildings: hold **M** and click a building, then place it; workers demolish the old one and build the new one from its materials (`[Building] MoveStructureKey`). | `MoveStructure` |
| Copy and paste: **Ctrl+C** and drag an area to copy its blocks, buildings and blueprints; **Ctrl+V** pastes them as blueprints, right-click rotates (`[Building] CopyKey`, `PasteKey`). | `CopyPaste` |
| The ward range spheres (shown while a ward is hovered or placed) are drawn fainter, so the ground under them can be seen (`[WardRange] Strength`). | `WardRangeFade` |

## Storage

| Feature | Switch |
|---|---|
| Large stockpile: 3 x 3 blocks, holds three times as much as the 2 x 2 stockpile. | `LargeStockpile` |
| Warehouse: a 9 x 6 hall in the build menu that holds 4500 resources of any kind; workers walk inside, and its brick works turns stored stone into bricks (60 planks, 80 bricks, 20 iron, 10 rope). | `Warehouse` |
| Mixed stockpiles: one pallet holds several resources, one resource type per column (four columns); raw stone (converted to bricks), bricks, logs, planks, iron, brimstone, crystals and other valuables (`[MixedStockpiles] NeverMix`) are never mixed. | `MixedStockpiles` |
| Workers prefer the nearest stockpile. | `StockpileChoice` |
| Idle workers consolidate small stockpiles into the fullest one. | `StockpileConsolidation` |
| "Empty stockpile" on the context wheel of a stockpile, large stockpile or warehouse: it takes nothing new and idle workers carry its contents to other storages. It stays empty (overhead icon, "Emptying" in its tooltip) until switched off; saved, host decides in multiplayer. | `EmptyStockpile` |
| A demolished or deconstructed stockpile (also the large one), warehouse, workshop or weapon stand drops what it holds instead of destroying it. | `StockpileSpill` |
| Hovering a stockpile lists what it holds next to the mouse (icon, name, count); a warehouse also shows how full it is in percent. The game's own icon-and-count line above it is hidden. | `StockpileTooltip` |

## Workshops, market and research

| Feature | Switch |
|---|---|
| Workshops can loop their queue until a stock limit per product (e.g. 20 fabric and 5 rope), each recipe waiting while there is enough. Turning on a recipe's limit also puts the recipe in the queue. | `CraftLoopLimit` |
| A looping workshop queue with several recipes takes turns: a recipe that has waited 20 seconds for an ingredient no stockpile holds goes to the back, and one whose ingredients are in stock starts instead. | `CraftRoundRobin` |
| Task areas can be dragged 50% larger than the game allows: harvest and base circles and quarries (`[LargerTasks] AreaScale`), and flatten tasks twice as large (`[LargerTasks] FlattenScale`). | `LargerTasks` |
| Quarries are dug out 30% faster (`[QuarrySpeed] Speed`): every swing of the pick counts as 1.3 swings, so a block takes fewer swings; the swing itself is not quicker. Other digging is unchanged. | `QuarrySpeed` |
| Tasks have six priority levels, 0 (locked) to 5 (high), instead of four: two new levels lie half way between low and medium and between medium and high; the raise and lower buttons step through all six and the task list shows the level as five pips. | `PriorityLevels` |
| Workers go on to their next task sooner: the wait after a task is 2 AI ticks instead of 10, after a failed one 10 instead of 30 (`[QuickNextTask] TicksAfterTask`, `TicksAfterFailure`). | `QuickNextTask` |
| Failsafe for a hanging worker: one that has not moved for 10 seconds in a walking task (60 in any other) has its task cancelled and puts down what it carries, then starts afresh; workshop workers and fighters are left alone (`[StuckReset] Seconds`, `LongSeconds`). | `StuckReset` |
| A finished order always leaves the workshop's queue (the game removed it by object, so a re-read queue entry could stay and a queue that did not loop made the same order again and again); it is matched by recipe id. | `CraftFinishFix` |
| **New:** "Copy setup" and "Paste setup" in a workshop's menu copy its queue, looping and stock limits to another workshop of the same kind (only the recipes it can make). | `WorkshopSetup` |
| A workshop's priority (e.g. the research station) can be set like a task's: locked, low, medium or high. | `WorkshopPriority` |
| Market: a market hall in the build menu (crafting group, 8 planks, 6 bricks, 2 fabric) where workers trade any resource for any other; it queues 12 trades instead of 8, details below. | `Market` |
| Smithy, armoury and research station in the build menu (crafting group) for weapon, armour and colony upgrades, details below. | `Upgrades` |

**Market** (`[Market]`): a 15% fee; prices rise with trading and recover over time (a trade then asks more for the same lot, never refused), so trading never makes resources. Coal and steel trade too (steel can only be given). In the C# menu you pick what to give and what to get, see the price right now, and queue how many trades to make; each queued trade is made once. The trades made before (the last 6) and favourites ("Fav", up to 8, kept at the top) are listed over the goods: a click chooses that trade, "x" takes it out of the list. Both lists are kept in the settings (`[Market] RecentTrades`, `FavouriteTrades`), the same at every market and in every save. Several workers fetch for the same trade, and workers walk inside.

**Upgrades:**
- Weapon upgrades (Sharpened Blades, Heavy Blades, Fletching, Steady Aim, Winch) and armour upgrades (Chainmail, Padded Gambeson, Reinforced Helmet, Shield Rims, Warded Plate) in three tiers: iron, steel and crystal.
- Colony upgrades at the research station, paid in dark crystals: Housing (up to 15 more bricktrons), Work Methods, Stacking (+33% storage per tier, up to twice the room: 4 layers instead of 2, taller crates; a stockpile only piles as high as there is room over it, and nothing can be built where its goods stand), Crystal Attunement, Light Boots, Firefly Lore, Giant Bricktrons, Scouting (more time between waves) and Efficient Mining (more stone, ore and crystal from digging and mining).
- 5 minutes of work per tier, with a progress bar over the researching worker; upgrades count for the whole team and are kept in the save.
- Researched tiers show on the soldiers' gear, and soldiers wear the researched chainmail, gambeson, plate, great helm and heater shield.
- Starting a research reserves its whole cost from the stockpiles at once and locks that upgrade at the team's other stations until it is finished or cancelled. Workers walk inside.
- **New:** in sandbox every research is free (there are no enemies to drop dark crystals); it still takes its usual time at its station. Other gamemodes pay as before.

## Army and combat

| Feature | Switch |
|---|---|
| **New:** a player's order always wins: while it waits or runs, the bricktron's instinct does not send it to fight and its own or its task's orders do not replace it; a stroke or shot that may not be interrupted is broken off so the order starts at once. | `PlayerOrdersFirst` |
| Call to arms: soldiers per class (limited by the sets you own), ranged and melee rally points, and a role for each worker (for the selected workers, or per worker in a list of all workers). | `CallToArms` |
| Archers always hit visible targets, and arrows never hurt allies. | `ArcherAccuracy` |
| A ranged fighter (archer, arbalist, slinger, alchemist, artificer) whose shots would hit the castle or rock in between walks to a nearby place with a clear shot (the wall's edge, up to 8 blocks away) and shoots from there, and otherwise holds its place instead of running into the fight, without climbing or dropping down to fight an enemy hand to hand (`[RangedReposition]`). | `RangedReposition` |
| Ranged fighters on the edge of a wall lean out over it and shoot enemies at the foot of the wall and all along it (`[WallLean] Distance`). | `WallLean` |
| Melee fighters low on health retreat to a healing ward or the home crystal and heal before fighting again (`[Retreat]`). | `Retreat` |
| Workers without a soldier's gear no longer fight enemies that come close on their own; they leave them to the soldiers (an order to attack still sends them). | `UnarmedWorkers` |
| Artificers heal team mates in attack range with green healing bolts (10% health per bolt, one at a time). | `ArtificerHealing` |
| Critical hits from combat level 3: double damage at 5% (level 3), 10% (5), 15% (8) or 20% (10), with a red "2x damage" over the bricktron and a red glowing sword or shot. | `CriticalHits` |
| Mixed weapon stands: one stand holds the weapons and armour of all soldier classes at once; equipping takes one whole kit of a single class, the one a call to arms asks for, otherwise the class with the most kits on the stand (`[MixedRacks] PerSlot`). | `MixedRacks` |
| Every soldier kit is one item (Knight kit, Archer kit, ...): kit recipes make one bundle, a soldier unpacks it when equipping and packs a whole kit again when handing it in. Saves with kit bundles need the mod. | `KitBundles` |
| Enemy waves are no longer shut out by trees (planted rows included): enemies that break their way may route through trees and fell them, and the trees drop their logs. | `EnemyTreeClearing` |
| Healtron: a new enemy, a green corruptron that heals its allies with a pulse (8% health every 4 s within 6 voxels) and a beam on the most wounded (5% per second within 10 voxels), but hits for only 40%. Raids bring one now and then (15% per corruptron, at most 2 per raid, invasion from wave 3) (`[Healtron]`). | `Healtron` |

## Workers and experience

| Feature | Switch |
|---|---|
| Separate work and combat XP, levels 1-10 with +5% per level, shown in the name tag with XP bars and a level-up effect. | `Experience` |
| Giant bricktrons: sacrifice one worker and pay 50 dark crystals (or, without `DarkCrystals`, 1.5x the energy of a new bricktron) to upgrade another. It is 1.25x as big, 2x as fast at everything, has 2x health and carries 2.5x as much, but still counts as one bricktron; one per 5 bricktrons; first researched at the research station (`[GiantBricktron]`). | `GiantBricktron` |
| A dead worker respawns for a flat 100 energy instead of the price of a new bricktron. | `RespawnCost` |
| Dying bricktrons drop their gear instead of losing it. | `DropGearOnDeath` |

## Pathfinding

| Feature | Switch |
|---|---|
| Shorter routes, nearby stairs found from walls, bigger search budgets, no pause after picking something up, stairs usable under walkways, route searches limited to a time budget per frame, long bricks and slabs update the route graph over their whole length, allies walk past rope bridge posts, and a route node left inside ground that has become solid is rebuilt when a bricktron is pushed out of it. | `Pathfinding` |
| Bricktrons walk straight lines across flat open ground instead of a voxel-by-voxel staircase: runs of plain flat steps of a route are pulled tight onto a straight line where the line, and the ground beside it the body brushes, is free and level (climbs, stairs, ladders, doors and forest stay as the search found them). Switch off with `[Pathfinding] StraightPaths`. | `Pathfinding` |
| Bricktrons walk through each other (allies never block a path; enemies still do), and a bricktron in a doorway no longer makes the area behind it unreachable. | `PassThroughAllies` |
| Experiment, Unity 6 port only, off by default: Unity's navigation mesh is built from the route graph on Unity's worker threads and kept up to date when the terrain changes, to measure it against the game's route search ([NavMesh](NavMesh.md)); nothing walks by it yet (`[Pathfinding] NavMeshExperiment`). | `Pathfinding` |

## UI

| Feature | Switch |
|---|---|
| Resource list (icon, name, count) in the top-right corner. A resource stays listed from the first time it is stocked (in red at 0, also after loading), plus storage used and free (with free as a percentage). A **Resources** button on the right-hand bar shows and hides it (`[ResourceList] Shown`). | `ResourceList` |
| The minimap shows the island (terrain colours, height, slopes, walls and buildings) under the units. | `MinimapTerrain` |
| Minimap zoom: the mouse wheel over the minimap zooms in and out (1x to 6x, `[Minimap] Zoom`); zoomed in, it follows the camera, and clicks still move the camera to the place clicked. | `MinimapZoom` |
| **Ctrl+F** searches the world: matching bricktrons (name, job), enemies, buildings, blueprints and resources get a yellow ring (several searches with commas). | `WorldSearch` |
| Invasion: a big warning before the next wave at 1 minute, 30 and 15 seconds, and a countdown from 5. | `WaveWarning` |
| Invasion: an arrow on the screen points where the next wave will come from, 30 seconds before it. | `WaveDirection` |
| Invasion: corruptron waves spawn at least 60 blocks from the nearest block a player has built, when the map has a spawn point that far (`[SpawnDistance] MinBlocks`), and the farther a spawn point is from the players' blocks the likelier it is picked; otherwise a random one. | `SpawnDistance` |
| Respawn status under a dead worker's firefly (time left, missing energy). | `RespawnStatus` |
| Energy hint above your home crystal when the firefly of a killed enemy or worker arrives ("+N energy"). | `EnergyHint` |
| Wild plants: young plants (fibre) seed themselves thinly in open grass, where a 5x5 square is free of blocks, trees and paths (`[WildPlants] Tries`); every plant smaller than full size grows to full size in 10 minutes (`GrowMinutes`). | `WildPlants` |
| Worn paths: grass your units walk a lot wears down to earth, and worn ground nobody walks grows its grass back over time; grass also spreads slowly onto bare earth beside it (`[WornPaths] Steps`, `HalfLifeMinutes`, `GrassSpreadMinutes`). | `WornPaths` |
| Units see 10% further through the fog of war (`[FogOfWar] VisionMultiplier`). | `WiderVision` |
| Castle Story Plus logo on the splash screen, main menu and loading screen, with the version number on the main menu and loading screen. | `PlusLogo` |
| **New:** coming back to the game with alt+tab no longer opens the construction menu: the build-mode key (Tab) is ignored for half a second after the window regains focus. | `FocusInputGuard` |

## Menus, saving and loading

| Feature | Switch |
|---|---|
| Game menus drawn in C# instead of the game's slow-to-build Lua menus, see below. | `CsMenus` |
| **Continue** on the title screen loads the latest save. | `ContinueButton` |
| **New:** a **Random World** map made from a seed with noise: archipelago, continent or highlands, small to large, forest, winter or desert, chosen on the new game screen with a live picture of the world. | `RandomWorld` |
| **Save and return to main menu** and **Save and quit game** (with a confirmation) in the quit dialog. | `SaveAndLeave` |
| Exit closes the game at once (main menu or in a game), with no long wait while everything is torn down. | `FastQuit` |
| The game's launcher remembers "Launch in fullscreen", resolution and monitor. | `LauncherOptions` |
| Fast startup: `[Startup] FastStartup = true` (or the launch option `-faststartup`) skips the logo screens at startup. | `FastStartup` |
| New Invasion worlds: choose how often enemy waves come (every 5, 10, 15, 20 or 30 minutes), saved with the world. | `WaveInterval` |
| Game speed keys in single player: **1** normal, **2** 2x, **3** 3x (`[GameSpeed]`). | `SpeedKeys` |
| Autosave every 2 minutes of play (`[Saving] AutosaveMinutes`, 0 = off), one autosave slot per map. | `AutoSave` |
| Saving does not freeze the game: the quicksave and save menu write over several frames, and the files are written in the background and swapped in at once. | `FastSave` |
| Faster map loading that keeps the window responsive. | `FasterLoading` |
| Checks GitHub for a new release at startup and offers to update from the main menu (see [Updates](Installation.md#updates)). | `UpdateCheck` |
| **Castle Story Plus settings** in the in-game Settings menu: autosave interval, firefly energy, giant bricktron values, debug mode, keys and feature switches. | (always on) |
| **Renderer choice (Unity 6 port, Linux):** OpenGL Core or Vulkan (`[Graphics] Renderer`, Default is OpenGL Core), in Castle Story Plus settings with a "Save and restart now" button. Unity picks the graphics API only when the game starts, so a choice that differs from the running one starts the game again with `-force-glcore` or `-force-vulkan` (also when it was started from Steam or a shortcut). Offered when the install converted the shaders for both (`Content/graphics.txt`); a start that does not get the chosen API goes back to Default. | (always on) |

**C# menus:** the whole in-game menu (`[LuaUi] CsGameMenu`, on by default; the game's GameMenu.lua is then not loaded at all), the gamemodes' intro, victory and defeat dialogs (`CsModals`) and the title screen's buttons (`CsMainMenu`) are drawn in C#. The other `[LuaUi]` switches draw parts of the Lua menu in C# when `CsGameMenu` is off.

## Performance

| Feature | Switch |
|---|---|
| More frames per second: shadows drawn to 150 blocks in 2 cascades instead of 400 in 4, and trees and terrain outside the screen cast a shadow only when it can land on the screen (`[RenderTuning]`). | `RenderTuning` |
| Less CPU time per frame: hidden icons over bricktrons and things no longer have their canvas rebuilt every frame, stockpile shelf displays only update when what they hold changes, a worker's resource search that found nothing is not repeated for 2 seconds, bricktrons off the screen are not posed, a unit's animation states are checked against its animator once per update instead of once per state, the animators of decoration (stockpile planks, beacon handles, furnace fires) and the overhead icons' unused test scripts and idle animators no longer run off the screen or at all, a unit standing still does not look up the floor under it every frame, who sees whom is refreshed at most as often as at 60 frames a second, a tunnel task's look for work and the map's reach regions are spread over several ticks, sound objects tell Wwise their position with fewer calls, and when the frame rate drops below 25 the units' AI runs in at most 2 of a frame's catch-up fixed updates ([Performance] AiTicksPerFrame), so a slow frame no longer makes the next one slower. Icons sorted by distance set their sorting order only when it changed. A game signal that nothing has connected to yet no longer keeps two empty lists of its own (some 700 000 objects in a big base, a ninth of the heap), so each garbage collection has less to look at. Overhead icons read the camera's position once per frame, and how a unit holds what it carries is remembered instead of looked up every frame. A faction's vision pass puts its bricktrons and buildings in buckets by place, so most tracking groups are settled for a whole bucket at once (the worst frame of the pass was up to 9 ms), and reads their positions once per step. The items a stockpile holds (thousands in a big base) are no longer gone over in every vision pass of every faction: nothing happens for them, so they are remembered as held until their holder or their place changes. Storage fill animations wait while the storage is off the screen. Mouse picking casts each identical ray once per frame. A task's check whether a worker can store or fetch its things stops once the answer is decided (several ms in one frame per asking worker), and a harvest task counts its goals with a plain loop. | `FrameSavers` |
| **Unity 6 port:** some of the work moves off the main thread: a storage's pallet animation is worked out by Unity's animation pass on its worker threads (`[Performance] ProgressionOnWorkers`), and the faction vision's detection step runs as a Burst job of the player's own (`[Performance] PlayerJobs`, players built since 31-10-2026). | `FrameSavers` |
| Fixes memory leaks in the game itself (worker error store, highlights, pie menus, Lua menus, observers that outlive their game, stale reservations, logger names, copied adjective texts, the empty enemy raids an invasion without a crystal made every second, the cleanup tasks' ever-growing list of litter picked up). | `GameLeakFixes` |
| Shorter garbage collection pauses: the Lua value heap (10 million slots, 280 MB, read by every collection) starts at 1 million slots and grows only when needed. | `LuaHeapShrink` |
| Shorter and fewer garbage collection pauses: each Lua script and coroutine gets stacks of 8,192 calls and 32,768 values (under 2 MB) instead of 131,072 of each (18 MB, made at once). | `LuaStackShrink` |

## Debug tools

The debug windows open only with **Debug mode** on (see [Configuration](Configuration.md)).

| Key | Feature | Switch |
|---|---|---|
| F9 | System log: what every bricktron does, problems, and the mod's log. | `SystemLog` |
| F8 | Debug menu (host only): instant build, energy, time of day, builders, healing, resources, enemies, invasion waves and the time between them, route view, 100% critrate. Its sections scroll when they are taller than the screen. | `DebugMenu` |
| X | **New:** X-ray (also in the debug menu: Off, Semi for caves and tunnels, Full for ore only) shows bricktrons, buildings and ore under the ground; X toggles the semi x-ray, also without debug mode (`[Debug] XRayKey`). | `DebugMenu` |
| F8 | **New:** "Spawn points" in the debug menu shows the corruptron spawn points as pillars (green may spawn, red too close to your blocks, yellow drawn last) with their distance and chance; click a pillar, then the ground, to move it (host only). | `DebugMenu` |
| F8 | **New:** "Routes" in the debug menu draws the routes of all your bricktrons while it is on (not only the selected ones); a route to a task chosen by TaskPrediction is walked in magenta. | `DebugMenu` |
| F7 | Performance window: the graphics API (OpenGL Core, Vulkan, Direct3D 11) and graphics card in its header, frame rate, frame times (1% low, p95, p99), garbage collections, frame time and frame rate graphs, a graph of the managed memory over the last 10 minutes (in use and its 30 s average), what takes the time each frame (every Update of the game, its libraries and the mod, measured while the window's "Measure systems" button is on; it costs about 1.5 ms a frame), and a switch for a small FPS counter at the top centre of the screen (frames per second and frame time over the last half second, yellow under 60, red under 30; `[Debug] FpsCounter`, it stays on with the window closed) and the last spikes. Frames over 100 ms (`[Performance] SpikeMs`) are logged with what took their time, written one JSON line each to `BepInEx/castlestoryplus_spikes.jsonl` and summed up per system in `castlestoryplus_spikes_summary.txt` (`[Performance] SpikeLog`, off by default: it measures the systems from the start, about 1.3 ms a frame in a big base). | `Performance` |
