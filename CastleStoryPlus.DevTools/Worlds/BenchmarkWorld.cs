using System;
using System.Collections;
using System.Collections.Generic;
using Brix.Assets;
using Brix.Engine;
using Brix.Engine.Nature;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Legacy;
using CastleStoryPlus.Core;
using Brix.Transactions;
using CastleStoryPlus.DevTools.Api;
using CastleStoryPlus.DevTools.Building;
using CastleStoryPlus.DevTools.Endpoints;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CastleStoryPlus.DevTools.Worlds;

// Builds the benchmark save: a big, busy base made only of the original game's things (its build menu's blocks and
// structures, its units, resources, recipes, trees and tasks; nothing the mod adds), so it loads in the original game
// too, saved as "CSP Benchmark" so every run starts from the same state (CSP_AUTOLOAD="CSP Benchmark"):
// - a new sandbox world on Big Island (official, large);
// - a platform of Width x Depth near the home crystal, levelled like the world editor's plateau brush, walled round:
//   4 high, merlons, a tower in each corner, a door in each side, lantern wards along the inside;
// - a row of workshops and other structures, a row with every block of the build menu, a field of stockpiles filled
//   with the game's resources, and a keep left as blueprints for the builders;
// - the forest west of the platform (the game's own trees, filled up), a harvest task in it, a quarry east of it;
// - 70 bricktrons (builders, knights, archers), loose resources to store, the workshops on a loop of their recipes;
// - optionally a raid of corruptrons.
// POST /api/benchmark/build starts it, GET /api/benchmark follows it; it ends with the save written.
internal class BenchmarkWorld : MonoBehaviour
{
	internal const string SaveName = "CSP Benchmark";

	private const string MapName = "BigIsland";

	private const int Width = 120;

	private const int Depth = 80;

	private const int WallHeight = 4;

	private const int PerFrame = 100;

	// Most items put in one stockpile (the game's own room decides below that).
	private const int MaxFill = 50;

	// Seconds the world runs after building, so the workers are busy when it is saved.
	private const float SettleSeconds = 30f;

	// The original game's structures in the workshop row (repeats for more of them).
	private static readonly string[] Structures =
	{
		"ChoppingBlock", "ChoppingBlock", "ChoppingBlock", "Workbench", "Workbench", "Forge", "Forge", "Loom", "Loom",
		"Furnace", "Furnace", "Altar", "MachineShop", "Lab", "Toolrack", "Toolrack", "Toolrack", "Catapult",
		"PylonGround", "PylonGround", "KnightStatue", "KnightStatue", "Barrel", "Barrel", "Barrel", "Barrel",
		"SentinelWard", "HealingWard", "CaptureWard"
	};

	// The original game's blocks, once each, in the block row.
	private static readonly string[] Blocks =
	{
		"Stone_Brick", "Stone_DoubleBrick", "Stone_Brick1x3", "Stone_Brick1x4", "Stone_Brick2x2", "Stone_Brick2x4",
		"Stone_Brick1x1x2", "Stone_Staircase", "Stone_StairSingle", "Stone_StairSpiralLeft", "Stone_StairSpiralRight",
		"Stone_StairCornerLeft", "Stone_StairCornerRight", "Stone_StairCornerSingle", "Stone_InvertStairCornerSingle",
		"Stone_Column_Base", "Stone_Column2_Base", "Stone_Column3_Base", "Stone_Column4_Base", "Stone_Column5_Base",
		"Stone_DoubleBrick_RoundR", "Stone_DoubleBrick_RoundL", "Stone_DoubleBrick_RoundLR", "Stone_Brick_Round",
		"Stone_Arch", "Stone_Merlon", "Stone_MerlonCorner", "Stone_MerlonDouble", "Stone_MerlonInverseCorner",
		"Stone_Parapet1", "Stone_Parapet2", "Stone_Parapet3", "Wood_SingleLog", "Wood_SinglePlank", "Wood_Slab1x1",
		"Wood_Slab2x1", "Wood_Slab2x2", "Wood_Slab2x4", "Wood_Slab1x3", "Wood_Slab1x5", "Wood_Slab2x3"
	};

