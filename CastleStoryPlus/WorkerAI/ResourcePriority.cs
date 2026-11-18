using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Game.Network;
using Brix.Game.Semantique;
using Brix.IO.Serialization.Contracts;
using CastleStoryPlus.Core;
using CastleStoryPlus.Economy;
using CastleStoryPlus.Upgrades;
using HarmonyLib;
using Motus.Behavior;
using Newtonsoft.Json.Serialization;
using Smooth.Slinq;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.WorkerAI;

// A priority per resource, set by the player in a small table beside the resource list (Features.ResourcePriority).
// It is kept per team (Faction): the host holds it, saves it with the world (an extra "resourcePriorities" property
// on the faction, ignored by the unmodded game) and syncs it to the clients; a client sets its own team's value at
// once and tells the host.
//
// 0 means never: workers leave that resource alone where they choose between resources. 1 to 5 weigh against
// distance: a worker takes the choice with the lowest distance / Weight(priority), so a resource at 5 is taken
// before one at 3 even if it is 2.5 times as far, and one at 1 only when it is 2.5 times as near. 3 is neutral (the
// game's own choice, nothing changes while every resource is at 3).
//
// Where it applies (the patches below, all on the host, where the AI runs):
//   - a quarry's blocks (DigGoal): which block a worker digs, among the blocks of the same layer (the game digs
//     top-down, the layer is not changed);
//   - a harvest area's trees, plants and rocks, and a clean-up area's loose items (HarvestProject, CleanupProject);
//   - the loose items an idle worker tidies up (AutoCleanup);
//   - a workshop's ingredients: which of several ingredients a worker fetches first (Labor.ClosestResource).
// QuarryLimit's limits stay a separate setting of each quarry.
[Feature(Features.ResourcePriority, Features.ResourcePriorityInfo)]
internal static class ResourcePriority
{
	internal const int Never = 0;

	internal const int Most = 5;

	internal const int Neutral = 3;

	private const int SetHash = 1129595272;

	// The cost of a distance at each priority: distance / weight. 0 is never.
	private static readonly float[] Weights = { 0f, 0.4f, 0.7f, 1f, 1.5f, 2.5f };

	internal sealed class Kind
	{
		// Saved by this, so the table can be reordered.
		public string Id;

		public string Name;

		public Type[] Types;
	}

	private static Kind[] _kinds;

	private static Dictionary<Type, int> _byType;

	// Per team: the priority of each kind; a team not in here has every kind at Neutral.
	private static readonly Dictionary<Faction, int[]> Teams = new Dictionary<Faction, int[]>();

	private static ConfigEntry<bool> _shown;

	private static bool _enabled;

	// The feature is on: the in-game menu draws the table.
	internal static bool Enabled
	{
		get { return _enabled; }
	}

	internal static bool Shown
	{
		get { return _shown != null && _shown.Value; }
	}

	internal static void ToggleShown()
	{
		if (_shown != null)
		{
			_shown.Value = !_shown.Value;
		}
	}

	// The kinds of the table: what workers choose between. Resources not in it (glass, rope, ...) stay at Neutral.
	internal static Kind[] Kinds
	{
		get
		{
			if (_kinds == null)
			{
				_kinds = new Kind[]
				{
					new Kind { Id = "stone", Name = "Stone", Types = new Type[] { Adjectif.stones.GetType() } },
					new Kind { Id = "bricks", Name = "Bricks", Types = new Type[] { Adjectif.stoneBlock.GetType() } },
					new Kind { Id = "wood", Name = "Wood", Types = new Type[] { Adjectif.woodBlock.GetType() } },
					new Kind { Id = "planks", Name = "Planks", Types = new Type[] { Adjectif.plankBlock.GetType() } },
					new Kind { Id = "rawIron", Name = "Raw iron", Types = new Type[] { Adjectif.rawIron.GetType() } },
					new Kind { Id = "iron", Name = "Iron", Types = new Type[] { Adjectif.iron.GetType() } },
					new Kind { Id = "orange", Name = "Orange crystal", Types = new Type[] { Adjectif.orangeCrystal.GetType() } },
					new Kind { Id = "blue", Name = "Blue crystal", Types = new Type[] { Adjectif.blueCrystal.GetType() } },
					new Kind { Id = "coal", Name = "Coal", Types = new Type[] { Adjectif.clay.GetType() } },
					new Kind { Id = "plants", Name = "Plants", Types = new Type[] { Adjectif.plant.GetType() } }
				};
				_byType = new Dictionary<Type, int>();
				for (int i = 0; i < _kinds.Length; i++)
				{
					foreach (Type type in _kinds[i].Types)
					{
						_byType[type] = i;
					}
				}
			}
			return _kinds;
		}
	}

