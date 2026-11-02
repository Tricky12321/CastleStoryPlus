using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Engine;
using Brix.Engine.Nature;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.UI;
using Brix.Game.Beacons;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using Brix.Game.Network;
using Brix.Input;
using Brix.Legacy;
using Brix.Lifecycle.Pooling;
using Brix.Lua;
using Brix.Utils;
using CastleStoryPlus.Building;
using CastleStoryPlus.Core;
using HarmonyLib;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Nature;

// A tree farm: "Tree farm" in the new task menu, next to "Landscape". It is placed and dragged out as a box like a
// landscape task (it is one, marked as a farm), shows in the task list as "Tree farm" and is deleted the same way.
// The host's builders plant saplings on free ground in the box every [Saplings] Spacing blocks (through the team's
// build task), and a tree harvest task made with the farm fells the trees in the box once they are full grown (a
// sapling only becomes a tree of the map when it has grown, so the young ones are left alone). A felled tree's spot
// is planted again. A farm never digs or fills like a landscape task.
//
// The farm mark is the landscape task's own threshold (the height it levels to) set far below the world: it is
// saved and synced with the task, and the game's dig/fill rules are skipped for it.
[Feature(Features.Saplings, Features.SaplingsInfo)]
internal static class TreeFarm
{
	internal const int FarmCode = -77777;

	internal const string Name = "Tree farm";

	// Sent as the spawn command's rotation (which the game ignores for landscape tasks) to ask for a farm.
	internal static readonly Quaternion FarmRotation = new Quaternion(0f, 0f, 1f, 0f);

	internal static ConfigEntry<int> Spacing;

	// Client: the landscape tool was started from the "Tree farm" button.
	internal static bool PlacingFarm;

	// Host: a farm's spawn command is being run.
	internal static bool SpawningFarm;

	// Host: the box of each farm's harvest task, so it fells only the trees in the box.
	internal static readonly Dictionary<HarvestProject, VoxelBounds> HarvestBoxes = new Dictionary<HarvestProject, VoxelBounds>();

