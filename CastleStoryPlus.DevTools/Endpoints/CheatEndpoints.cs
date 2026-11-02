using System;
using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Beacons;
using Brix.Game.Network;
using Brix.Transactions;
using CastleStoryPlus.Combat;
using CastleStoryPlus.Core;
using CastleStoryPlus.DevTools.Api;
using CastleStoryPlus.Diagnostics;
using UnityEngine;
using UnityEngine.Networking;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// Cheats for testing and for playing through the API faster: new bricktrons without crystal energy, items
// (logs, stone, iron, ...) out of nothing, and enemies (raids) on demand. Host only. Finishing blueprints at once is /api/blueprints/complete.
internal static class CheatEndpoints
{
	private const int MaxCount = 50;

	// How far from the home crystal enemies are spawned by default.
	private const int EnemyDistance = 20;

	private const int MaxEnemyLevel = 10;

	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("POST", "/api/cheat/bricktrons", "Cheat: spawns the player's bricktrons without crystal energy, as builders, knights or archers (with their gear)", Bricktrons)
		{
			Parameters = "count (default 1, at most 50), job (builder, knight or archer; default builder), at (x,y,z; default next to the home crystal)"
		});
		routes.Add(new Route("POST", "/api/cheat/items", "Cheat: spawns loose items (resources, tools) on the ground for the workers to carry to the stockpiles", Items)
		{
			Parameters = "key (Factory/Name, e.g. RawResources/RawIron or BuildResources/IronIngot; a bare name is looked up in RawResources and BuildResources; required), count (default 1, at most 50), at (x,y,z; default next to the home crystal)"
		});
		routes.Add(new Route("POST", "/api/cheat/enemies", "Cheat: spawns enemies of the enemy faction, by default as a new raid that attacks the player's home crystal", Enemies)
		{
			Parameters = "type (corruptron, biftron, minitron, crawler, magitron or healtron; default corruptron), count (default 1, at most 50), at (x,y,z; default on the ground about 20 voxels from the home crystal), level (CharacterLevel 1-10, default 1), raid (default true: hired into a new raid project aimed at the home crystal as an invasion wave is; false: left to their instinct)"
		});
	}

	private static void NeedHost()
	{
		if (!NetworkServer.active)
		{
			throw new ApiException(409, "Only on the host (single player or hosting a game)");
		}
		if (User.LocalUser == null || User.LocalUser.faction == null || !Architecte.commence || Architecte.termine)
		{
			throw new ApiException(409, "No game in progress (or still loading)");
		}
	}

	private static int Count(ApiRequest request)
	{
		int count = request.Int("count", 1);
		if (count < 1 || count > MaxCount)
		{
			throw new ApiException(400, "'count' must be from 1 to " + MaxCount);
		}
		return count;
	}

	// Next to the home crystal, spread so spawned things do not stack in one voxel.
	private static Vector3 Center(ApiRequest request)
	{
		return (request.Get("at") != null) ? request.Vector("at") : WorldEndpoints.HomeVoxel().ToVector3() + new Vector3(4f, 0f, 2f);
	}

	private static Vector3 Around(ApiRequest request, int index)
	{
		Vector3 at = (request.Get("at") != null) ? request.Vector("at") : WorldEndpoints.HomeVoxel().ToVector3() + new Vector3(2f, 0f, 0f);
		return at + new Vector3(index % 5, 0f, index / 5);
	}

	private static object Bricktrons(ApiRequest request)
	{
		NeedHost();
		int count = Count(request);
		string job = request.String("job", "builder");
		Occupation.Type occupation;
		try
		{
			occupation = (Occupation.Type)Enum.Parse(typeof(Occupation.Type), job, true);
		}
		catch (ArgumentException)
		{
			throw new ApiException(400, "'job' must be builder, knight or archer, was " + job);
		}
		if (occupation != Occupation.Type.Builder && occupation != Occupation.Type.Knight && occupation != Occupation.Type.Archer)
		{
			throw new ApiException(400, "'job' must be builder, knight or archer, was " + job);
		}
		Faction faction = User.LocalUser.faction;
		UNetProjectCmd command = User.LocalUser.GetComponent<UNetProjectCmd>();
		for (int i = 0; i < count; i++)
		{
			Vector3 at = Around(request, i);
			command.CmdSpawn(Test.Bricktron.Factory, Test.Bricktron.Name, new Vector3(Mathf.Round(at.x), at.y, Mathf.Round(at.z)), Quaternion.identity, faction.gameObject, occupation, true);
		}
		return Obj("spawned", count, "job", occupation.ToString());
	}

	private static object Items(ApiRequest request)
	{
		NeedHost();
		int count = Count(request);
		Factory.AssetKey key = ItemKey(request.Required("key"));
		List<object> spawned = new List<object>();
		for (int i = 0; i < count; i++)
		{
			GameObject item = Transactor.SpawnUNet(key, User.LocalUser.faction, SpawnSpread.Place(Center(request), i, count), Quaternion.identity);
			if (item == null)
			{
				throw new ApiException(400, "The game could not spawn " + key.Factory + "/" + key.Name);
			}
			SpawnSpread.Calm(item);
			spawned.Add(item.InstanceId());
		}
		return Obj("key", key.Factory + "/" + key.Name, "spawned", spawned);
	}

	private static Factory.AssetKey ItemKey(string text)
	{
		int slash = text.IndexOf('/');
		if (slash > 0)
		{
			return new Factory.AssetKey(text.Substring(0, slash), text.Substring(slash + 1));
		}
		foreach (Type type in new[] { typeof(RawResources), typeof(BuildResources) })
		{
			foreach (System.Reflection.FieldInfo field in type.GetFields())
			{
				if (field.FieldType == typeof(Factory.AssetKey) && string.Equals(field.Name, text, StringComparison.OrdinalIgnoreCase))
				{
					return (Factory.AssetKey)field.GetValue(null);
				}
			}
		}
		throw new ApiException(400, "Unknown item " + text + " (give Factory/Name)");
	}

	private static object Enemies(ApiRequest request)
	{
		NeedHost();
		int count = Count(request);
		string type = request.String("type", "corruptron").ToLowerInvariant();
		Factory.AssetKey key;
		Occupation.Type occupation;
		bool healtron = false;
		switch (type)
		{
			case "corruptron":
				key = Test.Corruptron;
				occupation = Occupation.Type.Corruptron;
				break;
			case "healtron":
				key = Test.Corruptron;
				occupation = Occupation.Type.Corruptron;
				healtron = true;
				break;
			case "biftron":
				key = Test.Biftron;
				occupation = Occupation.Type.Biftron;
				break;
			case "minitron":
				key = Test.Minitron;
				occupation = Occupation.Type.Minitron;
				break;
			case "crawler":
				key = Test.Crawler;
				occupation = Occupation.Type.Crawler;
				break;
			case "magitron":
				key = Test.Magitron;
				occupation = Occupation.Type.Magitron;
				break;
			default:
				throw new ApiException(400, "'type' must be corruptron, biftron, minitron, crawler, magitron or healtron, was " + type);
		}
		if (healtron && !CastleStoryPlus.Plugin.IsEnabled(Features.Healtron))
		{
			throw new ApiException(409, "The Healtron feature is off");
		}
		int level = request.Int("level", 1);
		if (level < 1 || level > MaxEnemyLevel)
		{
			throw new ApiException(400, "'level' must be from 1 to " + MaxEnemyLevel);
		}
		Faction enemy = Faction.FindByName("Corruptrons") ?? Faction.DefaultEnemy;
		if (enemy == null)
		{
			throw new ApiException(409, "No enemy faction in this game");
		}
		Vector3 home = WorldEndpoints.HomeVoxel().ToVector3();
		bool given = request.Get("at") != null;
		Vector3 center = given ? request.Vector("at") : AwayFrom(home);
		List<object> spawned = new List<object>();
		List<Labor> units = new List<Labor>();
		string failed = null;
		for (int i = 0; i < count; i++)
		{
			Vector3 at = center + new Vector3(i % 5, 0f, i / 5);
			if (!given)
			{
				int ground = WorldEndpoints.GroundY(Mathf.FloorToInt(at.x), Mathf.FloorToInt(at.z));
				at.y = (ground == int.MinValue) ? center.y : ground + 1;
			}
			GameObject go = Transactor.SpawnUNet(key, enemy, at, Quaternion.identity);
			if (go == null)
			{
				// The ones spawned so far stay and are reported.
				failed = "The game could not spawn " + key.Factory + "/" + key.Name + " (" + spawned.Count + " of " + count + " spawned)";
				break;
			}
			go.GetComponent<Occupation>().SetOccupation(occupation, true);
			Profession profession = go.GetComponent<Profession>();
			if (profession != null)
			{
				profession.CharacterLevel = level;
			}
			Labor labor = go.GetComponent<Labor>();
			// Exactly what was asked for: the raid does not roll a plain corruptron into a Healtron.
			if (healtron)
			{
				Healtron.Make(labor);
			}
			else
			{
				Healtron.MarkRolled(labor);
			}
			if (labor != null)
			{
				units.Add(labor);
			}
			spawned.Add(go.InstanceId());
		}
		if (spawned.Count == 0)
		{
			throw new ApiException(500, failed);
		}
		// The raid only once there is someone to hire, so a failed spawn leaves no empty raid behind.
		RaidProject raid = (request.Bool("raid", true) && units.Count > 0) ? NewRaid(enemy, home) : null;
		if (raid != null)
		{
			foreach (Labor labor in units)
			{
				Hire(raid, labor);
			}
		}
		return Obj(
			"type", type,
			"level", level,
			"faction", enemy.name,
			"at", Vec(center),
			"raid", (raid != null) ? (object)raid.gameObject.InstanceId() : null,
			"spawned", spawned,
			"error", failed);
	}

	// About EnemyDistance voxels from the home crystal, in the first of 8 directions with ground under it.
	private static Vector3 AwayFrom(Vector3 home)
	{
		for (int i = 0; i < 8; i++)
		{
			float angle = i * Mathf.PI / 4f;
			int x = Mathf.RoundToInt(home.x + Mathf.Cos(angle) * EnemyDistance);
			int z = Mathf.RoundToInt(home.z + Mathf.Sin(angle) * EnemyDistance);
			int ground = WorldEndpoints.GroundY(x, z);
			if (ground != int.MinValue)
			{
				return new Vector3(x, ground + 1, z);
			}
		}
		throw new ApiException(404, "No ground about " + EnemyDistance + " voxels from the home crystal; give 'at'");
	}

	// A raid project as the invasion script makes one (fy_RaidProject:New, SetBeaconPosition): its beacon on the
	// home crystal, where its units walk aggressively to and fight what they meet.
	internal static RaidProject NewRaid(Faction enemy, Vector3 target)
	{
		GameObject go = Transactor.SpawnUNet(Groupes.RaidProjectAI, enemy, Vector3.zero, Quaternion.identity);
		RaidProject raid = (go != null) ? go.GetComponent<RaidProject>() : null;
		if (raid == null)
		{
			throw new ApiException(500, "The game could not make a raid project");
		}
		BeaconDriver driver = go.GetComponent<BeaconDriver>();
		if (driver != null && driver.Beacon != null)
		{
			driver.Beacon.SetVisualPosition(target);
		}
		else if (driver != null)
		{
			driver.SetBaliseAt(target);
		}
		raid.name = "DevTools raid";
		return raid;
	}

	// As the game's change-project command does for the invasion script's Project.AssignLabor.
	internal static void Hire(RaidProject raid, Labor labor)
	{
		if (raid.IsFull())
		{
			raid.MaximumCrewSize++;
		}
		labor.Autonomy = raid.Hire(labor) ? AutonomyStatus.LockedToOperable : AutonomyStatus.InstinctOnly;
	}
}
