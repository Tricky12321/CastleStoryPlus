using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Network;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Treasure;

// The treasure's castle ruins (and the chests in them) show through the fog of war, as the ground does: the game
// hides every object of another faction where its faction cannot see, and the ruins' blocks are the neutral
// faction's. Neutral objects that are not units are seen by everyone; neutral units and the other factions' objects
// stay hidden in the fog. A neutral object something carries (a bricktron's scarf, its tool, what it hauls) is seen
// only with what carries it, as the game has it: else an enemy's scarves floated in the fog without the bricktrons.
[Feature(Features.Treasure, Features.TreasureInfo)]
[HarmonyPatch(typeof(LocalFactionVision), nameof(LocalFactionVision.IsVisibleForFaction))]
internal static class RuinsInFog
{
	private static void Postfix(LocalFactionVision __instance, ref bool __result)
	{
		if (__result || __instance == null)
		{
			return;
		}
		Faction neutral = Faction.DefaultNeutral;
		if (neutral != null && __instance.faction == neutral && __instance.GetComponent<Labor>() == null && !Carried(__instance))
		{
			__result = true;
		}
	}

	private static bool Carried(LocalFactionVision vision)
	{
		if (vision.IgnoreParentVision)
		{
			return false;
		}
		GenericParentTracker tracker = vision.GetComponent<GenericParentTracker>();
		return tracker != null && tracker.IsTracked();
	}
}