	private static void Enable()
	{
		_enabled = true;
		_shown = Plugin.Cfg.Bind("ResourcePriority", "Shown", true, "Whether the resource priority table beside the resource list is shown (the \"Priorities\" button on the right-hand bar toggles it).");
		NetworkBehaviour.RegisterCommandDelegate(typeof(UNetProjectCmd), SetHash, InvokeSet);
		GameSession.OnLeave(Teams.Clear);
		GameUi.GameMenuItems.Extend((GameUi.GameMenuModel menu) =>
		{
			int index = menu.RightBar.FindIndex((GameUi.UiItem i) => i != null && i.Id == "resourceList");
			if (index < 0)
			{
				index = menu.RightBar.FindIndex((GameUi.UiItem i) => i != null && i.Id == "settings");
			}
			menu.RightBar.Insert(index + 1, new GameUi.UiButton
			{
				Id = "resourcePriority",
				Label = () => "Priorities",
				Icon = () => GameUi.GameUiState.Icon(Brix.UI.Icons.IconKeys._UI_PriorityMedium_alt),
				Highlight = () => Shown,
				OnClick = (bool shift, bool ctrl) => ToggleShown()
			});
		});
	}

	internal static float Weight(int priority)
	{
		return Weights[Mathf.Clamp(priority, 0, Most)];
	}

	// The cost of a distance to a resource at a priority: lowest wins; infinite for never.
	internal static float Cost(float distance, int priority)
	{
		float weight = Weight(priority);
		return (weight <= 0f) ? float.MaxValue : distance / weight;
	}

	internal static int Get(Faction faction, int kind)
	{
		return (faction != null && kind >= 0 && Teams.TryGetValue(faction, out int[] values) && kind < values.Length) ? values[kind] : Neutral;
	}

	// The priority of a resource of the team; Neutral for one that is not in the table.
	internal static int Get(Faction faction, Ressource resource)
	{
		return Get(faction, KindOf(resource));
	}

	internal static int KindOf(Ressource resource)
	{
		if (resource == null)
		{
			return -1;
		}
		_ = Kinds;
		return _byType.TryGetValue(resource.GetType(), out int kind) ? kind : -1;
	}

	// Whether any resource of the team is off neutral (nothing changes for a team that has not set a priority).
	internal static bool Customised(Faction faction)
	{
		if (faction == null || !NetworkServer.active || !Teams.TryGetValue(faction, out int[] values))
		{
			return false;
		}
		foreach (int value in values)
		{
			if (value != Neutral)
			{
				return true;
			}
		}
		return false;
	}

	internal static void Set(Faction faction, int kind, int priority)
	{
		if (faction == null || kind < 0 || kind >= Kinds.Length)
		{
			return;
		}
		priority = Mathf.Clamp(priority, Never, Most);
		if (Get(faction, kind) == priority)
		{
			return;
		}
		if (!Teams.TryGetValue(faction, out int[] values))
		{
			Prune();
			values = new int[Kinds.Length];
			for (int i = 0; i < values.Length; i++)
			{
				values[i] = Neutral;
			}
			Teams[faction] = values;
		}
		values[kind] = priority;
		if (NetworkServer.active)
		{
			faction.SetDirtyBit(ResourcePriorityNetwork.DirtyBit);
		}
	}

	// Teams of a game that was left.
	private static void Prune()
	{
		List<Faction> gone = new List<Faction>();
		foreach (Faction key in Teams.Keys)
		{
			if (key == null)
			{
				gone.Add(key);
			}
		}
		foreach (Faction key in gone)
		{
			Teams.Remove(key);
		}
	}

