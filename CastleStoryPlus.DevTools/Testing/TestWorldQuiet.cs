using System;
using Brix.Assets;
using CastleStoryPlus.Nature;
using CastleStoryPlus.Treasure;
using HarmonyLib;

namespace CastleStoryPlus.DevTools.Testing;

// What the mod adds to a world at random, kept off the tests' ground so a run does not depend on the dice:
// - no new wild plants seed themselves while tests run (WildPlants tries columns at random every game minute);
// - Test Island (tools/make_test_island.py), the map the test runner plays on, gets no treasure chests: their
//   places, ruins and guards are drawn at random (and the chests stay when the run takes the units away).
// The Essentials edition has neither (the patches are left out; the classes stay for the code that uses them).
internal static class WildPlantsPause
{
	internal static bool Paused;
}

#if !ESSENTIALS
[HarmonyPatch(typeof(WildPlants), "Seed")]
internal static class WildPlantsPausePatch
{
	private static bool Prefix()
	{
		return !WildPlantsPause.Paused;
	}
}
#endif

#if !ESSENTIALS
[HarmonyPatch(typeof(TreasureChests), "Place")]
#endif
internal static class NoTreasureOnTestIslandPatch
{
#if !ESSENTIALS
	// How many times a level set about placing the chests (on any map), for the treasure tests.
	internal static int Calls;

	private static bool Prefix(Asset_Map map)
	{
		Calls++;
		if (!IsTestIsland(map))
		{
			return true;
		}
		DevToolsPlugin.Log.LogInfo("Tests: no treasure chests on Test Island");
		return false;
	}

#endif
	internal static bool IsTestIsland(Asset_Map map)
	{
		return map != null && ((map.codename != null && map.codename.StartsWith("TestIsland", StringComparison.Ordinal)) || map.displayName == "Test Island");
	}
}
