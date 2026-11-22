using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using Brix.Components;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.Components;
using Brix.Legacy;
using Brix.Lifecycle.Pooling;
using CastleStoryPlus.Core;
using HarmonyLib;
using Smooth.Slinq;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.WorkerAI;

// Workers keep their own work to the base (#181). Now and then a single bricktron set out on its own across the map,
// to the ruins with corruptrons on them, and drew them back with it. The game's search for the nearest thing holding a
// resource (Knowledge.ClosestObjectToPickup: a builder's material, a workshop's ingredient) and for the nearest storage
// with room (Knowledge.ClosestStorageWithRoomFor) look across the whole map at the team's own things and at everything
// no player owns: the loot of an opened treasure chest, a treasure chest itself, loose blocks and items lying anywhere.
// With no such thing in the base, the nearest one could be hundreds of blocks out, next to enemies. Now those searches
// keep to the base area:
// - something no player owns is only taken in the base area and with no enemy near it;
// - the team's own things are taken anywhere (logs at a far woodcutting task) unless enemies stand near them outside
//   the base area; in the base area they always are (a siege does not stop the builders).
// The base area is every place within [BaseArea] Radius blocks (across) of the home crystal, the gather point or one
// of the team's stockpiles, and every task zone of the team (its circle and [BaseArea] ZoneMargin round it): the
// player's own tasks are where the player wants work done. Enemies are the living units of an enemy faction within
// [BaseArea] EnemyDistance blocks. A player's order (picking an item up, a task) is not limited.
// Also a diagnostic: a worker of a player that is out of the base area with no player's order sending it there gets a
// line in BepInEx/idle.log (and the BepInEx log) once per errand: where it is, how far out, its task, the thing it goes
// for and what it last chose in one of these searches, so a report of a wandering bricktron can be traced.
// Host only: the workers' decisions run there, so in multiplayer the host's switch and settings apply.
[Feature(Features.BaseArea, Features.BaseAreaInfo)]
internal sealed class BaseArea : MonoBehaviour
{
	internal static ConfigEntry<int> Radius;

	internal static ConfigEntry<int> ZoneMargin;

	internal static ConfigEntry<int> EnemyDistance;

	private const float AnchorSeconds = 2f;

	private const float EnemySeconds = 1f;

	private const float WatchSeconds = 2f;

	// A player's order keeps its worker off the log this long after it was given (and as long as its task runs).
	private const float PlayerOrderSeconds = 60f;

	// How far out of the base area a worker may be before it is logged (one on the edge goes in and out of it).
	private const float ReportSlack = 4f;

	// How long what a worker chose in a search is named with it in the log.
	private const float ChoiceSeconds = 60f;

	private const int RecentLines = 50;

	private static bool _enabled;

	// The base area of each team: x, z, radius three by three, and when it was worked out.
	private static readonly Dictionary<Faction, List<float>> Anchors = new Dictionary<Faction, List<float>>();

	private static readonly Dictionary<Faction, float> AnchorsAt = new Dictionary<Faction, float>();

	// The living units of the AI factions: x, y, z three by three, and the faction of each.
	private static readonly List<float> EnemyPoints = new List<float>();

	private static readonly List<Faction> EnemyFactions = new List<Faction>();

	private static float _enemiesAt = float.MinValue;

	// The worker and team of the search running now (null: none, or not a worker's own search).
	private static Faction _searchFaction;

	// The player's last order to each worker: when, and its task.
	private static readonly Dictionary<Labor, float> PlayerOrderAt = new Dictionary<Labor, float>();

	private static readonly Dictionary<Labor, Task> PlayerTasks = new Dictionary<Labor, Task>();

	// What each worker last chose out of the base area in a search, and when.
	private static readonly Dictionary<Labor, string> Choices = new Dictionary<Labor, string>();

	private static readonly Dictionary<Labor, float> ChoicesAt = new Dictionary<Labor, float>();

	// The errand each worker out of the base area was logged for (its task and target), until it is back.
	private static readonly Dictionary<Labor, string> Reported = new Dictionary<Labor, string>();

