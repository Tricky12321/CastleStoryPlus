#if UNITY6
using System;
using System.Collections.Generic;
using System.IO;
using Brix.Components;
using Brix.Engine;
using Brix.Engine.Blocks;
using Brix.Engine.FlatWorld.Layers;
using Brix.Utils;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Multiplayer;

// The world's state that every peer keeps for itself, in chunks of 16 x 16 columns over the whole height, for the
// desync protection (DesyncProtection): a hash of each chunk, a snapshot of it and making a chunk match a snapshot.
// The map files are the same everywhere (the lobby checks them), so a chunk is what has changed since: its columns
// (bottom, top, crust type and volume), the voxels dug out of it (the negative layer), the voxels added to it with
// their resources (the terrain surface layer) and the blocks placed in it (position, rotation, type).
internal static class WorldChunks
{
	internal const int Size = 16;

	internal struct Block
	{
		public XYZ Position;

		public int Rotation;

		public int Type;

		public float Damage;
	}

	internal sealed class Snapshot
	{
		public int Key;

		// bottom, top, crust type, crust volume for each column, x then z.
		public readonly int[] Columns = new int[Size * Size * 4];

		public readonly List<XYZ> Removed = new List<XYZ>();

		public readonly List<XYZ> Added = new List<XYZ>();

		public readonly List<VoxelTerrainResources> AddedResources = new List<VoxelTerrainResources>();

		public readonly List<Block> Blocks = new List<Block>();
	}

	private static readonly AccessTools.FieldRef<ColumnPointSet, Dictionary<XYZ, IntClasse>> Points = AccessTools.FieldRefAccess<ColumnPointSet, Dictionary<XYZ, IntClasse>>("_removedSet");

	// The first voxels and blocks the last Apply changed, for the log.
	internal static readonly List<XYZ> LastChanged = new List<XYZ>();

	// The blocks by chunk, made again for every sweep over the chunks (Blocks).
	private static readonly Dictionary<int, List<Block>> BlocksByChunk = new Dictionary<int, List<Block>>();

	internal static LayerVoxelEngine Engine => VoxelEngine.Impl as LayerVoxelEngine;

	internal static bool Ready => Engine != null && Engine.test != null && BrixSingleton<BlockEngine>.Instance != null;

	internal static int Key(int chunkX, int chunkZ)
	{
		return (chunkX & 0xFFFF) | (chunkZ << 16);
	}

	internal static int KeyOf(XYZ p)
	{
		XYZ start = Engine.test.worldStart;
		return Key(Div(p.x - start.x), Div(p.z - start.z));
	}

	// Every chunk of the world.
	internal static List<int> All()
	{
		List<int> keys = new List<int>();
		XYZ size = Engine.test.worldSize;
		for (int cz = 0; cz * Size < size.z; cz++)
		{
			for (int cx = 0; cx * Size < size.x; cx++)
			{
				keys.Add(Key(cx, cz));
			}
		}
		return keys;
	}

	// The blocks placed now, by chunk; called before a sweep hashes the chunks.
	internal static void CollectBlocks()
	{
		foreach (List<Block> list in BlocksByChunk.Values)
		{
			list.Clear();
		}
		foreach (RootBlockData root in BrixSingleton<BlockEngine>.Instance.RootBlocks)
		{
			if (root == null || root.BlockInfo == null || root.BlockInfo.ID <= 0)
			{
				continue;
			}
			int key = KeyOf(root.Position);
			if (!BlocksByChunk.TryGetValue(key, out List<Block> list))
			{
				list = new List<Block>();
				BlocksByChunk[key] = list;
			}
			list.Add(new Block { Position = root.Position, Rotation = root.RotationIndex, Type = root.BlockInfo.ID, Damage = root.Damage });
		}
	}

