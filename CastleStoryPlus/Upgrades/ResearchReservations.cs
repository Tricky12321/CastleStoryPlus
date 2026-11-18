using System;
using System.Collections.Generic;
using Brix.Components;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Game.Utils;
using Brix.Lifecycle.Pooling;
using CastleStoryPlus.Core;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Upgrades;

// One research at a time per upgrade line for the whole team, paid up front. A research queued at any of the team's
// smithies, armouries or research stations locks its line at all the others, and reserves its full cost from the
// team's stockpiles (and tool racks) at once: other researches, workshops, blueprints and the giant bricktron only
// see what is left over. Starting a research is refused when that is not enough. Everything is worked out from the
// stations' queues (saved and synced by the game) and what they and the stockpiles hold, so it survives save and
// load as it is and needs no network data of its own: a research's claim is its cost minus what its station already
// holds, and materials a worker is carrying to a station still count as stock. Cancelling or finishing a research
// takes it out of the queue, which ends its lock and its reservation.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
internal static class ResearchReservations
{
	internal sealed class Lock
	{
		public UpgradeStation Station;

		public int Tier;

		// Worked on now (first in its station's queue), else waiting in the queue.
		public bool Current;
	}

	private sealed class Team
	{
		public float Until;

		public readonly Dictionary<int, Lock> Locks = new Dictionary<int, Lock>();

		public readonly Dictionary<Type, int> Reserved = new Dictionary<Type, int>();

		public readonly Dictionary<Type, int> Stock = new Dictionary<Type, int>();
	}

	private const float CacheSeconds = 0.5f;

	private static readonly Dictionary<Faction, Team> Teams = new Dictionary<Faction, Team>();

	// Every resource any research costs, one per type.
	private static List<Ressource> _tracked;

	// Server: workers on their way to fill a station, with the task they fetch for.
	private static readonly Dictionary<Labor, Task> Fetchers = new Dictionary<Labor, Task>();

	// Set while a search for a station's ingredients runs: such a search may use the reserved materials.
	[ThreadStatic]
	internal static bool ForResearch;

	// The faction whose search for something to pick up runs, or null.
	[ThreadStatic]
	internal static Faction Searching;

	private static readonly Dictionary<Type, int> Claim = new Dictionary<Type, int>();

	private static void Enable()
	{
		GameSession.OnLeave(() =>
		{
			Teams.Clear();
			Fetchers.Clear();
			ForResearch = false;
			Searching = null;
		});
	}

	internal static List<Ressource> Tracked
	{
		get
		{
			if (_tracked == null || _tracked.Count == 0)
			{
				_tracked = new List<Ressource>();
				HashSet<Type> types = new HashSet<Type>();
				foreach (UpgradeLine line in UpgradeLines.All)
				{
					for (int tier = 1; tier <= line.Tiers; tier++)
					{
						foreach (Ingredient ingredient in line.Cost[tier])
						{
							if (types.Add(ingredient.Resource.GetType()))
							{
								_tracked.Add(ingredient.Resource);
							}
						}
					}
				}
			}
			return _tracked;
		}
	}

	internal static void Invalidate(Faction faction)
	{
		if (faction != null)
		{
			Teams.Remove(faction);
		}
	}

	// The line's research queued somewhere in the team, or false.
	internal static bool TryGetLock(Faction faction, int line, out Lock found)
	{
		found = null;
		Team team = Get(faction, false);
		return team != null && team.Locks.TryGetValue(line, out found);
	}

	internal static int Reserved(Faction faction, Ressource resource)
	{
		Team team = Get(faction, false);
		return (team != null && team.Reserved.TryGetValue(resource.GetType(), out int reserved)) ? reserved : 0;
	}

	internal static int Stock(Faction faction, Ressource resource)
	{
		Team team = Get(faction, false);
		return (team != null && team.Stock.TryGetValue(resource.GetType(), out int stock)) ? stock : 0;
	}

	// What the stockpiles hold that no research has reserved (below 0 when they hold less than is reserved).
	internal static int Free(Faction faction, Ressource resource)
	{
		return Stock(faction, resource) - Reserved(faction, resource);
	}

	// Whether any of the team's researches reserves the resource (a quick check before the searches look closer).
	internal static bool IsReserved(Faction faction, Type type)
	{
		Team team = Get(faction, false);
		return team != null && team.Reserved.TryGetValue(type, out int reserved) && reserved > 0;
	}