	// The original game's resources the stockpiles are filled with, in turn.
	private static readonly string[] Fills =
	{
		"RawResources/RawStone", "RawResources/RawIron", "RawResources/RawFiber", "RawResources/RawClay",
		"RawResources/RawOrangeCrystal", "RawResources/RawBlueCrystal", "BuildResources/IronIngot",
		"BuildResources/GlassIngot", "BuildResources/Fabric", "BuildResources/Rope", "BuildResources/Cog",
		"Blocks/Wood_SingleLog", "Blocks/Wood_DoublePlank_AA"
	};

	// Recipes of the original game to keep each kind of workshop busy, tried in order.
	private static readonly Dictionary<string, string[]> Recipes = new Dictionary<string, string[]>
	{
		{ "ChoppingBlock", new[] { "Planks" } },
		{ "Furnace", new[] { "IronIngot", "GlassIngot" } },
		{ "Loom", new[] { "FabricPile", "RopeBallot" } },
		{ "Workbench", new[] { "Quiver", "Bow", "Cog" } },
		{ "Forge", new[] { "Helmet", "Sword", "Shield" } },
		{ "Altar", new[] { "LanternWard", "HealingWard" } },
		{ "MachineShop", new[] { "Cog" } }
	};

	// Loose resources dropped by the stockpiles for the workers to store: key, count.
	// (No tuples: the .NET 3.5 build has no ValueTuple.)
	private static readonly KeyValuePair<string, int>[] Loose =
	{
		new KeyValuePair<string, int>("RawResources/RawStone", 300),
		new KeyValuePair<string, int>("RawResources/RawIron", 100),
		new KeyValuePair<string, int>("RawResources/RawFiber", 100),
		new KeyValuePair<string, int>("RawResources/RawClay", 50)
	};

	internal static BenchmarkWorld Instance;

	private readonly List<string> _log = new List<string>();

	private readonly Dictionary<string, int[]> _placed = new Dictionary<string, int[]>();

	private readonly List<Blueprint> _batch = new List<Blueprint>();

	private List<Route> _routes;

	private string _state = "idle";

	private bool _running;

	private XYZ _corner;

	private Project _project;

	private void Awake()
	{
		Instance = this;
	}

	internal object Status()
	{
		return Json.Obj("state", _state, "running", _running, "corner", _running || _corner != default(XYZ) ? PathEndpoints.Voxel(_corner) : null, "log", _log);
	}

	internal object Placed()
	{
		Dictionary<string, object> result = new Dictionary<string, object>();
		foreach (KeyValuePair<string, int[]> pair in _placed)
		{
			result[pair.Key] = Json.Obj("placed", pair.Value[0], "refused", pair.Value[1]);
		}
		return result;
	}

	internal void Begin(int enemies, bool newWorld)
	{
		if (_running)
		{
			throw new ApiException(409, "The benchmark world is being built already (GET /api/benchmark)");
		}
		_log.Clear();
		_placed.Clear();
		_corner = default;
		_running = true;
		StartCoroutine(Run(enemies, newWorld));
	}

	private IEnumerator Run(int enemies, bool newWorld)
	{
		IEnumerator steps = Steps(enemies, newWorld);
		while (true)
		{
			object current;
			try
			{
				if (!steps.MoveNext())
				{
					break;
				}
				current = steps.Current;
			}
			catch (Exception ex)
			{
				Note("Failed: " + ex.Message);
				DevToolsPlugin.Log.LogError("Benchmark world: " + ex);
				_state = "failed";
				_running = false;
				yield break;
			}
			yield return current;
		}
		_state = "done";
		_running = false;
	}