	internal static ulong Hash(int key)
	{
		LayerVoxelEngine engine = Engine;
		TestWorldConfig3 world = engine.test;
		Dictionary<XYZ, IntClasse> removed = Points(world.layers.GetLayer<NegativeLayer>().removedSet);
		TerrainSurfaceLayer surface = world.layers.GetLayer<TerrainSurfaceLayer>();
		Dictionary<XYZ, IntClasse> added = Points(surface.addedSet);
		ulong hash = 14695981039346656037UL;
		Origin(key, out int x0, out int z0);
		for (int x = x0; x < x0 + Size; x++)
		{
			for (int z = z0; z < z0 + Size; z++)
			{
				engine.GetColumnData(x, z, out int bottom, out int top, out int type, out int volume);
				hash = Mix(hash, bottom);
				hash = Mix(hash, top);
				hash = Mix(hash, type);
				hash = Mix(hash, volume);
				XYZ column = new XYZ(x, 0, z);
				if (removed.TryGetValue(column, out IntClasse ys) && !ys.Empty)
				{
					hash = Mix(hash, -1);
					for (int i = 0; i < ys.Count; i++)
					{
						hash = Mix(hash, ys[i]);
					}
				}
				if (added.TryGetValue(column, out IntClasse addedYs) && !addedYs.Empty)
				{
					hash = Mix(hash, -2);
					for (int i = 0; i < addedYs.Count; i++)
					{
						hash = Mix(hash, addedYs[i]);
						if (surface.p2resources.TryGetValue(new XYZ(x, addedYs[i], z), out VoxelTerrainResources resources))
						{
							hash = MixResources(hash, resources);
						}
					}
				}
			}
		}
		// The blocks in any order: each one's own hash, summed.
		ulong blocks = 0UL;
		if (BlocksByChunk.TryGetValue(key, out List<Block> list))
		{
			foreach (Block block in list)
			{
				ulong one = Mix(Mix(Mix(Mix(Mix(14695981039346656037UL, block.Position.x), block.Position.y), block.Position.z), block.Rotation), block.Type);
				blocks += one;
			}
		}
		return Mix(hash, (int)blocks) ^ (blocks << 1);
	}

	internal static Snapshot Take(int key)
	{
		LayerVoxelEngine engine = Engine;
		TestWorldConfig3 world = engine.test;
		Dictionary<XYZ, IntClasse> removed = Points(world.layers.GetLayer<NegativeLayer>().removedSet);
		TerrainSurfaceLayer surface = world.layers.GetLayer<TerrainSurfaceLayer>();
		Dictionary<XYZ, IntClasse> added = Points(surface.addedSet);
		Snapshot snapshot = new Snapshot { Key = key };
		Origin(key, out int x0, out int z0);
		int c = 0;
		for (int x = x0; x < x0 + Size; x++)
		{
			for (int z = z0; z < z0 + Size; z++)
			{
				engine.GetColumnData(x, z, out int bottom, out int top, out int type, out int volume);
				snapshot.Columns[c++] = bottom;
				snapshot.Columns[c++] = top;
				snapshot.Columns[c++] = type;
				snapshot.Columns[c++] = volume;
				XYZ column = new XYZ(x, 0, z);
				if (removed.TryGetValue(column, out IntClasse ys))
				{
					for (int i = 0; i < ys.Count; i++)
					{
						snapshot.Removed.Add(new XYZ(x, ys[i], z));
					}
				}
				if (added.TryGetValue(column, out IntClasse addedYs))
				{
					for (int i = 0; i < addedYs.Count; i++)
					{
						XYZ p = new XYZ(x, addedYs[i], z);
						snapshot.Added.Add(p);
						surface.p2resources.TryGetValue(p, out VoxelTerrainResources resources);
						snapshot.AddedResources.Add(resources ?? new VoxelTerrainResources());
					}
				}
			}
		}
		CollectBlocks();
		if (BlocksByChunk.TryGetValue(key, out List<Block> list))
		{
			snapshot.Blocks.AddRange(list);
		}
		return snapshot;
	}

