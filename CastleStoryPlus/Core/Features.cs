namespace CastleStoryPlus.Core;

// Feature names and descriptions, shared by all patch classes of a feature.
internal static class Features
{
	public const string Economy = "Economy";

	public const string EconomyInfo = "New workers arrive faster (see [Economy] EnergyMultiplier).";

	public const string Pathfinding = "Pathfinding";

	public const string PathfindingInfo = "Shorter routes, nearby stairs found from walls, bigger search budgets, no pause after picking something up, stairs usable under walkways, route searches limited to a time budget per frame, long bricks and slabs update the route graph over their whole length, allies walk past rope bridge posts, a route node left inside ground that has become solid is rebuilt when a bricktron is pushed out of it, and, in the Unity 6 port, bricktrons walk straight lines across flat open ground instead of a staircase ([Pathfinding] StraightPaths) and the player's own units get their routes from Unity's navigation mesh, the game's search staying for enemies and as the fallback ([Pathfinding] NavMeshExperiment, NavMeshRoutes).";

	public const string StockpileChoice = "StockpileChoice";

	public const string StockpileChoiceInfo = "Workers prefer the nearest stockpile instead of walking to fuller ones far away.";

	public const string BlueprintsVisible = "BlueprintsVisible";

	public const string BlueprintsVisibleInfo = "Pending blueprints are always visible, not only in build mode.";

	public const string DeconstructColour = "DeconstructColour";

	public const string DeconstructColourInfo = "Blueprints marked for deconstruction stay dark orange, also while their build task is not selected.";

	public const string WorkerAI = "WorkerAI";

	public const string WorkerAIInfo = "Workers start work on their own, help other task groups when theirs is done, react faster, and only dig, harvest or clean up what they can store all of.";

	public const string BuildJobs = "BuildJobs";

	public const string BuildJobsInfo = "No duplicate build jobs; a worker takes as much material as it can carry for a blueprint and the blueprints near it, and delivers to them on one trip; builders also build blueprints that cost nothing (saplings).";

	public const string ArcherAccuracy = "ArcherAccuracy";

	public const string ArcherAccuracyInfo = "Bricktron archers always hit a target they can see, and arrows never hurt allies.";

	public const string RangedReposition = "RangedReposition";

	public const string RangedRepositionInfo = "A ranged fighter (archer, arbalist, slinger, alchemist, artificer) whose shots at an enemy in range would hit the castle or rock in between walks to a nearby place with a clear shot (the wall's edge) and shoots from there, instead of standing still or running out; holding its place, it does not climb or drop down to fight an enemy hand to hand.";

	public const string UnarmedWorkers = "UnarmedWorkers";

	public const string UnarmedWorkersInfo = "Workers without a soldier's gear no longer go to fight enemies that come close on their own; they leave them to the soldiers. An order to attack still sends them.";

	public const string EnemyTreeClearing = "EnemyTreeClearing";

	public const string EnemyTreeClearingInfo = "Enemy waves are no longer shut out by trees (planted rows of them included): enemies that break their way may route through trees and fell the trees in their way.";

	public const string SaveAndLeave = "SaveAndLeave";

	public const string SaveAndLeaveInfo = "SAVE AND RETURN TO MAIN MENU and SAVE AND QUIT GAME (with a confirmation) buttons in the Quit Game dialog.";

	public const string FastQuit = "FastQuit";

	public const string FastQuitInfo = "Exit (main menu or in a game) closes the game at once, instead of destroying every object one by one and then shutting the engine down in full.";

	public const string ContinueButton = "ContinueButton";

	public const string ContinueButtonInfo = "CONTINUE button on the title screen that loads the latest save.";

	public const string Eyedropper = "Eyedropper";

	public const string EyedropperInfo = "Middle-click (without dragging) a block, structure or blueprint to select it for building a copy.";

	public const string FocusInputGuard = "FocusInputGuard";

	public const string FocusInputGuardInfo = "Ignores the build-mode key (Tab) for half a second after the game window regains focus, so alt+tab no longer opens the construction menu.";

	public const string StockpileConsolidation = "StockpileConsolidation";

	public const string StockpileConsolidationInfo = "Idle workers move resources out of small stockpiles into the fullest one.";

	public const string RespawnStatus = "RespawnStatus";

	public const string RespawnStatusInfo = "Under a dead worker's firefly: what is left before it respawns (travel time, absorption time, missing energy).";

	public const string RespawnCost = "RespawnCost";

	public const string RespawnCostInfo = "A dead worker's firefly respawns for a flat 100 energy instead of the price of a new bricktron; it carries no refund, so the respawn costs exactly that.";

	public const string Experience = "Experience";

