using System;
using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Transactions;
using CastleStoryPlus.DevTools.Building;
using CastleStoryPlus.DevTools.Endpoints;
using CastleStoryPlus.GameUi;
using CastleStoryPlus.Upgrades;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CastleStoryPlus.DevTools.Worlds;

// A test world for trying the mod's menus and buildings: on the running game (a sandbox is meant), one of every
// building and block of the build menu, laid out in rows near the home crystal on a flat stone floor:
// - the structures by the build menu's groups (storage, production, crafting, structures, wards, decorations), one
//   row per group in the menu's order;
// - the blocks by the menu's groups (standard, plugs, round, crenelations, wood), each row from small to big;
// - every research of the team done (all lines at their top tier);
// - a stockpile of each size (2 x 2, 3 x 3) for every resource and every Stacking tier (none, then 1 to the top; the
//   stockpile's own tier, StockpileTiers), filled up with it;
// - standing trades on the market (rope and iron), so its window has cards;
// - an armour stand (tool rack) for every soldier class, full of its kits, and one with a kit of every class.
// The site is the flattest place without trees near the home crystal; the floor is raised to its highest column
// with stone blocks. Everything is built at once (InstantBuild) and the stockpiles filled directly, over a few
// frames: POST starts it, GET follows it. Save the game afterwards to keep it.
internal partial class TestWorldBuilder : MonoBehaviour
{
	private sealed class Piece
	{
		public Factory.AssetKey Key;

		// Where its footprint's lowest corner goes, relative to the site's corner on the floor.
		public int X;

		public int Z;

		public XYZ Size;

		public XYZ Offset;

		public string Section;

		// A stockpile's resource to fill it with, else null.
		public Factory.AssetKey Fill;

		// The stockpile's own Stacking tier (StockpileTiers).
		public int StackingTier = -1;

		// An armour stand's kits: put on in turn (one of each, again and again) while it has room, else null.
		public List<List<Factory.AssetKey>> Kits;

		public Blueprint Blueprint;
	}

	private const int Gap = 2;

	private const int SectionGap = 4;

	private const int Margin = 3;

	// Depth of the free band across the site for the home crystal.
	private const int CrystalBand = 22;

	private const int MaxFill = 2000;

	private const int PlacePerFrame = 200;

	private const int SpawnPerFrame = 60;

	private static readonly string[] StructureGroupNames = { "", "Storage", "Production", "Crafting", "Structures", "Wards", "Decorations" };

	private static readonly string[] BlockGroupNames = { "", "Standard blocks", "Plugs", "Round blocks", "Crenelations", "Wood" };

	private static readonly HashSet<string> Skip = new HashSet<string> { "RopeBridgePlacement", "RopeBridgeFrame", "RopeBridgePlank" };

	internal static TestWorldBuilder Instance;

	// What it is doing now and what it did, for GET /api/testworld.
	private string _state = "idle";

	private readonly List<string> _log = new List<string>();

	private readonly Dictionary<string, int> _outcomes = new Dictionary<string, int>();

	private XYZ _corner;

	private int _width;

	private int _depth;

	private int _floor;

	private int _floorBlocks;

	private int _filled;

	private bool _running;

	private List<Piece> _pieces = new List<Piece>();

	private void Awake()
	{
		Instance = this;
	}

	internal object Status()
	{
		return Json.Obj(
			"state", _state,
			"running", _running,
			"corner", (_width > 0) ? PathEndpoints.Voxel(_corner) : null,
			"size", (_width > 0) ? new int[2] { _width, _depth } : null,
			"floorY", _floor,
			"floorBlocks", _floorBlocks,
			"outcomes", _outcomes,
			"itemsStored", _filled,
			"log", _log);
	}

	internal void Begin(int width)
	{
		if (_running)
		{
			throw new Api.ApiException(409, "The test world is being built already (GET /api/testworld)");
		}
		_log.Clear();
		_outcomes.Clear();
		_filled = 0;
		_floorBlocks = 0;
		_width = 0;
		List<Piece> pieces = Plan(width, out int depth, out int band);
		_width = width;
		_depth = depth;
		// Round the home crystal (buildings and blocks on one side, stockpiles and stands on the other), else anywhere
		// near it.
		if (!AroundCrystal(width, depth, band, out _corner, out _floor) && !FindSite(width, depth, out _corner, out _floor))
		{
			throw new Api.ApiException(409, "No place without trees for a " + width + " x " + depth + " test world near the home crystal");
		}
		_pieces = pieces;
		Note("Site " + width + " x " + depth + " at " + _corner.x + "," + _corner.z + ", floor at y " + _floor + "; " + pieces.Count + " pieces");
		_running = true;
		StartCoroutine(Build(pieces));
	}