	// The local player's team: shown at once, then sent to the host.
	internal static void Change(int kind, int priority)
	{
		Faction faction = (User.LocalUser != null) ? User.LocalUser.faction : null;
		if (faction == null)
		{
			return;
		}
		Set(faction, kind, priority);
		UNetProjectCmd cmd = User.LocalUser.GetComponent<UNetProjectCmd>();
		if (cmd == null || cmd.isServer)
		{
			return;
		}
		NetworkWriter writer = new NetworkWriter();
		writer.Write((short)0);
		writer.Write((short)5);
		writer.WritePackedUInt32((uint)SetHash);
		writer.Write(cmd.GetComponent<NetworkIdentity>().netId);
		writer.WritePackedUInt32((uint)kind);
		writer.WritePackedUInt32((uint)priority);
		cmd.SendCommandInternal(writer, 0, "CmdSetResourcePriority");
	}

	private static void InvokeSet(NetworkBehaviour obj, NetworkReader reader)
	{
		if (!NetworkServer.active)
		{
			return;
		}
		int kind = (int)reader.ReadPackedUInt32();
		int priority = (int)reader.ReadPackedUInt32();
		User user = obj.GetComponent<User>();
		if (user != null && user.faction != null)
		{
			Set(user.faction, kind, priority);
		}
	}

	// The kind of resource a worker gets from a goal, or -1 (loose items by what they are, trees as wood, a quarry's
	// block by what it gives, plants and rocks by what they give).
	internal static int KindOf(Goal goal)
	{
		if (goal is DigGoal dig)
		{
			return KindOfFlag(QuarryLimit.FlagOf(Voxel.GetBlockInfo(dig.voxel.position).ResourceKey));
		}
		if (goal is TreeGoal)
		{
			return KindOf(Adjectif.woodBlock);
		}
		if (goal is NatureHarvestGoal nature)
		{
			return KindOf(nature.NData.RepresentativeRessource());
		}
		if (goal is CleanUpGoal)
		{
			return KindOf(Apparence.PrincipaleOf(goal.gameObject));
		}
		return -1;
	}

	private static int KindOfFlag(int flag)
	{
		switch (flag)
		{
			case QuarryLimit.Stone:
				return KindOf(Adjectif.stones);
			case QuarryLimit.Iron:
				return KindOf(Adjectif.rawIron);
			case QuarryLimit.Brimstone:
				return KindOf(Adjectif.orangeCrystal);
			case QuarryLimit.Coal:
				return KindOf(Adjectif.clay);
			case QuarryLimit.Blue:
				return KindOf(Adjectif.blueCrystal);
		}
		return -1;
	}

	// The team's priority for what a goal gives (Neutral when it is not in the table).
	internal static int Get(Faction faction, Goal goal)
	{
		return Get(faction, KindOf(goal));
	}

	// Where a source is: a stockpile for what is in one, else the item itself.
	internal static Vector3 Where(GameObject item)
	{
		Transform parent = item.transform.parent;
		Recepteur holder = (parent != null) ? parent.GetComponentInParent<Recepteur>() : null;
		return ((holder != null) ? holder.transform : item.transform).position;
	}

	// The loose items without those at 0, lowest cost first (distance from a point / weight); stable.
	internal static List<GameObject> ByCost(Faction faction, List<GameObject> items, Vector3 from)
	{
		List<KeyValuePair<float, GameObject>> ranked = new List<KeyValuePair<float, GameObject>>(items.Count);
		for (int i = 0; i < items.Count; i++)
		{
			int priority = Get(faction, Apparence.PrincipaleOf(items[i]));
			if (priority != Never)
			{
				ranked.Add(new KeyValuePair<float, GameObject>(Cost(Vector3.Distance(from, items[i].transform.position), priority) + i * 0.0001f, items[i]));
			}
		}
		ranked.Sort((KeyValuePair<float, GameObject> a, KeyValuePair<float, GameObject> b) => a.Key.CompareTo(b.Key));
		List<GameObject> result = new List<GameObject>(ranked.Count);
		foreach (KeyValuePair<float, GameObject> entry in ranked)
		{
			result.Add(entry.Value);
		}
		return result;
	}

	// "stone:5,wood:1" for saves: only what is off neutral.
	internal static string Encode(Faction faction)
	{
		if (faction == null || !Teams.TryGetValue(faction, out int[] values))
		{
			return string.Empty;
		}
		StringBuilder text = new StringBuilder();
		for (int i = 0; i < values.Length && i < Kinds.Length; i++)
		{
			if (values[i] != Neutral)
			{
				if (text.Length > 0)
				{
					text.Append(',');
				}
				text.Append(Kinds[i].Id).Append(':').Append(values[i]);
			}
		}
		return text.ToString();
	}