	public const string ExperienceInfo = "Work and combat XP and levels (+5% per level), shown in the name tag with XP bars and a level-up effect.";

	public const string ResourceList = "ResourceList";

	public const string ResourceListInfo = "Resource list (icon, name, count) in the top-right corner instead of the icon grid under the minimap; a resource stays listed from the first time it is stocked (saved with the world), with storage used and free (also as a percentage); a \"Resources\" button on the right-hand bar shows and hides it.";

	public const string CallToArms = "CallToArms";

	public const string CallToArmsInfo = "Call to arms with soldiers per class, ranged/melee rally points and a role per worker (settings button next to the call to arms button).";

	public const string FasterLoading = "FasterLoading";

	public const string FasterLoadingInfo = "Faster map loading that keeps the window responsive (background file reading, time-budgeted loading loops, big Lua files compiled once and cached).";

	public const string CsMenus = "CsMenus";

	public const string CsMenusInfo = "Game menus drawn in C# instead of the game's Lua menus, which take long to build when a map loads. Each menu is switched on on its own in [LuaUi]; all are off by default except the gamemodes' dialogs (CsModals) and the title screen's buttons (CsMainMenu).";

	public const string PlusLogo = "PlusLogo";

	public const string PlusLogoInfo = "Castle Story Plus logo on the splash screen, main menu and loading screen, with the mod's version under it on the main menu and loading screen.";

	public const string FastStartup = "FastStartup";

	public const string FastStartupInfo = "Launch option -faststartup skips the logo screens at startup and goes straight to the main menu.";

	public const string MoveStructure = "MoveStructure";

	public const string MoveStructureInfo = "Move buildings (workshops, stockpiles, racks...): hold the move key and click a building, then place it. Workers demolish the old one (materials and contents are dropped for re-use) and build the new one.";

	public const string CopyPaste = "CopyPaste";

	public const string CopyPasteInfo = "Ctrl+C, then drag an area to copy its blocks, buildings and blueprints. Ctrl+V pastes the copy as blueprints into the selected build project (right-click rotates).";

	public const string UpdateCheck = "UpdateCheck";

	public const string UpdateCheckInfo = "Check GitHub for a new Castle Story Plus release at startup and offer to update from the main menu.";

	public const string AutoSave = "AutoSave";

	public const string AutoSaveInfo = "Save the game automatically every few minutes of play (see [Saving] AutosaveMinutes, or Settings > Castle Story Plus settings in the game). One autosave per map.";

	public const string LauncherOptions = "LauncherOptions";

	public const string LauncherOptionsInfo = "The game's launcher remembers its resolution, monitor and \"Launch in fullscreen\" (its script is fixed when the game starts, from the next launch on).";

	public const string FastSave = "FastSave";

	public const string FastSaveInfo = "Saving no longer freezes the game: the quicksave and save menu write game objects over several frames like the autosave, the save files are written in the background and swapped in at once (the old save is kept if writing fails), and game objects are written compact.";

	public const string WildPlants = "WildPlants";

	public const string WildPlantsInfo = "Plants seed themselves in open grass: now and then a young plant (fibre) grows where a 5x5 square of grass is free of blocks, trees and paths, thinly, so meadows fill up slowly and stop ([WildPlants] Tries); plants below full size grow to full size in 10 minutes ([WildPlants] GrowMinutes).";

	public const string WornPaths = "WornPaths";

	public const string WornPathsInfo = "Paths wear into the grass: grass the players' units walk much turns to earth, and worn ground nobody walks any more grows its grass back over time; grass also spreads slowly onto bare earth beside it (steps, half life and spread time in [WornPaths]).";

	public const string WiderVision = "WiderVision";

	public const string WiderVisionInfo = "Units see 10% further through the fog of war ([FogOfWar] VisionMultiplier).";

	public const string GiantBricktron = "GiantBricktron";

	public const string GiantBricktronInfo = "Giant bricktrons: select two workers and upgrade one (1.25x size, 2x speed for everything it does, 2x health, 2.5x carrying) for 50 dark crystals (with DarkCrystals; otherwise 1.5x the energy of a new bricktron); the other is sacrificed. One giant bricktron per 5 bricktrons. With Upgrades, Giant Bricktrons must first be researched at the research station.";

	public const string EnergyHint = "EnergyHint";

	public const string EnergyHintInfo = "When the firefly of a killed enemy or worker reaches your home crystal, the energy it brings rises above the crystal.";

	public const string TaskPrediction = "TaskPrediction";

