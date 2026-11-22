#if ESSENTIALS
using System;
using System.Collections.Generic;
using System.Reflection;
using Brix.External.Factories;
using Brix.IO.Serialization;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Saving;

// Essentials edition only: a save made with the Full edition holds game objects of features Essentials leaves out (a
// market, warehouse, large stockpile, research station, ladder, road, sapling, coal or steel item, kit bundle...).
// Their factory key does not exist here. The game's loader creates the saved objects one by one and leaves out one
// that fails, so such an object was already skipped, with a stack trace that does not say why. This says it once per
// kind in plain words before the loader skips it; the rest of the world loads as before (UnknownBlocks does the same
// for blocks, e.g. the Full edition's custom bricks and slabs).
[Feature]
[HarmonyPatch]
internal static class UnknownObjects
{
	private static readonly HashSet<string> Told = new HashSet<string>();

	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(DefaultGameObjectFactory), nameof(DefaultGameObjectFactory.Spawn));
		yield return AccessTools.Method(typeof(PooledGameObjectFactory), nameof(PooledGameObjectFactory.Spawn));
	}

	private static void Prefix(object assetKey)
	{
		Factory.AssetKey key = assetKey as Factory.AssetKey;
		if (key == null || Factory.Exist(key))
		{
			return;
		}
		string name = key.ToString();
		if (Told.Add(name))
		{
			Plugin.Log.LogWarning("UnknownObjects: '" + name + "' in this save is not part of the " + Edition.Id + " edition (a Full edition building or item): it is left out of the world");
		}
		throw new InvalidOperationException("'" + name + "' is not part of the " + Edition.Id + " edition");
	}
}
#endif
