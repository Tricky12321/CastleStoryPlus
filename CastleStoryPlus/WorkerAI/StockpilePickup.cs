using System;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Game.Semantique;
using CastleStoryPlus.Core;
using HarmonyLib;
using Motus.Behavior;
using Smooth.Algebraics;
using UnityEngine;

namespace CastleStoryPlus.WorkerAI;

// Server: a worker fetching from a stockpile takes any item of the kind it came for once it is there.
// The game sends a worker to one particular item of the stockpile: the first one of the kind, so every worker
// fetching that kind from the stockpile goes for the same item. The first to arrive takes it (with as many more as
// it carries), and every other worker failed its task ("object has been moved from the container") although the
// stockpile still held plenty, and searched again, often just to fail the same way. The worker trace showed this as
// the most common reason for failed build tasks. Now the worker fails only when the stockpile holds none of the kind.
[Feature(Features.StockpilePickup, Features.StockpilePickupInfo)]
[HarmonyPatch(typeof(PickUpNode), "_GoPickUp")]
internal static class StockpilePickup
{
	private static bool Prefix(Labor labor, GameComponent itemToPickUp, int upTo, ref Node __result)
	{
		if (itemToPickUp == null || labor == null || labor.recepteur.Carries(itemToPickUp))
		{
			return true;
		}
		Recepteur stockpile = itemToPickUp.ParentRecepteur;
		if (stockpile == null || stockpile.Fonction == FonctionDeContenant.personnage || stockpile.gameObject == itemToPickUp.gameObject)
		{
			return true;
		}
		GameComponent target = stockpile.GetComponent<GameComponent>();
		if (target == null)
		{
			return true;
		}
		Factory.AssetKey key = itemToPickUp.AssetKey;
		Node n = (StaticNode)Node.Sequence.Do((Labor _labor, GameComponent _target) =>
		{
			Knowledge.Instance.ReserveUntil(_target.gameObject, _labor.signal.WorkEnded);
		}, labor, target).Label("Reserve Until", labor).Do((Labor _labor, GameComponent t) => _labor.RunToExactly(Location.Of(t)), labor, target)
			.Do((Func<Labor, Recepteur, Factory.AssetKey, int, Node>)TakeAny, labor, stockpile, key, upTo)
			.Label("Pick up " + itemToPickUp.name + " from " + stockpile.name);
		__result = n.FailWhen(SharedWorkException<Exceptions.NullTarget>.Get).Condition(GameComponentExtender.IsNullOrReleased, target)
			.FailWhen(SharedWorkException<Exceptions.ObjectDoesNotFitInDestination>.Get)
			.Condition((Tuple<Labor, Factory.AssetKey> t) => !t._1.recepteur.HasRoomFor(t._2), Tuple.Create(labor, key))
			.FailWhen(SharedWorkException<Exceptions.ObjectHasBeenMovedFromRecepteur>.Get)
			.Condition((Tuple<Recepteur, Factory.AssetKey> t) => t._1.IsNullOrReleased() || !t._1.FirstObjectOf(t._2, out GameObject _), Tuple.Create(stockpile, key));
		return false;
	}

	// At the stockpile: whichever item of the kind is there now.
	private static Node TakeAny(Labor labor, Recepteur stockpile, Factory.AssetKey key, int upTo)
	{
		if (stockpile.IsNullOrReleased() || !stockpile.FirstObjectOf(key, out GameObject item) || item == null)
		{
			return Node.FailWork<Exceptions.ObjectHasBeenMovedFromRecepteur>();
		}
		GameComponent component = item.GetComponent<GameComponent>();
		if (component == null)
		{
			return Node.FailWork<Exceptions.ObjectHasBeenMovedFromRecepteur>();
		}
		return labor.TakeSome(component, upTo);
	}
}
