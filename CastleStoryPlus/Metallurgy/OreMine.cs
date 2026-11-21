using System.Collections.Generic;
using Brix.Common.Collections;
using Brix.Engine;
using Brix.Engine.Nature;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Beacons;
using Brix.Game.Components;
using Brix.Game.Network;
using Brix.Input;
using Brix.Legacy;
using Brix.Lifecycle.Pooling;
using Brix.UI.Icons;
using Brix.Utils;
using CastleStoryPlus.Core;
using HarmonyLib;
using Motus.Behavior;
using Smooth.Slinq;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Metallurgy;

// An ore mine: "Ore mine" in the new task menu's harvest tasks, after "Brimstone". It is placed and sized like a
// harvest task (a sphere round its beacon), and its bricktrons dig the ore in the sphere they can get at: orange
// crystal, iron, blue crystal and coal blocks that touch an empty space (a tunnel or quarry wall, a cave, the
// surface). Digging one out lays the ore behind it bare, so a vein is followed into the rock. It digs no stone;
// ore no bricktron can stand next to is left. Everything dug is kept.
//
// It is a harvest task whose accepted nature types are only a flag of our own (OreHarvest): saved and synced with
// the task, no nature is harvested for it, and the unmodded game sees a harvest task that harvests nothing. The
// host keeps its dig goals (OreHarvestGoals), the way a tunnel keeps them for its volume.
//
// The first ore mines were quarries marked by a flag of their resource setting (OreOnly); those still dig only
// ore, but new ones are harvest tasks.
[Feature(Features.OreMine, Features.OreMineInfo)]
internal static class OreMine
{
	internal const int OreOnly = 1 << 10;

	internal const int OreHarvest = 1 << 8;

	internal const string Name = "Ore mine";

	// Client: the harvest tool was started from the "Ore mine" button.
	internal static bool PlacingOreMine;

	private static void Enable()
	{
		GameSession.OnLeave(() => PlacingOreMine = false);
		GameUi.GameMenuItems.Extend(AddToMenu);
		GameUi.GameMenuTexts.NameOverrides.Add((Project project) => IsOreMine(project) ? Name : null);
		GameUi.GameMenuTexts.IconOverrides.Add((Project project) => IsOreMine(project) ? IconKeys._Raw_Orange_Crystal.Get64() : null);
	}

	private static void StartPlacing()
	{
		Instructions.Execute("Tool_HarvestIron");
		PlacingOreMine = true;
	}

	// The C# in-game menu: the new task menu's button, after "Brimstone".
	private static void AddToMenu(GameUi.GameMenuModel menu)
	{
		menu.NewTask.Find("newHarvestTask").Insert("newHarvestBrimstoneTask", new GameUi.UiButton
		{
			Id = "newOreMineTask",
			Label = () => Name,
			Icon = () => GameUi.GameUiState.Icon(IconKeys._Raw_Orange_Crystal),
			IconColor = () => GameUi.GameUiState.UnderTaskCap ? LuaUi.LuiStyle.White : LuaUi.LuiStyle.Gray,
			Tooltip = () => "Ore mine: bricktrons dig the ore in the sphere they can get at (in tunnel and quarry walls, caves), no stone",
			Highlight = () => PlacingOreMine && InputModeController.HarvestBeingPlaced() == NatureType.iron,
			OnClick = (bool shift, bool ctrl) =>
			{
				if (!GameUi.GameUiState.Paused && GameUi.GameUiState.UnderTaskCap)
				{
					StartPlacing();
				}
			}
		});
	}

	internal static bool IsOreMine(QuarryData data)
	{
		return data != null && ((int)data.TypesToHarvest & OreOnly) != 0;
	}

	internal static bool IsOreMine(HarvestProject project)
	{
		return project != null && project.data != null && ((int)project.AcceptedTypes & OreHarvest) != 0;
	}

	internal static bool IsOreMine(Project project)
	{
		if (project is HarvestProject harvest)
		{
			return IsOreMine(harvest);
		}
		return project is MineProject && IsOreMine(project.GetComponent<QuarryData>());
	}

