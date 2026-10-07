namespace CastleStoryPlus.Core;

// Feature names and descriptions, shared by all patch classes of a feature.
internal static class Features
{
	public const string Economy = "Economy";

	public const string EconomyInfo = "New workers arrive faster (see [Economy] EnergyMultiplier).";

	public const string Pathfinding = "Pathfinding";

	public const string PathfindingInfo = "Shorter routes, nearby stairs found from walls, bigger search budgets, no pause after picking something up, stairs usable under walkways, route searches limited to a time budget per frame.";

	public const string StockpileChoice = "StockpileChoice";

	public const string StockpileChoiceInfo = "Workers prefer the nearest stockpile instead of walking to fuller ones far away.";

	public const string BlueprintsVisible = "BlueprintsVisible";

	public const string BlueprintsVisibleInfo = "Pending blueprints are always visible, not only in build mode.";

	public const string DeconstructColour = "DeconstructColour";

	public const string DeconstructColourInfo = "Blueprints marked for deconstruction stay dark orange, also while their build task is not selected.";

	public const string WorkerAI = "WorkerAI";

	public const string WorkerAIInfo = "Workers start work on their own, help other task groups when theirs is done, and react faster.";

	public const string BuildJobs = "BuildJobs";

	public const string BuildJobsInfo = "No duplicate build jobs; a worker takes as much material as it can carry for a blueprint and the blueprints near it, and delivers to them on one trip.";

	public const string ArcherAccuracy = "ArcherAccuracy";

	public const string ArcherAccuracyInfo = "Bricktron archers always hit a target they can see, and arrows never hurt allies.";

	public const string SaveAndLeave = "SaveAndLeave";

	public const string SaveAndLeaveInfo = "SAVE AND RETURN TO MAIN MENU and SAVE AND QUIT GAME (with a confirmation) buttons in the Quit Game dialog.";

	public const string FastQuit = "FastQuit";

	public const string FastQuitInfo = "Exit (main menu or in a game) closes the game at once, instead of destroying every object one by one and then shutting the engine down in full.";

	public const string ContinueButton = "ContinueButton";

	public const string ContinueButtonInfo = "CONTINUE button on the title screen that loads the latest save.";

	public const string Eyedropper = "Eyedropper";

	public const string EyedropperInfo = "Middle-click (without dragging) a block, structure or blueprint to select it for building a copy.";

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

	public const string FasterLoadingInfo = "Faster map loading that keeps the window responsive (background file reading, time-budgeted loading loops).";

	public const string CsMenus = "CsMenus";

	public const string CsMenusInfo = "Game menus drawn in C# instead of the game's Lua menus, which take long to build when a map loads. Each menu is switched on on its own in [LuaUi]; all are off by default.";

	public const string PlusLogo = "PlusLogo";

	public const string PlusLogoInfo = "Castle Story Plus logo on the splash screen, main menu and loading screen.";

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

	public const string GiantBricktron = "GiantBricktron";

	public const string GiantBricktronInfo = "Giant bricktrons: select two workers and upgrade one (1.25x size, 2x speed for everything it does, 2x health, 2.5x carrying) for 50 dark crystals (with DarkCrystals; otherwise 1.5x the energy of a new bricktron); the other is sacrificed. One giant bricktron per 5 bricktrons. With Upgrades, Giant Bricktrons must first be researched at the research station.";

	public const string EnergyHint = "EnergyHint";

	public const string EnergyHintInfo = "When the firefly of a killed enemy or worker reaches your home crystal, the energy it brings rises above the crystal.";

	public const string TaskReservation = "TaskReservation";

	public const string TaskReservationInfo = "A worker reserves the task it chooses, so idle workers no longer walk to the same task together.";

	public const string ResourceReservation = "ResourceReservation";

	public const string ResourceReservationInfo = "A worker reserves the resources it sets out to fetch (a loose item, or as many items of a stockpile as it will take), so other workers fetch elsewhere instead of walking to the same ones.";