	internal static void Decode(Faction faction, string text)
	{
		if (faction == null || string.IsNullOrEmpty(text))
		{
			return;
		}
		foreach (string part in text.Split(','))
		{
			string[] pair = part.Split(':');
			if (pair.Length != 2 || !int.TryParse(pair[1], out int priority))
			{
				continue;
			}
			for (int i = 0; i < Kinds.Length; i++)
			{
				if (Kinds[i].Id == pair[0])
				{
					Set(faction, i, priority);
				}
			}
		}
	}

	// Network: the number of kinds, then each one's priority.
	internal static void Write(Faction faction, NetworkWriter writer)
	{
		writer.WritePackedUInt32((uint)Kinds.Length);
		for (int i = 0; i < Kinds.Length; i++)
		{
			writer.WritePackedUInt32((uint)Get(faction, i));
		}
	}

	internal static void Read(Faction faction, NetworkReader reader)
	{
		int count = (int)reader.ReadPackedUInt32();
		for (int i = 0; i < count; i++)
		{
			int priority = (int)reader.ReadPackedUInt32();
			if (i < Kinds.Length && faction != null)
			{
				Set(faction, i, priority);
			}
		}
	}
}

// Server: a worker leaves what is at 0 alone. The worker is told why ("goal unavailable"), as QuarryLimit does.
[Feature(Features.ResourcePriority, Features.ResourcePriorityInfo)]
[HarmonyPatch]
internal static class ResourcePriorityMatch
{
	private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(DigGoal), nameof(DigGoal.IsMatchFor));
		yield return AccessTools.Method(typeof(TreeGoal), nameof(TreeGoal.IsMatchFor));
		yield return AccessTools.Method(typeof(NatureHarvestGoal), nameof(NatureHarvestGoal.IsMatchFor));
		yield return AccessTools.Method(typeof(CleanUpGoal), nameof(CleanUpGoal.IsMatchFor));
	}

	private static void Postfix(Goal __instance, Labor labor, AIErrorsStore errorDestination, ref bool __result)
	{
		if (!__result || labor == null || !ResourcePriority.Customised(labor.faction) || ResourcePriority.Get(labor.faction, __instance) != ResourcePriority.Never)
		{
			return;
		}
		__result = false;
		errorDestination?.AddDecisionError(SharedWorkException<Exceptions.GoalUnavailable>.Get.Problem, labor);
	}
}

// The priority the game compares goals by: the distance in it costs distance / weight. Only the distance changes (a
// quarry's layer, the penalty for a goal another worker has stay as they are). A quarry's blocks use this; a
// harvest and a clean-up area also have their goals ordered by it (ResourcePrioritySelect).
[Feature(Features.ResourcePriority, Features.ResourcePriorityInfo)]
[HarmonyPatch]
internal static class ResourcePriorityGoal
{
	private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(DigGoal), nameof(DigGoal.PriorityFor), new Type[] { typeof(Labor), typeof(float).MakeByRefType() });
		yield return AccessTools.Method(typeof(TreeGoal), nameof(TreeGoal.PriorityFor), new Type[] { typeof(Labor), typeof(float).MakeByRefType() });
		yield return AccessTools.Method(typeof(NatureHarvestGoal), nameof(NatureHarvestGoal.PriorityFor), new Type[] { typeof(Labor), typeof(float).MakeByRefType() });
		yield return AccessTools.Method(typeof(CleanUpGoal), nameof(CleanUpGoal.PriorityFor), new Type[] { typeof(Labor), typeof(float).MakeByRefType() });
	}

	[HarmonyPriority(Priority.Last)]
	private static void Postfix(Goal __instance, Labor labor, ref int __result)
	{
		if (__result == int.MinValue || labor == null || !ResourcePriority.Customised(labor.faction))
		{
			return;
		}
		int priority = ResourcePriority.Get(labor.faction, __instance);
		if (priority == ResourcePriority.Neutral)
		{
			return;
		}
		if (priority == ResourcePriority.Never)
		{
			__result = int.MinValue;
			return;
		}
		float distance = RouteOrder.Distance(labor, __instance);
		__result += Mathf.RoundToInt(distance - distance / ResourcePriority.Weight(priority));
	}
}

