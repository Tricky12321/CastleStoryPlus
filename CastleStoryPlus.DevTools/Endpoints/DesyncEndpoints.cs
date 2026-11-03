#if UNITY6
using System.Collections.Generic;
using Brix.Components;
using Brix.Engine;
using Brix.Engine.Blocks;
using CastleStoryPlus.DevTools.Api;
using CastleStoryPlus.Multiplayer;
using UnityEngine;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// The desync protection (Multiplayer/DesyncProtection): its state, a comparison at once, and a desync made on
// purpose: terrain or a block taken away on this peer only, as a local collapse would, to see it corrected.
internal static class DesyncEndpoints
{
	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/desync", "Desync protection: chunks hashed here, comparisons voted (host), chunks out of sync, chunks corrected here, the last vote, the average time hashing takes a frame", Get));
		routes.Add(new Route("POST", "/api/desync/check", "The host compares the worlds at once (the next round; a chunk is corrected after two rounds in a row)", Check));
		routes.Add(new Route("POST", "/api/desync/break", "Takes away terrain voxels (or the blocks) in a box on this peer only, without telling the others: a desync made on purpose", Break)
		{
			Parameters = "at (x,y,z, required), radius (default 1: a 3x3x3 box), blocks (true: the placed blocks there instead of terrain)"
		});
	}

	private static object Get(ApiRequest request)
	{
		return Obj(
			"hashedChunks", DesyncProtection.HashedChunks,
			"rounds", DesyncProtection.Rounds,
			"differingOnce", DesyncProtection.Suspects,
			"outOfSync", DesyncProtection.OutOfSync,
			"corrected", DesyncProtection.Corrected,
			"lastVote", DesyncProtection.LastVote,
			"hashMsPerFrame", DesyncProtection.HashFrames > 0 ? Round((float)(DesyncProtection.HashMilliseconds / DesyncProtection.HashFrames)) : 0f);
	}

	private static object Check(ApiRequest request)
	{
		DesyncProtection.CheckNow();
		return Obj("checking", true);
	}

	private static object Break(ApiRequest request)
	{
		Vector3 at = request.Vector("at");
		int radius = request.Int("radius", 1);
		bool blocks = request.Bool("blocks", false);
		XYZ center = XYZ.FromVector3(at);
		int removed = 0;
		Voxel.Engine.BeginChanges();
		for (int x = -radius; x <= radius; x++)
		{
			for (int y = -radius; y <= radius; y++)
			{
				for (int z = -radius; z <= radius; z++)
				{
					XYZ p = center + new XYZ(x, y, z);
					if (blocks)
					{
						if (BrixSingleton<BlockEngine>.Instance.GetBlockData(p)?.RootOrSelf is RootBlockData root)
						{
							root.DeBlockClient(root.BlockInfo.ID);
							removed++;
						}
					}
					else if (Voxel.IsTerrain(p))
					{
						using (IVoxel voxel = Voxel.GetTerrainBlockData(p))
						{
							voxel.DeBlockClient(-1);
						}
						removed++;
					}
				}
			}
		}
		Voxel.Engine.ApplyChanges();
		Architecte.AppliquerChangements();
		return Obj("removed", removed, "blocks", blocks);
	}
}
#endif