	private IEnumerator Steps(int enemies, bool newWorld)
	{
		if (newWorld)
		{
			_state = "starting a new world";
			Call("POST", "/api/world/new", "map", MapName, "gamemode", "sandbox");
			float until = Time.realtimeSinceStartup + 300f;
			yield return new WaitForSecondsRealtime(1f);
			while (WorldStarter.Instance != null && WorldStarter.Instance.Busy)
			{
				if (Time.realtimeSinceStartup > until)
				{
					throw new Exception("The new world did not load within 5 minutes");
				}
				yield return new WaitForSecondsRealtime(1f);
			}
			Note("World: " + (WorldStarter.Instance != null ? WorldStarter.Instance.LastResult : "?"));
			yield return new WaitForSecondsRealtime(3f);
		}
		XYZ home = WorldEndpoints.HomeVoxel();
		Note("Home crystal at " + home.x + "," + home.y + "," + home.z);
		// A platform this big is flat nowhere: the place nearest a level one is levelled the way the world editor's
		// plateau brush does it (each column's top set to the level, its trees, plants, boulders and ore taken away).
		if (!PickSite(out _corner, out int floor))
		{
			throw new Exception("No place for a " + Width + " x " + Depth + " platform near the home crystal");
		}
		int trees = ClearTrees();
		int moved = Level(floor);
		Note("Platform " + Width + " x " + Depth + " at " + _corner.x + "," + _corner.z + ", levelled at y " + floor + " (" + moved + " columns, " + trees + " trees cleared)");
		yield return new WaitForSecondsRealtime(2f);
		_project = ProjectEndpoints.BuildProject(0, out bool _);

		_state = "raising the floor";
		yield return Floor(floor);
		yield return WaitForBuilds();

		_state = "building the wall";
		yield return Wall();
		yield return WaitForBuilds();

		_state = "building the rows";
		yield return RowsOf();
		yield return WaitForBuilds();

		_state = "filling the stockpiles";
		yield return FillStockpiles();

		_state = "planting the forest";
		Forest();
		yield return null;

		_state = "bricktrons";
		// On the platform (the home crystal can be far off), in the open part north of the stockpile field and the keep.
		string spawn = Vec(_corner.x + Width / 2, _corner.y, _corner.z + 62);
		Call("POST", "/api/cheat/bricktrons", "count", "50", "job", "builder", "at", spawn);
		Call("POST", "/api/cheat/bricktrons", "count", "10", "job", "knight", "at", spawn);
		Call("POST", "/api/cheat/bricktrons", "count", "10", "job", "archer", "at", spawn);
		yield return new WaitForSecondsRealtime(2f);

		_state = "loose resources";
		string near = Vec(_corner.x + 20, _corner.y, _corner.z + 36);
		foreach (KeyValuePair<string, int> loose in Loose)
		{
			for (int left = loose.Value; left > 0; left -= 50)
			{
				TryCall("POST", "/api/cheat/items", "key", loose.Key, "count", Math.Min(50, left).ToString(), "at", near);
				yield return null;
			}
		}

		_state = "tasks";
		Tasks();
		yield return new WaitForSecondsRealtime(2f);

		_state = "workshops";
		WorkshopsBusy();

		if (enemies > 0)
		{
			_state = "enemies";
			TryCall("POST", "/api/cheat/enemies", "count", Math.Min(50, enemies).ToString(), "type", "corruptron");
		}

		_state = "letting it run";
		yield return new WaitForSecondsRealtime(SettleSeconds);

		_state = "saving";
		Save();
	}

	// ---- layout

	// Stone blocks from the ground up to the floor in every lower column of the platform.
	private IEnumerator Floor(int floor)
	{
		Factory.AssetKey block = TestWorldBuilder.FloorBlock();
		if (block == null)
		{
			Note("No 1 x 1 x 1 block for the floor; the pieces stand on the ground as it is");
			yield break;
		}
		int blocks = 0;
		for (int i = 0; i < Width; i++)
		{
			for (int j = 0; j < Depth; j++)
			{
				int x = _corner.x + i;
				int z = _corner.z + j;
				for (int y = WorldEndpoints.GroundY(x, z) + 1; y <= floor; y++)
				{
					BlueprintPlacer.Result result = BlueprintPlacer.Place(block, new XYZ(x, y, z), 0, _project, force: true);
					if (result.Placed)
					{
						_batch.Add(result.Blueprint);
						blocks++;
					}
					if (_batch.Count >= PerFrame)
					{
						Flush();
						yield return null;
					}
				}
			}
		}
		Flush();
		Note("Floor: " + blocks + " blocks");
	}

