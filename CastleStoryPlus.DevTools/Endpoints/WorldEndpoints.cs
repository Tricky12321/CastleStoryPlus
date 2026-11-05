using System;
using System.Collections.Generic;
using Brix.Assets;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Legacy;
using CastleStoryPlus.DevTools.Api;
using CastleStoryPlus.DevTools.Worlds;
using CastleStoryPlus.Giant;
using UnityEngine;
using UnityEngine.Networking;
using static CastleStoryPlus.DevTools.Endpoints.Json;
using Object = UnityEngine.Object;

namespace CastleStoryPlus.DevTools.Endpoints;

// Worlds: the maps a new single-player game can start on with their modes and presets, starting one (leaving the
// current game without saving), the player's base (home crystal, bricktrons) and the terrain heights around a point.
internal static class WorldEndpoints
{
	private const string Sandbox = "sandbox";

	private const string WorldEditor = "world_editor";

	// The heights scan from here down.
	private const int TopY = 255;

	private const int MaxRadius = 40;

	// How far from its bricktrons a crystal of no faction may stand to count as their home before the game starts.
	private const float MaxSpawnDistance = 32f;

	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/worlds", "The maps a new single-player game can start on (codename, name, size) with the modes and presets of each, as the new game screen lists them", Worlds)
		{
			Parameters = "q (part of the map's name or codename)",
			TimeoutMs = 60000
		});
		routes.Add(new Route("POST", "/api/world/new", "Starts a new single-player world from the title screen or from a game (left without saving). Answers at once; GET /api/status shows worldStart and loading until the world is in. Sandbox (the default mode) has no enemies: a home crystal and 5 bricktrons", NewWorld)
		{
			Parameters = "map (codename, short codename or name; default: the first official map), gamemode (codename, default sandbox), preset (codename, short codename or name; default: the mode's default), waves (Invasion only: minutes between waves, 5 to 30)",
			TimeoutMs = 60000
		});
		routes.Add(new Route("GET", "/api/saves", "The single-player saves (name, map, last played mode, when), newest first", Saves));
		routes.Add(new Route("POST", "/api/world/load", "Loads a single-player save in its last played mode, from the title screen or from a game (left without saving), as the title screen's Continue does. Answers at once; GET /api/status shows worldStart and loading until the world is in", LoadSave)
		{
			Parameters = "name (the save's name, or part of it; GET /api/saves lists them)",
			TimeoutMs = 60000
		});
		routes.Add(new Route("GET", "/api/base", "The player's base: faction, home crystal (position, voxel, energy, bricktrons it can spawn; isHome false: the game has not marked it yet, as before the intro is closed, so the faction's crystal nearest its bricktrons), bricktrons by job, build projects", Base));
		routes.Add(new Route("GET", "/api/ground", "Terrain heights around a point: per column the top solid voxel (build on y+1), the trees in the area (count and the 10 nearest root voxels), and optionally flat squares to build on, nearest first", Ground)
		{
			Parameters = "x, z (default: the home crystal), radius (default 8, at most 40), flat (side of the flat squares to look for, e.g. 6; 0: none; each with the trees on it and around it), noTrees (true: only flat squares without trees, e.g. for a quarry), heights (false: leave out the height map)"
		});
	}

	private static void NeedHost()
	{
		if (!NetworkServer.active)
		{
			throw new ApiException(409, "Only on the host (single player or hosting a game)");
		}
		if (User.LocalUser == null || User.LocalUser.faction == null)
		{
			throw new ApiException(409, "No game in progress (or still loading)");
		}
	}

	// The new game screen's maps: new maps (not saves) playable in single player, with their supported modes (the
	// map's markers checked by each mode's validator, which loadSubassets does).
	private static List<Asset_Map> SaveList()
	{
		List<Asset_Map> result = new List<Asset_Map>();
		foreach (Asset_Map save in AssetDispensary.saves.SendQuery(AssetQueryParameters.AllSPSources | AssetQueryParameters.RefreshCache, AssetPlayableContext.Singleplayer))
		{
			if (save != null)
			{
				result.Add(save);
			}
		}
		result.Sort((a, b) => b.dateTimeCreated.CompareTo(a.dateTimeCreated));
		return result;
	}

	private static object Saves(ApiRequest request)
	{
		List<object> saves = new List<object>();
		foreach (Asset_Map save in SaveList())
		{
			Asset_Gamemode mode = save.GetLastPlayedGamemode();
			saves.Add(Obj("name", save.customName, "map", save.displayName, "codename", save.codename, "mode", (mode != null) ? mode.codename : null, "currentVersion", save.IsCurrentVersion(), "saved", save.dateTimeCreated.ToString("yyyy-MM-dd HH:mm:ss")));
		}
		return Obj("count", saves.Count, "saves", saves);
	}

	private static object LoadSave(ApiRequest request)
	{
		WorldStarter starter = WorldStarter.Instance;
		if (starter == null)
		{
			throw new ApiException(503, "The world starter is not ready");
		}
		if (starter.Busy)
		{
			throw new ApiException(409, "A world is already being started: " + starter.Phase);
		}
		if (Architecte.instance != null && !Architecte.commence)
		{
			throw new ApiException(409, "A world is loading; wait until it is in (GET /api/status)");
		}
		string name = request.String("name", null);
		if (string.IsNullOrEmpty(name))
		{
			throw new ApiException(400, "Give the save's name: name= (GET /api/saves lists them)");
		}
		List<Asset_Map> saves = SaveList();
		Asset_Map save = saves.Find(s => string.Equals(s.customName, name, StringComparison.OrdinalIgnoreCase)) ?? saves.Find(s => s.customName != null && s.customName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
		if (save == null)
		{
			throw new ApiException(404, "No save '" + name + "' (GET /api/saves lists them)");
		}
		starter.BeginSave(save);
		return Obj("loading", true, "phase", starter.Phase, "name", save.customName, "map", save.displayName, "hint", "Poll GET /api/status until inGame is true and worldStart is null");
	}

	private static List<Asset_Map> Maps()
	{
		List<Asset_Map> result = new List<Asset_Map>();
		foreach (Asset_Map map in AssetDispensary.maps.SendQuery(AssetQueryParameters.AllSPSources | AssetQueryParameters.RefreshCache, AssetPlayableContext.Singleplayer, loadSubassets: true))
		{
			if (map != null && !map.IsShell() && map.IsNewGame() && map.IsCurrentVersion() && map.playableContext.HasFlag(AssetPlayableContext.Singleplayer))
			{
				result.Add(map);
			}
		}
		return result;
	}

	private static List<Asset_Gamemode> Modes(Asset_Map map)
	{
		List<Asset_Gamemode> result = new List<Asset_Gamemode>();
		foreach (Asset_Gamemode mode in map.GetGamemodes(AssetPlayableContext.Singleplayer))
		{
			if (mode != null && mode.codename != WorldEditor)
			{
				result.Add(mode);
			}
		}
		return result;
	}

	private static bool Matches(string codename, string shortCodename, string displayName, string name)
	{
		return codename.Equals(name, StringComparison.OrdinalIgnoreCase)
			|| shortCodename.Equals(name, StringComparison.OrdinalIgnoreCase)
			|| displayName.Equals(name, StringComparison.OrdinalIgnoreCase);
	}

	private static object Worlds(ApiRequest request)
	{
		string filter = request.String("q", string.Empty);
		List<object> maps = new List<object>();
		foreach (Asset_Map map in Maps())
		{
			if (filter.Length > 0 && (map.codename + " " + map.displayName).IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
			{
				continue;
			}
			List<object> modes = new List<object>();
			foreach (Asset_Gamemode mode in Modes(map))
			{
				List<object> presets = new List<object>();
				foreach (Asset_GamemodePreset preset in mode.GetPresets(AssetQueryParameters.AllSPSources, AssetPlayableContext.Singleplayer))
				{
					presets.Add(Obj("codename", preset.codename, "shortCodename", preset.shortCodename, "name", preset.displayName, "default", preset.isDefault));
				}
				modes.Add(Obj("codename", mode.codename, "name", mode.displayName, "enemies", mode.codename != Sandbox, "presets", presets));
			}
			maps.Add(Obj(
				"codename", map.codename,
				"shortCodename", map.shortCodename,
				"name", map.displayName,
				"size", map.mapSize.ToString(),
				"author", map.authorType.ToString(),
				"modes", modes));
		}
		return Obj("count", maps.Count, "maps", maps);
	}

	private static object NewWorld(ApiRequest request)
	{
		if (Neo.multiplayer)
		{
			throw new ApiException(409, "A new world can only be started in single player, not from a multiplayer game");
		}
		WorldStarter starter = WorldStarter.Instance;
		if (starter.Busy)
		{
			throw new ApiException(409, "A world is already being started: " + starter.Phase);
		}
		if (Architecte.instance != null && !Architecte.commence)
		{
			throw new ApiException(409, "A world is loading; wait until it is in (GET /api/status)");
		}
		string mapName = request.String("map", null);
		string modeName = request.String("gamemode", request.String("mode", Sandbox));
		List<Asset_Map> maps = Maps();
		Asset_Map map = null;
		foreach (Asset_Map candidate in maps)
		{
			if (mapName == null ? (candidate.authorType == AssetAuthorType.Official && Modes(candidate).Exists(m => m.codename == modeName)) : Matches(candidate.codename, candidate.shortCodename, candidate.displayName, mapName))
			{
				map = candidate;
				break;
			}
		}
		if (map == null)
		{
			throw new ApiException(404, (mapName == null) ? "No official map has the mode '" + modeName + "'" : "No map '" + mapName + "' (GET /api/worlds lists them)");
		}
		Asset_Gamemode mode = Modes(map).Find(m => m.codename.Equals(modeName, StringComparison.OrdinalIgnoreCase) || m.displayName.Equals(modeName, StringComparison.OrdinalIgnoreCase));
		if (mode == null)
		{
			List<string> names = Modes(map).ConvertAll(m => m.codename);
			throw new ApiException(404, "The map " + map.displayName + " has no mode '" + modeName + "' (it has: " + string.Join(", ", names.ToArray()) + ")");
		}
		string presetName = request.String("preset", null);
		Asset_GamemodePreset preset;
		if (presetName == null)
		{
			preset = mode.GetDefaultPreset(AssetQueryParameters.AllSPSources, AssetPlayableContext.Singleplayer);
		}
		else
		{
			preset = mode.GetPresets(AssetQueryParameters.AllSPSources, AssetPlayableContext.Singleplayer).Find(p => Matches(p.codename, p.shortCodename, p.displayName, presetName));
			if (preset == null)
			{
				throw new ApiException(404, "The mode " + mode.codename + " has no preset '" + presetName + "' (GET /api/worlds lists them)");
			}
		}
		int waves = request.Int("waves", 0);
		if (waves != 0 && (mode.codename != "invasion" || waves < 1 || waves > 60))
		{
			throw new ApiException(400, "'waves' is for Invasion only: minutes between waves (1 to 60). For no enemies use gamemode=sandbox");
		}
		starter.Begin(map, mode, preset, waves);
		return Obj(
			"starting", true,
			"phase", starter.Phase,
			"map", map.displayName,
			"mapCodename", map.codename,
			"gamemode", mode.codename,
			"preset", (preset != null) ? preset.codename : null,
			"enemies", mode.codename != Sandbox,
			"hint", "Poll GET /api/status until inGame is true and worldStart is null");
	}

	// The faction's home crystal. The game marks it (FireflyNest.isHome) from the game mode's script once the player
	// has started the game (the intro dialog closed); until then the faction's own crystal nearest its bricktrons,
	// else (no crystal of the faction yet) any crystal next to its bricktrons, the one they spawned at.
	internal static FireflyNest HomeNest(Faction faction)
	{
		FireflyNest home = GiantBricktron.HomeNest(faction);
		if (home != null)
		{
			return home;
		}
		List<Vector3> bricktrons = new List<Vector3>();
		foreach (Labor labor in Object.FindObjectsOfType<Labor>())
		{
			if (labor != null && faction.IsSame(labor.gameObject))
			{
				bricktrons.Add(labor.transform.position);
			}
		}
		FireflyNest best = null;
		float bestDistance = float.MaxValue;
		bool bestOwn = false;
		foreach (FireflyNest nest in FireflyNest.FireflyNests)
		{
			if (nest == null || nest.mainRecepteur == null || !nest.gameObject.activeInHierarchy)
			{
				continue;
			}
			bool own = nest.faction == faction;
			float distance = float.MaxValue;
			foreach (Vector3 position in bricktrons)
			{
				distance = Mathf.Min(distance, (position - nest.transform.position).sqrMagnitude);
			}
			if (!own && distance > MaxSpawnDistance * MaxSpawnDistance)
			{
				continue;
			}
			if ((own && !bestOwn) || (own == bestOwn && distance < bestDistance))
			{
				best = nest;
				bestDistance = distance;
				bestOwn = own;
			}
		}
		return best;
	}

	// Where things are measured from by default: the home crystal, else the first own bricktron.
	internal static XYZ HomeVoxel()
	{
		Faction faction = User.LocalUser.faction;
		FireflyNest nest = HomeNest(faction);
		if (nest != null)
		{
			return XYZ.FromVector3(nest.transform.position);
		}
		foreach (Labor labor in Object.FindObjectsOfType<Labor>())
		{
			if (labor != null && faction.IsSame(labor.gameObject))
			{
				return XYZ.FromVector3(labor.transform.position);
			}
		}
		throw new ApiException(404, "No home crystal and no bricktron of the player's faction");
	}

	private static object Base(ApiRequest request)
	{
		NeedHost();
		Faction faction = User.LocalUser.faction;
		FireflyNest nest = HomeNest(faction);
		Dictionary<string, int> jobs = new Dictionary<string, int>();
		int count = 0;
		foreach (Labor labor in Object.FindObjectsOfType<Labor>())
		{
			if (labor == null || !faction.IsSame(labor.gameObject))
			{
				continue;
			}
			count++;
			string job = (labor.Occupation != null) ? labor.Occupation.CurrentJob.ToString() : "none";
			jobs.TryGetValue(job, out int n);
			jobs[job] = n + 1;
		}
		object crystal = null;
		if (nest != null)
		{
			XYZ voxel = XYZ.FromVector3(nest.transform.position);
			crystal = Obj(
				"position", Vec(nest.transform.position),
				"isHome", nest.isHome,
				"voxel", PathEndpoints.Voxel(voxel),
				"groundY", GroundY(voxel.x, voxel.z),
				"energy", nest.GetAvailablePurifiedEnergy(),
				"newBricktronEnergy", nest.NewFireflyRequiredEnergy,
				"spawnable", nest.GetNewSpawnableCharacterCount());
		}
		return Obj(
			"faction", faction.name,
			"homeCrystal", crystal,
			"bricktrons", count,
			"jobs", jobs,
			"projects", ProjectEndpoints.List(faction, null));
	}

	// The top solid voxel of a column (terrain or block), or int.MinValue.
	// Trees standing with their root in the columns of the rectangle (a quarry under a tree cannot be dug: the
	// workers cannot get at it).
	internal static List<XYZ> TreesOver(int minX, int minZ, int maxX, int maxZ)
	{
		List<XYZ> result = new List<XYZ>();
		TreeRegistry trees = TreeRegistry.Instance;
		if (trees == null)
		{
			return result;
		}
		foreach (XYZ tree in trees._treeData.Keys)
		{
			if (tree.x >= minX && tree.x <= maxX && tree.z >= minZ && tree.z <= maxZ)
			{
				result.Add(tree);
			}
		}
		return result;
	}

	internal static int GroundY(int x, int z)
	{
		for (int y = TopY; y >= 0; y--)
		{
			if (Voxel.IsFull(new XYZ(x, y, z)))
			{
				return y;
			}
		}
		return int.MinValue;
	}

	private static object Ground(ApiRequest request)
	{
		NeedHost();
		XYZ center;
		if (request.Get("x") != null || request.Get("z") != null)
		{
			center = new XYZ(request.Int("x", 0), 0, request.Int("z", 0));
		}
		else
		{
			center = HomeVoxel();
		}
		int radius = Mathf.Clamp(request.Int("radius", 8), 0, MaxRadius);
		int flat = Mathf.Clamp(request.Int("flat", 0), 0, 2 * radius + 1);
		int size = 2 * radius + 1;
		int[,] heights = new int[size, size];
		List<int[]> rows = new List<int[]>();
		int min = int.MaxValue;
		int max = int.MinValue;
		for (int dz = 0; dz < size; dz++)
		{
			int[] row = new int[size];
			for (int dx = 0; dx < size; dx++)
			{
				int y = GroundY(center.x - radius + dx, center.z - radius + dz);
				heights[dx, dz] = y;
				row[dx] = (y == int.MinValue) ? -1 : y;
				if (y != int.MinValue)
				{
					min = Mathf.Min(min, y);
					max = Mathf.Max(max, y);
				}
			}
			rows.Add(row);
		}
		List<object> spots = null;
		if (flat > 0)
		{
			spots = FlatSpots(heights, size, flat, center, radius, request.Bool("noTrees", false));
		}
		// The trees are no game objects (TreeRegistry keeps them by their root voxel): the ones in the area, nearest first.
		List<XYZ> trees = TreesOver(center.x - radius, center.z - radius, center.x + radius, center.z + radius);
		trees.Sort((a, b) => FlatDistance(a, center).CompareTo(FlatDistance(b, center)));
		List<object> nearestTrees = new List<object>();
		for (int i = 0; i < trees.Count && i < 10; i++)
		{
			nearestTrees.Add(PathEndpoints.Voxel(trees[i]));
		}
		return Obj(
			"center", new int[2] { center.x, center.z },
			"radius", radius,
			"origin", new int[2] { center.x - radius, center.z - radius },
			"layout", "heights[row][col] is the top solid voxel y at x = origin.x + col, z = origin.z + row (-1: none); build on y + 1",
			"min", (min == int.MaxValue) ? (int?)null : min,
			"max", (max == int.MinValue) ? (int?)null : max,
			"heights", request.Bool("heights", true) ? rows : null,
			"flat", spots,
			"trees", Obj("count", trees.Count, "nearest", nearestTrees));
	}

	internal static int FlatDistance(XYZ a, XYZ b)
	{
		int dx = a.x - b.x;
		int dz = a.z - b.z;
		return dx * dx + dz * dz;
	}

	// Squares of side flat where every column has the same top, nearest the center first (at most 10).
	private static List<object> FlatSpots(int[,] heights, int size, int flat, XYZ center, int radius, bool noTrees)
	{
		List<KeyValuePair<float, object>> found = new List<KeyValuePair<float, object>>();
		for (int z0 = 0; z0 + flat <= size; z0++)
		{
			for (int x0 = 0; x0 + flat <= size; x0++)
			{
				int y = heights[x0, z0];
				bool same = y != int.MinValue;
				for (int dz = 0; same && dz < flat; dz++)
				{
					for (int dx = 0; same && dx < flat; dx++)
					{
						same = heights[x0 + dx, z0 + dz] == y;
					}
				}
				if (!same)
				{
					continue;
				}
				int x = center.x - radius + x0;
				int z = center.z - radius + z0;
				float cx = x + (flat - 1) / 2f - center.x;
				float cz = z + (flat - 1) / 2f - center.z;
				int treeCount = TreesOver(x - 1, z - 1, x + flat, z + flat).Count;
				if (noTrees && treeCount > 0)
				{
					continue;
				}
				found.Add(new KeyValuePair<float, object>(cx * cx + cz * cz, Obj("from", new int[3] { x, y + 1, z }, "to", new int[3] { x + flat - 1, y + 1, z + flat - 1 }, "groundY", y, "trees", treeCount)));
			}
		}
		found.Sort((a, b) => a.Key.CompareTo(b.Key));
		List<object> result = new List<object>();
		for (int i = 0; i < found.Count && result.Count < 10; i++)
		{
			result.Add(found[i].Value);
		}
		return result;
	}
}