	// The latest lines, for the developer tools.
	internal static readonly List<string> Recent = new List<string>();

	private readonly List<Labor> _labors = new List<Labor>();

	private float _nextWatch;

	private static void Enable()
	{
		Radius = Plugin.Cfg.Bind("BaseArea", "Radius", 40, new ConfigDescription("Workers' own searches for material and storage take nothing no player owns further than this many blocks from the home crystal, the gather point and the team's stockpiles (task zones count as base too).", new AcceptableValueRange<int>(10, 500)));
		ZoneMargin = Plugin.Cfg.Bind("BaseArea", "ZoneMargin", 8, new ConfigDescription("How many blocks round a task's zone count as base area too.", new AcceptableValueRange<int>(0, 100)));
		EnemyDistance = Plugin.Cfg.Bind("BaseArea", "EnemyDistance", 20, new ConfigDescription("Workers' own searches take nothing with a living enemy within this many blocks of it (the team's own things in the base area excepted).", new AcceptableValueRange<int>(0, 100)));
		Radius.SettingChanged += (object sender, System.EventArgs args) => Refresh();
		ZoneMargin.SettingChanged += (object sender, System.EventArgs args) => Refresh();
		GameSession.OnLeave(() =>
		{
			Refresh();
			PlayerOrderAt.Clear();
			PlayerTasks.Clear();
			Choices.Clear();
			ChoicesAt.Clear();
			Reported.Clear();
			_searchFaction = null;
		});
		_enabled = true;
		Plugin.Root.AddComponent<BaseArea>();
	}

	// The base areas and enemies are worked out again at the next question.
	internal static void Refresh()
	{
		Anchors.Clear();
		AnchorsAt.Clear();
		_enemiesAt = float.MinValue;
	}

	// ---- the base area

	private static List<float> AnchorsOf(Faction faction)
	{
		if (Anchors.TryGetValue(faction, out List<float> cached) && AnchorsAt.TryGetValue(faction, out float at) && Time.time - at < AnchorSeconds)
		{
			return cached;
		}
		List<float> list = cached ?? new List<float>();
		list.Clear();
		float radius = Radius.Value;
		FireflyNest nest = HomeCrystal.Of(faction);
		if (nest != null)
		{
			Add(list, nest.transform.position, radius);
		}
		if (faction.Gatherpoint != Vector3.zero)
		{
			Add(list, faction.Gatherpoint, radius);
		}
		HashSet<GameObject> stockpiles = BrixSingleton<AutoList>.Instance?.GetInstances(ObjetsDynamiques.Palette);
		if (stockpiles != null)
		{
			foreach (GameObject pile in stockpiles)
			{
				if (pile == null || !pile.activeInHierarchy)
				{
					continue;
				}
				GameComponent component = pile.GetComponent<GameComponent>();
				if (component != null && component.faction == faction)
				{
					Add(list, pile.transform.position, radius);
				}
			}
		}
		// The team's task zones (harvest, quarry, cleanup...): not the idle task (the gather point is in already) nor
		// a raid's target, which is where the soldiers go, not the workers.
		List<IOperable<Labor>> projects = new List<IOperable<Labor>>();
		ProjectDatabase.For(faction).Projects().AddTo(projects);
		foreach (IOperable<Labor> operable in projects)
		{
			if (!(operable is Project project) || !project.IsStillAlive || project is IdleProject || project is RaidProject2)
			{
				continue;
			}
			Beacon beacon = project.Beacon;
			if (beacon != null)
			{
				Add(list, beacon.position.ToVector3(), beacon.radius + ZoneMargin.Value);
			}
		}
		Anchors[faction] = list;
		AnchorsAt[faction] = Time.time;
		return list;
	}

	// A unit that is still in the game and not a corpse (as GiantBricktron.IsAlive, which the Essentials edition has not).
	private static bool IsAlive(Labor labor)
	{
		if (labor.IsNullOrReleased() || labor.state == null)
		{
			return false;
		}
		Locomotion4 locomotion = (labor.navigation != null) ? labor.navigation.Locomotion : null;
		return locomotion == null || !locomotion.IsCorpse;
	}