	public const string TaskPredictionInfo = "A worker that finishes a task of a quarry, tunnel, dig, flatten, harvest, cleanup or build area, or a step at its workshop, or takes what it dug or felled there to storage, goes on to the area's next task at once, chosen by the game's own choice, instead of standing 1 to 1.5 seconds while the game searches every project.";

	public const string TaskReservation = "TaskReservation";

	public const string TaskReservationInfo = "A worker reserves the task it chooses, so idle workers no longer walk to the same task together.";

	public const string JobBoard = "JobBoard";

	public const string JobBoardInfo = "Idle workers remember which tasks just had nothing for them and skip asking those again until something there changes (a new goal, a worker leaving, storage or routes changing) or a few seconds have passed. [JobBoard] JobBoardMode: Off, Shadow (only counts what would be skipped, for the performance window) or On.";

	public const string ResourceReservation = "ResourceReservation";

	public const string ResourceReservationInfo = "A worker reserves the resources it sets out to fetch (a loose item, or as many items of a stockpile as it will take), so other workers fetch elsewhere instead of walking to the same ones.";

	public const string DirectTask = "DirectTask";

	public const string DirectTaskInfo = "Select bricktrons and right-click a task (blueprint, tree or block of an area...): they start that task at once and stay in their crew.";

	public const string PlayerOrdersFirst = "PlayerOrdersFirst";

	public const string PlayerOrdersFirstInfo = "A player's order always wins: while it waits or runs, the bricktron's instinct does not send it to fight and its own or its task's orders do not replace it, and a stroke or shot that may not be interrupted is broken off so the order starts at once.";

	public const string AssignNow = "AssignNow";

	public const string AssignNowInfo = "Bricktrons put on a task (right-click the task with them selected) start on it at once instead of finishing what they do, also when they are in its crew already; one that has not started within 4 seconds is reported on screen with the reason (cannot reach it, no storage room, nothing to do, paused...).";

	public const string PickUpAround = "PickUpAround";

	public const string PickUpAroundInfo = "The pick up order on a loose item: each selected bricktron takes an item of that kind no other one is after, then goes on picking up that kind lying round it until its hands are full.";

	public const string StockpilePickup = "StockpilePickup";

	public const string StockpilePickupInfo = "A worker fetching from a stockpile takes any item of the kind there, instead of failing when the one item the game picked was taken by another worker first.";

	public const string PassThroughAllies = "PassThroughAllies";

	public const string PassThroughAlliesInfo = "Bricktrons walk through each other (allies never block a path; enemies still do), and a bricktron in a doorway no longer makes the area behind it unreachable.";

	public const string SpeedKeys = "SpeedKeys";

	public const string SpeedKeysInfo = "Game speed keys in single player: 1 = normal, 2 = 2x, 3 = 3x (see [GameSpeed]). With the debug menu's fast speed keys on: 4 = 10x, 5 = 20x.";

	public const string MinimapTerrain = "MinimapTerrain";

	public const string MinimapTerrainInfo = "The minimap shows the island (terrain colours, height, slopes, walls and buildings) under the units; dug and filled ground shows within 2 s, walls and buildings within a minute.";

	public const string MinimapZoom = "MinimapZoom";

	public const string MinimapZoomInfo = "Mouse wheel over the minimap zooms it (1x to 6x, kept in [Minimap] Zoom); zoomed in, it follows the camera.";

	public const string CameraZoomOut = "CameraZoomOut";

	public const string CameraZoomOutInfo = "The camera zooms further out than the game allows ([Camera] MaxZoomOut, 250 blocks by default).";

	public const string WardRangeFade = "WardRangeFade";

	public const string WardRangeFadeInfo = "The ward range spheres (shown while a ward is hovered or placed) are drawn fainter, so the ground under them can be seen ([WardRange] Strength).";

	public const string FasterTaskSearch = "FasterTaskSearch";

	public const string FasterTaskSearchInfo = "Workers find their next task faster in big task areas (mining, digging, many trees): the search uses a real-time budget per frame instead of pausing every few tasks.";

	public const string WaveWarning = "WaveWarning";

	public const string WaveWarningInfo = "Invasion: big warning before the next wave at 1 minute, 30 and 15 seconds, and a countdown for the last 5 seconds.";

	public const string WaveDirection = "WaveDirection";

	public const string WaveDirectionInfo = "Invasion: the next wave's spawn point is picked ahead, and from 30 seconds before the wave an arrow on the screen points where it will come from.";

	public const string AutoCleanup = "AutoCleanup";

	public const string AutoCleanupInfo = "Idle workers pick up loose items near the base or near themselves and store them, without a cleanup zone (see [AutoCleanup] Radius). Items on top of a building or stockpile are taken from its foot. The range is also set on the home crystal's task (+ and -, Shift 5 at a time), with a blue ring around the crystal, shown or hidden with \"Show ranges\" / \"Hide ranges\" on the crystal's context wheel ([BaseRanges] ShowRings).";

