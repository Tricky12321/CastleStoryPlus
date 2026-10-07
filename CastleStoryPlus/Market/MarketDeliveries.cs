using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Game.Semantique;
using CastleStoryPlus.Core;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;

namespace CastleStoryPlus.Market;

// Several workers bring what a market order needs. A workshop has one operator, who fetches, works and stores; at a
// market up to MaxHelpers other workers fetch for the current order as well, without taking over. Every fetch
// (the operator's too) says what it brings, and what is still to fetch leaves out what is on its way, so a lot
// of 60 plant fibres is split over the workers instead of each bringing all of it, and a lot is shared out evenly
// (at least 5 each) so a small lot is fetched by several at once too. Server side only; nothing is
// saved (a loaded game starts with nothing on its way).
[Feature(Features.Market, Features.MarketInfo)]
internal static class MarketDeliveries
{
	private sealed class Delivery
	{
		public Labor Labor;

		public Type Resource;

		public int Amount;

		public float Until;
	}

	// An entry left behind (a task dropped without its clean-up) does not hold an order forever.
	private const float IncomingSeconds = 120f;

	// The most a worker picks up in one go (the game's own limit for a fetch).
	private const int MostPerTrip = 20;

	// The least a worker is sent for when a lot is shared out, so no one walks for a handful.
	private const int LeastPerTrip = 5;

	internal static ConfigEntry<int> MaxHelpers;

	private static readonly Dictionary<CraftingStation, List<Delivery>> Bringing = new Dictionary<CraftingStation, List<Delivery>>();

	private static void Enable()
	{
		MaxHelpers = Plugin.Cfg.Bind("Market", "MaxHelpers", 6, new ConfigDescription("Workers besides the market's own who may fetch goods for its current trade at the same time.", new AcceptableValueRange<int>(0, 20)));
		GameSession.OnLeave(() => Bringing.Clear());
	}

	internal static int Incoming(CraftingStation station, Type resource)
	{
		Expire(station);
		int amount = 0;
		if (Bringing.TryGetValue(station, out List<Delivery> list))
		{
			foreach (Delivery entry in list)
			{
				if (entry.Resource == resource)
				{
					amount += entry.Amount;
				}
			}
		}
		return amount;
	}

	internal static int HelperCount(CraftingStation station)
	{
		Expire(station);
		int count = 0;
		if (Bringing.TryGetValue(station, out List<Delivery> list))
		{
			foreach (Delivery entry in list)
			{
				if (entry.Labor != station.Operator)
				{
					count++;
				}
			}
		}
		return count;
	}

	private static void Register(CraftingStation station, Labor labor, Type resource, int amount)
	{
		if (!Bringing.TryGetValue(station, out List<Delivery> list))
		{
			list = new List<Delivery>();
			Bringing[station] = list;
		}
		RemoveOf(list, labor);
		list.Add(new Delivery { Labor = labor, Resource = resource, Amount = amount, Until = Time.time + IncomingSeconds });
	}

	private static void Release(CraftingStation station, Labor labor)
	{
		if (station != null && Bringing.TryGetValue(station, out List<Delivery> list))
		{
			RemoveOf(list, labor);
			if (list.Count == 0)
			{
				Bringing.Remove(station);
			}
		}
	}

	private static void Expire(CraftingStation station)
	{
		if (station != null && Bringing.TryGetValue(station, out List<Delivery> list))
		{
			float now = Time.time;
			for (int i = list.Count - 1; i >= 0; i--)
			{
				if (list[i].Labor == null || now > list[i].Until)
				{
					list.RemoveAt(i);
				}
			}
		}
	}

	// Loops rather than RemoveAll with lambdas, which would be new objects on every worker's search.
	private static void RemoveOf(List<Delivery> list, Labor labor)
	{
		for (int i = list.Count - 1; i >= 0; i--)
		{
			if (list[i].Labor == labor)
			{
				list.RemoveAt(i);
			}
		}
	}

	// What is still to fetch less what is on its way; null when nothing is left.
	internal static Description Uncovered(CraftingStation station, Description stuff)
	{
		if (stuff == null)
		{
			return null;
		}
		// Nothing on its way: the same as what is to fetch, without building a copy (asked on every worker's search).
		if (!Bringing.TryGetValue(station, out List<Delivery> bringing) || bringing.Count == 0)
		{
			return stuff.GetRessources().Any((Ressource resource) => resource.quantifiable.valeur > 0) ? stuff : null;
		}
		Description left = new Description();
		bool any = false;
		foreach (Ressource resource in stuff.GetRessources().ToList())
		{
			int amount = resource.quantifiable.valeur - Incoming(station, resource.GetType());
			if (amount > 0)
			{
				left.Add(Adjectif.New(resource.GetType(), amount));
				any = true;
			}
		}
		return any ? left : null;
	}