	internal object PieceList()
	{
		List<object> list = new List<object>();
		Renderer[] renderers = (_width > 0) ? Object.FindObjectsOfType<Renderer>() : new Renderer[0];
		if (_width > 0)
		{
			foreach (Piece piece in _pieces)
			{
				Bounds? drawn = DrawnBounds(renderers, piece);
				List<string> kits = null;
				if (piece.Kits != null)
				{
					kits = new List<string>();
					foreach (List<Factory.AssetKey> kit in piece.Kits)
					{
						List<string> names = new List<string>();
						foreach (Factory.AssetKey key in kit)
						{
							names.Add(key.Name);
						}
						kits.Add(string.Join("+", names.ToArray()));
					}
				}
				list.Add(Json.Obj(
					"name", piece.Key.Name,
					"section", piece.Section,
					"centre", new float[3] { _corner.x + piece.X + piece.Size.x / 2f, _corner.y, _corner.z + piece.Z + piece.Size.z / 2f },
					"size", new int[3] { piece.Size.x, piece.Size.y, piece.Size.z },
					"fill", (piece.Fill != null) ? piece.Fill.Name : null,
					"stackingTier", (piece.StackingTier >= 0) ? (int?)piece.StackingTier : null,
					"kits", kits,
					"bounds", drawn.HasValue ? Json.Obj("centre", new float[3] { drawn.Value.center.x, drawn.Value.center.y, drawn.Value.center.z }, "size", new float[3] { drawn.Value.size.x, drawn.Value.size.y, drawn.Value.size.z }) : null));
			}
		}
		return Json.Obj("count", list.Count, "pieces", list);
	}