	public const string AutoRepair = "AutoRepair";

	public const string AutoRepairInfo = "Idle builders repair damaged blocks near the base (home crystal and stockpiles, see [AutoRepair] Radius), without a repair zone. No materials; blocks under attack are left alone. The range is also set on the home crystal's task (+ and -, Shift 5 at a time), with a green ring around the crystal, shown or hidden with \"Show ranges\" / \"Hide ranges\" on the crystal's context wheel ([BaseRanges] ShowRings).";

	public const string Market = "Market";

	public const string MarketInfo = "Market building (build menu, crafting): trade any resource for any other. Prices rise with trading and recover over time (a trade then asks more for the same lot), with a fee, so trading never makes resources (see [Market]). Coal and steel can be traded too (steel only given). In the C# menu the market keeps goods in stock: up to 12 standing trades (other workshops queue 8), each with how many to keep and what to pay with. The window lists the trades made before and favourites to choose again with one click. Up to 3 more workers help fetch for the current trade. Workers walk inside.";

	public const string Retreat = "Retreat";

	public const string RetreatInfo = "Melee fighters low on health run to a healing ward or the home crystal and heal before fighting again (see [Retreat]).";

	public const string MixedStockpiles = "MixedStockpiles";

	public const string MixedStockpilesInfo = "A stockpile holds several resources at once: each of its four columns holds one resource type.";

	public const string MixedRacks = "MixedRacks";

	public const string MixedRacksInfo = "A weapon stand holds the weapons and armour of all soldier classes at once (as many weapons, props and hats as before, any classes mixed); equipping takes one whole kit of a single class (see [MixedRacks]).";

	public const string KitBundles = "KitBundles";

	public const string KitBundlesInfo = "Every soldier kit is one item (Knight kit, Archer kit, ...): the kit recipes make one bundle with the summed cost and work instead of separate pieces, a soldier that equips a bundle unpacks it, one that hands in a whole kit packs it again, and loose whole kits on a weapon stand are packed into a bundle. Saves with kit bundles need the mod (bundles already made are still unpacked with this switched off).";

	public const string Saplings = "Saplings";

	public const string SaplingsInfo = "Plant trees: a tree sapling in the build menu (structures), free, planted by a builder. It grows slowly from a tiny tree to a full tree over 30 minutes of game time (see [Saplings]) and can then be felled like any tree. A tree farm (new task menu, next to Landscape) is a box dragged out like a landscape task: builders plant saplings in it and a harvest task fells them when full grown, and replants.";

	public const string TreeStumps = "TreeStumps";

	public const string TreeStumpsInfo = "Tree harvest areas also remove the stumps of felled trees: more axe work than a tree, fewer logs (see [TreeStumps]).";

	public const string GameLeakFixes = "GameLeakFixes";

	public const string GameLeakFixesInfo = "Fixes memory leaks in the game itself, so memory no longer climbs during long games or after playing several games.";

	public const string LuaHeapShrink = "LuaHeapShrink";

	public const string LuaHeapShrinkInfo = "Shorter garbage collection pauses: the Lua value heap (10 million slots, 280 MB, read by every collection) starts at 1 million slots and grows only when needed.";

	public const string LuaStackShrink = "LuaStackShrink";

	public const string LuaStackShrinkInfo = "Shorter and fewer garbage collection pauses: each Lua script and coroutine gets stacks of 8,192 calls and 32,768 values (under 2 MB) instead of 131,072 of each (18 MB, made at once).";

	public const string ItemHint = "ItemHint";

	public const string ItemHintInfo = "The mouse over a resource lying on the ground shows a small label at the cursor with its icon and name.";

	public const string SidePlanks = "SidePlanks";

	public const string SidePlanksInfo = "A wooden slab is also held from the side, by a plank right next to it that the game's rule holds (one step out, no endless chains), so a slab laid beside a finished walkway can be built.";

	public const string BuildShortage = "BuildShortage";

	public const string BuildShortageInfo = "Building that waits for material says so: a table at the bottom right (foldable to a strip of icons with the missing counts) lists each resource the build tasks need more of than the stockpiles hold (with what it is made from, e.g. logs for planks), and every blueprint still waiting for material shows the resource on red (not enough in stock), green (enough in stock) or blue (a worker is bringing it).";

	public const string BuildNeeds = "BuildNeeds";

	public const string BuildNeedsInfo = "Hovering a blueprint shows the resources it still needs (icon and count); a block of a build task shows what the whole task still needs.";