	private static void Enable()
	{
		Spacing = Plugin.Cfg.Bind("Saplings", "Spacing", 3, new ConfigDescription("Blocks between the saplings a tree farm plants (trees need 2 at least).", new AcceptableValueRange<int>(2, 6)));
		GameSession.OnLeave(() =>
		{
			PlacingFarm = false;
			HarvestBoxes.Clear();
		});
		LuaInjection.AddAction("NewTreeFarm", StartPlacing);
		GameUi.GameMenuItems.Extend(AddToMenu);
		LuaInjection.AddFunction("IsPlacingTreeFarm", (ScriptExecutionContext context, CallbackArguments args) => DynValue.NewBoolean(PlacingFarm && InputModeController.ProjectBeingPlaced() == Groupes.Landscape));
		LuaInjection.AddFunction("IsTreeFarm", (ScriptExecutionContext context, CallbackArguments args) => DynValue.NewBoolean(IsFarm(ProjectArg(args, "IsTreeFarm"))));
		GameUi.GameMenuTexts.IconOverrides.Add((Project project) => IsFarm(project) ? UI.BuildIcons.SpriteFor("sapling_area") : null);
		GameUi.GameMenuTexts.NameOverrides.Add(NameOf);
		LuaInjection.AddFunction("TreeFarmIcon", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			if (!IsFarm(ProjectArg(args, "TreeFarmIcon")))
			{
				return DynValue.Nil;
			}
			Sprite sprite = UI.BuildIcons.SpriteFor("sapling_area");
			return (sprite != null) ? DynValue.FromObject(context.OwnerScript, sprite) : DynValue.Nil;
		});
		// The new task menu: after "Landscape".
		LuaInjection.AddPatch(Features.Saplings, "LUI/Menus/GameMenu.lua", "_m.bh.newLandscapeTask = h\nend\n", LuaInjection.Mode.InsertAfter,
			"\n---tree farm (Castle Story Plus)\n"
			+ "do\n"
			+ "local h = MenuHandle.New()\n"
			+ "h.Icon = " + UI.BuildIcons.Lua("sapling_area", "_UI_Landscape") + "\n"
			+ "h.IconColor = ||Project:ProjectUnderCap() and White or Gray\n"
			+ "h.IconSize = ||32\n"
			+ "h.Label = ||\"" + Name + "\"\n"
			+ "h.hasFgLabel = true\n"
			+ "h.FrontImage_Width = ||64\n"
			+ "h.FrontImage_Height = ||64\n"
			+ "h.OnAction = ||not UIGame.IsPaused() and Project:ProjectUnderCap() and CastleStoryPlus.NewTreeFarm()\n"
			+ "h.Highlight = ||CastleStoryPlus.IsPlacingTreeFarm() and HighlightMode.Bottom or HighlightMode.None\n"
			+ "h.ev_refresh = Event.New()\n"
			+ "h.onRefreshHighlight = Event.New()\n"
			+ "_m.onSetPickerOptions:AddListener(||h.onRefreshHighlight:Invoke())\n"
			+ "_m.mg.newTask:AddChild(h)\n"
			+ "_m.mh.newTask.onLoad:AddListener(function(m) m.AddMenuHandleToggle(h, _m.sh.newMiningTask) end)\n"
			+ "_m.mh.newTask.onOpen:AddListener(||h.ev_refresh:Invoke())\n"
			+ "_m.bh.newTreeFarmTask = h\n"
			+ "end\n");
		// A farm is not a landscape task to the menus: no level/dig buttons.
		LuaInjection.AddPatch(Features.Saplings, "LUI/Data/Data_Project.lua", "return self:IsSelectedOfType(fy_LandscapeProject)", LuaInjection.Mode.Replace,
			"return self:IsSelectedOfType(fy_LandscapeProject) and not CastleStoryPlus.IsTreeFarm(self:GetSelected())");
		// Its icon in the task list.
		LuaInjection.AddPatch(Features.Saplings, "LUI/Menus/Game/Menu_TaskList.lua", "\t\tif projectType == fy_HarvestProject then\n\t\t\treturn Data.Project:GetHarvestIcon(project)\n", LuaInjection.Mode.InsertBefore,
			"\t\tlocal farmIcon = CastleStoryPlus.TreeFarmIcon(project)\n\t\tif farmIcon then return farmIcon end\n");
	}

	private static void StartPlacing()
	{
		Instructions.Execute("Tool_Landscape");
		PlacingFarm = true;
	}

	// The C# in-game menu: the new task menu's button, after "Landscape".
	private static void AddToMenu(GameUi.GameMenuModel menu)
	{
		menu.NewTask.Find("newMiningTask").Insert("newLandscapeTask", new GameUi.UiButton
		{
			Id = "newTreeFarmTask",
			Label = () => Name,
			Icon = () => UI.BuildIcons.SpriteFor("sapling_area") ?? Brix.UI.Icons.IconKeys._UI_Landscape.Get64(),
			IconColor = () => GameUi.GameUiState.UnderTaskCap ? LuaUi.LuiStyle.White : LuaUi.LuiStyle.Gray,
			Highlight = () => PlacingFarm && InputModeController.ProjectBeingPlaced() == Groupes.Landscape,
			OnClick = (bool shift, bool ctrl) =>
			{
				if (!GameUi.GameUiState.Paused && GameUi.GameUiState.UnderTaskCap)
				{
					StartPlacing();
				}
			}
		});
	}

	private static Project ProjectArg(CallbackArguments args, string function)
	{
		return (args.Count > 0 && !args[0].IsNil()) ? args.AsComponent<Project>(0, function, allowNil: true) : null;
	}

	internal static LandscapeData DataOf(Component component)
	{
		if (component == null)
		{
			return null;
		}
		LandscapeData data = component.GetComponentInParent<LandscapeData>();
		return (data != null) ? data : component.GetComponentInChildren<LandscapeData>();
	}

	internal static bool IsFarm(LandscapeData data)
	{
		return data != null && data.Threshold == FarmCode;
	}

	internal static bool IsFarm(Component component)
	{
		return (component is LandscapeProject || component is LandscapeData || component is LandscapeProjectUI || component is LandscapeGoalProvider) && IsFarm(DataOf(component));
	}

	// The task list name of a farm, or null.
	internal static string NameOf(Project project)
	{
		return IsFarm(project) ? Name : null;
	}
}