	// The wall along the platform's edge, a door in the middle of each side, merlons and corner towers.
	private IEnumerator Wall()
	{
		int midX = Width / 2;
		int midZ = Depth / 2;
		for (int y = 0; y < WallHeight; y++)
		{
			for (int i = 0; i < Width; i++)
			{
				if (Mathf.Abs(i - midX) > 1)
				{
					Put("Stone_Brick", 0, i, y, 0);
					Put("Stone_Brick", 0, i, y, Depth - 1);
				}
			}
			for (int j = 1; j < Depth - 1; j++)
			{
				if (Mathf.Abs(j - midZ) > 1)
				{
					Put("Stone_Brick", 0, 0, y, j);
					Put("Stone_Brick", 0, Width - 1, y, j);
				}
			}
			Flush();
			yield return null;
		}
		for (int i = 0; i < Width; i += 2)
		{
			Put("Stone_Merlon", 0, i, WallHeight, 0);
			Put("Stone_Merlon", 0, i, WallHeight, Depth - 1);
		}
		for (int j = 2; j < Depth - 1; j += 2)
		{
			Put("Stone_Merlon", 0, 0, WallHeight, j);
			Put("Stone_Merlon", 0, Width - 1, WallHeight, j);
		}
		Flush();
		yield return null;
		foreach (int ci in new[] { 1, Width - 4 })
		{
			foreach (int cj in new[] { 1, Depth - 4 })
			{
				for (int y = 0; y < WallHeight + 4; y++)
				{
					for (int a = 0; a < 3; a++)
					{
						for (int b = 0; b < 3; b++)
						{
							Put("Stone_Brick", 0, ci + a, y, cj + b);
						}
					}
				}
				Flush();
				yield return null;
			}
		}
		yield return WaitForBuilds();
		Put("Door", 0, midX - 1, 0, 0);
		Put("Door", 0, midX - 1, 0, Depth - 1);
		Put("Door", 90, 0, 0, midZ - 1);
		Put("Door", 90, Width - 1, 0, midZ - 1);
		// Lantern wards along the inside of the wall.
		for (int i = 6; i < Width - 6; i += 6)
		{
			Put("LanternWard", 0, i, 0, 2);
			Put("LanternWard", 0, i, 0, Depth - 3);
		}
		for (int j = 6; j < Depth - 6; j += 6)
		{
			Put("LanternWard", 0, 2, 0, j);
			Put("LanternWard", 0, Width - 3, 0, j);
		}
		Flush();
	}