	public const string BuildBlocked = "BuildBlocked";

	public const string BuildBlockedInfo = "Hovering a blueprint that is not being built says why: its task is not committed, paused or has no workers, something is in the way, it waits for support, or no bricktron can get to it.";

	public const string LargeStockpile = "LargeStockpile";

	public const string LargeStockpileInfo = "Large stockpile in the build menu: 3 x 3 blocks, holds three times as much as the 2 x 2 stockpile.";

	public const string CustomBlocks = "CustomBlocks";

	public const string CustomBlocksInfo = "New blocks in the brick wheel: stone bricks of 2 x 2, 2 x 4, 1 x 3 and 1 x 4 that stand on one supported block (the long ones can bridge a gap between two walls), a brick on end (1 x 1, 2 high), wooden slabs (half a block high) of 1 x 1, 2 x 1, 2 x 2, 2 x 4, 1 x 3, 1 x 5 and 2 x 3 that also hold on to the side of stone, and a wood ladder.";

	public const string Ladders = "Ladders";

	public const string LaddersInfo = "Wood ladder in the build menu (wood group): bricktrons climb it straight up and down, slower than stairs ([Ladders] ClimbSpeed, RouteCost) and only with free hands or a bag.";

	public const string GlobalBuildJob = "GlobalBuildJob";

	public const string GlobalBuildJobInfo = "One build task per team: every placed blueprint goes into it, other build tasks are merged into it, and placing with the idle group or the repair task selected no longer fails.";

	public const string BlockAnchor = "BlockAnchor";

	public const string BlockAnchorInfo = "In build mode X picks which cell of the held block goes where the mouse points (Shift+X the other way round), through all of the block's cells; choosing another block starts again.";

	public const string WoodOverhang = "WoodOverhang";

	public const string WoodOverhangInfo = "Wood blocks and wooden slabs can be built out sideways from other wood (a slab in line with a slab walkway holds): up to 5 blocks may hang into the air from a supported wood block (see [WoodOverhang]).";

	public const string DragRelease = "DragRelease";

	public const string DragReleaseInfo = "A drag build (a row of blocks) stops when the mouse button is no longer held, also when it was let go over the interface or outside the window.";

	public const string Warehouse = "Warehouse";

	public const string WarehouseInfo = "Warehouse in the build menu: a 9 x 6 hall that holds 4500 resources of any kind, workers walk inside, and its brick works turns stored stone into bricks.";

	public const string TaskFlow = "TaskFlow";

	public const string TaskFlowInfo = "Workers start their next task right away, fetch materials from the stockpile that makes the shortest trip to where they are needed, and store a full bag straight after digging, felling or harvesting.";

	public const string SystemLog = "SystemLog";

	public const string SystemLogInfo = "With debug mode on (Castle Story Plus settings, or Options > Gameplay), F9 opens a system log window: what every bricktron is doing, problems (errors, warnings, failed bricktron tasks) and the mod's log.";

	public const string DebugMenu = "DebugMenu";

	public const string DebugMenuInfo = "With debug mode on (Castle Story Plus settings, or Options > Gameplay), F8 opens a debug menu (host only): instant build (what is placed is built at once), add energy, spawn builders, heal, drop resources, spawn or kill enemies, move, start or freeze the next invasion wave, change the time between waves, and draw the selected bricktrons' routes (coarse, straightened, walked), and make every hit of your bricktrons critical (100% critrate).";

	public const string Performance = "Performance";

	public const string PerformanceInfo = "With debug mode on (Castle Story Plus settings, or Options > Gameplay), F7 opens a performance window: frame rate, frame times (1% low, p95, p99), garbage collections, graphs of the last frames, what takes the time each frame, the last spikes and a switch for an FPS counter at the top of the screen; frames over 100 ms are logged with what took their time.";

	public const string RenderTuning = "RenderTuning";

	public const string RenderTuningInfo = "More frames per second: shadows are drawn out to 150 blocks in 2 cascades instead of 400 in 4, and trees and terrain outside the screen only cast a shadow when it can land on the screen.";

	public const string FrameSavers = "FrameSavers";