	internal static bool IsOre(XYZ pos)
	{
		Factory.AssetKey key = Voxel.GetBlockInfo(pos).ResourceKey;
		return key != null && (key == RawResources.RawOrangeCrystal || key == RawResources.RawIron || key == RawResources.RawBlueCrystal || key == Metallurgy.CoalKey);
	}

	// Ore a bricktron can dig: terrain ore touching an empty space.
	internal static bool IsBareOre(XYZ pos)
	{
		return IsOre(pos) && Voxel.IsTerrain(pos) && !Voxel.IsEmpty(pos) && !Voxel.IsInvincible(pos) && !Voxel.IsBlock(pos) && Voxel.HasEmptyNeighbor(pos)
			&& !NatureRegistry.HasNatureBlockerAt(pos) && !NatureRegistry.HasNatureBlockerAt(pos + XYZ.up);
	}
}

// Host: the dig goals of an ore mine (a harvest task with the OreHarvest flag), for the bare ore within its beacon's
// radius. The ore is looked for on another thread in the mine's own copy of the voxels (OreSnapshot): the main thread
// fills the copy once (a slice of rows per frame, 2% of a frame's time, at most 0.3 ms) and again every RefillSeconds,
// the game's voxel change signals keep it up to date in between, and every RefreshSeconds a pool thread goes over the
// sphere in it. The main thread then checks only the cells it found with the full rules (Wants: the crew's reach map,
// trees and boulders too) before it makes a goal, also spread over frames; goals no longer wanted (dug, out of the
// sphere) are dropped at the end of a pass. Before, the main thread went over the whole sphere itself every pass.
internal class OreHarvestGoals : MonoBehaviour
{
	private const float RefreshSeconds = 5f;

	// The copy is filled again from the game's voxels this often, in case a change went by without a signal.
	private const float RefillSeconds = 120f;

	// Time the work may take per frame: a share of the last frame's time, within these bounds (at least one row or
	// cell), so a fast frame rate is not held back by a fixed slice.
	private const float FrameShare = 0.02f;

	private const float MinMillisecondsPerFrame = 0.05f;

	private const float MaxMillisecondsPerFrame = 0.3f;

	private static readonly System.Diagnostics.Stopwatch Watch = new System.Diagnostics.Stopwatch();

	internal VoxelGoalsStorage Storage;

	private HarvestProject _project;

	private XYZ _centre;

	private int _radius;

	private float _next;

	private OreSnapshot _snapshot;

	private float _refillAt;

	// A pass is under way: search once the copy is filled, then make the goals.
	private bool _passing;

	// The cells the search found and the next one to check.
	private List<XYZ> _found;

	private int _foundIndex;

	// Cells the full rules turned down (no place to stand, not safe to dig), not checked again before then. Every pass
	// went over them all again, some hundreds of checks every 5 seconds that kept the pass running nearly every frame.
	private readonly Dictionary<XYZ, float> _turnedDown = new Dictionary<XYZ, float>();

	private const float TurnedDownSeconds = 30f;

	internal static VoxelGoalsStorage StorageOf(HarvestProject project)
	{
		OreHarvestGoals goals = (project != null) ? project.GetComponent<OreHarvestGoals>() : null;
		return (goals != null) ? goals.Storage : null;
	}

	private void Awake()
	{
		_project = GetComponent<HarvestProject>();
	}

	private void Update()
	{
		if (!NetworkServer.active || _project == null)
		{
			return;
		}
		if (Storage != null && _snapshot != null && _passing)
		{
			Pass();
			return;
		}
		if (Time.time < _next)
		{
			return;
		}
		_next = Time.time + RefreshSeconds;
		Beacon beacon = _project.Beacon;
		if (!OreMine.IsOreMine(_project) || beacon == null)
		{
			Drop();
			return;
		}
		_centre = beacon.position;
		_radius = Mathf.Max(1, Mathf.RoundToInt(beacon.radius));
		if (Storage == null)
		{
			XYZ reach = new XYZ(_radius, _radius, _radius);
			Storage = new VoxelGoalsStorage(new VoxelBounds(_centre - reach, _centre + reach), gameObject, Test.DigGoal, Setup, Wants);
			Storage.DigGoalSetting = DefaultDigGoalResourceSetting.Get;
			_project.GoalProviders.Add(Storage);
		}
		if (_snapshot == null || _snapshot.Centre != _centre || _snapshot.Radius != _radius)
		{
			_snapshot?.Dispose();
			_snapshot = new OreSnapshot(_centre, _radius);
			_refillAt = Time.time + RefillSeconds;
		}
		else if (Time.time >= _refillAt)
		{
			_snapshot.Refill();
			_refillAt = Time.time + RefillSeconds;
		}
		_passing = true;
		_found = null;
	}