	// The workshop row, the block row, the stockpile field and the keep (blueprints only).
	private IEnumerator RowsOf()
	{
		int x = 5;
		int z = 5;
		int deepest = 0;
		foreach (string name in Structures)
		{
			XYZ size = PutPiece(name, ref x, z, Width - 5, 3);
			deepest = Mathf.Max(deepest, size.z);
			if (x >= Width - 5)
			{
				x = 5;
				z += deepest + 3;
				deepest = 0;
			}
		}
		Flush();
		yield return null;
		x = 5;
		z += deepest + 4;
		deepest = 0;
		foreach (string name in Blocks)
		{
			XYZ size = PutPiece(name, ref x, z, Width - 5, 2);
			deepest = Mathf.Max(deepest, size.z);
			if (x >= Width - 5)
			{
				x = 5;
				z += deepest + 2;
				deepest = 0;
			}
		}
		Flush();
		yield return null;
		// Stockpiles: 12 x 4 at 3 apart.
		int field = z + deepest + 4;
		for (int row = 0; row < 4; row++)
		{
			for (int col = 0; col < 12; col++)
			{
				Put("Pallet", 0, 5 + col * 3, 0, field + row * 3);
			}
		}
		Flush();
		yield return null;
		// The keep: a hollow 12 x 12 stone tower, 8 high, as blueprints for the builders.
		int kx = Width - 30;
		int kz = field;
		for (int y = 0; y < 8; y++)
		{
			for (int a = 0; a < 12; a++)
			{
				for (int b = 0; b < 12; b++)
				{
					if (a == 0 || b == 0 || a == 11 || b == 11)
					{
						PlaceBlueprint(Key("Stone_Brick"), new XYZ(_corner.x + kx + a, _corner.y + y, _corner.z + kz + b), 0, true);
					}
				}
			}
		}
		ProjectEndpoints.Commit(_project);
		Note("Keep: 12 x 12 x 8 as blueprints at " + (_corner.x + kx) + "," + (_corner.z + kz));
	}

	// The stockpiles' resources: each in turn, up to MaxFill or the stockpile's room.
	// The build menu's Pallet is the factory's "Palette".
	private static bool IsPallet(Recepteur storage)
	{
		FactoryImprint imprint = storage.GetComponent<FactoryImprint>();
		return imprint != null && imprint.AssetKey != null && imprint.AssetKey.Name == "Palette";
	}

	private IEnumerator FillStockpiles()
	{
		Faction faction = User.LocalUser.faction;
		List<Factory.AssetKey> fills = new List<Factory.AssetKey>();
		foreach (string text in Fills)
		{
			Factory.AssetKey key = ItemKey(text);
			if (Factory.Exist(key))
			{
				fills.Add(key);
			}
		}
		int stored = 0;
		int index = 0;
		foreach (Recepteur storage in Object.FindObjectsOfType<Recepteur>())
		{
			if (storage == null || storage.GetComponentInParent<Blueprint>() != null || !IsPallet(storage) || fills.Count == 0)
			{
				continue;
			}
			Factory.AssetKey fill = fills[index++ % fills.Count];
			for (int n = 0; n < MaxFill && storage.HasRoomFor(fill); n++)
			{
				GameObject item = Transactor.SpawnUNet(fill, faction, storage.transform.position + Vector3.up, Quaternion.identity);
				if (item == null)
				{
					break;
				}
				storage.AddObject(item);
				stored++;
				if (stored % 60 == 0)
				{
					yield return null;
				}
			}
		}
		Note("Stockpiles: " + index + " filled, " + stored + " items");
	}

	// A forest of the map's own trees in a band west of the platform, 3 apart, on bare ground.
	// Over land all the way, clear of the crystal, and with the least ground to cut or fill to the mean height.
	private bool PickSite(out XYZ corner, out int floor)
	{
		XYZ home = WorldEndpoints.HomeVoxel();
		const int Reach = 110;
		const int Step = 4;
		int baseX = home.x - Reach - Width;
		int baseZ = home.z - Reach - Depth;
		int[,] heights = new int[2 * Reach + 2 * Width + 1, 2 * Reach + 2 * Depth + 1];
		for (int i = 0; i < heights.GetLength(0); i++)
		{
			for (int j = 0; j < heights.GetLength(1); j++)
			{
				heights[i, j] = WorldEndpoints.GroundY(baseX + i, baseZ + j);
			}
		}
		long best = long.MaxValue;
		corner = home;
		floor = home.y;
		for (int dx = -Reach - Width; dx <= Reach; dx += Step)
		{
			for (int dz = -Reach - Depth; dz <= Reach; dz += Step)
			{
				int x0 = home.x + dx;
				int z0 = home.z + dz;
				if (home.x >= x0 - 8 && home.x <= x0 + Width + 8 && home.z >= z0 - 8 && home.z <= z0 + Depth + 8)
				{
					continue;
				}
				long sum = 0;
				bool missing = false;
				for (int i = 0; i < Width && !missing; i++)
				{
					for (int j = 0; j < Depth; j++)
					{
						int h = heights[x0 - baseX + i, z0 - baseZ + j];
						if (h == int.MinValue)
						{
							missing = true;
							break;
						}
						sum += h;
					}
				}
				if (missing)
				{
					continue;
				}
				int level = (int)Math.Round((double)sum / (Width * Depth));
				long cost = 0;
				for (int i = 0; i < Width; i++)
				{
					for (int j = 0; j < Depth; j++)
					{
						cost += Math.Abs(heights[x0 - baseX + i, z0 - baseZ + j] - level);
					}
				}
				long score = cost * 4 + Math.Abs(dx + Width / 2) + Math.Abs(dz + Depth / 2);
				if (score < best)
				{
					best = score;
					corner = new XYZ(x0, level + 1, z0);
					floor = level;
				}
			}
		}
		return best != long.MaxValue;
	}

