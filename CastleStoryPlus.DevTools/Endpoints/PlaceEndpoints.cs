using CastleStoryPlus.Core;
using System;
using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Network;
using CastleStoryPlus.DevTools.Api;
using CastleStoryPlus.DevTools.Building;
using UnityEngine;
using UnityEngine.Networking;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// Building through the API: the blueprints the build menu offers, and placing them (one, or a box of them) into a
// build project with the game's own fitting, validation and spawning (BlueprintPlacer).
internal static class PlaceEndpoints
{
	private const int MaxPieces = 1000;

	// The build menu's blueprints by tab (the game's LUI/Meta tables; the mod's own shapes, CustomBlocks, are added
	// to the stone tab).
	private static readonly string[] StoneBlocks =
	{
		"Stone_DoubleBrick", "Stone_Brick", "Stone_Brick2x2", "Stone_Brick2x4", "Stone_Brick1x3", "Stone_Brick1x4", "Stone_Brick1x1x2",
		"Stone_Staircase", "Stone_StairSingle", "Stone_StairSpiralLeft", "Stone_StairSpiralRight", "Stone_StairCornerLeft", "Stone_StairCornerRight",
		"Stone_StairCornerSingle", "Stone_InvertStairCornerSingle", "Stone_Column_Base", "Stone_Column2_Base", "Stone_Column3_Base", "Stone_Column4_Base",
		"Stone_Column5_Base", "Stone_BeamPlug_OneSide", "Stone_BeamPlug_TwoSides", "Stone_BeamPlug_Corner", "Stone_BeamPlug_ThreeSides",
		"Stone_BeamPlug_AllSides", "Stone_DoubleBrick_RoundR", "Stone_DoubleBrick_RoundL", "Stone_DoubleBrick_RoundLR", "Stone_Brick_Round",
		"Stone_Arch", "Stone_Brace1", "Stone_Brace2", "Stone_Merlon", "Stone_MerlonCorner", "Stone_MerlonDouble", "Stone_MerlonInverseCorner",
		"Stone_Parapet1", "Stone_Parapet2", "Stone_Parapet3"
	};

	private static readonly string[] WoodBlocks =
	{
		"Wood_SingleLog", "Wood_Beam", "Wood_VerticalBeam", "Wood_Brace", "Wood_SinglePlank", "Wood_Ramp_TopA", "Wood_Ramp_JunctionA",
		"Wood_Slab1x1", "Wood_Slab2x1", "Wood_Slab2x2", "Wood_Slab2x4", "Wood_Slab1x3", "Wood_Slab1x5", "Wood_Slab2x3", "Wood_Ladder"
	};

	private static readonly string[] Structures =
	{
		"Pallet", "Toolrack", "ChoppingBlock", "Furnace", "Loom", "Workbench", "Forge", "Lab", "Altar", "MachineShop",
		"ShakyDoor", "Door", "HeavyDoor", "Catapult", "Barrel", "BearTrap", "PylonGround", "LanternWard", "SentinelWard", "HealingWard",
		"CaptureWard", "Window", "DoubleWindow", "KnightStatue", "PaintingSmall", "PaintingBig", "PaintingWide", "PaintingTall"
	};

	// What the structures are, for choosing.
	private static readonly Dictionary<string, string> Notes = new Dictionary<string, string>
	{
		{ "Pallet", "stockpile" },
		{ "Toolrack", "tool rack: kits for jobs" },
		{ "ChoppingBlock", "workshop: logs to planks" },
		{ "Furnace", "workshop: iron ore to iron" },
		{ "Loom", "workshop: cloth" },
		{ "Workbench", "workshop: tools and kits" },
		{ "Forge", "workshop: iron gear" },
		{ "Lab", "research" },
		{ "Altar", "workshop" },
		{ "MachineShop", "workshop: siege" }
	};

