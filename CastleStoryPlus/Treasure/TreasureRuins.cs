using System;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Engine.Nature;
using Brix.Legacy;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using Brix.Game.AI.Damage;
using Brix.Game.Factories;
using Brix.Game.Locomotion;
using Brix.Pathfinding.Test;
using Brix.Transactions;
using Brix.Utils;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Treasure;

// A small castle ruin round each new chest, built of the game's own stone blocks (so it is saved, dug and climbed
// like any other building, and belongs to the neutral faction). Little is left of it:
// - two or three stretches of its old wall on the ring round the chest, 1 to 3 blocks high, highest in their middle
//   and worn down to their ends;
// - now and then the stump of a 2x2 corner tower, 2 to 4 high;
// - rubble: stone blocks fallen from the walls lying on the ground round it.
// It has no floor: the chest stands on the ground's surface. No trees grow within TreeFreeRadius of it.
//
// The ruin's stone is first worked out cell by cell, then laid layer by layer in 1x2 bricks like a mason's bond:
// the bricks run along x on even layers and along z on odd ones, and their joints move one cell from layer to
// layer, so a wall's joints are staggered. A cell no brick fits is a single block.
//
// A block (x, y, z) fills x-0.5..x+0.5 (and so on), so a block's top is half a block above its layer.
// No block rests on the ground's smooth surface layer (the crust: the game counts it as no support), so the lowest
// blocks go in the crust's cells, as a player's block built on grass does; the terrain itself is never changed. The game sees a
// block as resting on another only from the frame after that one was made (blocks it sees resting on nothing fall
// down), so a block on the terrain is made at once and one on another block waits in a queue until the blocks under
// it are a frame old (Tick, every frame).
internal static class TreasureRuins
{
	// Half the side of the ruin's square (the walls are on this ring).
	internal const int Radius = 4;

	// No trees within this of the ruin's middle (the guardians roam up to 9 from the chest).
	private const int TreeFreeRadius = 12;

	// How far below the spot's layer the ground is looked for.
	private const int MaxFill = 3;

	// A block to make: its key, the cell it is placed at, its quarter turns and every cell it fills.
	private sealed class Piece
	{
		public Factory.AssetKey Key;

		public XYZ Root;

		public int Turns;

		public List<XYZ> Cells;

		public Faction Faction;

		public float Until;
	}

	private const float WaitSeconds = 20f;

	// Blocks waiting for the blocks under them.
	private static readonly List<Piece> Queue = new List<Piece>();

	// The frame each of the ruins' cells was filled in.
	private static readonly Dictionary<XYZ, int> MadeIn = new Dictionary<XYZ, int>();

	// Every cell of the ruins built in this game (until it is left).
	private static readonly HashSet<XYZ> Built = new HashSet<XYZ>();

	// The 1x2 brick's second cell (from the cell it is placed at) for each quarter turn; read once from its volume.
	private static XYZ[] _doubleCells;

	private static bool _doubleCellsRead;

