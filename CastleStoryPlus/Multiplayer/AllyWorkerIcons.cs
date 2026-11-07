using Brix.Game.AI;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Multiplayer;

// The icon over a bricktron put on a task is its task's beacon icon. A project's beacon is only sent to its own
// faction's players, so in an allied player's game the bricktron's task had no beacon and the game threw on every
// refresh of its icon. Those refreshes run inside the network update of the items the bricktron picks up and puts
// down (equipping a tool refreshes the icon): the exception broke off that update, so the other player's game lost
// track of what was carried and delivered (material for a building lay on the ground) and filled its log with
// thousands of errors. Such a bricktron shows no task icon instead.
[Feature(Features.AllyWorkerIcons, Features.AllyWorkerIconsInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.GetAssignationIcon))]
internal static class AllyWorkerIcons
{
	private static bool Prefix(Labor __instance, ref Sprite __result)
	{
		if (__instance.Operable != null)
		{
			return true;
		}
		Project project = __instance.Project;
		if (project != null && project.Balise != null && project.Balise.Meta != null)
		{
			return true;
		}
		__result = null;
		return false;
	}
}