	public const string FrameSaversInfo = "Less CPU time per frame: hidden icons over bricktrons and things no longer have their canvas rebuilt every frame, stockpile shelf displays only update when what they hold changes, a worker's resource search that found nothing is not repeated for 2 seconds, bricktrons off the screen are not posed, a unit's animation states are checked against its animator once per update instead of once per state, the animators of decoration (stockpile planks, beacon handles, furnace fires) and the overhead icons' unused test scripts and idle animators no longer run off the screen or at all, a unit standing still does not look up the floor under it every frame, who sees whom is refreshed at most as often as at 60 frames a second, a tunnel task's look for work and the map's reach regions are spread over several ticks, sound objects tell Wwise their position with fewer calls, and when the frame rate drops below 25 the units' AI runs in at most 2 of a frame's catch-up fixed updates ([Performance] AiTicksPerFrame), so a slow frame no longer makes the next one slower. Icons sorted by distance set their sorting order only when it changed. A game signal that nothing has connected to yet no longer keeps two empty lists of its own, so each garbage collection has less to look at. Overhead icons read the camera's position once per frame, and how a unit holds what it carries is remembered instead of looked up every frame. A faction's vision pass puts its bricktrons and buildings in buckets by place, so most tracking groups are settled for a whole bucket at once (the worst frame of the pass was up to 9 ms), and reads their positions once per step. Storage fill animations wait while the storage is off the screen. Mouse picking casts each identical ray once per frame. A task's check whether a worker can store or fetch its things stops once the answer is decided (several ms in one frame per asking worker), and a harvest task counts its goals with a plain loop.";

	public const string QuarryLimit = "QuarryLimit";

	public const string QuarryLimitInfo = "Quarries can dig each resource (stone as bricks, iron, brimstone, coal, blue crystal) until the team has a set amount: small + and - over each resource button.";

	public const string StockpileSpill = "StockpileSpill";

	public const string StockpileSpillInfo = "A demolished or deconstructed stockpile (also the large one), warehouse, workshop or weapon stand drops what it holds on the ground instead of destroying it.";

	public const string EmptyStockpile = "EmptyStockpile";

	public const string EmptyStockpileInfo = "\"Empty stockpile\" on the context wheel of a stockpile, large stockpile or warehouse: it takes nothing new and idle workers carry what it holds to other storages; it stays empty until switched off.";

	public const string QuarryDepth = "QuarryDepth";

	public const string QuarryDepthInfo = "A quarry's depth can be changed at any time, also while it is being dug, with + and - over a depth button in the quarry menu; quarries can go 3 times as deep as before.";

	public const string WorldSearch = "WorldSearch";

	public const string WorldSearchInfo = "Ctrl+F opens a search bar: everything in the world matching the text (bricktrons by name or job, enemies, buildings, blueprints, resources) gets a yellow ring under it; several searches separated by commas.";

	public const string WaveInterval = "WaveInterval";

	public const string WaveIntervalInfo = "A new Invasion world can have its enemy waves come every 5, 10 (default), 15, 20 or 30 minutes, chosen under the difficulty on the new game screen; saved with the world.";

	public const string CraftLoopLimit = "CraftLoopLimit";

	public const string CraftLoopLimitInfo = "A workshop's looping queue can loop until a stock limit per product (\"-  20  +\" on every recipe row): a recipe waits while the stockpiles hold enough of what it makes and starts again when the stock drops.";

	public const string SpawnDistance = "SpawnDistance";

	public const string SpawnDistanceInfo = "Corruptron waves spawn at least 60 blocks from the nearest block a player has built, when the map has a spawn point that far, and the farther a spawn point is from the players' blocks the likelier it is picked; otherwise a random one as before ([SpawnDistance] MinBlocks).";

	public const string CraftFinishFix = "CraftFinishFix";

	public const string CraftFinishFixInfo = "A finished order always leaves the workshop's queue: the game removed it by object, and an entry read again from the synced queue was another object, so a queue that did not loop made the same order again and again. It is now matched by recipe id.";

	public const string StuckReset = "StuckReset";

	public const string StuckResetInfo = "Failsafe for a hanging worker: one that has not moved for 10 seconds in a walking task (60 in any other) has its task cancelled and puts down what it carries, then starts afresh ([StuckReset] Seconds, LongSeconds).";

	public const string QuickNextTask = "QuickNextTask";

	public const string QuickNextTaskInfo = "Workers go on to their next task sooner: the wait after a task is 2 AI ticks instead of 10, after a failed one 10 instead of 30 ([QuickNextTask] TicksAfterTask, TicksAfterFailure).";

	public const string PriorityLevels = "PriorityLevels";

	public const string PriorityLevelsInfo = "Tasks have six priority levels, 0 (locked) to 5 (high), instead of four: two new levels lie half way between low and medium and between medium and high; the task list shows the level as five pips.";

	public const string LargerTasks = "LargerTasks";

	public const string LargerTasksInfo = "Task areas can be dragged 50% larger than the game allows: harvest and base circles and quarries ([LargerTasks] AreaScale), and flatten tasks twice as large ([LargerTasks] FlattenScale).";

	public const string QuarrySpeed = "QuarrySpeed";