	// Builds the ruin round the spot (x, the first empty layer, z) and returns where the chest stands.
	internal static Vector3 Build(Vector3 spot, System.Random random)
	{
		int cx = Mathf.RoundToInt(spot.x);
		int level = Mathf.RoundToInt(spot.y);
		int cz = Mathf.RoundToInt(spot.z);
		Faction neutral = Faction.DefaultNeutral;
		ClearTrees(new XYZ(cx, level, cz));
		HashSet<XYZ> stone = new HashSet<XYZ>();
		// A few stretches of the old wall round the ring, each highest in its middle and worn down to its ends.
		List<XYZ> ring = Ring();
		int pieces = 2 + random.Next(2);
		int at = random.Next(ring.Count);
		for (int p = 0; p < pieces; p++)
		{
			int length = 3 + random.Next(4);
			int peak = 1 + random.Next(3);
			for (int i = 0; i < length; i++)
			{
				double middle = 1.0 - Math.Abs(2.0 * i / (length - 1) - 1.0);
				int height = Mathf.Clamp((int)Math.Round(peak * (0.4 + 0.6 * middle) - random.NextDouble() * 0.6), 1, peak);
				XYZ offset = ring[(at + i) % ring.Count];
				Wall(stone, cx + offset.x, level, cz + offset.z, height);
			}
			at += length + 2 + random.Next(Math.Max(1, ring.Count / pieces - length));
		}
		// Now and then the stump of a corner tower.
		if (random.NextDouble() < 0.5)
		{
			int sx = (random.Next(2) == 0) ? -1 : 1;
			int sz = (random.Next(2) == 0) ? -1 : 1;
			int height = 2 + random.Next(3);
			for (int i = 0; i < 2; i++)
			{
				for (int j = 0; j < 2; j++)
				{
					Wall(stone, cx + sx * (Radius - i), level, cz + sz * (Radius - j), Math.Max(1, height - random.Next(2)));
				}
			}
		}
		int placed = Lay(stone, neutral);
		placed += Rubble(cx, level, cz, 5 + random.Next(5), random, neutral);
		Plugin.Log.LogInfo("Treasure: a castle ruin of " + placed + " blocks at " + cx + ", " + level + ", " + cz + " (" + Queue.Count + " waiting for the blocks under them)");
		return new Vector3(cx, Surface(cx, Top(cx, level, cz), cz), cz);
	}

	// The height of the ground's surface in the column whose top is the cell (x, top, z): a full cell's top, or the
	// smooth surface layer's mean height (its four corners' levels, in thirds of a block from the cell's bottom).
	private static float Surface(int x, int top, int z)
	{
		int levels = Voxel.GetNiveaux(new XYZ(x, top, z));
		int sum = 0;
		for (int corner = 0; corner < 4; corner++)
		{
			sum += (levels >> (corner * 4)) & 3;
		}
		return top - 0.5f + sum / 12f;
	}

	// The trees within TreeFreeRadius of the ruin's middle are taken away (no logs): the guardians roam there, and the
	// ruin's walls would stand in their trunks.
	private static void ClearTrees(XYZ middle)
	{
		TreeRegistry registry = TreeRegistry.Instance;
		if (registry == null)
		{
			return;
		}
		List<XYZ> trees = new List<XYZ>();
		registry.GetTreesInRadius(middle, TreeFreeRadius).AddTo(trees);
		foreach (XYZ tree in trees)
		{
			registry.Remove(tree);
		}
	}

	// The ring of the ruin's walls (offsets from its middle), in order round it.
	private static List<XYZ> Ring()
	{
		List<XYZ> ring = new List<XYZ>();
		for (int i = -Radius; i < Radius; i++)
		{
			ring.Add(new XYZ(i, 0, -Radius));
		}
		for (int i = -Radius; i < Radius; i++)
		{
			ring.Add(new XYZ(Radius, 0, i));
		}
		for (int i = Radius; i > -Radius; i--)
		{
			ring.Add(new XYZ(i, 0, Radius));
		}
		for (int i = Radius; i > -Radius; i--)
		{
			ring.Add(new XYZ(-Radius, 0, i));
		}
		return ring;
	}

	// The ground's top in the column (searched down from just above the spot's layer), or the layer under the spot
	// when there is none within reach.
	private static int Top(int x, int level, int z)
	{
		for (int y = level + 1; y >= level - MaxFill; y--)
		{
			if (!Voxel.IsEmpty(new XYZ(x, y, z)))
			{
				return y;
			}
		}
		return level - 1;
	}

	// A piece of wall on the ground in the column, the height blocks above the ground's top.
	private static void Wall(HashSet<XYZ> stone, int x, int level, int z, int height)
	{
		int top = Top(x, level, z);
		int bed = Bed(x, top, z);
		Column(stone, x, bed, z, height + (top + 1 - bed));
	}