	private static void Add(List<float> list, Vector3 at, float radius)
	{
		list.Add(at.x);
		list.Add(at.z);
		list.Add(radius);
	}

	// How many blocks the place lies outside the team's base area (0 or less: in it).
	internal static float OutsideBy(Faction faction, Vector3 at)
	{
		if (!_enabled || faction == null)
		{
			return 0f;
		}
		return BaseAreaRules.OutsideBy(at.x, at.z, AnchorsOf(faction));
	}

	internal static bool InBase(Faction faction, Vector3 at)
	{
		return OutsideBy(faction, at) <= 0f;
	}

	// ---- enemies

	private static void RefreshEnemies()
	{
		if (Time.time - _enemiesAt < EnemySeconds)
		{
			return;
		}
		_enemiesAt = Time.time;
		EnemyPoints.Clear();
		EnemyFactions.Clear();
		foreach (Labor labor in Live<Labor>.Active())
		{
			Faction faction = (labor != null) ? labor.faction : null;
			if (faction == null || !faction.isAI || !labor.gameObject.activeInHierarchy || !IsAlive(labor))
			{
				continue;
			}
			Vector3 at = labor.transform.position;
			EnemyPoints.Add(at.x);
			EnemyPoints.Add(at.y);
			EnemyPoints.Add(at.z);
			EnemyFactions.Add(faction);
		}
	}

	// Whether a living unit of a faction at war with the team stands within EnemyDistance of the place.
	internal static bool NearEnemy(Faction faction, Vector3 at)
	{
		if (!_enabled || faction == null || EnemyDistance.Value <= 0)
		{
			return false;
		}
		RefreshEnemies();
		float radius = EnemyDistance.Value;
		for (int i = 0; i < EnemyFactions.Count; i++)
		{
			if (BaseAreaRules.Near(at.x, at.y, at.z, EnemyPoints[3 * i], EnemyPoints[3 * i + 1], EnemyPoints[3 * i + 2], radius) && Affiliation.IsEnemy(faction, EnemyFactions[i]))
			{
				return true;
			}
		}
		return false;
	}

	// ---- the searches

	internal static void BeginSearch(Faction faction)
	{
		_searchFaction = (NetworkServer.active && faction != null && !faction.isAI && !faction.IsSpectator) ? faction : null;
	}

	internal static void EndSearch()
	{
		_searchFaction = null;
	}

	// Whether the running search may take the thing (true when no worker's own search is running).
	internal static bool MayTake(IGameComponent gc)
	{
		if (_searchFaction == null || gc == null)
		{
			return true;
		}
		GameObject go = gc.GameObject;
		return go == null || Judge(_searchFaction, gc.faction, go.transform.position) == BaseAreaRules.Verdict.Allowed;
	}

	// The rule for a thing of owner at the place, to a worker of the team.
	internal static BaseAreaRules.Verdict Judge(Faction team, Faction owner, Vector3 at)
	{
		bool own = owner != null && owner != Faction.DefaultNeutral && !owner.isAI;
		bool inside = InBase(team, at);
		// The common case first, without going through the enemies (asked for every thing a search weighs).
		if (own && inside)
		{
			return BaseAreaRules.Verdict.Allowed;
		}
		return BaseAreaRules.Judge(own, inside, NearEnemy(team, at));
	}

	// A search chose something out of the base area (allowed: the team's own, no enemy near): noted for the log.
	internal static void Chose(Labor labor, IGameComponent gc, string what)
	{
		if (!_enabled || labor == null || gc == null || gc.GameObject == null || !NetworkServer.active)
		{
			return;
		}
		Faction faction = labor.faction;
		if (faction == null || faction.isAI)
		{
			return;
		}
		Vector3 at = gc.GameObject.transform.position;
		float outside = OutsideBy(faction, at);
		if (outside <= 0f)
		{
			return;
		}
		string choice = what + " " + gc.GameObject.name + " at " + Place(at) + ", " + outside.ToString("0") + " blocks out of the base area (the team's own, no enemy near)";
		bool repeat = Choices.TryGetValue(labor, out string was) && was == choice;
		Choices[labor] = choice;
		ChoicesAt[labor] = Time.time;
		if (!repeat)
		{
			Write(labor.name + " chose " + choice);
		}
	}