	// One frame's share of a pass: fill the copy, search it on a pool thread, make the goals of what it found.
	private void Pass()
	{
		Watch.Reset();
		Watch.Start();
		double budget = Mathf.Clamp(Time.unscaledDeltaTime * 1000f * FrameShare, MinMillisecondsPerFrame, MaxMillisecondsPerFrame);
		if (!_snapshot.Filled)
		{
			while (_snapshot.FillRow() && Watch.Elapsed.TotalMilliseconds < budget)
			{
			}
			return;
		}
		if (_found == null)
		{
			_found = _snapshot.TakeFound();
			if (_found == null)
			{
				_snapshot.StartSearch();
				return;
			}
			_foundIndex = 0;
		}
		while (_foundIndex < _found.Count)
		{
			XYZ cell = _found[_foundIndex++];
			// A cell with a goal already needs nothing (most of them, after the first pass).
			if (Storage.goalsPerVoxel.ContainsKey(cell) || (_turnedDown.TryGetValue(cell, out float until) && Time.time < until))
			{
				continue;
			}
			Storage.SetBounds(new VoxelBounds(cell, cell));
			Storage.FetchNewGoals();
			if (Storage.goalsPerVoxel.ContainsKey(cell))
			{
				_turnedDown.Remove(cell);
			}
			else
			{
				_turnedDown[cell] = Time.time + TurnedDownSeconds;
			}
			if (Watch.Elapsed.TotalMilliseconds >= budget)
			{
				return;
			}
		}
		XYZ size = new XYZ(_radius, _radius, _radius);
		Storage.SetBounds(new VoxelBounds(_centre - size, _centre + size));
		Storage.RemoveOutdatedGoals();
		_found = null;
		_passing = false;
		// The next pass after a rest.
		_next = Time.time + RefreshSeconds;
	}

	private bool Wants(XYZ pos)
	{
		XYZ d = pos - _centre;
		// Never a quarry's stair step or the ground under one (ore in a quarry wall is often right there).
		return d.x * d.x + d.y * d.y + d.z * d.z <= _radius * _radius && OreMine.IsBareOre(pos) && HasStandingPlace(pos) && SafeToDig(pos) && !WorkerAI.QuarryStairs.Keeps(pos);
	}

	private static readonly XYZ[] Sides = { new XYZ(1, 0, 0), new XYZ(-1, 0, 0), new XYZ(0, 0, 1), new XYZ(0, 0, -1) };

	// A place on the crew's routes a bricktron can stand to dig the block: beside it at its height or one below (the
	// block at chest or head height), or on top of it. Ore with none (in a ceiling, high in a wall) would only have
	// the crew walk about.
	private bool HasStandingPlace(XYZ pos)
	{
		if (CanStand(pos + XYZ.up, pos) && Known(pos + XYZ.up))
		{
			return true;
		}
		foreach (XYZ side in Sides)
		{
			XYZ level = pos + side;
			XYZ below = pos + side - XYZ.up;
			if ((CanStand(level, pos) && Known(level)) || (CanStand(below, pos) && Known(below)))
			{
				return true;
			}
		}
		return false;
	}