// Host: plants and keeps the harvest task, on every landscape task that is a farm.
internal class TreeFarmTick : MonoBehaviour
{
	private const float TickSeconds = 6f;

	// Blueprints put down per tick at most, so a new farm fills over a little while.
	private const int PlantsPerTick = 4;

	// Columns looked at per tick at most (a big box is gone through over several ticks), and per frame: a tick's
	// columns are looked at over the following frames (each column is a walk down its voxels and a look round the
	// spot found), so a tick costs a few ms spread out instead of 20-40 ms in one frame.
	private const int ColumnsPerTick = 400;

	private const int ColumnsPerFrame = 8;

	// A spot the sapling's own placing rules refused is not tried again for this long: a try is a pooled blueprint,
	// its fitter and its validator, and the answer only changes when the ground round it does.
	private const float RefusedSeconds = 60f;

	private static readonly Dictionary<XYZ, float> Refused = new Dictionary<XYZ, float>();

	private LandscapeData _data;

	private float _next;

	private int _cursor;

	private HarvestProject _harvest;

	// The tick's planting under way: its columns, how many are still to look at, what it found so far.
	private User _user;

	private BuildProject _team;

	private VoxelBounds _box;

	private List<XYZ> _columns;

	private int _left;

	private int _planted;

	private int _looked;

	private int _noGround;

	private int _taken;

	private int _invalid;

	private readonly Dictionary<string, int> _reasons = new Dictionary<string, int>();

	private void Start()
	{
		_data = GetComponent<LandscapeData>();
		_next = Time.time + 2f;
		if (Refused.Count > 4096)
		{
			Refused.Clear();
		}
	}

	private void Update()
	{
		if (_data == null || !NetworkServer.active)
		{
			return;
		}
		if (_columns != null)
		{
			PlantSome();
			return;
		}
		if (Time.time < _next)
		{
			return;
		}
		_next = Time.time + TickSeconds;
		if (!TreeFarm.IsFarm(_data))
		{
			DropHarvest();
			return;
		}
		Project project = _data.GetComponentInParent<Project>() ?? _data.GetComponentInChildren<Project>();
		User user = UserOf((project != null) ? project.faction : null);
		if (user == null)
		{
			Report("no player for the task's team");
			return;
		}
		try
		{
			VoxelBounds box = _data.Bounds;
			KeepHarvest(user, box);
			Plant(user, box);
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogWarning("TreeFarm: " + ex.Message);
		}
	}

	private void OnDisable()
	{
		EndPass();
		// Released with its task (deleted): its harvest task goes with it.
		if (NetworkServer.active)
		{
			DropHarvest();
		}
	}

	private void DropHarvest()
	{
		if (_harvest == null)
		{
			return;
		}
		TreeFarm.HarvestBoxes.Remove(_harvest);
		if (_harvest.IsStillAlive)
		{
			NetworkServer.UnSpawn(_harvest.gameObject);
			ObjectPoolSingleton.Release(_harvest.gameObject);
		}
		_harvest = null;
	}

	private static User UserOf(Faction faction)
	{
		if (faction == null)
		{
			return null;
		}
		if (User.LocalUser != null && User.LocalUser.faction == faction)
		{
			return User.LocalUser;
		}
		foreach (User user in FindObjectsOfType<User>())
		{
			if (user != null && user.faction == faction)
			{
				return user;
			}
		}
		return null;
	}

	private static XYZ Centre(VoxelBounds box)
	{
		return new XYZ((box.min.x + box.max.x) / 2, box.max.y, (box.min.z + box.max.z) / 2);
	}