	// Sets the top of every column on the platform to the level; returns the columns changed.
	private int Level(int level)
	{
		int moved = 0;
		VoxelEngine.BeginChanges();
		try
		{
			for (int i = 0; i < Width; i++)
			{
				for (int j = 0; j < Depth; j++)
				{
					int x = _corner.x + i;
					int z = _corner.z + j;
					int ground = WorldEndpoints.GroundY(x, z);
					XYZ crust = new XYZ(x, ground, z);
					NatureRegistry.RemoveNatureAt(crust);
					BoulderRegistry.Remove(crust);
					IronRegistry.Remove(crust);
					PyriteRegistry.Remove(crust);
					if (ground != level)
					{
						VoxelEngine.SetColumnTopVoxel(x, z, VoxelEngine.GetColumnTop(x, z) + level - ground);
						moved++;
					}
					VoxelEngine.SetColumnCrustVolume(x, z, 1f);
					VoxelEngine.SetColumnType(x, z, VoxelTerrainType.Grass);
				}
			}
		}
		finally
		{
			VoxelEngine.ApplyChanges();
		}
		GameSignals.Invoke(GameSignals.OnTerrainEdited);
		return moved;
	}

	// The trees standing on the platform (and a cell round it).
	private int ClearTrees()
	{
		List<XYZ> trees = WorldEndpoints.TreesOver(_corner.x - 1, _corner.z - 1, _corner.x + Width + 1, _corner.z + Depth + 1);
		int cleared = 0;
		foreach (XYZ tree in trees)
		{
			if (TreeRegistry.Instance.Remove(tree))
			{
				cleared++;
			}
		}
		return cleared;
	}

	private void Forest()
	{
		TreeRegistry registry = TreeRegistry.Instance;
		if (registry == null)
		{
			Note("No tree registry; no forest");
			return;
		}
		int planted = 0;
		for (int i = 0; i < 8; i++)
		{
			for (int j = 0; j < Depth; j += 3)
			{
				int x = _corner.x - 6 - i * 3;
				int z = _corner.z + j + ((i % 2 == 0) ? 0 : 1);
				int ground = WorldEndpoints.GroundY(x, z);
				if (ground == int.MinValue)
				{
					continue;
				}
				XYZ cell = new XYZ(x, ground + 1, z);
				if (!Voxel.IsEmpty(cell) || Voxel.IsEmpty(cell - XYZ.up) || registry.HasTree(cell))
				{
					continue;
				}
				int info = (TreeRegistry.GetPseudoRandomArbreInfo(cell.ToScalaire()) & 0xF) | (15 << 4);
				if (registry.Add(cell, info))
				{
					planted++;
				}
			}
		}
		Architecte.AppliquerChangements();
		int standing = WorldEndpoints.TreesOver(_corner.x - 30, _corner.z, _corner.x - 1, _corner.z + Depth).Count;
		Note("Forest: " + planted + " trees planted, " + standing + " standing west of the platform");
	}