	public const string DirectTask = "DirectTask";

	public const string DirectTaskInfo = "Select bricktrons and right-click a task (blueprint, tree or block of an area...): they start that task at once and stay in their crew.";

	public const string StockpilePickup = "StockpilePickup";

	public const string StockpilePickupInfo = "A worker fetching from a stockpile takes any item of the kind there, instead of failing when the one item the game picked was taken by another worker first.";

	public const string PassThroughAllies = "PassThroughAllies";

	public const string PassThroughAlliesInfo = "Bricktrons walk through each other (allies never block a path; enemies still do), and a bricktron in a doorway no longer makes the area behind it unreachable.";

	public const string SpeedKeys = "SpeedKeys";

	public const string SpeedKeysInfo = "Game speed keys in single player: 1 = normal, 2 = 2x, 3 = 3x (see [GameSpeed]).";

	public const string MinimapTerrain = "MinimapTerrain";

	public const string MinimapTerrainInfo = "The minimap shows the island (terrain colours, height, slopes, walls and buildings) under the units, refreshed every 15 s.";

	public const string FasterTaskSearch = "FasterTaskSearch";

	public const string FasterTaskSearchInfo = "Workers find their next task faster in big task areas (mining, digging, many trees): the search uses a real-time budget per frame instead of pausing every few tasks.";

	public const string WaveWarning = "WaveWarning";

	public const string WaveWarningInfo = "Invasion: big warning before the next wave at 30 and 15 seconds, and a countdown for the last 5 seconds.";

	public const string WaveDirection = "WaveDirection";

	public const string WaveDirectionInfo = "Invasion: the next wave's spawn point is picked ahead, and from 30 seconds before the wave an arrow on the screen points where it will come from.";

	public const string AutoCleanup = "AutoCleanup";

	public const string AutoCleanupInfo = "Idle workers pick up loose items near the base or near themselves and store them, without a cleanup zone (see [AutoCleanup] Radius). Items on top of a building or stockpile are taken from its foot. The range is also set on the home crystal's task (+ and -, Shift 5 at a time), with a blue ring around the crystal while its task is selected.";

	public const string AutoRepair = "AutoRepair";

	public const string AutoRepairInfo = "Idle builders repair damaged blocks near the base (home crystal and stockpiles, see [AutoRepair] Radius), without a repair zone. No materials; blocks under attack are left alone. The range is also set on the home crystal's task (+ and -, Shift 5 at a time), with a green ring around the crystal while its task is selected.";

	public const string Market = "Market";

	public const string MarketInfo = "Market building (build menu, crafting): trade any resource for any other. Prices rise with trading and recover over time (a trade then asks more for the same lot), with a fee, so trading never makes resources (see [Market]). Coal and steel can be traded too (steel only given). Up to 3 more workers help fetch for the current trade. Workers walk inside.";

	public const string Retreat = "Retreat";

	public const string RetreatInfo = "Melee fighters low on health run to a healing ward or the home crystal and heal before fighting again (see [Retreat]).";

	public const string MixedStockpiles = "MixedStockpiles";

	public const string MixedStockpilesInfo = "A stockpile holds several resources at once: each of its four columns holds one resource type.";

	public const string MixedRacks = "MixedRacks";

	public const string MixedRacksInfo = "A weapon stand holds the weapons and armour of all soldier classes at once (as many weapons, props and hats as before, any classes mixed); equipping takes one whole kit of a single class (see [MixedRacks]).";

	public const string TreeStumps = "TreeStumps";

	public const string TreeStumpsInfo = "Tree harvest areas also remove the stumps of felled trees: more axe work than a tree, fewer logs (see [TreeStumps]).";

	public const string GameLeakFixes = "GameLeakFixes";

	public const string GameLeakFixesInfo = "Fixes memory leaks in the game itself, so memory no longer climbs during long games or after playing several games.";

	public const string LuaHeapShrink = "LuaHeapShrink";