	// ---- the player's orders

	internal static void PlayerOrder(Labor labor, Task task)
	{
		if (labor == null)
		{
			return;
		}
		PlayerOrderAt[labor] = Time.time;
		PlayerTasks[labor] = task;
	}

	private static bool UnderPlayerOrder(Labor labor)
	{
		if (PlayerOrderAt.TryGetValue(labor, out float at) && Time.time - at < PlayerOrderSeconds)
		{
			return true;
		}
		return PlayerTasks.TryGetValue(labor, out Task task) && task != null && labor.CurrentTask == task;
	}

	// ---- the watch (host)

	private void Update()
	{
		if (!NetworkServer.active || Time.time < _nextWatch)
		{
			return;
		}
		_nextWatch = Time.time + WatchSeconds;
		Live<Labor>.Active(_labors);
		foreach (Labor labor in _labors)
		{
			Faction faction = (labor != null) ? labor.faction : null;
			if (faction == null || faction.isAI || faction.IsSpectator || !labor.gameObject.activeInHierarchy || !IsAlive(labor))
			{
				continue;
			}
			Watch(labor, faction);
		}
		Forget();
	}

	private static void Watch(Labor labor, Faction faction)
	{
		Task task = labor.CurrentTask;
		GameObject target = (task != null) ? task.Target : null;
		Vector3 at = labor.transform.position;
		// Where it is going when its task has a target (a worker crossing open ground to a task zone of the team is
		// not wandering), else where it is.
		float outside = (target != null && target.activeInHierarchy) ? OutsideBy(faction, target.transform.position) : OutsideBy(faction, at);
		if (!BaseAreaRules.Report(outside, UnderPlayerOrder(labor), ReportSlack))
		{
			if (OutsideBy(faction, at) <= 0f)
			{
				Reported.Remove(labor);
			}
			return;
		}
		// A fight changes its task at every blow: one line per fight.
		string errand = (labor.Activity == Activity.Fighting) ? "fighting" : ((task != null) ? task.Description : "-") + "|" + ((target != null) ? target.name : "-");
		if (Reported.TryGetValue(labor, out string was) && was == errand)
		{
			return;
		}
		Reported[labor] = errand;
		StringBuilder text = new StringBuilder();
		text.Append(labor.name).Append(" out of the base area of its own accord, ").Append(outside.ToString("0")).Append(" blocks out")
			.Append(" | at ").Append(Place(at))
			.Append(" | task ").Append((task != null && !string.IsNullOrEmpty(task.Description)) ? task.Description : (task != null ? "(task)" : "none"));
		if (target != null)
		{
			text.Append(" | target ").Append(target.name).Append(" at ").Append(Place(target.transform.position));
		}
		text.Append(" | job ").Append((labor.Project != null) ? labor.Project.GetType().Name + " " + labor.Project.name : "none")
			.Append(" | activity ").Append(labor.Activity)
			.Append(" | autonomy ").Append((labor.Autonomy != null) ? labor.Autonomy.Description : "-")
			.Append(" | carrying ").Append(labor.recepteur.IsEmpty() ? "nothing" : "a load");
		if (NearEnemy(faction, at))
		{
			text.Append(" | enemies near");
		}
		if (Choices.TryGetValue(labor, out string choice) && ChoicesAt.TryGetValue(labor, out float chosenAt) && Time.time - chosenAt < ChoiceSeconds)
		{
			text.Append(" | last chose ").Append(choice);
		}
		Write(text.ToString());
	}