// A harvest area's and a clean-up area's goals nearest first by cost (walking distance as RouteOrder, the straight
// line without it): the game takes the first goal that matches (a clean-up area) or the best of the first few (a
// harvest area), so the order is what the priority has to change. Runs before RouteOrder's, which stays for a team
// that has set no priority.
[Feature(Features.ResourcePriority, Features.ResourcePriorityInfo)]
[HarmonyPatch]
internal static class ResourcePrioritySelect
{
	private static readonly List<KeyValuePair<float, Goal>> Ranked = new List<KeyValuePair<float, Goal>>();

	private static readonly Comparison<KeyValuePair<float, Goal>> LowestFirst = (KeyValuePair<float, Goal> a, KeyValuePair<float, Goal> b) => a.Key.CompareTo(b.Key);

	[HarmonyPatch(typeof(HarvestProject), "SelectBestGoal")]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.High)]
	private static bool HarvestSelect(HarvestProject __instance, Labor labor, Var<Goal> result, ref Node __result)
	{
		return Select(__instance, labor, result, ref __result);
	}

	[HarmonyPatch(typeof(CleanupProject), "SelectBestGoal")]
	[HarmonyPrefix]
	[HarmonyPriority(Priority.High)]
	private static bool CleanupSelect(CleanupProject __instance, Labor labor, Var<Goal> result, ref Node __result)
	{
		return Select(__instance, labor, result, ref __result);
	}

	// The valid goals without those at 0, lowest cost first (stable).
	internal static List<Goal> Order(Labor labor, List<Goal> goals)
	{
		Ranked.Clear();
		RouteDistance.Field field = Plugin.IsEnabled(Features.WorkerAI) ? RouteDistance.For(labor) : null;
		Vector3 from = labor.transform.position;
		for (int i = 0; i < goals.Count; i++)
		{
			Goal goal = goals[i];
			if (goal.IsNullOrReleased() || !goal.IsValid())
			{
				continue;
			}
			int priority = ResourcePriority.Get(labor.faction, goal);
			if (priority == ResourcePriority.Never)
			{
				continue;
			}
			float distance = (field != null) ? RouteOrder.Rank(field, from, goal) : Vector3.Distance(from, goal.transform.position);
			// The index keeps the game's order between equals.
			Ranked.Add(new KeyValuePair<float, Goal>(ResourcePriority.Cost(distance, priority) + i * 0.0001f, goal));
		}
		Ranked.Sort(LowestFirst);
		List<Goal> ordered = new List<Goal>(Ranked.Count);
		foreach (KeyValuePair<float, Goal> entry in Ranked)
		{
			ordered.Add(entry.Value);
		}
		Ranked.Clear();
		return ordered;
	}

	private static bool Select(Project project, Labor labor, Var<Goal> result, ref Node __result)
	{
		if (labor == null || !ResourcePriority.Customised(labor.faction))
		{
			return true;
		}
		List<Goal> goals = new List<Goal>();
		foreach (IGoalProvider provider in project.GetGoalProviders())
		{
			provider?.GetAll(goals);
		}
		goals = Order(labor, goals);
		__result = project.SelectBestGoalPooledList(goals.GetEnumerator(), labor, result);
		return false;
	}
}

// A workshop's worker fetches the ingredients one at a time: the game takes the first ingredient of the recipe that
// it finds anywhere (Labor.ClosestResource). With several ingredients wanted at different priorities, the one of the
// lowest cost (distance / weight) is fetched first, and one at 0 is not fetched at all. With the same priority on
// all of them, or only one wanted, the game's search runs as it was.
[Feature(Features.ResourcePriority, Features.ResourcePriorityInfo)]
[HarmonyPatch(typeof(Labor), "ClosestResource", new Type[] { typeof(GameObject), typeof(int), typeof(Description) }, new ArgumentType[] { ArgumentType.Out, ArgumentType.Out, ArgumentType.Normal })]
internal static class ResourcePriorityIngredients
{
	private static readonly List<Ressource> Wanted = new List<Ressource>();