	public const string LuaHeapShrinkInfo = "Shorter garbage collection pauses: the Lua value heap (10 million slots, 280 MB, read by every collection) starts at 1 million slots and grows only when needed.";

	public const string BuildNeeds = "BuildNeeds";

	public const string BuildNeedsInfo = "Hovering a blueprint shows the resources it still needs (icon and count); a block of a build task shows what the whole task still needs.";

	public const string LargeStockpile = "LargeStockpile";

	public const string LargeStockpileInfo = "Large stockpile in the build menu: 3 x 3 blocks, holds three times as much as the 2 x 2 stockpile.";

	public const string CustomBlocks = "CustomBlocks";

	public const string CustomBlocksInfo = "New blocks in the brick wheel: stone bricks of 2 x 2, 2 x 4, 1 x 3 and 1 x 4 that stand on one supported block (the long ones can bridge a gap between two walls), a brick on end (1 x 1, 2 high), wooden slabs (half a block high) of 1 x 1, 2 x 1, 2 x 2 and 2 x 4 that also hold on to the side of stone, and a wood ladder.";

	public const string Ladders = "Ladders";

	public const string LaddersInfo = "Wood ladder in the build menu (wood group): bricktrons climb it straight up and down, slower than stairs ([Ladders] ClimbSpeed, RouteCost) and only with free hands or a bag.";

	public const string GlobalBuildJob = "GlobalBuildJob";

	public const string GlobalBuildJobInfo = "One build task per team: every placed blueprint goes into it, other build tasks are merged into it, and placing with the idle group or the repair task selected no longer fails.";

	public const string DragRelease = "DragRelease";

	public const string DragReleaseInfo = "A drag build (a row of blocks) stops when the mouse button is no longer held, also when it was let go over the interface or outside the window.";

	public const string Warehouse = "Warehouse";

	public const string WarehouseInfo = "Warehouse in the build menu: a 9 x 6 hall that holds 4500 resources of any kind, workers walk inside, and its brick works turns stored stone into bricks.";

	public const string TaskFlow = "TaskFlow";

	public const string TaskFlowInfo = "Workers start their next task right away, fetch materials from the stockpile that makes the shortest trip to where they are needed, and store a full bag straight after digging, felling or harvesting.";

	public const string SystemLog = "SystemLog";

	public const string SystemLogInfo = "F9 opens a system log window: what every bricktron is doing, problems (errors, warnings, failed bricktron tasks) and the mod's log.";

	public const string DebugMenu = "DebugMenu";

	public const string DebugMenuInfo = "F8 opens a debug menu (host only): add energy, spawn builders, heal, drop resources, spawn or kill enemies, move, start or freeze the next invasion wave, change the time between waves, and draw the selected bricktrons' routes (coarse, straightened, walked), and make every hit of your bricktrons critical (100% critrate).";

	public const string Performance = "Performance";

	public const string PerformanceInfo = "F7 opens a performance window: frame rate, frame times (1% low, p95, p99), garbage collections, graphs of the last frames, what takes the time each frame and the last spikes; frames over 100 ms are logged with what took their time.";

	public const string QuarryLimit = "QuarryLimit";

	public const string QuarryLimitInfo = "Quarries can dig each resource (stone as bricks, iron, brimstone, coal, blue crystal) until the team has a set amount: small + and - over each resource button.";

	public const string StockpileSpill = "StockpileSpill";

	public const string StockpileSpillInfo = "A demolished stockpile or warehouse drops what it holds on the ground instead of destroying it.";

	public const string QuarryDepth = "QuarryDepth";

	public const string QuarryDepthInfo = "A quarry's depth can be changed at any time, also while it is being dug, with + and - over a depth button in the quarry menu; quarries can go 3 times as deep as before.";

	public const string WorldSearch = "WorldSearch";

	public const string WorldSearchInfo = "Ctrl+F opens a search bar: everything in the world matching the text (bricktrons by name or job, enemies, buildings, blueprints, resources) gets a yellow ring under it; several searches separated by commas.";