	// The circle round the box.
	private static int RadiusFor(VoxelBounds box)
	{
		float hx = (box.max.x - box.min.x) / 2f + 1f;
		float hz = (box.max.z - box.min.z) / 2f + 1f;
		return Mathf.CeilToInt(Mathf.Sqrt(hx * hx + hz * hz));
	}

	// The tree harvest task round the box: the one already there (a save), else a new one; moved and sized with it.
	private void KeepHarvest(User user, VoxelBounds box)
	{
		XYZ centre = Centre(box);
		int radius = RadiusFor(box);
		if (_harvest == null || !_harvest.IsStillAlive)
		{
			_harvest = Find(user.faction, centre);
		}
		if (_harvest == null)
		{
			UNetGroupe groupe = user.GetComponent<UNetGroupe>();
			GameObject go = groupe.CreateGroup(Groupes.Harvest, centre.ToVector3(), spawnSelected: false);
			go.GetComponent<HarvestData>().Set((int)NatureType.tree);
			NetworkServer.Spawn(go, go.GetComponent<AssetKeyHolder>().AssetKey.AssetId);
			_harvest = go.GetComponent<HarvestProject>();
			if (_harvest != null)
			{
				_harvest.Commit();
			}
			Plugin.Log.LogInfo("TreeFarm: made the tree harvest task for the farm at " + centre + " (radius " + radius + ")");
		}
		if (_harvest == null)
		{
			return;
		}
		TreeFarm.HarvestBoxes[_harvest] = box;
		BeaconDriver driver = _harvest.GetComponent<BeaconDriver>();
		if (driver == null)
		{
			return;
		}
		if (driver.NetworkNetworkRadius != radius)
		{
			driver.NetworkNetworkRadius = radius;
			if (driver.Beacon != null)
			{
				driver.Beacon.radius = radius;
			}
		}
		if (driver.Beacon != null && driver.Beacon.position != centre)
		{
			driver.NetworkNetworkPosition = centre.ToVector3();
			driver.Beacon.SetGameplayPosition(centre.ToVector3());
		}
	}

	private static HarvestProject Find(Faction faction, XYZ centre)
	{
		foreach (HarvestProject project in FindObjectsOfType<HarvestProject>())
		{
			Beacon beacon = (project != null && project.faction == faction) ? project.Beacon : null;
			if (beacon == null || project.AcceptedTypes != NatureType.tree || TreeFarm.HarvestBoxes.ContainsKey(project))
			{
				continue;
			}
			XYZ d = beacon.position - centre;
			if (d.x * d.x + d.z * d.z <= 2)
			{
				return project;
			}
		}
		return null;
	}

	// Starts going through the box's grid columns, a part at a time (PlantSome over the next frames), putting a
	// sapling blueprint where one can grow.
	private void Plant(User user, VoxelBounds box)
	{
		BuildProject team = GlobalBuildJob.TeamProject(user);
		if (team == null)
		{
			Report("no team build task");
			return;
		}
		int spacing = (TreeFarm.Spacing != null) ? TreeFarm.Spacing.Value : 3;
		List<XYZ> columns = new List<XYZ>();
		for (int x = box.min.x; x <= box.max.x; x++)
		{
			if (Mod(x, spacing) != 0)
			{
				continue;
			}
			for (int z = box.min.z; z <= box.max.z; z++)
			{
				if (Mod(z, spacing) == 0)
				{
					columns.Add(new XYZ(x, 0, z));
				}
			}
		}
		if (columns.Count == 0)
		{
			return;
		}
		_user = user;
		_team = team;
		_box = box;
		_columns = columns;
		_left = Mathf.Min(columns.Count, ColumnsPerTick);
		_planted = 0;
		_looked = 0;
		_noGround = 0;
		_taken = 0;
		_invalid = 0;
		_reasons.Clear();
		PlantSome();
	}