	private static readonly Dictionary<string, int[]> Footprints = new Dictionary<string, int[]>();

	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("POST", "/api/blueprints/remove", "Takes away the player's blueprints (not yet built) in a box, or by id, as the player's delete does; material already delivered is dropped", Remove)
		{
			Parameters = "from, to (x,y,z: the box; a blueprint counts when its position is inside), ids (comma separated blueprint ids instead)"
		});
		routes.Add(new Route("POST", "/api/blueprints/complete", "Cheat: finishes the player's buildable blueprints at once (and demolitions), without workers or materials; repeats so pieces resting on finished ones follow", Complete)
		{
			Parameters = "project (build project id; default: every project)"
		});
		routes.Add(new Route("GET", "/api/blueprints/keys", "The blueprints the build menu offers (stone, wood, structures: stockpile, workshops, doors, ...) with their footprint (size x, y, z at turn 0 and the offset of its first voxel from 'at') when a game is running", Keys)
		{
			Parameters = "all (true: every key of the Blueprints factory, not only the menu's)"
		});
		routes.Add(new Route("POST", "/api/blueprints/place", "Places blueprints like the player does (fitting, validation, the game's spawn into the build project) and commits the project so it gets built. With 'to', fills the box at-to with the blueprint, stepping by its footprint, bottom layer first. Answers per piece: placed (buildable, waiting for support) or refused (obstructed, unsupported, invalid)", Place)
		{
			Parameters = "key (blueprint key name, e.g. Stone_DoubleBrick, Pallet; required), at (x,y,z: the empty voxel it goes in, on the ground: groundY + 1; required), to (x,y,z: the box's other corner), turn (degrees: 0, 90, 180, 270), project (build project id; default: the one used last, else the last selected, else a new one), force (true: also place unsupported blueprints, which wait for support), commit (false: leave the project uncommitted), instant (true: cheat, built at once without workers or materials; pieces waiting for support follow within a few seconds), ground (true: y is counted from the ground of each column, at.y = 0 is the first layer on the ground: walls over uneven ground), pieces (true: list every piece; default only the refused ones), demolish (true: take down the placed blocks of this blueprint instead, as the remove tool does; with instant at once)",
			TimeoutMs = 60000
		});
	}

	private static void NeedGame(bool client = false)
	{
		if (!NetworkServer.active && !(client && NetworkClient.active))
		{
			throw new ApiException(409, "Only on the host (single player or hosting a game)");
		}
		if (User.LocalUser == null || User.LocalUser.faction == null || !Architecte.commence || Architecte.termine)
		{
			throw new ApiException(409, "No game in progress (or still loading)");
		}
	}

	private static Factory.AssetKey Key(string name)
	{
		Factory.AssetKey key = new Factory.AssetKey("Blueprints", name);
		if (Factory.Exist(key))
		{
			return key;
		}
		// Any case.
		Factory factory = Factory.GetFactory("Blueprints");
		if (factory != null)
		{
			foreach (Factory.AssetKey candidate in factory.AssetKeys)
			{
				if (candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
				{
					return candidate;
				}
			}
		}
		throw new ApiException(404, "No blueprint '" + name + "' (GET /api/blueprints/keys lists them)");
	}

	private static object Keys(ApiRequest request)
	{
		bool inGame = NetworkServer.active && User.LocalUser != null && User.LocalUser.faction != null && Architecte.commence && !Architecte.termine;
		List<object> result = new List<object>();
		HashSet<string> listed = new HashSet<string>();
		AddKeys(result, listed, "stone", StoneBlocks, inGame);
		AddKeys(result, listed, "wood", WoodBlocks, inGame);
		AddKeys(result, listed, "structure", Structures, inGame);
		if (request.Bool("all", false))
		{
			Factory factory = Factory.GetFactory("Blueprints");
			if (factory != null)
			{
				List<string> others = new List<string>();
				foreach (Factory.AssetKey key in factory.AssetKeys)
				{
					if (!listed.Contains(key.Name))
					{
						others.Add(key.Name);
					}
				}
				others.Sort(StringComparer.Ordinal);
				AddKeys(result, listed, "other", others.ToArray(), false);
			}
		}
		return Obj("count", result.Count, "footprints", inGame ? "size [x, y, z] at turn 0; offset: its first voxel relative to 'at'" : "start a game to see the footprints", "keys", result);
	}

	private static void AddKeys(List<object> result, HashSet<string> listed, string tab, string[] names, bool withFootprint)
	{
		foreach (string name in names)
		{
			Factory.AssetKey key = new Factory.AssetKey("Blueprints", name);
			if (!listed.Add(name) || !Factory.Exist(key))
			{
				continue;
			}
			int[] footprint = withFootprint ? Footprint(key) : null;
			Notes.TryGetValue(name, out string note);
			result.Add(Obj(
				"key", name,
				"tab", tab,
				"note", note,
				"size", (footprint != null) ? new int[3] { footprint[0], footprint[1], footprint[2] } : null,
				"offset", (footprint != null) ? new int[3] { footprint[3], footprint[4], footprint[5] } : null));
		}
	}

	// Measured once per key by fitting a held copy high above the home crystal.
	private static int[] Footprint(Factory.AssetKey key)
	{
		if (Footprints.TryGetValue(key.Name, out int[] cached))
		{
			return cached;
		}
		int[] footprint = null;
		try
		{
			XYZ at = WorldEndpoints.HomeVoxel() + new XYZ(0, 60, 0);
			BlueprintPlacer.Fit fit = BlueprintPlacer.Try(key, at, 0);
			if (fit != null && fit.HasCells)
			{
				XYZ size = fit.Size;
				footprint = new int[6] { size.x, size.y, size.z, fit.Min.x - at.x, fit.Min.y - at.y, fit.Min.z - at.z };
			}
		}
		catch (Exception ex)
		{
			DevToolsPlugin.Log.LogWarning("DevTools: no footprint for " + key.Name + ": " + ex.Message);
		}
		Footprints[key.Name] = footprint;
		return footprint;
	}

	// On a client the blueprints are sent to the host as the player's placing does; they show up a moment later.
	private static object Place(ApiRequest request)
	{
		NeedGame(client: true);
		if (!NetworkServer.active && (request.Bool("instant", false) || request.Bool("demolish", false)))
		{
			throw new ApiException(409, "instant and demolish only on the host");
		}
		string name = request.Required("key");
		Factory.AssetKey key = Key(name);
		if (key.Name == "RopeBridgePlacement" || key.Name == "RopeBridgeFrame" || key.Name == "RopeBridgePlank")
		{
			throw new ApiException(400, "Rope bridges are placed with two ends; the API does not place them");
		}
		int turn = request.Int("turn", 0);
		if (turn % 90 != 0)
		{
			throw new ApiException(400, "'turn' must be 0, 90, 180 or 270 degrees");
		}
		int turns = ((turn / 90) % 4 + 4) % 4;
		XYZ at = XYZ.FromVector3(request.Vector("at"));
		XYZ to = (request.Get("to") != null) ? XYZ.FromVector3(request.Vector("to")) : at;
		bool force = request.Bool("force", false);
		bool ground = request.Bool("ground", false);
		bool allPieces = request.Bool("pieces", false);
		bool demolish = request.Bool("demolish", false);
		Project project = ProjectEndpoints.BuildProject(request.Int("project", 0), out bool created);

		// The box is filled stepping by the blueprint's size (turned), bottom layer first so the lower pieces hold
		// the upper ones.
		XYZ step = new XYZ(1, 1, 1);
		if (to != at)
		{
			BlueprintPlacer.Fit first = BlueprintPlacer.Try(key, at, turns);
			if (first != null && first.HasCells)
			{
				step = first.Size;
			}
		}
		XYZ min = new XYZ(Mathf.Min(at.x, to.x), Mathf.Min(at.y, to.y), Mathf.Min(at.z, to.z));
		XYZ max = new XYZ(Mathf.Max(at.x, to.x), Mathf.Max(at.y, to.y), Mathf.Max(at.z, to.z));
		long count = (long)((max.x - min.x) / step.x + 1) * ((max.y - min.y) / step.y + 1) * ((max.z - min.z) / step.z + 1);
		if (count > MaxPieces)
		{
			throw new ApiException(400, "The box needs " + count + " pieces; at most " + MaxPieces + " per call");
		}

		List<object> pieces = new List<object>();
		List<Blueprint> placedBlueprints = new List<Blueprint>();
		Dictionary<string, int> outcomes = new Dictionary<string, int>();
		int placed = 0;
		int total = 0;
		for (int y = min.y; y <= max.y; y += step.y)
		{
			for (int z = min.z; z <= max.z; z += step.z)
			{
				for (int x = min.x; x <= max.x; x += step.x)
				{
					int placeY = y;
					if (ground)
					{
						int groundY = WorldEndpoints.GroundY(x, z);
						if (groundY == int.MinValue)
						{
							total++;
							outcomes.TryGetValue("no ground", out int none);
							outcomes["no ground"] = none + 1;
							continue;
						}
						placeY = groundY + 1 + y;
					}
					BlueprintPlacer.Result result = demolish
						? BlueprintPlacer.Demolish(key, new XYZ(x, placeY, z), turns, project, request.Bool("instant", false))
						: BlueprintPlacer.Place(key, new XYZ(x, placeY, z), turns, project, force);
					if (result.Placed)
					{
						placed++;
						if (result.Blueprint != null)
						{
							placedBlueprints.Add(result.Blueprint);
						}
					}
					total++;
					outcomes.TryGetValue(result.Outcome, out int n);
					outcomes[result.Outcome] = n + 1;
					if (!allPieces && result.Placed)
					{
						continue;
					}
					pieces.Add(Obj("at", PathEndpoints.Voxel(result.At), "placed", result.Placed, "outcome", result.Outcome, "detail", result.Detail, "id", result.Placed ? (int?)result.Id : null));
				}
			}
		}
		InstantBuild.Outcome instant = null;
		if (placed > 0 && !demolish && request.Bool("instant", false))
		{
			instant = InstantBuild.Complete(placedBlueprints);
		}
		if (placed > 0 && request.Bool("commit", true))
		{
			ProjectEndpoints.Commit(project);
		}
		return Obj(
			"built", instant?.Built,
			"waitingToBuild", instant?.Left,
			"key", key.Name,
			"turn", turns * 90,
			"step", PathEndpoints.Voxel(step),
			"project", ProjectEndpoints.Describe(project),
			"projectCreated", created,
			"placed", placed,
			"refused", total - placed,
			"outcomes", outcomes,
			"pieces", pieces);
	}

	private static object Complete(ApiRequest request)
	{
		NeedGame();
		int id = request.Int("project", 0);
		Project project = (id != 0) ? ProjectEndpoints.Find(id) : null;
		InstantBuild.Outcome outcome = InstantBuild.Complete(InstantBuild.Of(User.LocalUser.faction, project));
		return Obj("built", outcome.Built, "notBuilt", outcome.Left);
	}

	private static object Remove(ApiRequest request)
	{
		NeedGame();
		HashSet<int> ids = new HashSet<int>();
		foreach (string part in request.String("ids", string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
		{
			ids.Add(int.Parse(part.Trim()));
		}
		bool byBox = ids.Count == 0;
		XYZ min = new XYZ(0, 0, 0);
		XYZ max = new XYZ(0, 0, 0);
		if (byBox)
		{
			XYZ from = XYZ.FromVector3(request.Vector("from"));
			XYZ to = XYZ.FromVector3(request.Vector("to"));
			min = new XYZ(Mathf.Min(from.x, to.x), Mathf.Min(from.y, to.y), Mathf.Min(from.z, to.z));
			max = new XYZ(Mathf.Max(from.x, to.x), Mathf.Max(from.y, to.y), Mathf.Max(from.z, to.z));
		}
		UNetBlueprint net = User.LocalUser.GetComponent<UNetBlueprint>();
		int removed = 0;
		foreach (Blueprint blueprint in InstantBuild.Of(User.LocalUser.faction, null))
		{
			if (byBox)
			{
				XYZ at = XYZ.FromVector3(blueprint.transform.position);
				if (at.x < min.x || at.y < min.y || at.z < min.z || at.x > max.x || at.y > max.y || at.z > max.z)
				{
					continue;
				}
			}
			else if (!ids.Contains(blueprint.gameObject.InstanceId()))
			{
				continue;
			}
			net.CmdDestroyBluePrint(blueprint.gameObject);
			removed++;
		}
		return Obj("removed", removed);
	}
}