	// What is drawn of the piece: the renderers standing in its footprint above the floor (not the terrain, the floor
	// or anything bigger than the piece could be), together.
	private Bounds? DrawnBounds(Renderer[] renderers, Piece piece)
	{
		float x0 = _corner.x + piece.X;
		float z0 = _corner.z + piece.Z;
		float largest = Mathf.Max(piece.Size.x, piece.Size.z, piece.Size.y) * 2f + 4f;
		Bounds? all = null;
		foreach (Renderer renderer in renderers)
		{
			if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer is ParticleSystemRenderer)
			{
				continue;
			}
			Bounds bounds = renderer.bounds;
			Vector3 c = bounds.center;
			if (c.x < x0 - 0.25f || c.x > x0 + piece.Size.x + 0.25f || c.z < z0 - 0.25f || c.z > z0 + piece.Size.z + 0.25f || c.y < _corner.y - 0.5f || Mathf.Max(bounds.size.x, bounds.size.z) > largest)
			{
				continue;
			}
			if (all.HasValue)
			{
				Bounds grown = all.Value;
				grown.Encapsulate(bounds);
				all = grown;
			}
			else
			{
				all = bounds;
			}
		}
		return all;
	}

	private void Note(string text)
	{
		_log.Add(text);
		DevToolsPlugin.Log.LogInfo("TestWorld: " + text);
	}

	private void Count(string outcome)
	{
		_outcomes.TryGetValue(outcome, out int n);
		_outcomes[outcome] = n + 1;
	}

	// ---- plan

	// The rows, left to right and wrapped at the width; returns the depth used and where the free band for the home
	// crystal starts (between the blocks and the stockpiles).
	private static List<Piece> Plan(int width, out int depth, out int band)
	{
		List<Piece> pieces = new List<Piece>();
		int z = 0;
		for (int group = 1; group < StructureGroupNames.Length; group++)
		{
			List<Piece> row = new List<Piece>();
			foreach (BuildCatalog.Entry entry in BuildCatalog.Structures)
			{
				if (entry.Group == group)
				{
					AddPiece(row, entry.Key, StructureGroupNames[group]);
				}
			}
			z = Row(pieces, row, width, z, Gap);
		}
		for (int group = 1; group < BlockGroupNames.Length; group++)
		{
			List<Piece> row = new List<Piece>();
			foreach (BuildCatalog.Entry entry in BuildCatalog.Blocks)
			{
				if (entry.Group == group)
				{
					AddPiece(row, entry.Key, BlockGroupNames[group]);
				}
			}
			// From small to big; the menu's order among the same size.
			List<Piece> sorted = new List<Piece>(row);
			sorted.Sort((Piece a, Piece b) =>
			{
				int bySize = Volume(a).CompareTo(Volume(b));
				return (bySize != 0) ? bySize : row.IndexOf(a).CompareTo(row.IndexOf(b));
			});
			z = Row(pieces, sorted, width, z, Gap);
		}
		// The band left free for the home crystal and the bricktrons round it.
		band = z - SectionGap + Margin;
		z += CrystalBand;
		// A stockpile of each size for every resource and every Stacking tier, filled with it: the game's 2 x 2, then
		// the mod's 3 x 3, each from no Stacking up to the top tier.
		List<Factory.AssetKey> resources = Resources();
#if !ESSENTIALS
		foreach (string kind in new[] { "Pallet", CastleStoryPlus.Building.LargeStockpile.Name })
		{
			for (int tier = 0; tier <= UpgradeLines.All[UpgradeLines.Stacking].Tiers; tier++)
#else
		// Essentials: the game's stockpile only, and no Stacking tiers (Upgrades and LargeStockpile are Full edition features).
		foreach (string kind in new[] { "Pallet" })
		{
			for (int tier = 0; tier <= 0; tier++)
#endif
			{
				List<Piece> stockpiles = new List<Piece>();
				foreach (Factory.AssetKey resource in resources)
				{
					int before = stockpiles.Count;
					AddPiece(stockpiles, new Factory.AssetKey("Blueprints", kind), "Stockpiles, Stacking " + tier);
					if (stockpiles.Count > before)
					{
						stockpiles[stockpiles.Count - 1].Fill = resource;
						stockpiles[stockpiles.Count - 1].StackingTier = tier;
					}
				}
				z = Row(pieces, stockpiles, width, z, 1);
			}
		}
		// An armour stand per soldier class with its kits, then one with every class's kit.
		List<Piece> stands = new List<Piece>();
		List<List<Factory.AssetKey>> all = new List<List<Factory.AssetKey>>();
		foreach (Occupation.Job job in StandJobs)
		{
			List<Factory.AssetKey> kit = KitKeys(job);
			if (kit.Count == 0)
			{
				continue;
			}
			all.Add(kit);
			int before = stands.Count;
			AddPiece(stands, new Factory.AssetKey("Blueprints", "Toolrack"), "Armour stands");
			if (stands.Count > before)
			{
				stands[stands.Count - 1].Kits = new List<List<Factory.AssetKey>> { kit };
			}
		}
		if (all.Count > 0)
		{
			int before = stands.Count;
			AddPiece(stands, new Factory.AssetKey("Blueprints", "Toolrack"), "Armour stands");
			if (stands.Count > before)
			{
				stands[stands.Count - 1].Kits = all;
			}
		}
		z = Row(pieces, stands, width, z, Gap);
		depth = Mathf.Max(1, z - SectionGap) + 2 * Margin;
		foreach (Piece piece in pieces)
		{
			piece.X += Margin;
			piece.Z += Margin;
		}
		return pieces;
	}

	private static readonly Occupation.Job[] StandJobs =
	{
		Occupation.Job.Knight,
		Occupation.Job.Archer,
		Occupation.Job.Arbalist,
		Occupation.Job.Halberdier,
		Occupation.Job.Alchemist,
		Occupation.Job.Artificer
	};

	// A soldier class's kit as the items' keys (weapon, prop, hat: what the class has of them).
	private static List<Factory.AssetKey> KitKeys(Occupation.Job job)
	{
		List<Factory.AssetKey> keys = new List<Factory.AssetKey>();
		List<Ressource> kit = CastleStoryPlus.Army.Kits.KitFor(job);
		if (kit == null)
		{
			return keys;
		}
		foreach (Ressource item in kit)
		{
			Factory.AssetKey key = Description.RepresentativeOf(item);
			if (key != null && Factory.Exist(key))
			{
				keys.Add(key);
			}
		}
		return keys;
	}

	private static int Volume(Piece piece)
	{
		return piece.Size.x * piece.Size.y * piece.Size.z;
	}

	private static void AddPiece(List<Piece> row, Factory.AssetKey key, string section)
	{
		if (key == null || Skip.Contains(key.Name) || !Factory.Exist(key))
		{
			return;
		}
		XYZ at = WorldEndpoints.HomeVoxel() + new XYZ(0, 60, 0);
		BlueprintPlacer.Fit fit = null;
		try
		{
			fit = BlueprintPlacer.Try(key, at, 0);
		}
		catch (Exception ex)
		{
			// Ramps fit to what is under the mouse and cannot be measured in the air: one voxel for them.
			DevToolsPlugin.Log.LogWarning("TestWorld: " + key.Name + " could not be measured (" + ex.GetType().Name + "), taken as 1 x 1 x 1");
			row.Add(new Piece { Key = key, Size = new XYZ(1, 1, 1), Offset = new XYZ(0, 0, 0), Section = section });
			return;
		}
		if (fit == null || !fit.HasCells)
		{
			DevToolsPlugin.Log.LogWarning("TestWorld: no footprint for " + key.Name + ", left out");
			return;
		}
		row.Add(new Piece { Key = key, Size = fit.Size, Offset = new XYZ(fit.Min.x - at.x, fit.Min.y - at.y, fit.Min.z - at.z), Section = section });
	}

	// Lays a row out from z on, wrapping at the width; answers the z after it.
	private static int Row(List<Piece> pieces, List<Piece> row, int width, int z, int gap)
	{
		if (row.Count == 0)
		{
			return z;
		}
		int x = 0;
		int deepest = 0;
		foreach (Piece piece in row)
		{
			if (x > 0 && x + piece.Size.x > width - 2 * Margin)
			{
				x = 0;
				z += deepest + gap;
				deepest = 0;
			}
			piece.X = x;
			piece.Z = z;
			x += piece.Size.x + gap;
			deepest = Mathf.Max(deepest, piece.Size.z);
			pieces.Add(piece);
		}
		return z + deepest + SectionGap;
	}

	// Every resource a stockpile can hold, once each: the game's raw and build resources and the mod's own.
	private static List<Factory.AssetKey> Resources()
	{
		List<Factory.AssetKey> keys = new List<Factory.AssetKey>();
		HashSet<string> seen = new HashSet<string>();
		foreach (Type type in new[] { typeof(RawResources), typeof(BuildResources) })
		{
			foreach (System.Reflection.FieldInfo field in type.GetFields())
			{
				if (field.IsStatic && field.FieldType == typeof(Factory.AssetKey))
				{
					AddResource(keys, seen, (Factory.AssetKey)field.GetValue(null));
				}
			}
		}
#if !ESSENTIALS
		foreach (Market.MarketPrices.Good good in Market.MarketPrices.Goods)
		{
			AddResource(keys, seen, good.Asset);
		}
		AddResource(keys, seen, Economy.DarkCrystals.Key);
#endif
		return keys;
	}

	// Once per resource the item is (several keys make the same one: steel, stone), and only what a stockpile takes.
	private static void AddResource(List<Factory.AssetKey> keys, HashSet<string> seen, Factory.AssetKey key)
	{
		// The pumpkin and the home crystal are build resources a stockpile never takes, though its template says so.
		if (key == null || !Factory.Exist(key) || key.Name == "Pumpkin" || key.Name == "Crystal")
		{
			return;
		}
		GameObject template = Factory.Peek(key);
		Ressource resource = (template != null) ? Apparence.PrincipaleOf(template) : null;
		if (resource == null || !seen.Add(resource.GetType().Name))
		{
			return;
		}
		GameObject pallet = Factory.Peek(ObjetsDynamiques.Palette);
		Recepteur storage = (pallet != null) ? pallet.GetComponent<Recepteur>() : null;
		if (storage != null && !storage.HasBaseRoomFor(key))
		{
			return;
		}
		keys.Add(key);
	}

	// ---- site

	// The place near the home crystal (not on it) without trees that needs the fewest floor blocks.
	// The site centred on the home crystal, the crystal in the middle of the free band: when it is flat enough
	// (a few blocks of floor at most) and without trees.
	private static bool AroundCrystal(int width, int depth, int band, out XYZ corner, out int floor)
	{
		XYZ home = WorldEndpoints.HomeVoxel();
		int x0 = home.x - width / 2;
		int z0 = home.z - band - (CrystalBand - SectionGap) / 2;
		corner = home;
		floor = home.y;
		if (WorldEndpoints.TreesOver(x0 - 1, z0 - 1, x0 + width + 1, z0 + depth + 1).Count > 0)
		{
			return false;
		}
		int top = int.MinValue;
		int bottom = int.MaxValue;
		for (int i = 0; i < width; i++)
		{
			for (int j = 0; j < depth; j++)
			{
				int h = WorldEndpoints.GroundY(x0 + i, z0 + j);
				if (h == int.MinValue)
				{
					return false;
				}
				top = Mathf.Max(top, h);
				bottom = Mathf.Min(bottom, h);
			}
		}
		if (top - bottom > 6)
		{
			return false;
		}
		corner = new XYZ(x0, top + 1, z0);
		floor = top;
		return true;
	}

	internal static bool FindSite(int width, int depth, out XYZ corner, out int floor, int maxRise = 6, bool overTrees = false)
	{
		XYZ home = WorldEndpoints.HomeVoxel();
		const int Reach = 110;
		const int Step = 6;
		int size = 2 * Reach + Mathf.Max(width, depth) + 1;
		int baseX = home.x - Reach - width;
		int baseZ = home.z - Reach - depth;
		int[,] heights = new int[size + width, size + depth];
		for (int i = 0; i < heights.GetLength(0); i++)
		{
			for (int j = 0; j < heights.GetLength(1); j++)
			{
				heights[i, j] = WorldEndpoints.GroundY(baseX + i, baseZ + j);
			}
		}
		List<XYZ> trees = WorldEndpoints.TreesOver(baseX, baseZ, baseX + heights.GetLength(0), baseZ + heights.GetLength(1));
		long best = long.MaxValue;
		corner = home;
		floor = home.y;
		for (int dx = -Reach - width; dx <= Reach; dx += Step)
		{
			for (int dz = -Reach - depth; dz <= Reach; dz += Step)
			{
				int x0 = home.x + dx;
				int z0 = home.z + dz;
				// Clear of the crystal and the bricktrons' spawn round it.
				if (home.x >= x0 - 8 && home.x <= x0 + width + 8 && home.z >= z0 - 8 && home.z <= z0 + depth + 8)
				{
					continue;
				}
				if (!overTrees && trees.Exists((XYZ t) => t.x >= x0 - 1 && t.x <= x0 + width && t.z >= z0 - 1 && t.z <= z0 + depth))
				{
					continue;
				}
				int top = int.MinValue;
				int bottom = int.MaxValue;
				bool missing = false;
				for (int i = 0; i < width && !missing; i++)
				{
					for (int j = 0; j < depth; j++)
					{
						int h = heights[x0 - baseX + i, z0 - baseZ + j];
						if (h == int.MinValue)
						{
							missing = true;
							break;
						}
						top = Mathf.Max(top, h);
						bottom = Mathf.Min(bottom, h);
					}
				}
				// Over the void, or more than a few blocks of floor to raise.
				if (missing || top - bottom > maxRise)
				{
					continue;
				}
				long blocks = 0;
				for (int i = 0; i < width; i++)
				{
					for (int j = 0; j < depth; j++)
					{
						blocks += top - heights[x0 - baseX + i, z0 - baseZ + j];
					}
				}
				long score = blocks * 4 + Mathf.Abs(dx + width / 2) + Mathf.Abs(dz + depth / 2);
				if (score < best)
				{
					best = score;
					corner = new XYZ(x0, top + 1, z0);
					floor = top;
				}
			}
		}
		return best != long.MaxValue;
	}

	// ---- build

	private IEnumerator Build(List<Piece> pieces)
	{
		_state = "researching everything";
		ResearchAll();
		yield return null;

		_state = "raising the floor";
		Project project = ProjectEndpoints.BuildProject(0, out bool _);
		IEnumerator floor = RaiseFloor(project);
		while (Step(floor))
		{
			yield return null;
		}
		yield return WaitForBuilds();

		_state = "placing the buildings and blocks";
		int count = 0;
		List<Blueprint> placed = new List<Blueprint>();
		foreach (Piece piece in pieces)
		{
			XYZ at = new XYZ(_corner.x + piece.X - piece.Offset.x, _corner.y - piece.Offset.y, _corner.z + piece.Z - piece.Offset.z);
			BlueprintPlacer.Result result = null;
			try
			{
				result = BlueprintPlacer.Place(piece.Key, at, 0, project, force: false);
			}
			catch (Exception ex)
			{
				Note(piece.Section + ": " + piece.Key.Name + " failed (" + ex.GetType().Name + ")");
			}
			if (result == null)
			{
				Count("error");
				continue;
			}
			if (!result.Placed && result.Outcome == "unsupported")
			{
				// Hung on a wall or resting on ends (paintings, braces, beams, planks): something to hold it, then
				// once more.
				InstantBuild.Complete(placed);
				placed.Clear();
				foreach (Support support in Supports)
				{
					yield return Hold(piece, support, project);
					// Turned too: some pieces only take support on one side.
					for (int turns = 0; turns < 4 && !result.Placed; turns++)
					{
						result = BlueprintPlacer.Place(piece.Key, at, turns, project, force: false);
					}
					if (result.Placed)
					{
						break;
					}
				}
			}
			Count(result.Placed ? "placed" : result.Outcome);
			if (result.Placed)
			{
				piece.Blueprint = result.Blueprint;
				placed.Add(result.Blueprint);
			}
			else
			{
				Note(piece.Section + ": " + piece.Key.Name + " not placed (" + result.Outcome + ")");
			}
			if (++count % 20 == 0)
			{
				InstantBuild.Complete(placed);
				placed.Clear();
				yield return null;
			}
		}
		InstantBuild.Complete(placed);
		ProjectEndpoints.Commit(project);
		yield return WaitForBuilds();

		_state = "filling the stockpiles";
		yield return null;
		Recepteur[] storages = Object.FindObjectsOfType<Recepteur>();
		Faction faction = User.LocalUser.faction;
		// Each stockpile's own Stacking tier first; the room follows within Stacking's check (every 3 seconds).
		bool tiers = false;
		foreach (Piece piece in pieces)
		{
			if (piece.Fill != null && piece.StackingTier >= 0)
			{
				Recepteur storage = Nearest(storages, new Vector3(_corner.x + piece.X + piece.Size.x / 2f, _corner.y, _corner.z + piece.Z + piece.Size.z / 2f));
#if !ESSENTIALS
				StockpileTiers.Set(storage, piece.StackingTier);
#endif
				tiers = true;
			}
		}
		if (tiers)
		{
			float until = Time.unscaledTime + 4f;
			while (Time.unscaledTime < until)
			{
				yield return null;
			}
		}
		foreach (Piece piece in pieces)
		{
			if (piece.Kits != null)
			{
				Vector3 at = new Vector3(_corner.x + piece.X + piece.Size.x / 2f, _corner.y, _corner.z + piece.Z + piece.Size.z / 2f);
				yield return FillStand(Nearest(storages, at), piece.Kits, faction);
				continue;
			}
			if (piece.Fill == null)
			{
				continue;
			}
			Vector3 centre = new Vector3(_corner.x + piece.X + piece.Size.x / 2f, _corner.y, _corner.z + piece.Z + piece.Size.z / 2f);
			Recepteur storage = Nearest(storages, centre);
			if (storage == null)
			{
				Note("No stockpile found for " + piece.Fill.Name);
				continue;
			}
			int stored = 0;
			while (stored < MaxFill && storage.HasRoomFor(piece.Fill))
			{
				GameObject item = Transactor.SpawnUNet(piece.Fill, faction, storage.transform.position + Vector3.up, Quaternion.identity);
				if (item == null)
				{
					Note("Could not make " + piece.Fill.Name);
					break;
				}
				storage.AddObject(item);
				stored++;
				_filled++;
				if (stored % SpawnPerFrame == 0)
				{
					yield return null;
				}
			}
			if (stored == 0)
			{
				Note("A stockpile takes no " + piece.Fill.Name);
			}
		}
		StandingTrades();
		_state = "done";
		_running = false;
		Note("Done: " + _filled + " items stored. Save the game to keep the test world.");
	}

	// Trades queued on the market, so its window shows a queue: rope and iron, paid the way the team can pay most often.
	private void StandingTrades()
	{
#if !ESSENTIALS
		Market.MarketStation market = Object.FindObjectOfType<Market.MarketStation>();
		if (market == null)
		{
			return;
		}
		int added = 0;
		foreach (Market.MarketPrices.Good good in Market.MarketPrices.Goods)
		{
			if (good.Name != "Rope" && good.Name != "Iron")
			{
				continue;
			}
			Market.MarketPrices.Trade best = Market.MarketOrders.BestPayment(market, good);
			if (best != null)
			{
				added += Market.MarketOrders.Barter(market, best.Give, good, 2);
			}
		}
		Note("Market: " + added + " trades queued (rope, iron)");
#endif
	}

	// Every upgrade line of the player's team at its top tier.
	private void ResearchAll()
	{
#if !ESSENTIALS
		Faction faction = (User.LocalUser != null) ? User.LocalUser.faction : null;
		if (faction == null)
		{
			Note("No team to research for");
			return;
		}
		foreach (UpgradeLine line in UpgradeLines.All)
		{
			TeamUpgrades.Unlock(faction, line.Index, line.Tiers);
		}
		Note("Researched every upgrade (" + UpgradeLines.All.Count + " lines) to its top tier");
#endif
	}

	// Kit after kit, a whole one at a time, while the stand has room for all of the next.
	private IEnumerator FillStand(Recepteur stand, List<List<Factory.AssetKey>> kits, Faction faction)
	{
		if (stand == null || stand.GetComponent<ToolRack>() == null)
		{
			Note("No armour stand found for its kits");
			yield break;
		}
		int stored = 0;
		for (int round = 0; round < ToolRack.TOOLRACK_CAPACITY * kits.Count; round++)
		{
			List<Factory.AssetKey> kit = kits[round % kits.Count];
			bool room = true;
			foreach (Factory.AssetKey key in kit)
			{
				room &= stand.HasRoomFor(key);
			}
			if (!room)
			{
				continue;
			}
			foreach (Factory.AssetKey key in kit)
			{
				GameObject item = Transactor.SpawnUNet(key, faction, stand.transform.position + Vector3.up, Quaternion.identity);
				if (item == null)
				{
					Note("Could not make " + key.Name);
					continue;
				}
				stand.AddObject(item);
				stored++;
				_filled++;
			}
			yield return null;
		}
		if (stored == 0)
		{
			Note("An armour stand took none of its kits");
		}
	}

	private enum Support
	{
		Back,
		Ends,
		Front,
		Sides,
		Above
	}

	private static readonly Support[] Supports = { Support.Back, Support.Ends, Support.Front, Support.Sides, Support.Above };

	// Stone that holds a piece: a wall behind or in front of it as wide and high as it, or a pillar at each end, from
	// the floor up; built at once.
	private IEnumerator Hold(Piece piece, Support support, Project project)
	{
		Factory.AssetKey block = FloorBlock();
		if (block == null)
		{
			yield break;
		}
		int x0 = _corner.x + piece.X;
		int z0 = _corner.z + piece.Z;
		int y0 = _corner.y;
		int y1 = _corner.y + piece.Size.y - 1;
		List<XYZ> cells = new List<XYZ>();
		for (int y = y0; y <= y1; y++)
		{
			switch (support)
			{
				case Support.Back:
					for (int x = x0; x < x0 + piece.Size.x; x++)
					{
						cells.Add(new XYZ(x, y, z0 - 1));
					}
					break;
				case Support.Front:
					for (int x = x0; x < x0 + piece.Size.x; x++)
					{
						cells.Add(new XYZ(x, y, z0 + piece.Size.z));
					}
					break;
				case Support.Sides:
					for (int x = x0; x < x0 + piece.Size.x; x++)
					{
						cells.Add(new XYZ(x, y, z0 - 1));
						cells.Add(new XYZ(x, y, z0 + piece.Size.z));
					}
					for (int z = z0; z < z0 + piece.Size.z; z++)
					{
						cells.Add(new XYZ(x0 - 1, y, z));
						cells.Add(new XYZ(x0 + piece.Size.x, y, z));
					}
					break;
				case Support.Above:
					if (y == y1)
					{
						// A lintel over it on the walls round it.
						for (int x = x0 - 1; x <= x0 + piece.Size.x; x++)
						{
							for (int z = z0 - 1; z <= z0 + piece.Size.z; z++)
							{
								cells.Add(new XYZ(x, y + 1, z));
							}
						}
					}
					break;
				case Support.Ends:
					for (int z = z0; z < z0 + piece.Size.z; z++)
					{
						cells.Add(new XYZ(x0 - 1, y, z));
						cells.Add(new XYZ(x0 + piece.Size.x, y, z));
					}
					break;
			}
		}
		List<Blueprint> built = new List<Blueprint>();
		foreach (XYZ cell in cells)
		{
			BlueprintPlacer.Result result = BlueprintPlacer.Place(block, cell, 0, project, force: true);
			if (result.Placed)
			{
				built.Add(result.Blueprint);
			}
		}
		if (built.Count > 0)
		{
			InstantBuild.Complete(built);
			yield return WaitForBuilds();
		}
	}

	private static bool Step(IEnumerator routine)
	{
		return routine.MoveNext();
	}

	// Stone blocks from the ground up to the floor in every lower column of the site.
	private IEnumerator RaiseFloor(Project project)
	{
		Factory.AssetKey block = FloorBlock();
		if (block == null)
		{
			Note("No 1 x 1 x 1 block for the floor; the pieces stand on the ground as it is");
			yield break;
		}
		List<Blueprint> batch = new List<Blueprint>();
		for (int i = 0; i < _width; i++)
		{
			for (int j = 0; j < _depth; j++)
			{
				int x = _corner.x + i;
				int z = _corner.z + j;
				for (int y = WorldEndpoints.GroundY(x, z) + 1; y <= _floor; y++)
				{
					BlueprintPlacer.Result result = BlueprintPlacer.Place(block, new XYZ(x, y, z), 0, project, force: true);
					if (result.Placed)
					{
						batch.Add(result.Blueprint);
						_floorBlocks++;
					}
					if (batch.Count >= PlacePerFrame)
					{
						InstantBuild.Complete(batch);
						batch.Clear();
						yield return null;
					}
				}
			}
		}
		InstantBuild.Complete(batch);
		ProjectEndpoints.Commit(project);
	}

	internal static Factory.AssetKey FloorBlock()
	{
		foreach (string name in new[] { "Stone_Brick", "Stone_DoubleBrick" })
		{
			Factory.AssetKey key = new Factory.AssetKey("Blueprints", name);
			if (!Factory.Exist(key))
			{
				continue;
			}
			BlueprintPlacer.Fit fit = BlueprintPlacer.Try(key, WorldEndpoints.HomeVoxel() + new XYZ(0, 60, 0), 0);
			if (fit != null && fit.HasCells && fit.Size.x == 1 && fit.Size.y == 1 && fit.Size.z == 1)
			{
				return key;
			}
		}
		return null;
	}

	// Until the instant builds left waiting for support are done (or given up).
	private IEnumerator WaitForBuilds()
	{
		float until = Time.unscaledTime + 20f;
		yield return null;
		while (InstantBuild.Queued > 0 && Time.unscaledTime < until)
		{
			yield return null;
		}
	}

	internal static Recepteur Nearest(Recepteur[] storages, Vector3 centre)
	{
		Recepteur best = null;
		float bestDistance = 2.5f;
		foreach (Recepteur storage in storages)
		{
			if (storage == null || storage.transform.parent != null && storage.GetComponentInParent<Blueprint>() != null)
			{
				continue;
			}
			Vector3 p = storage.transform.position;
			float distance = new Vector2(p.x - centre.x, p.z - centre.z).magnitude;
			if (distance < bestDistance && Mathf.Abs(p.y - centre.y) < 3f)
			{
				best = storage;
				bestDistance = distance;
			}
		}
		return best;
	}
}
