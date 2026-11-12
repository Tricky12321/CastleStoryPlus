using Brix.Game.AI;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine.Networking;

namespace CastleStoryPlus.Workers;

// Syncs the stats by appending them to CharacterState's own network data, in both the initial state and
// every update. Both ends must run the mod (modded and unmodded peers cannot play together).
[Feature]
[HarmonyPatch(typeof(CharacterState), nameof(CharacterState.OnSerialize))]
internal static class WorkerStatsNetwork
{
	// A sync var bit the game does not use (it uses 0x01-0x10); setting it makes UNET send an update.
	public const uint DirtyBit = 0x80u;

	private static void Postfix(CharacterState __instance, NetworkWriter writer, ref bool __result)
	{
		WorkerStats stats = WorkerStats.Peek(__instance);
		writer.WritePackedUInt32((uint)(stats?.WorkXp ?? 0));
		writer.WritePackedUInt32((uint)(stats?.CombatXp ?? 0));
		writer.WritePackedUInt32((uint)(stats?.CallToArmsRole ?? 0));
		writer.WritePackedUInt32((uint)(stats?.Tier ?? 0));
		writer.WritePackedUInt32((uint)(stats?.Variant ?? 0));
		writer.WritePackedUInt32((uint)(stats?.Guard ?? 0));
		writer.WritePackedUInt32((uint)(stats?.HasPost ?? 0));
		writer.Write(stats?.PostX ?? 0);
		writer.Write(stats?.PostY ?? 0);
		writer.Write(stats?.PostZ ?? 0);
		__result = true;
	}
}

[Feature]
[HarmonyPatch(typeof(CharacterState), nameof(CharacterState.OnDeserialize))]
internal static class WorkerStatsNetworkRead
{
	private static void Postfix(CharacterState __instance, NetworkReader reader)
	{
		int work = (int)reader.ReadPackedUInt32();
		int combat = (int)reader.ReadPackedUInt32();
		int role = (int)reader.ReadPackedUInt32();
		int tier = (int)reader.ReadPackedUInt32();
		int variant = (int)reader.ReadPackedUInt32();
		int guard = (int)reader.ReadPackedUInt32();
		int hasPost = (int)reader.ReadPackedUInt32();
		int postX = reader.ReadInt32();
		int postY = reader.ReadInt32();
		int postZ = reader.ReadInt32();
		WorkerStats stats = WorkerStats.For(__instance);
		if (stats.WorkXp == work && stats.CombatXp == combat && stats.CallToArmsRole == role && stats.Tier == tier && stats.Variant == variant && stats.Guard == guard && stats.HasPost == hasPost && stats.PostX == postX && stats.PostY == postY && stats.PostZ == postZ)
		{
			return;
		}
		stats.Guard = guard;
		stats.HasPost = hasPost;
		stats.PostX = postX;
		stats.PostY = postY;
		stats.PostZ = postZ;
		stats.WorkXp = work;
		stats.CombatXp = combat;
		stats.CallToArmsRole = role;
		stats.Tier = tier;
		bool variantChanged = stats.Variant != variant;
		stats.Variant = variant;
		WorkerStats.RaiseChanged(__instance);
		if (variantChanged)
		{
			WorkerStats.RaiseVariantChanged(__instance);
		}
	}
}

// A pooled bricktron that is reused starts with fresh stats.
[Feature]
[HarmonyPatch(typeof(Labor), nameof(Labor.OnRequested))]
internal static class WorkerStatsReset
{
	private static void Prefix(Labor __instance)
	{
		WorkerStats.Forget(__instance.state);
	}
}