	// A stack of cells from y up, stopped by anything in the way; how many it got.
	private static int Column(HashSet<XYZ> stone, int x, int y, int z, int height)
	{
		for (int i = 0; i < height; i++)
		{
			XYZ cell = new XYZ(x, y + i, z);
			if (stone.Contains(cell) || !Free(cell))
			{
				return i;
			}
			stone.Add(cell);
		}
		return height;
	}

	// Lays the stone cells in 1x2 bricks, layer by layer from the bottom (see the top); how many blocks.
	private static int Lay(HashSet<XYZ> stone, Faction faction)
	{
		List<XYZ> cells = new List<XYZ>(stone);
		cells.Sort((a, b) => (a.y != b.y) ? a.y.CompareTo(b.y) : ((a.x != b.x) ? a.x.CompareTo(b.x) : a.z.CompareTo(b.z)));
		HashSet<XYZ> laid = new HashSet<XYZ>();
		int placed = 0;
		foreach (XYZ cell in cells)
		{
			if (laid.Contains(cell))
			{
				continue;
			}
			XYZ along = (cell.y % 2 == 0) ? XYZ.right : XYZ.forward;
			XYZ across = (cell.y % 2 == 0) ? XYZ.forward : XYZ.right;
			// The bond's way first, joints staggered; else the other way, else any free neighbour on.
			XYZ partner = Partner(stone, laid, cell, along, true) ?? Partner(stone, laid, cell, across, true) ?? Partner(stone, laid, cell, along, false) ?? Partner(stone, laid, cell, across, false) ?? cell;
			laid.Add(cell);
			if (partner != cell)
			{
				laid.Add(partner);
				placed += Double(cell, partner, faction) ? 1 : 0;
			}
			else
			{
				placed += Place(Blocks.Stone_Brick, cell, 0, new List<XYZ> { cell }, faction) ? 1 : 0;
			}
		}
		return placed;
	}

	// The cell after this one in the direction to join it with, if it is stone not yet laid (staggered: only from a
	// cell where a brick starts on this layer).
	private static XYZ? Partner(HashSet<XYZ> stone, HashSet<XYZ> laid, XYZ cell, XYZ direction, bool staggered)
	{
		XYZ next = cell + direction;
		if (!stone.Contains(next) || laid.Contains(next))
		{
			return null;
		}
		int position = (direction.x != 0) ? cell.x : cell.z;
		if (staggered && ((position + cell.y) & 1) != 0)
		{
			return null;
		}
		return next;
	}

	// A 1x2 brick filling the two cells (falls back to two single blocks if its turn is not known).
	private static bool Double(XYZ a, XYZ b, Faction faction)
	{
		XYZ[] seconds = DoubleCells();
		for (int turns = 0; turns < 4 && seconds != null; turns++)
		{
			if (seconds[turns] == b - a)
			{
				return Place(Blocks.Stone_DoubleBrick, a, turns, new List<XYZ> { a, b }, faction);
			}
			if (seconds[turns] == a - b)
			{
				return Place(Blocks.Stone_DoubleBrick, b, turns, new List<XYZ> { a, b }, faction);
			}
		}
		bool first = Place(Blocks.Stone_Brick, a, 0, new List<XYZ> { a }, faction);
		return Place(Blocks.Stone_Brick, b, 0, new List<XYZ> { b }, faction) || first;
	}

	private static XYZ[] DoubleCells()
	{
		if (_doubleCellsRead)
		{
			return _doubleCells;
		}
		_doubleCellsRead = true;
		XYZ[] seconds = new XYZ[4];
		for (int turns = 0; turns < 4; turns++)
		{
			if (!SecondCell(Blueprints.Stone_DoubleBrick, turns, out seconds[turns]) && !SecondCell(Blocks.Stone_DoubleBrick, turns, out seconds[turns]))
			{
				Plugin.Log.LogWarning("Treasure: no second cell for Stone_DoubleBrick turned " + turns * 90 + "°, the ruins use single blocks");
				return null;
			}
		}
		Plugin.Log.LogInfo("Treasure: Stone_DoubleBrick's second cell per quarter turn: " + seconds[0] + ", " + seconds[1] + ", " + seconds[2] + ", " + seconds[3]);
		_doubleCells = seconds;
		return seconds;
	}

