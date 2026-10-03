using System.Reflection;
using Brix.Game.Components;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Economy;

// Each brewed firefly carries more energy than the blue crystals it costs, so new workers arrive faster.
[Feature(Features.Economy, Features.EconomyInfo)]
[HarmonyPatch]
internal static class EnergyPatch
{
	private static MethodBase TargetMethod()
	{
		return AccessTools.Method(typeof(FireflyNest.Brewery), nameof(FireflyNest.Brewery.ProcessFirefly));
	}

	private static void Postfix(GameObject go)
	{
		Firefly firefly = go.GetComponent<Firefly>();
		if (firefly != null)
		{
			firefly.pureEnergy = Mathf.RoundToInt(firefly.pureEnergy * Plugin.EnergyMultiplier.Value);
		}
	}
}
