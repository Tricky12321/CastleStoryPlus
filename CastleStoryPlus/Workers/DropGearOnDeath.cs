using System.Collections.Generic;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Workers;

// Server: a dying bricktron drops its gear (weapons, shields, hats, bags, artificer and alchemist gear) on the ground
// instead of taking it into the corpse, where it disappears. Gear the game conjured (magical tools, e.g. from a
// respawn with tools) and aesthetic items still disappear, so dying cannot make gear.
[Feature(Features.DropGearOnDeath, Features.DropGearOnDeathInfo)]
[HarmonyPatch(typeof(Locomotion4), "Die")]
internal static class DropGearOnDeath
{
	private static void Prefix(Locomotion4 __instance)
	{
		if (__instance.IsCorpse)
		{
			return;
		}
		Toolbag toolbag = __instance.GetComponent<Toolbag>();
		if (toolbag == null)
		{
			return;
		}
		List<GameObject> tools = new List<GameObject>();
		toolbag.Tools(ref tools);
		foreach (GameObject item in tools)
		{
			Tool tool = (item != null) ? item.GetComponent<Tool>() : null;
			if (tool == null || tool.Magical || tool.Aesthetic)
			{
				continue;
			}
			toolbag.UnequipTool(item, Tool.OnDropBehaviours.Drop);
		}
	}
}