	public const string QuarrySpeedInfo = "Bricktrons dig out quarries 30% faster ([QuarrySpeed] Speed): every swing of the pick counts as 1.3 swings, so a block takes fewer swings; the swing itself is not quicker.";

	public const string WallLean = "WallLean";

	public const string WallLeanInfo = "Archers and other ranged fighters on the edge of a wall lean out over it, so they can shoot enemies at the foot of the wall and all along it ([WallLean] Distance).";

	public const string IdleLog = "IdleLog";

	public const string IdleLogInfo = "Bricktrons with a job that stand without a task for 15 seconds or more are written to BepInEx/idle.log: how long, where, their job, their last task and why they turned tasks down ([Debug] IdleLogSeconds).";

	public const string LeanCoal = "LeanCoal";

	public const string LeanCoalInfo = "Mining gives 30% less coal on average: each coal from a coal vein dug out or a coal deposit mined is lost with a 30% chance ([LeanCoal] Reduction).";

	public const string RichIron = "RichIron";

	public const string RichIronInfo = "Iron in the ground gives 25% more raw iron on average: iron ore dug out and iron deposits mined ([RichIron] Bonus).";

	public const string CraftRoundRobin = "CraftRoundRobin";

	public const string CraftRoundRobinInfo = "A looping workshop queue with several recipes takes turns: a recipe that has waited 20 seconds for an ingredient no stockpile holds goes to the back, and a recipe whose ingredients are in stock starts instead.";

	public const string BuildPriority = "BuildPriority";

	public const string BuildPriorityInfo = "The context wheel over a blueprint (e.g. a warehouse being built) has a Top priority button: builders work on it before everything else; a yellow ring marks it; saved with the blueprint.";

	public const string BaseJobs = "BaseJobs";

	public const string BaseJobsInfo = "Cleaning up and repairing round the base are two fixed tasks in the task list (a cleanup and a repair zone on the home crystal, as wide as the AutoCleanup / AutoRepair range) with their own priority and crew, instead of chores every idle worker does; they cannot be deleted.";

	public const string UnreachableTasks = "UnreachableTasks";

	public const string UnreachableTasksInfo = "Tasks and things no worker can walk to (blueprints, dig voxels, trees and plants to harvest, repairs, loose items) are marked unreachable: blueprints turn red, the rest get a red ring, and workers try them only when no task before them (10 places further up the list they pick from) can be done, instead of walking there again and again. A mark is only lifted when something is built, removed or dug within 3 blocks.";

	public const string UnreachableThought = "UnreachableThought";

	public const string UnreachableThoughtInfo = "A worker's \"Can't reach my destination\" says what it could not reach and how far it is (\"Can't reach my destination: Log, 12 blocks away\"). Host and single player; multiplayer clients see the plain text.";

	public const string WorkLimit = "WorkLimit";

	public const string WorkLimitInfo = "How many bricktrons work on a task at a time (e.g. a quarry with 4): a Workers at a time button among the task's actions with + and -; All when there is no limit; saved with the task.";

	public const string PauseTasks = "PauseTasks";

	public const string PauseTasksInfo = "A task can be paused instead of deleted (pause button next to delete): no bricktron works on it until it is resumed, and it keeps its blueprints and zones; its crew are released and put back when it is resumed; saved with the task.";

	public const string WorkshopPriority = "WorkshopPriority";

	public const string WorkshopPriorityInfo = "A workshop's priority (research station, smithy, furnace...) can be set like a task's in its menu: locked, low, medium or high, picked side by side under the recipes (the game keeps every workshop at high).";

	public const string DropGearOnDeath = "DropGearOnDeath";

	public const string DropGearOnDeathInfo = "A dying bricktron drops its gear (weapons, shields, hats, bags) on the ground instead of losing it with the corpse.";

	public const string ArtificerHealing = "ArtificerHealing";

	public const string ArtificerHealingInfo = "Artificers heal the most wounded team mate in attack range with green healing bolts: 10% health per bolt, one at a time, before attacking.";

	public const string Healtron = "Healtron";

	public const string HealtronInfo = "Healtron: a green corruptron that heals its allies. Raids sometimes bring one (15% per corruptron, at most 2 per raid, in invasion from wave 3): every 4 seconds a pulse heals every ally within 6 voxels by 8% of their health, and a green beam heals the most wounded ally within 10 voxels by 5% per second. Its own attacks do only 40% damage ([Healtron]).";

	public const string CriticalHits = "CriticalHits";

	public const string CriticalHitsInfo = "From combat level 3 a sword strike or shot can be a critical hit for double damage (5% at level 3, 10% at 5, 15% at 8, 20% at 10): a red \"2x damage\" rises over the bricktron and its weapon glows red for that hit.";