	// The next ColumnsPerFrame columns of the tick's pass; the pass ends with its columns or with the tick's plants.
	private void PlantSome()
	{
		if (_user == null || _team == null)
		{
			EndPass();
			return;
		}
		int inFrame = 0;
		try
		{
			while (_left > 0 && _planted < PlantsPerTick && inFrame < ColumnsPerFrame)
			{
				_cursor = (_cursor + 1) % _columns.Count;
				XYZ column = _columns[_cursor];
				_left--;
				_looked++;
				inFrame++;
				if (!FindSpot(column.x, column.z, _box, out XYZ spot, out bool ground))
				{
					if (ground)
					{
						_taken++;
						string reason = LastReason ?? "?";
						_reasons[reason] = (_reasons.TryGetValue(reason, out int n) ? n : 0) + 1;
					}
					else
					{
						_noGround++;
					}
					continue;
				}
				if (Refused.TryGetValue(spot, out float until) && Time.time < until)
				{
					_invalid++;
					continue;
				}
				if (PlaceSapling(_user, _team, spot))
				{
					_planted++;
					Refused.Remove(spot);
				}
				else
				{
					_invalid++;
					Refused[spot] = Time.time + RefusedSeconds;
				}
			}
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogWarning("TreeFarm: " + ex.Message);
			EndPass();
			return;
		}
		if (_left > 0 && _planted < PlantsPerTick)
		{
			return;
		}
		if (_planted == 0)
		{
			Report(_looked + " spots looked at in box " + _box.min + " - " + _box.max + ": " + _noGround + " without ground, " + _taken + " taken (" + string.Join(", ", System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(_reasons, (KeyValuePair<string, int> r) => r.Key + " " + r.Value))) + "), " + _invalid + " refused by the sapling's placing rules");
		}
		EndPass();
	}

	private void EndPass()
	{
		_columns = null;
		_user = null;
		_team = null;
	}

	private float _nextReport;

	// Why nothing was planted, now and then.
	private void Report(string why)
	{
		if (Time.time < _nextReport)
		{
			return;
		}
		_nextReport = Time.time + 120f;
		Plugin.Log.LogInfo("TreeFarm: nothing planted at " + Centre(_data.Bounds) + ": " + why);
	}

	private static int Mod(int a, int b)
	{
		return ((a % b) + b) % b;
	}

	// The free cell on the ground in the column, within the box's height.
	// Why the last spot FindSpot found was taken.
	private static string LastReason;

	private static bool FindSpot(int x, int z, VoxelBounds box, out XYZ spot, out bool ground)
	{
		ground = false;
		for (int y = box.max.y + 1; y >= box.min.y; y--)
		{
			XYZ cell = new XYZ(x, y, z);
			if (!Voxel.IsEmpty(cell))
			{
				continue;
			}
			IVoxel below = Voxel.GetTerrainBlockData(cell - XYZ.up);
			if (below == null || below.IsNull)
			{
				continue;
			}
			// Rounded ground (IsCrust: a smooth voxel) is most natural ground; the sapling's own placing rules
			// (PlaceSapling) decide whether it can stand there.
			below.Dispose();
			ground = true;
			spot = cell;
			LastReason = WhyTaken(cell);
			return LastReason == null;
		}
		spot = XYZ.zero;
		return false;
	}

	private static bool IsFree(XYZ cell)
	{
		return WhyTaken(cell) == null;
	}

	// Why a sapling cannot go in the cell, or null.
	internal static string WhyTaken(XYZ cell)
	{
		if (!Voxel.IsEmpty(cell + XYZ.up))
		{
			return "no room above";
		}
		if (TreeRegistry.Instance == null || TreeRegistry.Instance.IsNearATree(cell))
		{
			return "near a tree";
		}
		if (DamagedTree.AllDamagedTrees.ContainsKey(cell))
		{
			return "a felled tree";
		}
		for (int dx = -1; dx <= 1; dx++)
		{
			for (int dz = -1; dz <= 1; dz++)
			{
				XYZ near = cell + new XYZ(dx, 0, dz);
				if (Saplings.Growth.ContainsKey(near))
				{
					return "a sapling near";
				}
				if (VolumeRegistry.HasVolumesWithPropertyAt(near, VPropertyMask.occupy | VPropertyMask.fantome))
				{
					return "a building or blueprint near";
				}
			}
		}
		return null;
	}

	// As the player's placing does (UNetBlueprint._PrepareSpawnBlueprint, fitted to the cell), into the team's build task.
	private static bool PlaceSapling(User user, BuildProject team, XYZ cell)
	{
		if (!ObjectPoolSingleton.RequestComponent<Blueprint>(out Blueprint blueprint, Saplings.Building.BlueprintKey, cell.ToVector3(), Quaternion.identity) || blueprint == null)
		{
			return false;
		}
		blueprint.affiliation.NetworkUserCreator = user.gameObject;
		blueprint.faction = user.faction;
		IFitter fitter = blueprint.gameObject.GetInterface<IFitter>();
		if (fitter != null)
		{
			if (fitter is AbstractFitter abstractFitter)
			{
				abstractFitter._rotation = Quaternion.identity;
			}
			fitter.FitAndOrient(new VoxelRaycastHit().PlaceAtVector(cell.ToVector3()));
		}
		ValidatorComponent validator = blueprint.GetComponent<ValidatorComponent>();
		ValidationResult result = (validator != null) ? validator.Validate() : ValidationResult.Valid;
		if ((result & (ValidationResult.Valid | ValidationResult.Buildable)) == 0)
		{
			ObjectPoolSingleton.Release(blueprint.gameObject);
			return false;
		}
		user.GetComponent<UNetBlueprint>()._FinishSpawnBlueprint(blueprint, instant: false, team.gameObject);
		GlobalBuildJob.Commit(blueprint);
		return true;
	}
}