	// Why the tier cannot be started at the station now, or null when it can.
	internal static string RefusalReason(Faction faction, UpgradeLine line, int tier, UpgradeStation station, bool fresh)
	{
		if (faction == null || station == null || line == null)
		{
			return "No team.";
		}
		if (!UpgradeLines.IsEnabled(line))
		{
			return "Switched off (the Sprint feature).";
		}
		if (TeamUpgrades.Tier(faction, line.Index) >= tier)
		{
			return "Already researched.";
		}
		Team team = Get(faction, fresh);
		if (team.Locks.TryGetValue(line.Index, out Lock queued))
		{
			return (queued.Station == station) ? "Already queued here." : ("In progress at " + Describe(queued.Station, station) + ".");
		}
		foreach (Ingredient ingredient in line.Cost[tier])
		{
			team.Stock.TryGetValue(ingredient.Resource.GetType(), out int stock);
			team.Reserved.TryGetValue(ingredient.Resource.GetType(), out int reserved);
			int free = Mathf.Max(0, stock - reserved);
			if (free < ingredient.Count)
			{
				return "Not enough free " + ingredient.Name + ": " + free + "/" + ingredient.Count + ((reserved > 0) ? (" (" + reserved + " reserved)") : string.Empty) + ".";
			}
		}
		return null;
	}

	// "the Research Station 24 m away", seen from another station.
	internal static string Describe(UpgradeStation station, UpgradeStation from)
	{
		if (station == null)
		{
			return "another station";
		}
		string name = HallName(station.Hall);
		if (from == null)
		{
			return "the " + name;
		}
		return "the " + name + " " + Mathf.RoundToInt(Vector3.Distance(station.transform.position, from.transform.position)) + " m away";
	}

	internal static string HallName(UpgradeHall hall)
	{
		switch (hall)
		{
			case UpgradeHall.Smithy:
				return UpgradeBuildings.SmithyName;
			case UpgradeHall.Armoury:
				return UpgradeBuildings.ArmouryName;
			default:
				return UpgradeBuildings.ResearchLabel;
		}
	}

	// "6 iron, 30 dark crystal" of what the team has reserved, empty when nothing.
	internal static string ReservedText(Faction faction)
	{
		Team team = Get(faction, false);
		if (team == null)
		{
			return string.Empty;
		}
		System.Text.StringBuilder text = new System.Text.StringBuilder();
		foreach (Ressource resource in Tracked)
		{
			if (!team.Reserved.TryGetValue(resource.GetType(), out int reserved) || reserved <= 0)
			{
				continue;
			}
			if (text.Length > 0)
			{
				text.Append(", ");
			}
			text.Append(reserved).Append(' ').Append(NameOf(resource));
		}
		return text.ToString();
	}

	private static string NameOf(Ressource resource)
	{
		foreach (UpgradeLine line in UpgradeLines.All)
		{
			for (int tier = 1; tier <= line.Tiers; tier++)
			{
				foreach (Ingredient ingredient in line.Cost[tier])
				{
					if (ingredient.Resource.GetType() == resource.GetType())
					{
						return ingredient.Name;
					}
				}
			}
		}
		return resource.GetType().Name;
	}

	// ---- fetching (server)

	internal static void BeginFetch(Labor labor)
	{
		if (labor != null)
		{
			Fetchers[labor] = labor.CurrentTask;
		}
	}

	// Whether the worker is fetching for a station's research right now.
	internal static bool IsFetching(Labor labor)
	{
		if (labor == null || !Fetchers.TryGetValue(labor, out Task task))
		{
			return false;
		}
		if (labor.IsNullOrReleased() || task == null || labor.CurrentTask != task)
		{
			Fetchers.Remove(labor);
			return false;
		}
		return true;
	}

	// A stockpile or tool rack (not a workshop, a bag or a loose item).
	internal static bool IsStorage(Recepteur recepteur)
	{
		return recepteur != null && recepteur.Fonction == FonctionDeContenant.entrepot && recepteur.GetComponent<CraftingStation>() == null;
	}

	// ---- the ledger

	private static Team Get(Faction faction, bool fresh)
	{
		if (faction == null)
		{
			return null;
		}
		if (!fresh && Teams.TryGetValue(faction, out Team cached) && Time.unscaledTime < cached.Until)
		{
			return cached;
		}
		Team team = Compute(faction);
		Teams[faction] = team;
		return team;
	}