	// After the workshop range's prefix (first), which this must run inside, before TaskFlow's and the research's.
	[HarmonyPriority(Priority.High)]
	private static bool Prefix(Labor __instance, ref GameObject closest, ref int qty, Description ingredients, ref bool __result)
	{
		if (ingredients == null || !ResourcePriority.Customised(__instance.faction))
		{
			return true;
		}
		Wanted.Clear();
		bool differ = false;
		int first = -1;
		foreach (Ressource resource in ingredients.GetRessources().ToList())
		{
			if (resource.quantifiable.valeur == 0)
			{
				continue;
			}
			int priority = ResourcePriority.Get(__instance.faction, resource);
			if (first < 0)
			{
				first = priority;
			}
			differ |= priority != first || priority == ResourcePriority.Never;
			Wanted.Add(resource);
		}
		if (!differ)
		{
			Wanted.Clear();
			return true;
		}
		// The per-decision cache holds the nearest source of each resource, but not while a destination or a research
		// has the search made without it (see their patches).
		bool uncached = TaskFlow.TryGetDestination(out _) || ResearchReservations.ForResearch;
		closest = null;
		qty = 0;
		float best = float.MaxValue;
		if (!uncached)
		{
			__instance.resourceCache = __instance.resourceCache.Bind(__instance);
			__instance.resourceCache.RefreshIfNeeded();
		}
		foreach (Ressource resource in Wanted)
		{
			int priority = ResourcePriority.Get(__instance.faction, resource);
			if (priority == ResourcePriority.Never)
			{
				continue;
			}
			GameObject item = uncached ? __instance.FindOneResource(resource) : __instance.resourceCache.GetUnsafe(resource);
			if (item.IsNullOrReleased() || !__instance.recepteur.HasRoomFor(item))
			{
				continue;
			}
			float cost = ResourcePriority.Cost(Vector3.Distance(__instance.transform.position, ResourcePriority.Where(item)), priority);
			if (cost < best)
			{
				best = cost;
				closest = item;
				qty = uncached ? 0 : resource.quantifiable.valeur;
			}
		}
		Wanted.Clear();
		__result = closest != null;
		return false;
	}
}

// Saves: an extra "resourcePriorities" property on the faction ("stone:5,wood:1", only what is off neutral).
[Feature(Features.ResourcePriority, Features.ResourcePriorityInfo)]
[HarmonyPatch(typeof(DefaultGameContractResolver), nameof(DefaultGameContractResolver.GenerateDefaultMonoBehaviourContract))]
internal static class ResourcePrioritySave
{
	private const string Property = "resourcePriorities";

	private class PriorityProvider : IValueProvider
	{
		public object GetValue(object target)
		{
			return ResourcePriority.Encode(target as Faction);
		}

		public void SetValue(object target, object value)
		{
			ResourcePriority.Decode(target as Faction, Convert.ToString(value));
		}
	}

	private static void Postfix(Type objectType, JsonObjectContract __result)
	{
		if (objectType != typeof(Faction) || __result == null || __result.Properties.Contains(Property))
		{
			return;
		}
		__result.Properties.AddProperty(new JsonProperty
		{
			PropertyName = Property,
			UnderlyingName = Property,
			PropertyType = typeof(string),
			DeclaringType = typeof(Faction),
			ValueProvider = new PriorityProvider(),
			Readable = true,
			Writable = true
		});
	}
}

// Syncs the priorities by appending them to Faction's own network data, in both the initial state and every update.
// Both ends must run the mod (modded and unmodded peers cannot play together).
[Feature(Features.ResourcePriority, Features.ResourcePriorityInfo)]
[HarmonyPatch(typeof(Faction), nameof(Faction.OnSerialize))]
internal static class ResourcePriorityNetwork
{
	// A sync var bit the game does not use (Faction uses 0x01-0x1000); setting it makes UNET send an update.
	public const uint DirtyBit = 0x10000000u;

	[HarmonyPriority(Priority.Last)]
	private static void Postfix(Faction __instance, NetworkWriter writer, ref bool __result)
	{
		ResourcePriority.Write(__instance, writer);
		__result = true;
	}
}

[Feature(Features.ResourcePriority, Features.ResourcePriorityInfo)]
[HarmonyPatch(typeof(Faction), nameof(Faction.OnDeserialize))]
internal static class ResourcePriorityNetworkRead
{
	[HarmonyPriority(Priority.Last)]
	private static void Postfix(Faction __instance, NetworkReader reader)
	{
		ResourcePriority.Read(__instance, reader);
	}
}