// Every landscape task gets the farm tick (it does nothing unless the task is a farm).
[Feature(Features.Saplings, Features.SaplingsInfo)]
[HarmonyPatch(typeof(LandscapeData), nameof(LandscapeData.OnStartAnywhere))]
internal static class TreeFarmStartPatch
{
	private static void Postfix(LandscapeData __instance)
	{
		if (__instance.GetComponent<TreeFarmTick>() == null)
		{
			__instance.gameObject.AddComponent<TreeFarmTick>();
		}
	}
}

// The game's landscape button and hotkey start a landscape task, not a farm.
[Feature(Features.Saplings, Features.SaplingsInfo)]
[HarmonyPatch(typeof(UIGameObserver), "lua_NewLandscapeTask")]
internal static class TreeFarmLandscapeButtonPatch
{
	private static void Prefix()
	{
		TreeFarm.PlacingFarm = false;
	}
}

// The landscape button is not lit while a farm is placed.
[Feature(Features.Saplings, Features.SaplingsInfo)]
[HarmonyPatch(typeof(UIGameObserver), "lua_IsPlacingNewLandscapeTask")]
internal static class TreeFarmLandscapeHighlightPatch
{
	private static void Postfix(ref DynValue __result)
	{
		if (TreeFarm.PlacingFarm)
		{
			__result = DynValue.False;
		}
	}
}

// Client or host: placing from the "Tree farm" button asks the host for a farm.
[Feature(Features.Saplings, Features.SaplingsInfo)]
[HarmonyPatch(typeof(LandscapeProjectPlacementPicker), "NetworkCreate")]
internal static class TreeFarmPlacePatch
{
	private static bool Prefix(LandscapeProjectPlacementPicker __instance)
	{
		if (!TreeFarm.PlacingFarm || User.LocalUser == null)
		{
			return true;
		}
		User.LocalUser.GetComponent<UNetGroupe>().CallCmdSpawnLandscapeProject(__instance._voxelRayHit.HitVector, TreeFarm.FarmRotation, spawnSelected: true);
		return false;
	}
}

// Host: a farm's spawn command.
[Feature(Features.Saplings, Features.SaplingsInfo)]
[HarmonyPatch(typeof(UNetGroupe), nameof(UNetGroupe.CmdSpawnLandscapeProject))]
internal static class TreeFarmSpawnPatch
{
	private static void Prefix(Quaternion rotation)
	{
		TreeFarm.SpawningFarm = Quaternion.Dot(rotation, TreeFarm.FarmRotation) > 0.999f;
	}

