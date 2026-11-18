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

// A block of the player's team was built: a blueprint of a placed block became the block (the end of the building
// itself; the blocks a loaded save sets up, and instant builds, do not go through it as a worker's work).
[Feature(Features.Achievements, Features.AchievementsInfo)]
[HarmonyPatch(typeof(Blueprint), nameof(Blueprint.MorphToObject))]
internal static class AchievementBuiltPatch
{
	private static void Postfix(Blueprint __instance, GameObject __result)
	{
		if (NetworkServer.active && __result != null && !__instance.IsAnti && __instance.BlueprintResult != null && __instance.BlueprintResult.ToString().StartsWith("PlacedBlock."))
		{
			AchievementTracker.Add(Faction.GetFaction(__instance.gameObject), AchievementCounter.Blocks);
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
	// When each tree or plant last counted (game seconds): objects come from a pool, so only a repeat within a few
	// seconds is the same one.
	private static readonly System.Collections.Generic.Dictionary<int, float> Counted = new System.Collections.Generic.Dictionary<int, float>();

	private static void Prefix(Goal __instance, Task work)
	{
		if (!NetworkServer.active || work == null || work.result != TaskResult.Succes || work.Status != TaskProgression.Finished)
		{
			return;
		}
		if (__instance is TreeGoal || __instance is NatureHarvestGoal)
		{
			Labor labor = work.Worker as Labor;
			// Several workers (or goals) can end work on the same tree or plant: each one counts once.
			// The goal itself, not its game object: the felled tree's goal is already destroyed here, and its game object throws.
			int id = __instance.InstanceId();
			if (labor != null && (!Counted.TryGetValue(id, out float last) || Time.time - last > 10f))
			{
				if (Counted.Count > 2000)
				{
					Counted.Clear();
				}
				Counted[id] = Time.time;
				AchievementTracker.Add(labor.faction, AchievementCounter.Gathered);
			}
		}
	}
}