	// The crew never digs itself off its own routes: with the block gone, a bricktron landing in its place (or falling
	// one block below it) has a way back up to a place on the routes, every place it can step to from there is on the
	// routes already (no cave or pit nobody can get out of is opened up), and there is no drop of two blocks or more
	// beside it. A block whose space stays too low to walk in opens nothing.
	private bool SafeToDig(XYZ pos)
	{
		XYZ landing = pos;
		if (IsEmptyWithout(landing - XYZ.up, pos))
		{
			landing -= XYZ.up;
			if (IsEmptyWithout(landing - XYZ.up, pos))
			{
				return false;
			}
		}
		if (!IsEmptyWithout(landing + XYZ.up, pos))
		{
			return landing == pos;
		}
		bool wayBack = false;
		foreach (XYZ side in Sides)
		{
			XYZ beside = landing + side;
			if (IsEmptyWithout(beside, pos) && IsEmptyWithout(beside - XYZ.up, pos) && IsEmptyWithout(beside - XYZ.up - XYZ.up, pos))
			{
				return false;
			}
			for (int dy = -1; dy <= 1; dy++)
			{
				XYZ step = beside + new XYZ(0, dy, 0);
				if (!CanStand(step, pos))
				{
					continue;
				}
				// Stepping up needs room over the landing's head as well.
				if (dy == 1 && !IsEmptyWithout(landing + XYZ.up + XYZ.up, pos))
				{
					continue;
				}
				if (!Known(step))
				{
					return false;
				}
				wayBack = true;
			}
		}
		return wayBack;
	}

	// On the crew's routes (the mod's map of where the faction's workers can walk); without the map, taken as yes.
	private bool Known(XYZ place)
	{
		Faction faction = (_project != null) ? _project.faction : null;
		return faction == null || !Pathfinding.ReachMap.Has(faction) || Pathfinding.ReachMap.Contains(faction, place);
	}

	// Empty once the block at dug is gone.
	private static bool IsEmptyWithout(XYZ cell, XYZ dug)
	{
		return cell == dug || Voxel.IsEmpty(cell);
	}

	private static bool CanStand(XYZ cell, XYZ dug)
	{
		return IsEmptyWithout(cell, dug) && IsEmptyWithout(cell + XYZ.up, dug) && !IsEmptyWithout(cell - XYZ.up, dug);
	}

	private void Setup(Goal goal)
	{
		if (goal is DigGoal dig)
		{
			// Nearest first, not top first as in a quarry.
			dig.UseHeightAdjustment = false;
		}
		goal.OnReleaseSignal.Connect((OreHarvestGoals self, Goal g) =>
		{
			if (self.Storage != null)
			{
				self.Storage.RemoveGoal(g);
			}
		}, this, goal);
	}

	private void OnDisable()
	{
		Drop();
	}

	// Released with its task, or no longer an ore mine: its goals go.
	private void Drop()
	{
		_snapshot?.Dispose();
		_snapshot = null;
		_passing = false;
		_found = null;
		if (Storage == null)
		{
			return;
		}
		VoxelGoalsStorage storage = Storage;
		Storage = null;
		if (_project != null)
		{
			_project.GoalProviders.Remove(storage);
		}
		List<Goal> goals = new List<Goal>();
		storage.GetAll(goals);
		foreach (Goal goal in goals)
		{
			if (!goal.IsNullOrReleased())
			{
				ObjectPoolSingleton.Release(goal.gameObject);
			}
		}
	}
}

// Every harvest task gets the ore goals keeper (it does nothing unless the task is an ore mine).
[Feature(Features.OreMine, Features.OreMineInfo)]
[HarmonyPatch(typeof(HarvestProject), "Awake")]
internal static class OreMineHarvestAwakePatch
{
	private static void Postfix(HarvestProject __instance)
	{
		if (__instance.GetComponent<OreHarvestGoals>() == null)
		{
			__instance.gameObject.AddComponent<OreHarvestGoals>();
		}
	}
}

// An ore mine's bricktrons choose among its dig goals.
[Feature(Features.OreMine, Features.OreMineInfo)]
[HarmonyPatch(typeof(HarvestProject), "SelectBestGoal")]
[HarmonyPriority(Priority.First)]
internal static class OreMineSelectPatch
{
	private static bool Prefix(HarvestProject __instance, Labor labor, Var<Goal> result, ref Node __result)
	{
		VoxelGoalsStorage storage = OreHarvestGoals.StorageOf(__instance);
		if (storage == null || !OreMine.IsOreMine(__instance))
		{
			return true;
		}
		__result = __instance.SelectBestGoalPooledList(__instance.GoalsNeedingEvaluation(storage.All(), labor).NoLeak().GetEnumerator(), labor, result);
		return false;
	}
}