	// Workers gone (dead, released) are dropped from the tables now and then.
	private static void Forget()
	{
		if (Reported.Count + PlayerOrderAt.Count + Choices.Count < 64)
		{
			return;
		}
		List<Labor> gone = new List<Labor>();
		foreach (Labor labor in PlayerOrderAt.Keys)
		{
			if (labor == null)
			{
				gone.Add(labor);
			}
		}
		foreach (Labor labor in Choices.Keys)
		{
			if (labor == null)
			{
				gone.Add(labor);
			}
		}
		foreach (Labor labor in Reported.Keys)
		{
			if (labor == null)
			{
				gone.Add(labor);
			}
		}
		foreach (Labor labor in gone)
		{
			PlayerOrderAt.Remove(labor);
			PlayerTasks.Remove(labor);
			Choices.Remove(labor);
			ChoicesAt.Remove(labor);
			Reported.Remove(labor);
		}
	}

	private static void Write(string line)
	{
		Recent.Add(line);
		if (Recent.Count > RecentLines)
		{
			Recent.RemoveAt(0);
		}
		Plugin.Log.LogInfo("BaseArea: " + line);
		Diagnostics.IdleLog.Line("[base area] " + line);
	}

	private static string Place(Vector3 at)
	{
		return Mathf.FloorToInt(at.x) + "," + Mathf.FloorToInt(at.y) + "," + Mathf.FloorToInt(at.z);
	}
}

// The search for the nearest thing holding a resource: note whose search it is (before FasterTaskSearch's prefix,
// which runs the search itself), and what it chose.
[Feature(Features.BaseArea, Features.BaseAreaInfo)]
[HarmonyPatch(typeof(Knowledge), nameof(Knowledge.ClosestObjectToPickup))]
[HarmonyPriority(Priority.High)]
internal static class BaseAreaPickupSearchPatch
{
	private static void Prefix(Faction faction)
	{
		BaseArea.BeginSearch(faction);
	}

	private static void Postfix(Labor source, IGameComponent __result)
	{
		BaseArea.Chose(source, __result, "material from");
	}

	private static void Finalizer()
	{
		BaseArea.EndSearch();
	}
}

// ... where each thing holding the resource is tested: what lies out of the base area or by enemies does not count.
[Feature(Features.BaseArea, Features.BaseAreaInfo)]
[HarmonyPatch(typeof(Knowledge), nameof(Knowledge._ClosestObjectToPickup_Test))]
internal static class BaseAreaPickupTestPatch
{
	private static void Postfix(IGameComponent gc, ref bool __result)
	{
		if (__result && !BaseArea.MayTake(gc))
		{
			__result = false;
		}
	}
}

// The same for the nearest storage with room for what a worker carries.
[Feature(Features.BaseArea, Features.BaseAreaInfo)]
[HarmonyPatch(typeof(Knowledge), nameof(Knowledge.ClosestStorageWithRoomFor))]
[HarmonyPriority(Priority.High)]
internal static class BaseAreaStorageSearchPatch
{
	private static void Prefix(Faction faction)
	{
		BaseArea.BeginSearch(faction);
	}

	private static void Postfix(Labor source, IGameComponent __result)
	{
		BaseArea.Chose(source, __result, "storage");
	}

	private static void Finalizer()
	{
		BaseArea.EndSearch();
	}
}

[Feature(Features.BaseArea, Features.BaseAreaInfo)]
[HarmonyPatch(typeof(Knowledge), nameof(Knowledge._ClosestStorageWithRoomFor_Test))]
internal static class BaseAreaStorageTestPatch
{
	private static void Postfix(IGameComponent gc, ref bool __result)
	{
		if (__result && !BaseArea.MayTake(gc))
		{
			__result = false;
		}
	}
}

// The player's orders, so a worker sent out by the player is not logged as gone out of its own accord.
[Feature(Features.BaseArea, Features.BaseAreaInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.SendOrder), new System.Type[] { typeof(Task), typeof(Labor.DecisionSource) })]
internal static class BaseAreaPlayerOrderPatch
{
	private static void Prefix(Labor __instance, Task task, Labor.DecisionSource source)
	{
		if (source == Labor.DecisionSource.User)
		{
			BaseArea.PlayerOrder(__instance, task);
		}
	}
}