	// A harvest task in the forest, a quarry east of the platform, the keep's builders; 8 workers each.
	private void Tasks()
	{
		List<int> projects = new List<int>();
		int harvest = ProjectId(TryCall("POST", "/api/projects/create", "type", "harvest", "at", Vec(_corner.x - 16, _corner.y, _corner.z + Depth / 2), "what", "tree"));
		projects.Add(harvest);
		int qx = _corner.x + Width + 6;
		int qz = _corner.z + 10;
		int qy = WorldEndpoints.GroundY(qx + 4, qz + 4);
		int quarry = 0;
		if (qy != int.MinValue)
		{
			quarry = ProjectId(TryCall("POST", "/api/projects/create", "type", "quarry", "from", Vec(qx, qy - 5, qz), "to", Vec(qx + 8, qy, qz + 8), "clear", "true"));
			projects.Add(quarry);
		}
		int keep = (_project != null) ? _project.gameObject.InstanceId() : 0;
		projects.Add(keep);
		foreach (int project in projects)
		{
			if (project != 0)
			{
				TryCall("POST", "/api/projects/hire", "id", project.ToString(), "count", "8");
			}
		}
		Note("Tasks: harvest " + harvest + ", quarry " + quarry + ", keep " + keep);
	}

	// Each workshop gets a worker and a recipe of the original game on a loop.
	private void WorkshopsBusy()
	{
		if (!(Call("GET", "/api/workshops") is Dictionary<string, object> answer) || !(answer["workshops"] is List<object> list))
		{
			return;
		}
		int busy = 0;
		foreach (object entry in list)
		{
			if (!(entry is Dictionary<string, object> workshop))
			{
				continue;
			}
			string id = workshop["id"].ToString();
			// "Furnace (1)": the workshop's key and its number.
			string type = workshop["type"].ToString();
			int number = type.IndexOf(" (", StringComparison.Ordinal);
			if (number > 0)
			{
				type = type.Substring(0, number);
			}
			TryCall("POST", "/api/workshops/worker", "id", id);
			if (!Recipes.TryGetValue(type, out string[] recipes))
			{
				continue;
			}
			foreach (string recipe in recipes)
			{
				if (TryCall("POST", "/api/workshops/queue", "id", id, "recipe", recipe, "count", "5", "loop", "true") != null)
				{
					busy++;
					break;
				}
			}
		}
		Note("Workshops working: " + busy + " of " + list.Count);
	}

	// Writes the running game as the "CSP Benchmark" save, over an older one of that name.
	private void Save()
	{
		Asset_Map loaded = GameParam.Map;
		Asset_Map save = null;
		foreach (Asset_Map existing in AssetDispensary.saves.SendSaveQuery(Asset_Map.SaveType.Manualsave))
		{
			if (existing != null && !existing.IsShell() && existing.customName == SaveName)
			{
				save = existing;
				break;
			}
		}
		if (save != null)
		{
			save.FillFrom(loaded, includeMapType: false);
			save.customName = SaveName;
			save.saveType = Asset_Map.SaveType.Manualsave;
			save.Disk_Overwrite();
		}
		else
		{
			save = Asset_Map.CreateShellFrom(loaded, Asset_Map.MapType.Save);
			save.customName = SaveName;
			save.saveType = Asset_Map.SaveType.Manualsave;
			save.Disk_WriteAsNew(AssetAuthorType.Official);
		}
		Note("Saved as \"" + SaveName + "\"");
	}

	// ---- placing