	// The cell other than the pivot that the block fills, turned, from its volume settings.
	private static bool SecondCell(Factory.AssetKey key, int turns, out XYZ second)
	{
		second = XYZ.zero;
		if (!Factory.Exist(key))
		{
			return false;
		}
		Dictionary<XYZ, VPropertyMask> settings = VolumePreprocessor.GetIndexedVSettings(key, RotationIndex(turns));
		if (settings == null)
		{
			return false;
		}
		foreach (KeyValuePair<XYZ, VPropertyMask> pair in settings)
		{
			if (pair.Key != XYZ.zero && (pair.Value & (VPropertyMask.occupy | VPropertyMask.fantome)) != 0)
			{
				second = pair.Key;
				return true;
			}
		}
		return false;
	}

	private static Quaternion Rotation(int turns)
	{
		return Quaternion.Euler(0f, turns * 90f, 0f);
	}

	private static int RotationIndex(int turns)
	{
		return PreprocessedRotations.RotationToDiscreteIndex(Rotation(turns));
	}

	// Stone blocks fallen from the walls, on the ground just outside them.
	private static int Rubble(int cx, int floor, int cz, int count, System.Random random, Faction faction)
	{
		int placed = 0;
		for (int tries = 0; tries < count * 4 && placed < count; tries++)
		{
			double angle = random.NextDouble() * Math.PI * 2.0;
			double far = Radius + 1.5 + random.NextDouble() * 2.5;
			int x = cx + (int)Math.Round(Math.Cos(angle) * far);
			int z = cz + (int)Math.Round(Math.Sin(angle) * far);
			for (int y = floor + 1; y >= floor - MaxFill; y--)
			{
				if (!Voxel.IsEmpty(new XYZ(x, y, z)))
				{
					XYZ cell = new XYZ(x, Bed(x, y, z), z);
					if (Place(Blocks.Stone_Brick, cell, 0, new List<XYZ> { cell }, faction))
					{
						placed++;
					}
					break;
				}
			}
		}
		return placed;
	}

	// The lowest layer a block over the ground's top (x, top, z) goes in: the top's own cell when it is the smooth
	// surface layer (the block goes in it, as a player's block built on grass does; the terrain is left as it is),
	// else the layer above it.
	private static int Bed(int x, int top, int z)
	{
		return Voxel.IsCrust(new XYZ(x, top, z)) ? top : top + 1;
	}

	// Terrain a block rests on: the game counts the smooth surface layer (the crust) as no support.
	private static bool IsGround(XYZ cell)
	{
		using (IVoxel voxel = Voxel.GetTerrainBlockData(cell))
		{
			return !voxel.IsNull && !voxel.IsCrust;
		}
	}

	// A block in its cells: at once when all it rests on is the terrain, else queued for the blocks under it.
	private static bool Place(Factory.AssetKey key, XYZ root, int turns, List<XYZ> cells, Faction faction)
	{
		foreach (XYZ cell in cells)
		{
			if (!Free(cell))
			{
				return false;
			}
		}
		Piece piece = new Piece { Key = key, Root = root, Turns = turns, Cells = cells, Faction = faction, Until = Time.time + WaitSeconds };
		bool onGround = true;
		foreach (XYZ below in Below(piece))
		{
			onGround &= IsGround(below);
		}
		if (onGround)
		{
			return Make(piece);
		}
		Queue.Add(piece);
		return true;
	}

	// The cells right under the piece (not its own).
	private static IEnumerable<XYZ> Below(Piece piece)
	{
		foreach (XYZ cell in piece.Cells)
		{
			XYZ below = new XYZ(cell.x, cell.y - 1, cell.z);
			if (!piece.Cells.Contains(below))
			{
				yield return below;
			}
		}
	}