	// Makes the chunk what the snapshot says, with the game's own changes (so the stability of what stands on it,
	// the route graph and the drawing follow); answers how many voxels and blocks were changed. On the host a block
	// is made or removed as the game does it, for every player; on a client only here.
	internal static int Apply(Snapshot truth)
	{
		LastChanged.Clear();
		Snapshot local = Take(truth.Key);
		LayerVoxelEngine engine = Engine;
		TestWorldConfig3 world = engine.test;
		TerrainSurfaceLayer surface = world.layers.GetLayer<TerrainSurfaceLayer>();
		HashSet<XYZ> changed = new HashSet<XYZ>();
		Voxel.Engine.BeginChanges();
		Origin(truth.Key, out int x0, out int z0);
		int c = 0;
		for (int x = x0; x < x0 + Size; x++)
		{
			for (int z = z0; z < z0 + Size; z++, c += 4)
			{
				if (truth.Columns[c] != local.Columns[c] || truth.Columns[c + 1] != local.Columns[c + 1] || truth.Columns[c + 2] != local.Columns[c + 2] || truth.Columns[c + 3] != local.Columns[c + 3])
				{
					engine.SetColumnData(x, z, truth.Columns[c], truth.Columns[c + 1], truth.Columns[c + 2], truth.Columns[c + 3]);
					int from = Math.Min(truth.Columns[c], local.Columns[c]);
					int to = Math.Max(truth.Columns[c + 1], local.Columns[c + 1]);
					for (int y = from; y <= to; y++)
					{
						changed.Add(new XYZ(x, y, z));
					}
				}
			}
		}
		HashSet<XYZ> truthRemoved = new HashSet<XYZ>(truth.Removed);
		HashSet<XYZ> localRemoved = new HashSet<XYZ>(local.Removed);
		foreach (XYZ p in truthRemoved)
		{
			if (!localRemoved.Contains(p) && world.SetVoxelCheck(p, false))
			{
				changed.Add(p);
			}
		}
		foreach (XYZ p in localRemoved)
		{
			if (!truthRemoved.Contains(p) && world.SetVoxelCheck(p, true))
			{
				changed.Add(p);
			}
		}
		Dictionary<XYZ, VoxelTerrainResources> truthAdded = new Dictionary<XYZ, VoxelTerrainResources>();
		for (int i = 0; i < truth.Added.Count; i++)
		{
			truthAdded[truth.Added[i]] = truth.AddedResources[i];
		}
		Dictionary<XYZ, VoxelTerrainResources> localAdded = new Dictionary<XYZ, VoxelTerrainResources>();
		for (int i = 0; i < local.Added.Count; i++)
		{
			localAdded[local.Added[i]] = local.AddedResources[i];
		}
		foreach (KeyValuePair<XYZ, VoxelTerrainResources> pair in truthAdded)
		{
			if (!localAdded.TryGetValue(pair.Key, out VoxelTerrainResources mine) || !SameResources(mine, pair.Value))
			{
				world.SetCustomTerrainResources(pair.Key, pair.Value);
				changed.Add(pair.Key);
			}
		}
		foreach (XYZ p in localAdded.Keys)
		{
			if (!truthAdded.ContainsKey(p))
			{
				surface.addedSet.Remove(p);
				surface.p2resources.Remove(p);
				world.SetDirty(p);
				changed.Add(p);
			}
		}
		Voxel.Engine.ApplyChanges();
		world.DrawDirtyMegaVoxels();
		int blocks = ApplyBlocks(truth, local);
		foreach (XYZ p in changed)
		{
			if (LastChanged.Count < 8)
			{
				LastChanged.Add(p);
			}
			GameSignals.Invoke(GameSignals.VoxelChanged, p);
			Architecte.Changement(p);
		}
		return changed.Count + blocks;
	}

	private static int ApplyBlocks(Snapshot truth, Snapshot local)
	{
		Dictionary<XYZ, Block> truthBlocks = new Dictionary<XYZ, Block>();
		foreach (Block block in truth.Blocks)
		{
			truthBlocks[block.Position] = block;
		}
		Dictionary<XYZ, Block> localBlocks = new Dictionary<XYZ, Block>();
		foreach (Block block in local.Blocks)
		{
			localBlocks[block.Position] = block;
		}
		BlockEngine blockEngine = BrixSingleton<BlockEngine>.Instance;
		int changed = 0;
		foreach (Block block in localBlocks.Values)
		{
			if (truthBlocks.TryGetValue(block.Position, out Block wanted) && wanted.Type == block.Type && wanted.Rotation == block.Rotation)
			{
				continue;
			}
			if (blockEngine.GetBlockData(block.Position)?.RootOrSelf is RootBlockData root && root.BlockInfo.ID == block.Type)
			{
				if (LastChanged.Count < 8)
				{
					LastChanged.Add(block.Position);
				}
				if (UnityEngine.Networking.NetworkServer.active)
				{
					root.DeBlock();
				}
				else
				{
					root.DeBlockClient(block.Type);
				}
				changed++;
			}
		}
		foreach (Block block in truthBlocks.Values)
		{
			if (localBlocks.TryGetValue(block.Position, out Block mine) && mine.Type == block.Type && mine.Rotation == block.Rotation)
			{
				continue;
			}
			BlockInfo info = block.Type.BlockInfoIDToBlockInfo();
			if (info == null || info.Type != BlockType.Block)
			{
				continue;
			}
			if (LastChanged.Count < 8)
			{
				LastChanged.Add(block.Position);
			}
			blockEngine.CreateBlock(block.Position, PreprocessedRotations.DiscreteRotationForIndexes[block.Rotation], info, block.Damage);
			Architecte.Changement(block.Position);
			changed++;
		}
		return changed;
	}