	public const string WaveInterval = "WaveInterval";

	public const string WaveIntervalInfo = "A new Invasion world can have its enemy waves come every 5, 10 (default), 15, 20 or 30 minutes, chosen under the difficulty on the new game screen; saved with the world.";

	public const string CraftLoopLimit = "CraftLoopLimit";

	public const string CraftLoopLimitInfo = "A workshop's looping queue can loop until a stock limit (on/off button with - and + next to the loop button): it waits while the stockpiles hold enough and starts again when the stock drops.";

	public const string BuildPriority = "BuildPriority";

	public const string BuildPriorityInfo = "The context wheel over a blueprint (e.g. a warehouse being built) has a Top priority button: builders work on it before everything else; a yellow ring marks it; saved with the blueprint.";

	public const string WorkLimit = "WorkLimit";

	public const string WorkLimitInfo = "How many bricktrons work on a task at a time (e.g. a quarry with 4): a Workers at a time button among the task's actions with + and -; All when there is no limit; saved with the task.";

	public const string PauseTasks = "PauseTasks";

	public const string PauseTasksInfo = "A task can be paused instead of deleted (pause button next to delete): no bricktron works on it until it is resumed, and it keeps its blueprints and zones; its crew are released and put back when it is resumed; saved with the task.";

	public const string WorkshopPriority = "WorkshopPriority";

	public const string WorkshopPriorityInfo = "A workshop's priority (research station, smithy, furnace...) can be set like a task's with - and + in its queue menu: locked, low, medium or high (the game keeps every workshop at high).";

	public const string DropGearOnDeath = "DropGearOnDeath";

	public const string DropGearOnDeathInfo = "A dying bricktron drops its gear (weapons, shields, hats, bags) on the ground instead of losing it with the corpse.";

	public const string ArtificerHealing = "ArtificerHealing";

	public const string ArtificerHealingInfo = "Artificers heal the most wounded team mate in attack range with green healing bolts: 10% health per bolt, one at a time, before attacking.";

	public const string CriticalHits = "CriticalHits";

	public const string CriticalHitsInfo = "From combat level 3 a sword strike or shot can be a critical hit for double damage (5% at level 3, 10% at 5, 15% at 8, 20% at 10): a red \"2x damage\" rises over the bricktron and its weapon glows red for that hit.";

	public const string BlueCrystalDeposits = "BlueCrystalDeposits";

	public const string BlueCrystalDepositsInfo = "Blue crystal veins in the deep rock of every map (the game leaves them out), so blue crystal can be mined, carried and stored.";

	public const string StockpileTooltip = "StockpileTooltip";

	public const string StockpileTooltipInfo = "Hovering a stockpile or weapon stand shows a list of what it holds next to the mouse: icon, name and count of each resource; a warehouse also shows how full it is in percent.";

	public const string DarkCrystals = "DarkCrystals";

	public const string DarkCrystalsInfo = "Slain enemies drop dark crystals (5 with a 1 in 3 chance, biftrons 15 with a 1 in 2 chance). The crystal tier of the upgrades costs dark crystals instead of blue crystal, and a giant bricktron costs 50 dark crystals instead of energy.";

	public const string Metallurgy = "Metallurgy";

	public const string MetallurgyInfo = "Coal (mined from veins in the deep rock of every map, or burnt from logs in the furnace) and steel (iron and coal at the forge), used by the steel tier of the upgrades.";

	public const string Upgrades = "Upgrades";

	public const string UpgradesInfo = "Smithy, armoury and research station buildings (build menu, crafting): research weapon and armour upgrades in three tiers (iron, steel, crystal), and colony upgrades for dark crystals (more bricktrons, work speed, storage room (+33% per tier, stockpiles stack up to 4 layers high), cheaper bricktrons, walking speed, firefly energy, giant bricktrons, time between waves, more yield from digging and mining), for the whole team. Workers walk inside.";
}
