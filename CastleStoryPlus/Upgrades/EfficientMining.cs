using Brix.Engine;
using Brix.Engine.Nature;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.Damage;
using Brix.Legacy;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Upgrades;

// Efficient Mining: every stone, ore or crystal that comes out of the ground has a chance (10%, 20%, 30% per tier)
// of coming with one more of the same kind, so a team gets that much more from digging on average. Counts for
// terrain a worker digs out (dig and quarry zones, levelling, the blue crystal veins) and for the natural rocks
// and ore deposits workers mine (boulders give stone, iron deposits raw iron, brimstone deposits brimstone). Plants
// and trees are not mining and keep their yield, and so do built blocks broken down (no stone out of nothing).
// Host only: the extra items are spawned like the game's own and synced by it.
internal static class EfficientMining
{
	// The chance for the team whose worker is breaking a terrain block right now, 0 when none.
	private static float _breaking;

	private static bool _spawning;

	internal static float Chance(Faction faction)
	{
		return (faction != null) ? ResearchEffects.Value(faction, UpgradeLines.EfficientMining) : 0f;
	}

	// How many more of count items: each has the chance of bringing one more.
	internal static int Extra(int count, float chance)
	{
		int extra = 0;
		for (int i = 0; i < count; i++)
		{
			if (Random.value < chance)
			{
				extra++;
			}
		}
		return extra;
	}

	internal static void Begin(GameObject attacker)
	{
		_breaking = (NetworkServer.active && attacker != null && attacker.GetComponent<Labor>() != null) ? Chance(Faction.GetFaction(attacker)) : 0f;
	}

	internal static void End()
	{
		_breaking = 0f;
	}

	// The game spawned what a block a worker broke held: maybe some more of it.
	internal static void Spawned(Vector3 position, int count, Factory.AssetKey key)
	{
		if (_breaking <= 0f || _spawning || key == null)
		{
			return;
		}
		Spawn(position, Extra(count, _breaking), key);
	}

	internal static void Spawn(Vector3 position, int count, Factory.AssetKey key)
	{
		if (count <= 0 || !NetworkServer.active)
		{
			return;
		}
		_spawning = true;
		try
		{
			Garnotte.Nouvelle(position, count, key);
		}
		finally
		{
			_spawning = false;
		}
	}
}

// A worker breaking terrain: the block is destroyed (and its stones spawned) inside this call.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(TerrainDamageReceiver), nameof(TerrainDamageReceiver.ReceiveAttack))]
internal static class EfficientMiningTerrainPatch
{
	private static void Prefix(GameObject attacker)
	{
		EfficientMining.Begin(attacker);
	}

	private static System.Exception Finalizer(System.Exception __exception)
	{
		EfficientMining.End();
		return __exception;
	}
}

[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(Garnotte), nameof(Garnotte.Nouvelle))]
internal static class EfficientMiningSpawnPatch
{
	private static void Postfix(Vector3 position, int qte, Factory.AssetKey assetKey)
	{
		EfficientMining.Spawned(position, qte, assetKey);
	}
}

// A worker mining a rock or an ore deposit: the game spawns what the hit gives a moment later, on every machine
// (an RPC), so the extra is rolled and spawned here on the host, for what the hit gives.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(NatureHarvestGoal), "HarvestHitAction")]
internal static class EfficientMiningNaturePatch
{
	private static void Prefix(NatureHarvestGoal __instance, Labor labor)
	{
		NatureData data = __instance.NData;
		if (!NetworkServer.active || labor == null || data == null || !data.Registered())
		{
			return;
		}
		float chance = EfficientMining.Chance(Faction.GetFaction(labor.gameObject));
		Factory.AssetKey key = (chance > 0f) ? KeyOf(data) : null;
		if (key == null)
		{
			return;
		}
		// What one hit gives (BoulderRegistry and MineralRegistry.Harvest), looked up before the hit can use the
		// deposit up.
		int count = (data is BoulderData) ? 4 : 1;
		EfficientMining.Spawn(data.RootPos.ToVector3() + new Vector3(0f, 0.5f, 0f), EfficientMining.Extra(count, chance), key);
	}

	private static Factory.AssetKey KeyOf(NatureData data)
	{
		if (data is BoulderData)
		{
			return RawResources.RawStone;
		}
		if (IronRegistry.GetMineral(data.RootPos) != null)
		{
			return RawResources.RawIron;
		}
		if (PyriteRegistry.GetMineral(data.RootPos) != null)
		{
			return RawResources.RawOrangeCrystal;
		}
		return null;
	}
}

// The research recipes of lines added later (Efficient Mining), after every other recipe, the market's coal and
// steel trades included (made first here if their own postfixes have not run yet), so saved queues keep their ids.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(Brix.Lua.LuaCrafting), nameof(Brix.Lua.LuaCrafting.Register))]
[HarmonyPriority(Priority.Last)]
internal static class EfficientMiningRecipesPatch
{
	private static void Postfix()
	{
		Metallurgy.MetallurgyLuaPatch.Run();
		UpgradeLines.RegisterRecipes(UpgradeHall.Research);
		Market.MarketPrices.RegisterLateTrades();
		UpgradeLines.RegisterLateRecipes();
	}
}