	private static int Lowest(Piece piece)
	{
		int y = int.MaxValue;
		foreach (XYZ cell in piece.Cells)
		{
			y = Mathf.Min(y, cell.y);
		}
		return y;
	}

	// Empty or only the ground's smooth surface layer, nothing growing, built or planned there.
	private static bool Free(XYZ cell)
	{
		return (Voxel.IsEmpty(cell) || Voxel.IsCrust(cell)) && !NatureRegistry.HasNatureAt(cell) && !VolumeRegistry.HasVolumesWithPropertyAt(cell, VPropertyMask.occupy | VPropertyMask.fantome);
	}

	// Spawns the finished block so that its pivot cell is the piece's root, as the game places a built blueprint.
	private static bool Make(Piece piece)
	{
		Quaternion rotation = Rotation(piece.Turns);
		Vector3 position = piece.Root.ToVector3() - rotation * CenterOffset(piece.Key);
		if (Transactor.SpawnUNet(Placed(piece.Key), piece.Faction, position, rotation) == null)
		{
			return false;
		}
		foreach (XYZ cell in piece.Cells)
		{
			MadeIn[cell] = Time.frameCount;
			Built.Add(cell);
		}
		return true;
	}

	// The offset from the block's transform to its pivot cell, from its blueprint (as BlockEngine places blocks).
	// The built block for a block key: a Blocks key is the loose block that falls and is carried (what a block that
	// loses its support becomes), the built one is what its blueprint makes (PlacedBlock.<name>).
	private static Factory.AssetKey Placed(Factory.AssetKey key)
	{
		Factory.AssetKey blueprint = new Factory.AssetKey("Blueprints", key.Name);
		if (Factory.Exist(blueprint))
		{
			Blueprint component = Factory.Peek(blueprint).GetComponent<Blueprint>();
			if (component != null && component.BlueprintResult != null)
			{
				return component.BlueprintResult;
			}
		}
		return new Factory.AssetKey("PlacedBlock", key.Name);
	}

	private static Vector3 CenterOffset(Factory.AssetKey key)
	{
		Factory.AssetKey blueprint = new Factory.AssetKey("Blueprints", key.Name);
		if (!Factory.Exist(blueprint))
		{
			return Vector3.zero;
		}
		BaseVoxelTransform transform = Factory.Peek(blueprint).GetComponent<BaseVoxelTransform>();
		return (transform != null) ? transform.CenterOffset : Vector3.zero;
	}

	// Every frame (server): the queued blocks whose blocks under them are there since an earlier frame, lowest first.
	internal static void Tick()
	{
		if (Queue.Count == 0)
		{
			if (MadeIn.Count > 0)
			{
				MadeIn.Clear();
			}
			return;
		}
		Queue.Sort((a, b) => Lowest(a).CompareTo(Lowest(b)));
		for (int i = 0; i < Queue.Count; i++)
		{
			Piece piece = Queue[i];
			bool ready = true;
			foreach (XYZ below in Below(piece))
			{
				if (Voxel.IsEmpty(below) || (MadeIn.TryGetValue(below, out int frame) && frame >= Time.frameCount))
				{
					ready = false;
					break;
				}
			}
			if (ready)
			{
				bool free = true;
				foreach (XYZ cell in piece.Cells)
				{
					free &= Free(cell);
				}
				if (free)
				{
					Make(piece);
				}
				Queue.RemoveAt(i--);
			}
			else if (Time.time > piece.Until)
			{
				Queue.RemoveAt(i--);
			}
		}
	}

	// Blocks of the ruins still waiting to be made.
	internal static bool Busy => Queue.Count > 0;

	internal static void Clear()
	{
		Queue.Clear();
		MadeIn.Clear();
		Built.Clear();
	}

	// Whether the block in the cell belongs to the neutral faction (the ruins' blocks).
	private static bool IsNeutralBlock(XYZ cell)
	{
		if (!Voxel.IsBlock(cell))
		{
			return false;
		}
		using (IBlockData block = Voxel.GetBlockData(cell))
		{
			GameObject go = (block.RootOrSelf as BlockData)?.PlacedBlock;
			Faction neutral = Faction.DefaultNeutral;
			return go != null && neutral != null && Faction.GetFaction(go) == neutral;
		}
	}