	// A piece at x, z of the platform (its footprint's lowest corner there), x moved on past it and the gap.
	private XYZ PutPiece(string name, ref int x, int z, int limit, int gap)
	{
		Factory.AssetKey key = Key(name);
		XYZ probe = _corner + new XYZ(0, 60, 0);
		BlueprintPlacer.Fit fit = null;
		try
		{
			fit = BlueprintPlacer.Try(key, probe, 0);
		}
		catch (Exception)
		{
		}
		XYZ size = (fit != null && fit.HasCells) ? fit.Size : new XYZ(1, 1, 1);
		XYZ offset = (fit != null && fit.HasCells) ? new XYZ(fit.Min.x - probe.x, fit.Min.y - probe.y, fit.Min.z - probe.z) : new XYZ(0, 0, 0);
		if (x + size.x > limit)
		{
			x = limit;
			return size;
		}
		PlaceBlueprint(key, new XYZ(_corner.x + x - offset.x, _corner.y - offset.y, _corner.z + z - offset.z), 0, false);
		x += size.x + gap;
		return size;
	}

	private void Put(string name, int turn, int x, int y, int z)
	{
		PlaceBlueprint(Key(name), new XYZ(_corner.x + x, _corner.y + y, _corner.z + z), turn / 90, false);
	}

	// Places a blueprint; built at once with the next Flush unless left for the builders.
	private void PlaceBlueprint(Factory.AssetKey key, XYZ at, int turns, bool leaveForBuilders)
	{
		BlueprintPlacer.Result result = null;
		try
		{
			result = BlueprintPlacer.Place(key, at, turns, _project, force: leaveForBuilders);
		}
		catch (Exception ex)
		{
			Note(key.Name + " at " + at.x + "," + at.y + "," + at.z + " failed (" + ex.GetType().Name + ")");
		}
		if (!_placed.TryGetValue(key.Name, out int[] counts))
		{
			counts = new int[2];
			_placed[key.Name] = counts;
		}
		if (result != null && result.Placed)
		{
			counts[0]++;
			if (!leaveForBuilders && result.Blueprint != null)
			{
				_batch.Add(result.Blueprint);
			}
		}
		else
		{
			counts[1]++;
		}
	}

	private void Flush()
	{
		if (_batch.Count > 0)
		{
			InstantBuild.Complete(_batch);
			_batch.Clear();
		}
	}

	private static IEnumerator WaitForBuilds()
	{
		float until = Time.unscaledTime + 20f;
		yield return null;
		while (InstantBuild.Queued > 0 && Time.unscaledTime < until)
		{
			yield return null;
		}
	}

	private static Factory.AssetKey Key(string name)
	{
		return new Factory.AssetKey("Blueprints", name);
	}

	private static Factory.AssetKey ItemKey(string text)
	{
		int slash = text.IndexOf('/');
		return new Factory.AssetKey(text.Substring(0, slash), text.Substring(slash + 1));
	}

	private static int ProjectId(object answer)
	{
		if (answer is Dictionary<string, object> result && result.TryGetValue("project", out object project) && project is Dictionary<string, object> described)
		{
			return Convert.ToInt32(described["id"]);
		}
		return 0;
	}

	private static string Vec(int x, int y, int z)
	{
		return x + "," + y + "," + z;
	}

	private object TryCall(string method, string path, params string[] parameters)
	{
		try
		{
			return Call(method, path, parameters);
		}
		catch (ApiException ex)
		{
			Note(method + " " + path + " " + string.Join(" ", parameters) + ": " + ex.Message);
			return null;
		}
	}

	// Runs an endpoint's handler directly, as the HTTP server would on the main thread.
	private object Call(string method, string path, params string[] parameters)
	{
		_routes = _routes ?? Routes.Build();
		Route route = _routes.Find((Route r) => r.Method == method && r.Path == path);
		if (route == null)
		{
			throw new Exception("No endpoint " + method + " " + path);
		}
		ApiRequest request = new ApiRequest { Method = method, Path = path };
		for (int i = 0; i + 1 < parameters.Length; i += 2)
		{
			request.Query[parameters[i]] = parameters[i + 1];
		}
		return route.Handler(request);
	}

	private void Note(string text)
	{
		_log.Add(DateTime.Now.ToString("HH:mm:ss") + " " + text);
		DevToolsPlugin.Log.LogInfo("Benchmark world: " + text);
	}
}