	private static Team Compute(Faction faction)
	{
		Team team = new Team
		{
			Until = Time.unscaledTime + CacheSeconds
		};
		List<Ressource> tracked = Tracked;
		foreach (UpgradeStation station in Live<UpgradeStation>.Active())
		{
			if (station.IsNullOrReleased() || Faction.GetFaction(station.gameObject) != faction)
			{
				continue;
			}
			List<RecipeInfo> queue = station.QueuedRecipes;
			if (queue == null || queue.Count == 0)
			{
				continue;
			}
			Claim.Clear();
			for (int i = 0; i < queue.Count; i++)
			{
				if (!UpgradeLines.TryGetResearch(queue[i], out UpgradeLine line, out int tier))
				{
					continue;
				}
				if (!team.Locks.ContainsKey(line.Index))
				{
					team.Locks[line.Index] = new Lock
					{
						Station = station,
						Tier = tier,
						Current = i == 0
					};
				}
				foreach (Ingredient ingredient in line.Cost[tier])
				{
					Claim.TryGetValue(ingredient.Resource.GetType(), out int claim);
					Claim[ingredient.Resource.GetType()] = claim + ingredient.Count;
				}
			}
			Recepteur held = station.recepteur;
			foreach (Ressource resource in tracked)
			{
				if (!Claim.TryGetValue(resource.GetType(), out int claim))
				{
					continue;
				}
				int outstanding = Mathf.Max(0, claim - ((held != null) ? held.ContentDescription.Value(resource) : 0));
				team.Reserved.TryGetValue(resource.GetType(), out int reserved);
				team.Reserved[resource.GetType()] = reserved + outstanding;
			}
		}
		foreach (Ressource resource in tracked)
		{
			team.Stock[resource.GetType()] = 0;
		}
		CountStorages(faction, team, tracked);
		CountFetchers(faction, team, tracked);
		return team;
	}

	private static void CountStorages(Faction faction, Team team, List<Ressource> tracked)
	{
		AutoList list = BrixSingleton<AutoList>.Instance;
		if (list == null)
		{
			return;
		}
		HashSet<GameObject> seen = new HashSet<GameObject>();
		foreach (Factory.AssetKey storage in new[] { ObjetsDynamiques.Palette, ObjetsDynamiques.Toolrack, ObjetsDynamiques.SingleToolrack })
		{
			HashSet<GameObject> instances = list.GetInstances(storage);
			if (instances == null)
			{
				continue;
			}
			foreach (GameObject go in instances)
			{
				if (go == null || !go.activeInHierarchy || !seen.Add(go) || Faction.GetFaction(go) != faction)
				{
					continue;
				}
				Recepteur recepteur = go.GetComponent<Recepteur>();
				if (recepteur != null)
				{
					Add(team, recepteur, tracked);
				}
			}
		}
	}

	// What workers carry to a station is still the team's stock (its claim only shrinks once it is delivered).
	private static void CountFetchers(Faction faction, Team team, List<Ressource> tracked)
	{
		if (Fetchers.Count == 0)
		{
			return;
		}
		foreach (Labor labor in new List<Labor>(Fetchers.Keys))
		{
			if (IsFetching(labor) && labor.recepteur != null && Faction.GetFaction(labor.gameObject) == faction)
			{
				Add(team, labor.recepteur, tracked);
			}
		}
	}

	private static void Add(Team team, Recepteur recepteur, List<Ressource> tracked)
	{
		foreach (Ressource resource in tracked)
		{
			int value = recepteur.ContentDescription.Value(resource);
			if (value != 0)
			{
				team.Stock[resource.GetType()] += value;
			}
		}
	}
}

// Server: a research is only queued when its line is free in the whole team and its cost can be reserved. Also
// covers a looping queue putting a finished research back.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(CraftingLabor), nameof(CraftingLabor.QueueRecipe))]
internal static class ResearchQueueLockPatch
{
	private static bool Prefix(CraftingLabor __instance, RecipeInfo recipe)
	{
		if (!NetworkServer.active || !UpgradeLines.TryGetResearch(recipe, out UpgradeLine line, out int tier))
		{
			return true;
		}
		UpgradeStation station = __instance.GetComponent<UpgradeStation>();
		Faction faction = Faction.GetFaction(__instance.gameObject);
		string reason = ResearchReservations.RefusalReason(faction, line, tier, station, fresh: true);
		if (reason != null)
		{
			Plugin.Log.LogInfo("Upgrades: " + line.Name + " tier " + tier + " not queued: " + reason);
			return false;
		}
		return true;
	}

	private static void Postfix(CraftingLabor __instance)
	{
		ResearchReservations.Invalidate(Faction.GetFaction(__instance.gameObject));
	}
}

// A station's fetch for its research: the same as the game's, marked so the search and the pick-up may use the
// reserved materials, and so the worker's load still counts as stock on the way.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(CraftingInstructions), "FindResource")]
internal static class ResearchFetchPatch
{
	private static bool Prefix(CraftingInstructions __instance, Labor labor, Description ingredients, ref Node __result)
	{
		CraftingStation station = __instance.station;
		if (!(station is UpgradeStation))
		{
			return true;
		}
		__result = (StaticNode)Node.Sequence.Do((Labor l, Description d) => Fetch(l, d), labor, ingredients).Do((Labor l, Recepteur r) => l.GoStoreIn(r), labor, station.recepteur).Label("GoStore");
		return false;
	}

