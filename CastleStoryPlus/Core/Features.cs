namespace CastleStoryPlus.Core;

// Feature names and descriptions, shared by all patch classes of a feature.
internal static class Features
{
	public const string Economy = "Economy";

	public const string EconomyInfo = "New workers arrive faster (see [Economy] EnergyMultiplier).";

	public const string Pathfinding = "Pathfinding";

	public const string PathfindingInfo = "Shorter routes, bigger search budgets, no pause after picking something up.";

	public const string StockpileChoice = "StockpileChoice";

	public const string StockpileChoiceInfo = "Workers prefer the nearest stockpile instead of walking to fuller ones far away.";

	public const string BlueprintsVisible = "BlueprintsVisible";

	public const string BlueprintsVisibleInfo = "Pending blueprints are always visible, not only in build mode.";

	public const string WorkerAI = "WorkerAI";

	public const string WorkerAIInfo = "Workers start work on their own, help other task groups when theirs is done, and react faster.";

	public const string BuildJobs = "BuildJobs";

	public const string BuildJobsInfo = "No duplicate build jobs and no carrying more material than a blueprint still needs.";

	public const string ArcherAccuracy = "ArcherAccuracy";

	public const string ArcherAccuracyInfo = "Bricktron archers always hit a target they can see, and arrows never hurt allies.";

	public const string SaveAndLeave = "SaveAndLeave";

	public const string SaveAndLeaveInfo = "SAVE & LEAVE button in the Quit Game dialog.";

	public const string ContinueButton = "ContinueButton";

	public const string ContinueButtonInfo = "CONTINUE button on the title screen that loads the latest save.";

	public const string Eyedropper = "Eyedropper";

	public const string EyedropperInfo = "Middle-click (without dragging) a block, structure or blueprint to select it for building a copy.";

	public const string StockpileConsolidation = "StockpileConsolidation";

	public const string StockpileConsolidationInfo = "Idle workers move resources out of small stockpiles into the fullest one.";

	public const string RespawnStatus = "RespawnStatus";

	public const string RespawnStatusInfo = "Under a dead worker's firefly: what is left before it respawns (travel time, absorption time, missing energy).";

	public const string Experience = "Experience";

	public const string ExperienceInfo = "Work and combat XP and levels (+5% per level), shown in the name tag with XP bars and a level-up effect.";

	public const string ResourceList = "ResourceList";

	public const string ResourceListInfo = "Resource list (icon, name, count) in the top-right corner instead of the icon grid under the minimap.";

	public const string CallToArms = "CallToArms";

	public const string CallToArmsInfo = "Call to arms with soldiers per class, ranged/melee rally points and a role per worker (settings button next to the call to arms button).";

	public const string FasterLoading = "FasterLoading";

	public const string FasterLoadingInfo = "Faster map loading that keeps the window responsive (background file reading, time-budgeted loading loops).";

	public const string PlusLogo = "PlusLogo";

	public const string PlusLogoInfo = "Castle Story Plus logo on the splash screen, main menu and loading screen.";

	public const string MoveStructure = "MoveStructure";

	public const string MoveStructureInfo = "Move buildings (workshops, stockpiles, racks...): hold the move key and click a building, then place it. Workers demolish the old one (materials and contents are dropped for re-use) and build the new one.";

	public const string CopyPaste = "CopyPaste";

	public const string CopyPasteInfo = "Ctrl+C, then drag an area to copy its blocks, buildings and blueprints. Ctrl+V pastes the copy as blueprints into the selected build project (right-click rotates).";

	public const string UpdateCheck = "UpdateCheck";

	public const string UpdateCheckInfo = "Check GitHub for a new Castle Story Plus release at startup and offer to update from the main menu.";

	public const string AutoSave = "AutoSave";

	public const string AutoSaveInfo = "Save the game automatically every few minutes of play (see [Saving] AutosaveMinutes, or Settings > Castle Story Plus settings in the game). One autosave per map.";

	public const string GiantBricktron = "GiantBricktron";

	public const string GiantBricktronInfo = "3x bricktrons: select two workers and upgrade one (2x size, 3x speed for everything it does, 3x health) for 1.5x the energy of a new bricktron; the other is sacrificed. One 3x bricktron per 5 bricktrons.";

	public const string EnergyHint = "EnergyHint";

	public const string EnergyHintInfo = "When the firefly of a killed enemy or worker reaches your home crystal, the energy it brings rises above the crystal.";

	public const string TaskReservation = "TaskReservation";

	public const string TaskReservationInfo = "A worker reserves the task it chooses, so idle workers no longer walk to the same task together.";

	public const string SpeedKeys = "SpeedKeys";

	public const string SpeedKeysInfo = "Game speed keys in single player: 1 = normal, 2 = 2x, 3 = 3x (see [GameSpeed]).";

	public const string MinimapTerrain = "MinimapTerrain";

	public const string MinimapTerrainInfo = "The minimap shows the island (terrain colours, height, slopes, walls and buildings) under the units, refreshed every 15 s.";

	public const string FasterTaskSearch = "FasterTaskSearch";

	public const string FasterTaskSearchInfo = "Workers find their next task faster in big task areas (mining, digging, many trees): the search uses a real-time budget per frame instead of pausing every few tasks.";

	public const string WaveWarning = "WaveWarning";

	public const string WaveWarningInfo = "Invasion: big warning before the next wave at 30 and 15 seconds, and a countdown for the last 5 seconds.";
}
