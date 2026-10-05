using System.Collections;
using System.Collections.Generic;
using Brix.Components;
using Brix.Engine;
using Brix.Engine.Blocks;
using Brix.Pathfinding.Test;
using Brix.Utils;
using CastleStoryPlus.DevTools.Pathing;
using UnityEngine;
using FactoryBlocks = Brix.External.Factories.Blocks;

namespace CastleStoryPlus.DevTools.Testing;

// Building test scenery in the running game: find a flat open spot, place blocks (removed again by the test's
// clean-up), and wait until the navigation graph has taken the change in.
internal static class TestWorld
{
	// A spot where every column of a width x depth rectangle (centred on the returned voxel) stands at the same
	// height with room for a bricktron and a wall above it (clear voxels above the ground). Searched outward from
	// near, up to radius voxels away.
	internal static bool FindFlatArea(XYZ near, int width, int depth, int clear, int radius, out XYZ center)
	{
		for (int ring = 0; ring <= radius; ring++)
		{
			for (int dx = -ring; dx <= ring; dx++)
			{
				for (int dz = -ring; dz <= ring; dz++)
				{
					if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != ring)
					{
						continue;
					}
					if (IsFlat(near.x + dx, near.z + dz, width, depth, clear, out center))
					{
						return true;
					}
				}
			}
		}
		center = XYZ.zero;
		return false;
	}

	private static bool IsFlat(int cx, int cz, int width, int depth, int clear, out XYZ center)
	{
		center = XYZ.zero;
		if (!PathProbe.Ground(cx, cz, out XYZ middle))
		{
			return false;
		}
		for (int x = cx - width / 2; x <= cx + width / 2; x++)
		{
			for (int z = cz - depth / 2; z <= cz + depth / 2; z++)
			{
				if (!PathProbe.Ground(x, z, out XYZ ground) || ground.y != middle.y)
				{
					return false;
				}
				for (int y = 0; y < clear; y++)
				{
					if (!Voxel.IsEmpty(new XYZ(x, ground.y + y, z)))
					{
						return false;
					}
				}
			}
		}
		center = middle;
		return true;
	}

	// Stone bricks at the given voxels; the test's clean-up removes them again.
	internal static void PlaceBlocks(TestContext t, List<XYZ> voxels)
	{
		BlockInfo stone = FactoryBlocks.Stone_Brick.ToBlockInfo();
		BlockEngine engine = BrixSingleton<BlockEngine>.Instance;
		foreach (XYZ voxel in voxels)
		{
			engine.CreateBlock(voxel, Quaternion.identity, stone);
		}
		List<XYZ> placed = new List<XYZ>(voxels);
		t.Cleanup(() =>
		{
			foreach (XYZ voxel in placed)
			{
				IBlockData block = engine.GetBlockData(voxel);
				if (block != null && !Voxel.IsEmpty(voxel))
				{
					block.RootOrSelf.DeBlock();
				}
			}
		});
		t.Log("Placed " + voxels.Count + " stone bricks");
	}

	// The game registers placed blocks a frame later and rebuilds the graph in its own Update.
	internal static IEnumerator WaitForGraph(TestContext t)
	{
		yield return t.Frames(3);
		TestPathfinding graph = TestPathfinding.Instance;
		yield return t.Until(() => graph.DirtyVoxels.Count == 0 && graph.QueuedVoxels.Count == 0, 15f, "the navigation graph to take in the change");
		yield return t.Frames(2);
	}
}