// An ore mine is not deleted when its crew leaves (a harvest task is, once it has no goals): ore out of reach now
// can be reached later (a ladder, more digging), so it stays until the player deletes it.
[Feature(Features.OreMine, Features.OreMineInfo)]
[HarmonyPatch(typeof(HarvestProject), "CheckForAutoDelete")]
internal static class OreMineKeepPatch
{
	private static bool Prefix(HarvestProject __instance)
	{
		return !OreMine.IsOreMine(__instance);
	}
}

// Any other tool (the harvest buttons or hotkeys among them) places what it always did.
[Feature(Features.OreMine, Features.OreMineInfo)]
[HarmonyPatch(typeof(Instructions), nameof(Instructions.Execute))]
internal static class OreMineToolPatch
{
	private static void Prefix()
	{
		OreMine.PlacingOreMine = false;
	}
}

// Client or host: placing from the "Ore mine" button asks the host for a harvest task with only the ore flag.
[Feature(Features.OreMine, Features.OreMineInfo)]
[HarmonyPatch(typeof(HarvestProjectPlacementPicker), "NetworkCreate")]
internal static class OreMinePlacePatch
{
	private static bool Prefix(HarvestProjectPlacementPicker __instance)
	{
		if (!OreMine.PlacingOreMine || User.LocalUser == null)
		{
			return true;
		}
		User.LocalUser.GetComponent<UNetGroupe>().CallCmdSpawnHarvestProject(__instance._voxelRayHit.HitVector, Quaternion.identity, spawnSelected: true, OreMine.OreHarvest);
		return false;
	}
}

// The first ore mines (quarries): the resource buttons set the whole setting, so an ore mine keeps its mark.
[Feature(Features.OreMine, Features.OreMineInfo)]
[HarmonyPatch(typeof(QuarryData), nameof(QuarryData.SetTypesToHarvest))]
internal static class OreMineKeepMarkPatch
{
	private static void Prefix(QuarryData __instance, ref int typesToHarvest)
	{
		if (OreMine.IsOreMine(__instance))
		{
			typesToHarvest |= OreMine.OreOnly;
		}
	}
}

// The first ore mines (quarries) dig only bare ore, and build no stairs.
[Feature(Features.OreMine, Features.OreMineInfo)]
[HarmonyPatch(typeof(QuarryGoalProvider2), "VoxelNeedsGoal")]
internal static class OreMineGoalPatch
{
	private static bool Prefix(QuarryGoalProvider2 __instance, XYZ pos, ref SpawnOrder __result)
	{
		if (!OreMine.IsOreMine(__instance.data))
		{
			return true;
		}
		__result = OreMine.IsBareOre(pos) ? new SpawnOrder(pos, XYZ.zero, Test.DigGoal) : SpawnOrder.None;
		return false;
	}
}

[Feature(Features.OreMine, Features.OreMineInfo)]
[HarmonyPatch(typeof(QuarryGoalProvider2), "RefreshStairs")]
internal static class OreMineNoStairsPatch
{
	private static void Postfix(QuarryGoalProvider2 __instance)
	{
		if (OreMine.IsOreMine(__instance.data))
		{
			__instance.stairs.Clear();
		}
	}
}

[Feature(Features.OreMine, Features.OreMineInfo)]
[HarmonyPatch(typeof(QuarryProjectUI), nameof(QuarryProjectUI.FromProvider))]
internal static class OreMineStairDrawerPatch
{
	private static void Postfix(QuarryProjectUI __instance, QuarryGoalProvider2 provider)
	{
		if (OreMine.IsOreMine(provider.data) && __instance.stairDrawer != null)
		{
			__instance.stairDrawer.Redraw(new List<XYZ>(), new List<Faces>());
		}
	}
}
