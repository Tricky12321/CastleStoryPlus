using Brix.Game.AI;
using Brix.Game;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Building;

// Pending blueprints were only shown in build/edit mode; now they are always visible to their own faction.
[Feature(Features.BlueprintsVisible, Features.BlueprintsVisibleInfo)]
[HarmonyPatch(typeof(BuildGoalProvider), nameof(BuildGoalProvider.GoalVisibles), MethodType.Getter)]
internal static class BlueprintVisibilityPatch
{
	private static bool Prefix(BuildGoalProvider __instance, ref bool __result)
	{
		__result = User.LocalUser != null && __instance.faction == User.LocalUser.faction;
		return false;
	}
}
