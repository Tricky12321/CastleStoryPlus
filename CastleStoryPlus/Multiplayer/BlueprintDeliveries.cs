using Brix.Game;
using Brix.Game.Components;
using Brix.Game.Network;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Multiplayer;

// Material delivered to a blueprint is sent to the players who have the blueprint. Who is sent an object is worked
// out from its fog of war (LocalFactionVision): an item a storage holds is seen with its holder, but a blueprint has
// no fog of war of its own (it is sent to its faction's players, Blueprint.OnRebuildObservers), and delivered
// material is the neutral faction's. So the host stopped sending every delivered plank and brick to the other
// players: their blueprint never received it, showed the full cost still needed (the warehouse's bricks) and the
// material was gone from their game. An item held by a blueprint is now seen by the blueprint's faction, the
// players the blueprint itself is sent to.
[Feature]
[HarmonyPatch(typeof(LocalFactionVision), nameof(LocalFactionVision.IsVisibleForFaction))]
internal static class BlueprintDeliveries
{
	private static void Postfix(LocalFactionVision __instance, Faction faction, ref bool __result)
	{
		if (__result || __instance == null || faction == null || __instance.IgnoreParentVision)
		{
			return;
		}
		GenericParentTracker tracker = __instance.GetComponent<GenericParentTracker>();
		if (tracker == null || !tracker.IsTracked())
		{
			return;
		}
		Blueprint blueprint = tracker.TrackingParent().GetComponent<Blueprint>();
		if (blueprint != null && blueprint.faction == faction)
		{
			__result = true;
		}
	}
}
