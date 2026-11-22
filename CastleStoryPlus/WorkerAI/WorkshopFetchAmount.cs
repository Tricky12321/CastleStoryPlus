using Brix.Game.AI;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Game.Semantique;
using CastleStoryPlus.Core;
using CastleStoryPlus.Market;
using HarmonyLib;
using Smooth.Slinq;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.WorkerAI;

// A workshop's worker takes only what the workshop still needs: the game's fetch for a crafting station takes up to
// 20 (as many as the hands hold) of the first ingredient found, so archer gear (2 planks) had 3 planks carried and
// knight armour (5 iron) 3 + 3 iron, one carried back. The take is cut to the station's missing amount of that
// resource (its queue's need minus its content) less what the worker already carries of it. Markets size their own
// fetches (MarketDeliveries); the research stations' reservations cut the take further where they apply.
[Feature(Features.TaskFlow, Features.TaskFlowInfo)]
[HarmonyPatch(typeof(PickUpNode), nameof(PickUpNode.TakeSome))]
internal static class WorkshopFetchAmount
{
	private static void Prefix(Labor labor, GameComponent itemToTake, ref int upTo)
	{
		if (!NetworkServer.active || labor == null || itemToTake.IsNullOrReleased())
		{
			return;
		}
		GameObject giver = labor.CurrentTask?.Giver;
		CraftingStation station = (giver != null) ? giver.GetComponent<CraftingStation>() : null;
#if !ESSENTIALS
		if (station == null || station is MarketStation || itemToTake.ParentRecepteur == station.recepteur)
#else
		if (station == null || itemToTake.ParentRecepteur == station.recepteur)
#endif
		{
			return;
		}
		Description needed = station.StuffToFetch();
		if (needed == null)
		{
			return;
		}
		foreach (Ressource resource in needed.GetRessources().ToList())
		{
			if (resource.quantifiable.valeur <= 0 || !Apparence.PrincipaleIs(itemToTake.gameObject, resource))
			{
				continue;
			}
			int carried = (labor.recepteur != null) ? labor.recepteur.ContentDescription.Value(resource.GetType()) : 0;
			int missing = Mathf.Max(1, resource.quantifiable.valeur - carried);
			if (upTo <= 0 || upTo > missing)
			{
				upTo = missing;
			}
			return;
		}
	}
}