	public const string BlueCrystalDeposits = "BlueCrystalDeposits";

	public const string BlueCrystalDepositsInfo = "Blue crystal veins in the deep rock of every map (the game leaves them out), so blue crystal can be mined, carried and stored.";

	public const string StockpileTooltip = "StockpileTooltip";

	public const string StockpileTooltipInfo = "Hovering a stockpile or weapon stand shows a list of what it holds next to the mouse: icon, name and count of each resource; a warehouse also shows how full it is in percent. The game's own line of icons and counts above the storage is hidden, as the list shows the same.";

	public const string DarkCrystals = "DarkCrystals";

	public const string DarkCrystalsInfo = "Slain enemies drop dark crystals (5 with a 1 in 3 chance, biftrons 15 with a 1 in 2 chance). The crystal tier of the upgrades costs dark crystals instead of blue crystal, and a giant bricktron costs 50 dark crystals instead of energy.";

	public const string MineShafts = "MineShafts";

	public const string MineShaftsInfo = "Mine shafts: 2 to 4 big lodes of ore in the deep rock of every map, showing on an island's cliff face. A lode holds about 1000 to 1500 blocks of orange crystal, iron, blue crystal (with BlueCrystalDeposits) and coal (with Metallurgy) and no stone, but its blocks take 3 times as long to dig.";

	public const string StuckEscape = "StuckEscape";

	public const string StuckEscapeInfo = "A bricktron shut in a hole (a pit dug too deep, a closed-off pocket) digs its own way out to the nearest place on its team's routes: steps one block up or down at most, terrain only, without drops (needs UnreachableTasks for the route map).";

	public const string Treasure = "Treasure";

	public const string TreasureInfo = "Treasure chests in every world (3-4 small, 5-7 medium, 8-10 large map), away from the starting points, each guarded by a pack of 2 to 10 beasts (the bigger the pack, the better the loot): Spirestalker (giraffe), Cindermane (lion), Boulderback (gorilla) and Ironhorn (rhino), voxel models over enemy units with their own health, damage and speed. The guards are peaceful until attacked, then the whole pack fights back. Kill them (or lure them away) and walk a bricktron up to the chest to open it: iron ingots, crystals, cogs, purified brimstone, dark crystals, soldier kits, bombs and explosive barrels spill out.";

	public const string RandomWorld = "RandomWorld";

	public const string RandomWorldInfo = "A \"Random World\" map in the new game list, made from a seed with noise: choose archipelago, continent or highlands, small to large, forest, winter or desert, how many trees, boulders, surface and deep ore, and a seed (UUID) on the new game screen, and the map's picture shows the world. Floating islands joined by grass routes (land bridges over the sky), with forests, plants, boulders, iron and pyrite, starting points, neutral crystals and corruptron spawn points (all gamemodes).";

	public const string OreMine = "OreMine";

	public const string OreMineInfo = "Ore mine (new task menu, harvest tasks, after Brimstone): placed and sized like a harvest task (a sphere), its bricktrons dig only the ore they can get at in it (orange crystal, iron, blue crystal, coal touching a tunnel, quarry wall or cave), following a vein into the rock, no stone. Unmodded players see a harvest task that harvests nothing.";

	public const string WorkshopSetup = "WorkshopSetup";

	public const string WorkshopSetupInfo = "\"Copy setup\" and \"Paste setup\" in a workshop's menu: copies its queue, looping and stock limits to another workshop of the same kind (only the recipes it can make).";

	public const string Metallurgy = "Metallurgy";

	public const string MetallurgyInfo = "Coal (mined from veins in the deep rock of every map, or burnt from logs in the furnace) and steel (iron and coal at the forge), used by the steel tier of the upgrades. The furnace burns coal as well as orange crystal, coal first.";

	public const string Upgrades = "Upgrades";

	public const string UpgradesInfo = "Smithy, armoury and research station buildings (build menu, crafting): research weapon and armour upgrades in three tiers (iron, steel, crystal), and colony upgrades for dark crystals (more bricktrons, work speed, storage room (+33% per tier, stockpiles stack up to 4 layers high), cheaper bricktrons, walking speed, firefly energy, giant bricktrons, time between waves, more yield from digging and mining), for the whole team. Researched tiers show on the soldiers' gear, and soldiers wear the researched chainmail, gambeson, plate, great helm and heater shield. Starting a research reserves its whole cost from the stockpiles at once and locks that upgrade at the team's other stations until it is finished or cancelled. Workers walk inside. A stockpile only piles as high as there is room over it, and nothing can be built where its goods stand.";
}