	// A fetch for the market: picks what is still uncovered (as much of it as one trip carries), says it is on its
	// way, brings it, and clears that when it is done or given up.
	internal static Node Fetch(CraftingStation station, Labor labor)
	{
		Description stuff = station.StuffToFetch();
		GameObject item = (stuff != null) ? labor.Find(stuff) : null;
		if (item == null)
		{
			return Node.FailWork<Exceptions.CannotFindWhatINeedToPickUp>();
		}
		// A trade has one ingredient: what is given.
		Ressource wanted = null;
		foreach (Ressource resource in stuff.GetRessources().ToList())
		{
			wanted = resource;
			break;
		}
		if (wanted == null)
		{
			return Node.FailWork<Exceptions.CannotFindWhatINeedToPickUp>();
		}
		int amount = TripSize(station, wanted);
		Register(station, labor, wanted.GetType(), amount);
		return Node.Sequence.Label("Market fetch")
			.Do(labor.GoPickUp(item.GetComponent<GameComponent>(), amount))
			.Do((Labor l, Recepteur target) => l.GoStoreIn(target), labor, station.recepteur)
			.Finally((Labor l) =>
			{
				Release(station, l);
			}, labor);
	}

	// A lot is shared out between the market's operator and its helpers: each brings an even share of the whole lot
	// (what is left plus what is on its way), at least LeastPerTrip and at most what one trip carries.
	private static int TripSize(CraftingStation station, Ressource wanted)
	{
		int left = wanted.quantifiable.valeur;
		int lot = left + Incoming(station, wanted.GetType());
		int share = Mathf.CeilToInt((float)lot / (MaxHelpers.Value + 1));
		return Mathf.Clamp(Mathf.Max(share, LeastPerTrip), 1, Mathf.Min(left, MostPerTrip));
	}

	internal static bool IsHelper(CraftingStation station, Labor labor)
	{
		return station is MarketStation && labor != null && station.Operator != null && station.Operator != labor;
	}

	// A worker who is not the market's operator may fetch for it when there is something left to fetch that it can
	// find and carry, and not too many others already help.
	internal static bool HelperHasWork(CraftingStation station, Labor labor)
	{
		if (labor.Profession.OccupationType != Occupation.Type.Builder || !labor.recepteur.IsEmpty() || HelperCount(station) >= MaxHelpers.Value)
		{
			return false;
		}
		if (!Knowledge.Instance.CanReach(labor, station))
		{
			return false;
		}
		Description stuff = station.StuffToFetch();
		GameObject item = (stuff != null) ? labor.Find(stuff) : null;
		return item != null && labor.recepteur.HasRoomFor(item);
	}
}

// What a market still needs leaves out what is on its way.
[Feature(Features.Market, Features.MarketInfo)]
[HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.StuffToFetch))]
internal static class MarketStuffToFetchPatch
{
	private static void Postfix(CraftingStation __instance, ref Description __result)
	{
		if (__instance is MarketStation && __result != null)
		{
			__result = MarketDeliveries.Uncovered(__instance, __result);
		}
	}
}

// Every fetch for a market goes through MarketDeliveries.Fetch, the operator's too.
[Feature(Features.Market, Features.MarketInfo)]
[HarmonyPatch(typeof(CraftingInstructions), "FindResource")]
internal static class MarketFetchPatch
{
	private static bool Prefix(CraftingInstructions __instance, Labor labor, ref Node __result)
	{
		CraftingStation station = __instance.station;
		if (!(station is MarketStation))
		{
			return true;
		}
		__result = Node.Sequence.Do((Labor l, CraftingStation s) => MarketDeliveries.Fetch(s, l), labor, station);
		return false;
	}
}

// Helpers: work only when there is something to fetch for them.
[Feature(Features.Market, Features.MarketInfo)]
[HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.HasWorkFor))]
internal static class MarketHelperWorkPatch
{
	private static bool Prefix(CraftingStation __instance, Labor labor, ref bool __result)
	{
		if (!MarketDeliveries.IsHelper(__instance, labor))
		{
			return true;
		}
		__result = MarketDeliveries.HelperHasWork(__instance, labor);
		return false;
	}
}

// Helpers get a fetch order without being hired, so the operator keeps the market.
[Feature(Features.Market, Features.MarketInfo)]
[HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.GetInstruction))]
internal static class MarketHelperInstructionPatch
{
	private static bool Prefix(CraftingStation __instance, Labor labor, Var<OrderPackage<Labor>> result, ref Node __result)
	{
		if (!MarketDeliveries.IsHelper(__instance, labor))
		{
			return true;
		}
		__result = (StaticNode)Node.Sequence.Do((Labor aLabor, Var<OrderPackage<Labor>> aResult, CraftingStation station) =>
		{
			Description stuff = station.StuffToFetch();
			aResult.Set((stuff != null && MarketDeliveries.HelperHasWork(station, aLabor)) ? Order.Package.OfDescription(station.instructions.FindIngredients, stuff, Labor.DecisionSource.Project) : OrderPackage<Labor>.Empty);
		}, labor, result, __instance);
		return false;
	}
}
