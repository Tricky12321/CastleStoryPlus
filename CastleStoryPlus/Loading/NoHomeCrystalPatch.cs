using Brix.Game;
using Brix.Game.Components;
using CastleStoryPlus.Core;
using HarmonyLib;
using MoonSharp.Interpreter;

namespace CastleStoryPlus.Loading;

// The game UI asks Lua's FireflyNest.GetInfo for the local faction's home crystal while a save loads. Without a
// home crystal (lost in the game, or by an earlier MoveStructure that could pick it up) it threw "no firefly nest
// found", which ended the load and left the loading screen up for good. Now GetInfo answers with zeros and
// SpawnUnit does nothing, so such a save loads.
[Feature]
[HarmonyPatch]
internal static class NoHomeCrystalPatch
{
	private static bool _warned;

	private static bool HasHomeCrystal()
	{
		Faction faction = (User.LocalUser != null) ? User.LocalUser.faction : null;
		foreach (FireflyNest nest in FireflyNest.FireflyNests)
		{
			if (nest != null && nest.isHome && nest.faction == faction)
			{
				return true;
			}
		}
		if (!_warned)
		{
			_warned = true;
			Plugin.Log.LogWarning("NoHomeCrystal: the local faction has no home crystal; the crystal display shows nothing");
		}
		return false;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(FireflyNest), nameof(FireflyNest.lua_GetInfo))]
	private static bool GetInfoPrefix(ScriptExecutionContext context, ref DynValue __result)
	{
		if (HasHomeCrystal())
		{
			return true;
		}
		__result = DynValue.NewTable(context.OwnerScript);
		__result.Table.Set("currentXp", DynValue.NewNumber(0));
		__result.Table.Set("totalXp", DynValue.NewNumber(1));
		__result.Table.Set("spawnableCount", DynValue.NewNumber(0));
		__result.Table.Set("autoRespawnFireflyCount", DynValue.NewNumber(0));
		return false;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(FireflyNest), nameof(FireflyNest.lua_SpawnUnit))]
	private static bool SpawnUnitPrefix(ref DynValue __result)
	{
		if (HasHomeCrystal())
		{
			return true;
		}
		__result = DynValue.Void;
		return false;
	}
}