	private static Node Fetch(Labor labor, Description ingredients)
	{
		ResearchReservations.BeginFetch(labor);
		bool was = ResearchReservations.ForResearch;
		ResearchReservations.ForResearch = true;
		try
		{
			return labor.FindAndPickUp(ingredients);
		}
		finally
		{
			ResearchReservations.ForResearch = was;
		}
	}
}

// ... and the station's check whether there is anything to fetch.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(CraftingStation), "CanFetch")]
internal static class ResearchCanFetchPatch
{
	private static void Prefix(CraftingStation __instance, out bool __state)
	{
		__state = ResearchReservations.ForResearch;
		if (__instance is UpgradeStation)
		{
			ResearchReservations.ForResearch = true;
		}
	}

	private static Exception Finalizer(Exception __exception, bool __state)
	{
		ResearchReservations.ForResearch = __state;
		return __exception;
	}
}

// A research's search skips the worker's per-decision cache: it is keyed by resource only, and holds what the
// other (limited) searches found. With TaskFlow's destination the search is already uncached.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(Labor), "ClosestResource", new Type[] { typeof(GameObject), typeof(int), typeof(Description) }, new ArgumentType[] { ArgumentType.Out, ArgumentType.Out, ArgumentType.Normal })]
[HarmonyPriority(Priority.Low)]
internal static class ResearchClosestResourcePatch
{
	private static bool Prefix(Labor __instance, ref GameObject closest, ref int qty, Description ingredients, ref bool __result)
	{
		if (!ResearchReservations.ForResearch || WorkerAI.TaskFlow.TryGetDestination(out _) || ingredients == null)
		{
			return true;
		}
		closest = WorkerAI.TaskFlow.FindForDestination(__instance, ingredients);
		qty = 0;
		__result = closest != null;
		return false;
	}
}

// The search for something to pick up: remember whose it is.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(Knowledge), nameof(Knowledge.ClosestObjectToPickup))]
internal static class ResearchSearchFactionPatch
{
	private static void Prefix(Faction faction, out Faction __state)
	{
		__state = ResearchReservations.Searching;
		ResearchReservations.Searching = faction;
	}

	private static Exception Finalizer(Exception __exception, Faction __state)
	{
		ResearchReservations.Searching = __state;
		return __exception;
	}
}

// ... and any search but a research's skips stockpiles for a resource the team has nothing free of.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(Knowledge), "_ClosestObjectToPickup_Test")]
internal static class ResearchSearchTestPatch
{
	private static void Postfix(IGameComponent gc, Ressource ressource, ref bool __result)
	{
		Faction faction = ResearchReservations.Searching;
		if (!__result || ResearchReservations.ForResearch || faction == null || ressource == null || !ResearchReservations.IsReserved(faction, ressource.GetType()))
		{
			return;
		}
		if (gc.ParentRecepteur == null && ResearchReservations.IsStorage(gc.recepteur) && ResearchReservations.Free(faction, ressource) <= 0)
		{
			__result = false;
		}
	}
}

// Taking from a stockpile for anything but a research leaves the reserved materials: at most what is free.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(PickUpNode), nameof(PickUpNode.TakeSome))]
internal static class ResearchTakePatch
{
	private static bool Prefix(Labor labor, GameComponent itemToTake, ref int upTo, ref Node __result, out Faction __state)
	{
		__state = null;
		if (!NetworkServer.active || labor == null || itemToTake.IsNullOrReleased() || !ResearchReservations.IsStorage(itemToTake.ParentRecepteur) || ResearchReservations.IsFetching(labor))
		{
			return true;
		}
		Faction faction = Faction.GetFaction(itemToTake.ParentRecepteur.gameObject);
		foreach (Ressource resource in ResearchReservations.Tracked)
		{
			if (!ResearchReservations.IsReserved(faction, resource.GetType()) || !Apparence.PrincipaleIs(itemToTake.gameObject, resource))
			{
				continue;
			}
			int free = ResearchReservations.Free(faction, resource);
			if (free <= 0)
			{
				__result = Node.Fail(SharedWorkException<Exceptions.CannotFindWhatINeedToPickUp>.Get);
				return false;
			}
			if (upTo <= 0 || upTo > free)
			{
				upTo = free;
			}
			__state = faction;
			break;
		}
		return true;
	}

	// The stock has changed: the next check counts again.
	private static void Postfix(Faction __state)
	{
		ResearchReservations.Invalidate(__state);
	}
}