	private static void Finalizer()
	{
		TreeFarm.SpawningFarm = false;
	}
}

// Host: the new farm's box (the game's default) is marked as a farm.
[Feature(Features.Saplings, Features.SaplingsInfo)]
[HarmonyPatch(typeof(LandscapeGoalProvider), nameof(LandscapeGoalProvider.SetDefault))]
internal static class TreeFarmDefaultPatch
{
	private static void Postfix(LandscapeGoalProvider __instance)
	{
		if (!TreeFarm.SpawningFarm || __instance.data == null)
		{
			return;
		}
		LandscapeData data = __instance.data;
		data.Set(data.NetworkboundsMin, data.NetworkboundsMax, TreeFarm.FarmCode);
	}
}

// Host: a farm stays a farm when its box is dragged (the landscape UI sends its height plane with the box).
[Feature(Features.Saplings, Features.SaplingsInfo)]
[HarmonyPatch(typeof(UNetGroupe), nameof(UNetGroupe.CmdSetLandscape))]
internal static class TreeFarmSetPatch
{
	private static void Prefix(GameObject instance, ref int threshold)
	{
		if (!instance.IsNullOrReleased() && TreeFarm.IsFarm(instance.GetComponent<LandscapeData>()))
		{
			threshold = TreeFarm.FarmCode;
		}
	}
}

// A farm never digs or fills.
[Feature(Features.Saplings, Features.SaplingsInfo)]
[HarmonyPatch(typeof(LandscapeGoalProvider), "VoxelNeedsGoal")]
internal static class TreeFarmNoGoalsPatch
{
	private static bool Prefix(LandscapeGoalProvider __instance, ref SpawnOrder __result)
	{
		if (!TreeFarm.IsFarm(__instance.data))
		{
			return true;
		}
		__result = SpawnOrder.None;
		return false;
	}
}

// A farm is not deleted for having no goals (a landscape task is, once levelled).
[Feature(Features.Saplings, Features.SaplingsInfo)]
[HarmonyPatch(typeof(LandscapeProject), "CheckForAutoDelete")]
internal static class TreeFarmKeepPatch
{
	private static bool Prefix(LandscapeProject __instance)
	{
		return !TreeFarm.IsFarm(__instance);
	}
}

// A farm's box shows without the landscape height plane.
[Feature(Features.Saplings, Features.SaplingsInfo)]
[HarmonyPatch]
internal static class TreeFarmPlanePatch
{
	private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
	{
		yield return AccessTools.PropertySetter(typeof(LandscapeProjectUI), nameof(LandscapeProjectUI.ShowThreshold));
		yield return AccessTools.PropertySetter(typeof(LandscapeProjectUI), nameof(LandscapeProjectUI.Showing));
	}

	private static void Postfix(LandscapeProjectUI __instance)
	{
		if (TreeFarm.IsFarm(__instance) && __instance.threshold != null)
		{
			__instance.threshold.Showing = false;
		}
	}
}

// A farm's harvest task fells only the trees in the farm's box.
[Feature(Features.Saplings, Features.SaplingsInfo)]
[HarmonyPatch(typeof(TreeGoalProvider), "AddNewGoals")]
internal static class TreeFarmHarvestBoxPatch
{
	private static void Prefix(TreeGoalProvider __instance, HashSet<XYZ> registeredTrees)
	{
		if (TreeFarm.HarvestBoxes.Count == 0)
		{
			return;
		}
		HarvestProject project = __instance.GetComponentInParent<HarvestProject>();
		if (project == null || !TreeFarm.HarvestBoxes.TryGetValue(project, out VoxelBounds box))
		{
			return;
		}
		registeredTrees.RemoveWhere((XYZ tree) => tree.x < box.min.x || tree.x > box.max.x || tree.z < box.min.z || tree.z > box.max.z);
	}
}