	// The enemies (the guardians among them) do not break through the ruins: their route search may plan a way
	// through blocks they break (TerrainPathNode), and a unit that is stuck hits the block in its way (HitBrick); the
	// neutral faction's blocks are left out of both.
	[Feature(Features.Treasure, Features.TreasureInfo)]
	[HarmonyPatch(typeof(TerrainPathNode), "IsBreakable")]
	private static class NoBreakingRoute
	{
		private static bool Prefix(XYZ p, ref bool __result)
		{
			if (!IsNeutralBlock(p))
			{
				return true;
			}
			__result = false;
			return false;
		}
	}

	[Feature(Features.Treasure, Features.TreasureInfo)]
	[HarmonyPatch(typeof(HitBrick), nameof(HitBrick.IsBreakable))]
	private static class NoHitting
	{
		private static bool Prefix(XYZ v, ref bool __result)
		{
			if (!IsNeutralBlock(v))
			{
				return true;
			}
			__result = false;
			return false;
		}
	}

	// The ruins take no damage from units that are not a player's (the guardians and other enemies hit the blocks
	// round their raid's beacon, which is the chest); the players can still dig them away.
	[Feature(Features.Treasure, Features.TreasureInfo)]
	[HarmonyPatch(typeof(PlacedBlockDamageReceiver), nameof(PlacedBlockDamageReceiver.ApplyDamage), typeof(float), typeof(DamageType), typeof(GameObject))]
	private static class NoEnemyDamage
	{
		private static bool Prefix(PlacedBlockDamageReceiver __instance, GameObject attacker)
		{
			if (attacker == null)
			{
				return true;
			}
			Faction neutral = Faction.DefaultNeutral;
			if (neutral == null || Faction.GetFaction(__instance.gameObject) != neutral)
			{
				return true;
			}
			Faction faction = Faction.GetFaction(attacker);
			return faction != null && faction.HasOwner();
		}
	}

	// A ruin block that loses its support is taken away instead of falling as a loose block: loose blocks are building
	// material, and the workers walked across the map to fetch the fallen ruin blocks for their blueprints. Built knows
	// only the blocks made in this game, so in a world loaded from a save a ruin block is known by its faction and place
	// instead: a neutral block near a chest (#58).
	[Feature(Features.Treasure, Features.TreasureInfo)]
	[HarmonyPatch(typeof(RootBlockData), nameof(RootBlockData.Physicalize))]
	private static class NoLooseBlocks
	{
		// How far from its chest (across) a ruin's block can be: the walls' corners and the rubble outside them.
		private const float Reach = 10f;

		private const float ReachHeight = 8f;

		private static readonly List<Vector3> ChestsNear = new List<Vector3>();

		private static bool Prefix(RootBlockData __instance)
		{
			if (!NetworkServer.active || (!Built.Remove(__instance.Position) && !InLoadedRuin(__instance)))
			{
				return true;
			}
			__instance.DeBlockClient(__instance.BlockInfo.ID);
			return false;
		}

		private static bool InLoadedRuin(RootBlockData block)
		{
			GameObject go = block.PlacedBlock;
			Faction neutral = Faction.DefaultNeutral;
			if (go == null || neutral == null || Faction.GetFaction(go) != neutral)
			{
				return false;
			}
			Vector3 at = go.transform.position;
			ChestsNear.Clear();
			TreasureChests.ChestPositions(ChestsNear);
			foreach (Vector3 chest in ChestsNear)
			{
				float dx = at.x - chest.x;
				float dz = at.z - chest.z;
				if (dx * dx + dz * dz <= Reach * Reach && Mathf.Abs(at.y - chest.y) <= ReachHeight)
				{
					return true;
				}
			}
			return false;
		}
	}
}