	internal static void Write(BinaryWriter writer, Snapshot snapshot)
	{
		writer.Write(snapshot.Key);
		foreach (int value in snapshot.Columns)
		{
			writer.Write(value);
		}
		writer.Write(snapshot.Removed.Count);
		foreach (XYZ p in snapshot.Removed)
		{
			WriteXyz(writer, p);
		}
		writer.Write(snapshot.Added.Count);
		for (int i = 0; i < snapshot.Added.Count; i++)
		{
			WriteXyz(writer, snapshot.Added[i]);
			VoxelTerrainResources resources = snapshot.AddedResources[i];
			writer.Write(resources.types.Length);
			for (int j = 0; j < resources.types.Length; j++)
			{
				writer.Write(resources.types[j]);
				writer.Write(resources.amounts[j]);
			}
		}
		writer.Write(snapshot.Blocks.Count);
		foreach (Block block in snapshot.Blocks)
		{
			WriteXyz(writer, block.Position);
			writer.Write(block.Rotation);
			writer.Write(block.Type);
			writer.Write(block.Damage);
		}
	}

	internal static Snapshot Read(BinaryReader reader)
	{
		Snapshot snapshot = new Snapshot { Key = reader.ReadInt32() };
		for (int i = 0; i < snapshot.Columns.Length; i++)
		{
			snapshot.Columns[i] = reader.ReadInt32();
		}
		int removed = reader.ReadInt32();
		for (int i = 0; i < removed; i++)
		{
			snapshot.Removed.Add(ReadXyz(reader));
		}
		int added = reader.ReadInt32();
		for (int i = 0; i < added; i++)
		{
			snapshot.Added.Add(ReadXyz(reader));
			VoxelTerrainResources resources = new VoxelTerrainResources();
			int kinds = reader.ReadInt32();
			for (int j = 0; j < kinds; j++)
			{
				int type = reader.ReadInt32();
				resources.Add(type, reader.ReadInt32());
			}
			snapshot.AddedResources.Add(resources);
		}
		int blocks = reader.ReadInt32();
		for (int i = 0; i < blocks; i++)
		{
			snapshot.Blocks.Add(new Block { Position = ReadXyz(reader), Rotation = reader.ReadInt32(), Type = reader.ReadInt32(), Damage = reader.ReadSingle() });
		}
		return snapshot;
	}

	private static void Origin(int key, out int x0, out int z0)
	{
		XYZ start = Engine.test.worldStart;
		x0 = start.x + (short)(key & 0xFFFF) * Size;
		z0 = start.z + (key >> 16) * Size;
	}

	private static int Div(int value)
	{
		return (value >= 0) ? value / Size : (value - Size + 1) / Size;
	}

	private static bool SameResources(VoxelTerrainResources a, VoxelTerrainResources b)
	{
		return MixResources(0UL, a) == MixResources(0UL, b);
	}

	private static ulong MixResources(ulong hash, VoxelTerrainResources resources)
	{
		// Types in any order: each type with its amount, summed.
		ulong sum = 0UL;
		for (int i = 0; i < resources.types.Length; i++)
		{
			sum += Mix(Mix(14695981039346656037UL, resources.types[i]), resources.amounts[i]);
		}
		return Mix(hash, (int)sum) ^ (sum << 3);
	}

	private static ulong Mix(ulong hash, int value)
	{
		for (int i = 0; i < 4; i++)
		{
			hash ^= (byte)(value >> (8 * i));
			hash *= 1099511628211UL;
		}
		return hash;
	}

	private static void WriteXyz(BinaryWriter writer, XYZ p)
	{
		writer.Write(p.x);
		writer.Write(p.y);
		writer.Write(p.z);
	}

	private static XYZ ReadXyz(BinaryReader reader)
	{
		return new XYZ(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
	}
}
#endif
