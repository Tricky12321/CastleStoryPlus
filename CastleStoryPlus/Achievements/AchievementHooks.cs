using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using CastleStoryPlus.Core;
using CastleStoryPlus.Weather;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Achievements;

// Where the game's code reports what the player's team did, for the achievements (AchievementList). Most run on the host
// (AchievementTracker.Add: the team that did it is known there); what every peer sees for the local player alone is
// counted on that peer (Local). The wave count (WaveWarning), the call to arms (CallToArms), the chests (TreasureChests)
// and the kills (SoulsToCrystal) report from their own code.
[Feature(Features.Achievements, Features.AchievementsInfo)]
internal static class AchievementHooks
{
	private static void Enable()
	{
		// Every peer: the colony's research (the faction's tiers changed, on the host or received) and the rain.
		Upgrades.TeamUpgrades.Changed += (Faction faction) =>
		{
			if (faction != null && faction == AchievementTracker.LocalFaction)
			{
				AchievementTracker.LocalMax(AchievementCounter.Research, Upgrades.UpgradeLines.ResearchedTiers(faction));
			}
		};
		Rain.Changed += (WeatherKind from, WeatherKind to, bool announce) =>
		{
			if (to == WeatherKind.Rain && announce)
			{
				AchievementTracker.Local(AchievementCounter.Rains);
			}
		};
	}

	// Host: a unit was killed by a player's unit (the soul's killer, SoulsToCrystal): an enemy, and a guardian when it
	// is one of the treasure's beasts.
	internal static void Killed(Labor victim, Faction killer)
	{
		if (!NetworkServer.active || victim == null || killer == null || victim.faction == null || !victim.faction.isAI)
		{
			return;
		}
		AchievementTracker.Add(killer, AchievementCounter.Kills);
		if (Treasure.Beasts.KindOf(victim) != null)
		{
			AchievementTracker.Add(killer, AchievementCounter.Guardians);
		}
	}
}

// A finished order of a workshop: a trade at a market, or any other crafting (the research station's, smithy's and
// armoury's orders are counted by the tiers they unlock).
[Feature(Features.Achievements, Features.AchievementsInfo)]
[HarmonyPatch(typeof(CraftingLabor), nameof(CraftingLabor.FinishRecipe))]
internal static class AchievementCraftPatch
{
	private static void Prefix(CraftingLabor __instance)
	{
		if (!NetworkServer.active || __instance == null)
		{
			return;
		}
		Faction team = Faction.GetFaction(__instance.gameObject);
		if (__instance.GetComponent<Market.MarketStation>() != null)
		{
			AchievementTracker.Add(team, AchievementCounter.Trades);
		}
		else if (__instance.GetComponent<Upgrades.UpgradeStation>() == null)
		{
			AchievementTracker.Add(team, AchievementCounter.Crafted);
		}
	}
}

// A block of the player's team was built (the game's own count of objects built): only the placed blocks, not what
// a loaded save sets up (nothing counts before the level is ready).
[Feature(Features.Achievements, Features.AchievementsInfo)]
[HarmonyPatch(typeof(StatsTracker), nameof(StatsTracker.ObjectBuilt))]
internal static class AchievementBuiltPatch
{
	private static void Prefix(Faction faction, Factory.AssetKey key)
	{
		if (NetworkServer.active && faction != null && key != null && key.ToString().StartsWith("PlacedBlock."))
		{
			AchievementTracker.Add(faction, AchievementCounter.Blocks);
		}
	}
}

// A block of ground dug by a unit of the team.
[Feature(Features.Achievements, Features.AchievementsInfo)]
[HarmonyPatch(typeof(StatsTracker), nameof(StatsTracker.DugVoxel))]
internal static class AchievementDugPatch
{
	private static void Prefix(GameObject murderer)
	{
		if (NetworkServer.active && murderer != null)
		{
			AchievementTracker.Add(Faction.GetFaction(murderer), AchievementCounter.Dug);
		}
	}
}

// A tree felled or a plant harvested: the task of a worker ended well.
[Feature(Features.Achievements, Features.AchievementsInfo)]
[HarmonyPatch(typeof(Goal), nameof(Goal.EndWork))]
internal static class AchievementGatherPatch
{
	private static void Prefix(Goal __instance, Task work)
	{
		if (!NetworkServer.active || work == null || work.result != TaskResult.Succes || work.Status != TaskProgression.Finished)
		{
			return;
		}
		if (__instance is TreeGoal || __instance is NatureHarvestGoal)
		{
			Labor labor = work.Worker as Labor;
			if (labor != null)
			{
				AchievementTracker.Add(labor.faction, AchievementCounter.Gathered);
			}
		}
	}
}
