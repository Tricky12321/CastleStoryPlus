using System;
using System.Collections.Generic;
using System.IO;
using Brix.Assets;
using Brix.Engine;
using Brix.Engine.FlatWorld.Layers;
using Brix.Engine.Nature;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Beacons;
using Brix.Game.Semantique;
using Brix.Lifecycle.Pooling;
using Brix.Lua;
using Brix.Transactions;
using CastleStoryPlus.Army;
using CastleStoryPlus.Core;
using CastleStoryPlus.Economy;
using CastleStoryPlus.Giant;
using HarmonyLib;
using MoonSharp.Interpreter;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Treasure;

// Treasure chests in every world: 3-4 on a small map, 5-7 on a medium one, 8-10 on a large one, on flat ground away
// from the starting points. Each is guarded by a pack of 2 to 10 beasts of any kind (BeastKinds), and the bigger the
// pack, the more and the better the loot
// hired into a raid whose beacon is on the chest, so they stay near it and attack bricktrons that come close. Once no
// living beast is within GuardRadius of a chest, a bricktron of a player that walks up to it opens it: the chest goes
// and its loot spills out. The guards are peaceful until attacked (BeastTemper): then the whole pack fights.
// The host places them the first time a world is played (a new world, or a save from before this feature); the
// chests and their state are kept in a file next to the save (the guards are ordinary units the game saves).
[Feature(Features.Treasure, Features.TreasureInfo)]
internal class TreasureChests : MonoBehaviour
{
	private sealed class Chest
	{
		public Vector3 Position;

		// How many beasts were set to guard it (2 to 10): the size of its loot.
		public int Guards;

		public bool Opened;

		// False until its chest is made: a new chest waits for its ruin to be built (TreasureRuins.Busy), as a chest
		// made at once had the ruin's blocks made round it and fell through the ground.
		public bool Made = true;

		public GameObject Go;

		// When a bricktron of this player last came to it while it was guarded (Time.unscaledTime).
		public float HintedAt = -100f;
	}

	// Seconds between two "defeat the guardians" hints for one chest.
	private const float HintSeconds = 15f;

	// Host to every peer: the bricktron (its network id) that came to a guarded chest; its player's peer shows the hint.
	private const short HintMessage = 204;

	private const string HintText = "Defeat the animals guarding this chest first";

	internal const string GuardRaidName = "Treasure guards";

	private const string SaveFile = "castlestoryplus_treasure.json";

	private const float GuardRadius = 22f;

	private const float OpenDistance = 2.6f;

	private const float CheckSeconds = 1f;

	private const int MinGuards = 2;

	private const int MaxGuards = 10;

	private static readonly List<Chest> Chests = new List<Chest>();

	private static readonly List<Labor> Units = new List<Labor>();

	private static bool _active;

	private float _nextCheck;

	private static void Enable()
	{
		BeastKinds.Bind(Plugin.Cfg);
		Workers.WorkerStats.VariantChanged += (CharacterState state) => Beasts.Dirty = true;
		GameSession.OnLevelReady(LevelReady);
		GameSession.OnLeave(() =>
		{
			Chests.Clear();
			BeastTemper.Clear();
			SentBackAt.Clear();
			TreasureRuins.Clear();
			_active = false;
		});
		Plugin.Root.AddComponent<TreasureChests>();
		// The invasion's kill count (host) counted the treasure guards as corruptrons and biftrons, so the scoreboard
		// showed kills before the first wave.
		LuaInjection.AddFunction("IsBeast", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			LuaDriver driver = (args.Count > 0 && !args[0].IsNil()) ? args.AsComponent<LuaDriver>(0, "IsBeast", allowNil: true) : null;
			return DynValue.NewBoolean(driver != null && Beasts.KindOf(driver.gameObject) != null);
		});
		LuaInjection.AddPatch(Features.Treasure, "Gamemodes/invasion/message.lua", "\tlocal OnUnitKilled =\n\t\tfunction(ld, ld_attacker)\n", LuaInjection.Mode.InsertAfter, "\t\t\t---Castle Story Plus (Treasure): a treasure guard is no wave's unit\n\t\t\tif CastleStoryPlus.IsBeast ~= nil and CastleStoryPlus.IsBeast(ld) then return end\n");
	}

	private void Update()
	{
		Beasts.Update();
		if (NetworkServer.active)
		{
			TreasureRuins.Tick();
		}
		if (!_active || !NetworkServer.active || Time.time < _nextCheck)
		{
			return;
		}
		_nextCheck = Time.time + CheckSeconds;
		try
		{
			Check();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("Treasure: " + ex.Message);
		}
	}

	// ---- placing (host, when the level is ready)

	private static void LevelReady()
	{
		// Every peer (the host too, through its own client) takes the "defeat the guardians" hints.
		NetworkClient client = Brix.Network.UNetManager.instance?.LocalNetworkClient;
		if (client != null)
		{
			if (client.handlers.ContainsKey(HintMessage))
			{
				client.UnregisterHandler(HintMessage);
			}
			client.RegisterHandler(HintMessage, HintReceived);
		}
		Chests.Clear();
		_active = false;
		Asset_Map map = GameParam.Map;
		if (!NetworkServer.active || map == null || !Playable())
		{
			return;
		}
		try
		{
			string file = string.IsNullOrEmpty(map.path) ? null : System.IO.Path.Combine(map.path, SaveFile);
			if (!map.IsNewGame() && file != null && File.Exists(file))
			{
				Load(file);
				FindOrSpawnChests();
				Plugin.Log.LogInfo("Treasure: " + Chests.Count + " chests from the save (" + Chests.FindAll((Chest c) => c.Opened).Count + " opened)");
			}
			else
			{
				Place(map);
			}
			_active = true;
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("Treasure: could not place the chests: " + ex);
		}
	}

	// Modes with enemies to fight; not the world editor, the credit island or the tutorial.
	private static bool Playable()
	{
		Asset_Gamemode mode = GameParam.Gamemode;
		if (mode == null || Faction.FindByName("Corruptrons") == null)
		{
			return false;
		}
		string name = mode.shortCodename ?? mode.codename ?? string.Empty;
		return name.IndexOf("world_editor", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("credit", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("tutorial", StringComparison.OrdinalIgnoreCase) < 0;
	}

	private static void Place(Asset_Map map)
	{
		System.Random random = new System.Random((map.codename ?? "map").GetHashCode() ^ System.Environment.TickCount);
		int count;
		int away;
		switch (map.mapSize)
		{
			case Asset_Map.MapSize.Small:
				count = 3 + random.Next(2);
				away = 55;
				break;
			case Asset_Map.MapSize.Large:
				count = 8 + random.Next(3);
				away = 90;
				break;
			default:
				count = 5 + random.Next(3);
				away = 75;
				break;
		}
		List<Vector3> starts = Positions("Player Starting Point");
		List<Vector3> spawns = Positions("Corruptron Spawn Point");
		List<Vector3> spots = Spots(starts, spawns, away, count, random);
		if (spots.Count == 0)
		{
			Plugin.Log.LogInfo("Treasure: no place for a chest on this map");
			return;
		}
		Faction enemy = Faction.FindByName("Corruptrons") ?? Faction.DefaultEnemy;
		for (int i = 0; i < spots.Count; i++)
		{
			Chest chest = new Chest { Position = TreasureRuins.Build(spots[i], random), Guards = MinGuards + random.Next(MaxGuards - MinGuards + 1), Made = false };
			Chests.Add(chest);
			Guard(chest, enemy, random);
		}
		Plugin.Log.LogInfo("Treasure: placed " + Chests.Count + " chests (" + map.mapSize + " map)");
	}

	private static List<Vector3> Positions(string group)
	{
		List<Vector3> positions = new List<Vector3>();
		List<LuaMarker> markers = LuaMarker.GetGroup(group);
		if (markers != null)
		{
			foreach (LuaMarker marker in markers)
			{
				if (marker != null)
				{
					positions.Add(marker.transform.position);
				}
			}
		}
		return positions;
	}

	private static float Nearest(Vector3 at, List<Vector3> others)
	{
		float best = float.MaxValue;
		foreach (Vector3 other in others)
		{
			float dx = other.x - at.x;
			float dz = other.z - at.z;
			best = Mathf.Min(best, dx * dx + dz * dz);
		}
		return (best == float.MaxValue) ? 100000f : Mathf.Sqrt(best);
	}

	// Flat, open, free ground (a 5x5 square within one block of height, nothing standing on it, land around it), far
	// from the starting points and spawn points and from each other.
	private static List<Vector3> Spots(List<Vector3> starts, List<Vector3> spawns, int away, int count, System.Random random)
	{
		HeightmapLayer heightmap = ((LayerVoxelEngine)Voxel.Engine).test.layers.GetLayer<HeightmapLayer>();
		Color32[] columns = heightmap.fillingTex.GetPixels32();
		int width = heightmap.fillingTex.Width;
		int height = heightmap.fillingTex.Height;
		List<Vector3> candidates = new List<Vector3>();
		for (int z = 8; z < height - 8; z += 3)
		{
			for (int x = 8; x < width - 8; x += 3)
			{
				Color32 c = columns[x + z * width];
				if (c.g <= c.r || !Flat(columns, width, x, z, c.g))
				{
					continue;
				}
				Vector3 at = new Vector3(x, c.g, z);
				if (Nearest(at, starts) < away || Nearest(at, spawns) < 25f || !Open(x, c.g, z))
				{
					continue;
				}
				candidates.Add(at);
			}
		}
		List<Vector3> spots = new List<Vector3>();
		for (int separation = 70; separation >= 30 && spots.Count < count; separation -= 20)
		{
			// Shuffled, then each candidate far enough from those taken.
			for (int i = candidates.Count - 1; i > 0; i--)
			{
				int j = random.Next(i + 1);
				Vector3 t = candidates[i];
				candidates[i] = candidates[j];
				candidates[j] = t;
			}
			foreach (Vector3 candidate in candidates)
			{
				if (spots.Count >= count)
				{
					break;
				}
				if (Nearest(candidate, spots) >= separation)
				{
					spots.Add(candidate);
				}
			}
		}
		return spots;
	}

	private static bool Flat(Color32[] columns, int width, int x, int z, byte top)
	{
		for (int dz = -2; dz <= 2; dz++)
		{
			for (int dx = -2; dx <= 2; dx++)
			{
				Color32 c = columns[x + dx + (z + dz) * width];
				if (c.g <= c.r || Mathf.Abs(c.g - top) > 1)
				{
					return false;
				}
			}
		}
		// Not at a coast.
		for (int k = 0; k < 4; k++)
		{
			int nx = x + ((k == 0) ? 6 : ((k == 1) ? -6 : 0));
			int nz = z + ((k == 2) ? 6 : ((k == 3) ? -6 : 0));
			Color32 c = columns[nx + nz * width];
			if (c.g <= c.r)
			{
				return false;
			}
		}
		return true;
	}

	// Nothing built, dug or growing on the 3x3 around the spot.
	private static bool Open(int x, int y, int z)
	{
		for (int dz = -1; dz <= 1; dz++)
		{
			for (int dx = -1; dx <= 1; dx++)
			{
				XYZ ground = new XYZ(x + dx, y - 1, z + dz);
				XYZ above = new XYZ(x + dx, y, z + dz);
				if (Voxel.IsEmpty(ground) || !Voxel.IsEmpty(above) || !Voxel.IsEmpty(above + XYZ.up) || NatureRegistry.HasNatureAt(above))
				{
					return false;
				}
			}
		}
		return true;
	}

	private static GameObject SpawnChest(Vector3 at, System.Random random)
	{
		GameObject go = Transactor.SpawnUNet(ObjetsDynamiques.Chest, Faction.DefaultNeutral, at, Quaternion.Euler(0f, 90f * random.Next(4), 0f));
		Settle(go, at);
		Hold(go);
		return go;
	}

	// Puts the chest's model (not its transform, which is off its middle and bottom) with its bottom's middle on the
	// ground at the spot: the ground's height from a short ray down from just above it (not from the trees over it).
	private static void Settle(GameObject go, Vector3 at)
	{
		if (go == null)
		{
			return;
		}
		float ground = at.y;
		float best = float.MinValue;
		foreach (RaycastHit hit in Physics.RaycastAll(new Vector3(at.x, at.y + 0.75f, at.z), Vector3.down, 2f))
		{
			if (hit.collider == null || hit.collider.isTrigger || hit.collider.transform.IsChildOf(go.transform) || hit.collider.GetComponentInParent<Labor>() != null)
			{
				continue;
			}
			if (hit.point.y > best)
			{
				best = hit.point.y;
			}
		}
		if (best > float.MinValue)
		{
			ground = best;
		}
		Renderer model = go.GetComponent<Renderer>();
		if (model == null)
		{
			return;
		}
		Bounds bounds = model.bounds;
		Vector3 shift = new Vector3(at.x - bounds.center.x, ground - bounds.min.y, at.z - bounds.center.z);
		go.transform.position += shift;
		Plugin.Log.LogInfo("Treasure: chest at " + at + " on the ground at " + ground.ToString("F2") + ((best > float.MinValue) ? " (ray)" : " (no ray hit)") + ", model moved by " + shift);
	}

	// The chest stays where it was put: its body does not fall or get pushed (a chest pushed by the beasts, or set on
	// the smooth surface layer, fell through the ground).
	private static void Hold(GameObject go)
	{
		Rigidbody body = (go != null) ? go.GetComponent<Rigidbody>() : null;
		if (body != null)
		{
			body.velocity = Vector3.zero;
			body.isKinematic = true;
		}
	}

	// The chest's beasts round it, each of a random kind, all in one raid whose beacon is the chest.
	private static void Guard(Chest chest, Faction enemy, System.Random random)
	{
		if (enemy == null)
		{
			return;
		}
		RaidProject raid = null;
		for (int i = 0; i < chest.Guards; i++)
		{
			BeastKind kind = (BeastKind)random.Next(BeastKinds.All.Length);
			float angle = (float)(random.NextDouble() * Math.PI * 2.0);
			// Outside the ruin's walls (TreasureRuins.Radius).
			float distance = TreasureRuins.Radius + 3f + (float)random.NextDouble() * (2f + chest.Guards * 0.5f);
			int x = Mathf.RoundToInt(chest.Position.x + Mathf.Cos(angle) * distance);
			int z = Mathf.RoundToInt(chest.Position.z + Mathf.Sin(angle) * distance);
			int y = GroundAbove(x, z, Mathf.RoundToInt(chest.Position.y));
			Labor labor = Beasts.Spawn(kind, enemy, new Vector3(x, y, z), 1);
			if (labor == null)
			{
				continue;
			}
			if (raid == null)
			{
				raid = NewRaid(enemy, chest.Position);
				if (raid == null)
				{
					return;
				}
			}
			if (raid.IsFull())
			{
				raid.MaximumCrewSize++;
			}
			labor.Autonomy = raid.Hire(labor) ? AutonomyStatus.LockedToOperable : AutonomyStatus.InstinctOnly;
		}
	}

	// The first empty y above solid ground near the chest's height.
	private static int GroundAbove(int x, int z, int near)
	{
		for (int y = near + 4; y > near - 6; y--)
		{
			if (!Voxel.IsEmpty(new XYZ(x, y - 1, z)) && Voxel.IsEmpty(new XYZ(x, y, z)) && Voxel.IsEmpty(new XYZ(x, y + 1, z)))
			{
				return y;
			}
		}
		return near;
	}

	private static RaidProject NewRaid(Faction enemy, Vector3 target)
	{
		GameObject go = Transactor.SpawnUNet(Groupes.RaidProjectAI, enemy, Vector3.zero, Quaternion.identity);
		RaidProject raid = (go != null) ? go.GetComponent<RaidProject>() : null;
		if (raid == null)
		{
			return null;
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
		go.name = GuardRaidName;
		return raid;
	}

	// For the DevTools: position, guards set, opened, and whether its guards are near.
	internal static IEnumerable<object[]> Describe()
	{
		foreach (Chest chest in Chests)
		{
			Vector3 at = (chest.Go != null) ? chest.Go.transform.position : chest.Position;
			yield return new object[] { at, chest.Guards, chest.Opened, !chest.Opened && Guarded(at) };
		}
	}

	// Where the treasure chests stand (their ruins are neutral blocks round them).
	internal static void ChestPositions(List<Vector3> result)
	{
		foreach (Chest chest in Chests)
		{
			result.Add(chest.Position);
		}
	}

	internal static bool IsGuardRaid(GameObject go)
	{
		return go != null && go.name == GuardRaidName;
	}

	// ---- the leash (host)

	// How far a beast may go from its chest (the nearest one not yet opened) before it is sent back.
	private const float LeashRadius = 30f;

	private const float LeashSeconds = 8f;

	private static readonly Dictionary<Labor, float> SentBackAt = new Dictionary<Labor, float>();

	// A beast further than LeashRadius from its chest walks back to it (a guard that wandered off, or chased a unit).
	private static void Leash()
	{
		float now = Time.time;
		foreach (Labor beast in Beasts.All)
		{
			if (beast == null || !Beasts.IsAliveBeast(beast))
			{
				continue;
			}
			Vector3 at = beast.transform.position;
			Chest home = null;
			float best = float.MaxValue;
			foreach (Chest chest in Chests)
			{
				if (chest.Opened)
				{
					continue;
				}
				float distance = Vector3.Distance(at, chest.Position);
				if (distance < best)
				{
					best = distance;
					home = chest;
				}
			}
			if (home == null || best <= LeashRadius || (SentBackAt.TryGetValue(beast, out float sent) && now < sent + LeashSeconds))
			{
				continue;
			}
			SentBackAt[beast] = now;
			Order.Move.WalkToward.Send(beast, Location.Of(XYZ.FromVector3(home.Position)), Labor.DecisionSource.Project);
		}
	}

	// ---- opening (host)

	private static void Check()
	{
		Live<Labor>.Active(Units);
		Leash();
		foreach (Chest chest in Chests)
		{
			if (chest.Opened)
			{
				continue;
			}
			if (!chest.Made)
			{
				if (TreasureRuins.Busy)
				{
					continue;
				}
				chest.Go = SpawnChest(chest.Position, new System.Random());
				chest.Made = true;
			}
			if (chest.Go == null || !chest.Go.activeInHierarchy)
			{
				chest.Go = FindChest(chest.Position);
				if (chest.Go == null)
				{
					continue;
				}
			}
			// A chest that fell (through the ground, or pushed off) is put back.
			if (chest.Go.transform.position.y < chest.Position.y - 3f)
			{
				Plugin.Log.LogWarning("Treasure: the chest at " + chest.Position + " fell to " + chest.Go.transform.position + ", put back");
				chest.Go.transform.position = chest.Position;
				Settle(chest.Go, chest.Position);
				Hold(chest.Go);
			}
			Vector3 at = chest.Go.transform.position;
			if (Guarded(at))
			{
				HintGuarded(chest, at);
				continue;
			}
			Labor opener = Opener(at);
			if (opener != null)
			{
				Open(chest, opener);
			}
		}
	}

	// A bricktron of this player came to a chest its guardians still guard: say why it does not open.
	private static void HintGuarded(Chest chest, Vector3 at)
	{
		if (Time.unscaledTime - chest.HintedAt < HintSeconds)
		{
			return;
		}
		Labor opener = Opener(at);
		NetworkIdentity identity = (opener != null) ? opener.GetComponent<NetworkIdentity>() : null;
		if (identity == null)
		{
			return;
		}
		chest.HintedAt = Time.unscaledTime;
		NetworkServer.SendToAll(HintMessage, new UnityEngine.Networking.NetworkSystem.IntegerMessage((int)identity.netId.Value));
	}

	// The hint on the peer whose bricktron came to the chest.
	private static void HintReceived(NetworkMessage message)
	{
		try
		{
			uint id = (uint)message.ReadMessage<UnityEngine.Networking.NetworkSystem.IntegerMessage>().value;
			GameObject go = ClientScene.FindLocalObject(new NetworkInstanceId(id));
			Labor labor = (go != null) ? go.GetComponent<Labor>() : null;
			Faction local = (User.LocalUser != null) ? User.LocalUser.faction : null;
			if (labor != null && local != null && labor.faction == local)
			{
				GameSignals.Invoke(GameSignals.OnAutoFadeDialog, HintText, 3f);
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("Treasure: hint: " + ex.Message);
		}
	}

	private static bool Guarded(Vector3 at)
	{
		foreach (Labor labor in Units)
		{
			if (Beasts.IsAliveBeast(labor) && (labor.transform.position - at).sqrMagnitude <= GuardRadius * GuardRadius)
			{
				return true;
			}
		}
		return false;
	}

	// A living bricktron of a player (not an AI faction) next to the chest.
	private static Labor Opener(Vector3 at)
	{
		foreach (Labor labor in Units)
		{
			if (labor == null || labor.faction == null || labor.faction.isAI || labor.faction.IsSpectator || !labor.gameObject.activeInHierarchy || !GiantBricktron.IsAlive(labor) || Beasts.KindOf(labor) != null)
			{
				continue;
			}
			if ((labor.transform.position - at).sqrMagnitude <= OpenDistance * OpenDistance)
			{
				return labor;
			}
		}
		return null;
	}

	private static void Open(Chest chest, Labor opener)
	{
		chest.Opened = true;
		Vector3 at = chest.Go.transform.position;
		ObjectPoolSingleton.Release(chest.Go);
		chest.Go = null;
		List<GameObject> loot = new List<GameObject>();
		System.Random random = new System.Random();
		// The loot grows with the pack that guarded it: more of everything, and the rarer things only from bigger packs.
		int g = chest.Guards;
		Drop(loot, BuildResources.IronIngot, 2 * g + random.Next(3), at);
		Drop(loot, RawResources.RawBlueCrystal, g + random.Next(2), at);
		Drop(loot, RawResources.RawOrangeCrystal, g / 2 + random.Next(2), at);
		if (g >= 5)
		{
			Drop(loot, BuildResources.Cog, (g - 3) / 2 + random.Next(2), at);
		}
		if (g >= 8)
		{
			Drop(loot, BuildResources.PurifiedBrimstone, g - 6 + random.Next(2), at);
		}
		if (Plugin.IsEnabled(Features.DarkCrystals))
		{
			Drop(loot, DarkCrystals.Key, 2 * g + random.Next(4), at);
		}
		// Soldier kits (one bundle per kit): one per 3 guards, knight or archer, the rarer classes from 6 guards.
		for (int i = 0; i < g / 3; i++)
		{
			KitBundles.Bundle bundle = KitBundles.All[random.Next((g >= 6) ? KitBundles.All.Length : 2)];
			Drop(loot, bundle.Key, 1, at);
		}
		if (g >= 4)
		{
			Drop(loot, BuildResources.Bomb, (g - 2) / 2 + random.Next(2), at);
		}
		if (g >= 7)
		{
			Drop(loot, ObjetsDynamiques.Baril, 1 + random.Next(2), at);
		}
		Recepteur.ScatterObjects(loot, at);
		Plugin.Log.LogInfo("Treasure: " + opener.gameObject.name + " opened a chest of " + g + " guards at " + at + " (" + loot.Count + " items)");
	}

	private static void Drop(List<GameObject> loot, Factory.AssetKey key, int count, Vector3 at)
	{
		for (int i = 0; i < count; i++)
		{
			GameObject go = Transactor.SpawnUNet(key, null, at + Vector3.up * 0.5f, Quaternion.identity);
			if (go != null)
			{
				loot.Add(go);
			}
		}
	}

	// ---- the save

	private static GameObject FindChest(Vector3 at)
	{
		foreach (FactoryImprint imprint in FindObjectsOfType<FactoryImprint>())
		{
			if (imprint.AssetKey != null && imprint.AssetKey.Name == ObjetsDynamiques.Chest.Name && imprint.AssetKey.Factory == ObjetsDynamiques.Chest.Factory && (imprint.transform.position - at).sqrMagnitude < 2.5f * 2.5f && imprint.gameObject.activeInHierarchy)
			{
				return imprint.gameObject;
			}
		}
		return null;
	}

	// The chests the game itself brought back with the save are used; missing ones are spawned again.
	private static void FindOrSpawnChests()
	{
		System.Random random = new System.Random();
		foreach (Chest chest in Chests)
		{
			if (chest.Opened)
			{
				continue;
			}
			chest.Go = FindChest(chest.Position) ?? SpawnChest(chest.Position, random);
		}
	}

	private static void Load(string file)
	{
		foreach (JToken token in JArray.Parse(File.ReadAllText(file)))
		{
			Chests.Add(new Chest
			{
				Position = new Vector3((float)token["x"], (float)token["y"], (float)token["z"]),
				// Saves from before the guard count had a tier of 1 to 3.
				Guards = (token["guards"] != null) ? (int)token["guards"] : 3 * (int)token["tier"],
				Opened = (bool)token["opened"]
			});
		}
	}

	// Server: called when the game has written a save into its folder.
	internal static void Write(string folder)
	{
		if (!_active)
		{
			return;
		}
		JArray chests = new JArray();
		foreach (Chest chest in Chests)
		{
			Vector3 at = (chest.Go != null && chest.Go.activeInHierarchy) ? chest.Go.transform.position : chest.Position;
			chests.Add(new JObject { ["x"] = at.x, ["y"] = at.y, ["z"] = at.z, ["guards"] = chest.Guards, ["opened"] = chest.Opened });
		}
		try
		{
			File.WriteAllText(System.IO.Path.Combine(folder, SaveFile), chests.ToString());
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("Treasure: could not save the chests: " + ex.Message);
		}
	}
}

// Saves the chests with the game.
[Feature(Features.Treasure, Features.TreasureInfo)]
[HarmonyPatch(typeof(Asset_Map), "Disk_Save")]
internal static class TreasureSavePatch
{
	private static void Postfix(Asset_Map __instance)
	{
		if (NetworkServer.active && !string.IsNullOrEmpty(__instance.path))
		{
			TreasureChests.Write(__instance.path);
		}
	}
}

// A guard raid that ends (its beasts all dead) is no invasion wave survived: the gamemode's destroy hook is not
// called for it. Its pooled object loses the guard name, so a raid that reuses it is an ordinary one.
[Feature(Features.Treasure, Features.TreasureInfo)]
[HarmonyPatch(typeof(LuaDriver), "InvokeTypeDestroy")]
internal static class TreasureGuardRaidDestroyPatch
{
	private static bool Prefix(LuaDriver __instance)
	{
		if (!TreasureChests.IsGuardRaid(__instance.gameObject))
		{
			return true;
		}
		__instance.gameObject.name = "RaidProjectAI";
		return false;
	}
}

// Guard raids are not counted as living enemy raids (invasion waits for none to play its "safe" music).
[Feature(Features.Treasure, Features.TreasureInfo)]
[HarmonyPatch(typeof(LuaStaticDriver), "CountOfFaction", new[] { typeof(MoonSharp.Interpreter.ScriptExecutionContext), typeof(MoonSharp.Interpreter.CallbackArguments) })]
internal static class TreasureGuardRaidCountPatch
{
	private static void Postfix(MoonSharp.Interpreter.CallbackArguments args, ref MoonSharp.Interpreter.DynValue __result)
	{
		LuaStaticDriver driver = args.AsUserData<LuaStaticDriver>(0, "CountOfFaction");
		Faction faction = args.AsComponent<Faction>(1, "CountOfFaction");
		if (driver == null || ReferenceEquals(__result, null))
		{
			return;
		}
		int guards = 0;
		foreach (LuaDriver instance in driver._allInstances)
		{
			if (instance != null && instance.faction == faction && TreasureChests.IsGuardRaid(instance.gameObject))
			{
				guards++;
			}
		}
		if (guards > 0)
		{
			__result = MoonSharp.Interpreter.DynValue.NewNumber(__result.Number - guards);
		}
	}
}

// The guards roam round their chest: the game's raid patrol (walks to random spots within the beacon's radius) is
// switched off for raids (RaidProject.DoesPatrol), and is on for the guard raids.
[Feature(Features.Treasure, Features.TreasureInfo)]
[HarmonyPatch(typeof(RaidProject), "DoesPatrol", MethodType.Getter)]
internal static class TreasureGuardPatrolPatch
{
	private static void Postfix(RaidProject __instance, ref bool __result)
	{
		if (!__result && __instance != null && TreasureChests.IsGuardRaid(__instance.gameObject))
		{
			__result = true;
		}
	}
}

// The guard raid's zone is a circle of RoamRadius round the chest, whatever its beacon's radius: a guard inside it
// patrols (random spots in it), one outside walks back. The raid's own beacon radius was tiny, so the guards never
// counted as there and kept walking at the chest, hitting what stood in their way.
[Feature(Features.Treasure, Features.TreasureInfo)]
[HarmonyPatch]
internal static class TreasureGuardZonePatch
{
	private const float RoamRadius = 9f;

	[HarmonyPatch(typeof(RaidProject2), "IsInZone")]
	[HarmonyPostfix]
	private static void InZone(RaidProject2 __instance, Labor labor, ref bool __result)
	{
		if (labor != null && TreasureChests.IsGuardRaid(__instance.gameObject))
		{
			__result = Vector3.Distance(__instance.BalisePosition.ToVector3(), labor.transform.position) < RoamRadius;
		}
	}

	// Always patrol, also from outside the zone: the raid's walk to its beacon (the chest's own cell) ended at the chest
	// in the way, which the guards then broke open to get through.
	[HarmonyPatch(typeof(RaidProject2), "_GetInstruction")]
	[HarmonyPrefix]
	private static bool AlwaysPatrol(RaidProject2 __instance, Labor labor, global::Var<OrderPackage<Labor>> result)
	{
		if (labor == null || __instance.Patrol == null || !TreasureChests.IsGuardRaid(__instance.gameObject))
		{
			return true;
		}
		// Near the chest a guard mostly stands about for a while before it wanders on.
		bool near = Vector3.Distance(__instance.BalisePosition.ToVector3(), labor.transform.position) < RoamRadius;
		OrderPackage<Labor> package = (near && UnityEngine.Random.value < 0.65f)
			? (OrderPackage<Labor>)Order.Package.OfFloat(Order.ForLabor.Wait, UnityEngine.Random.Range(3f, 9f))
			: Order.Package.Of(__instance.Patrol, Labor.DecisionSource.Project);
		package.SetPrepare((BaseLabor l) => __instance.Hire(l as Labor));
		result.Set(package);
		return false;
	}

	// A guardian never breaks an object out of its way (the game's walk breaks what blocks it, the chest too).
	[HarmonyPatch(typeof(Brix.Game.AI.Nodes.NavigationNodes), nameof(Brix.Game.AI.Nodes.NavigationNodes.BreakObject))]
	[HarmonyPrefix]
	private static bool NoBreaking(Labor labor, ref Motus.Behavior.Node __result)
	{
		if (Beasts.KindOf(labor) == null)
		{
			return true;
		}
		__result = Motus.Behavior.Node.Empty;
		return false;
	}

	[HarmonyPatch(typeof(RaidProject2), "PatrolNode")]
	[HarmonyPrefix]
	private static bool Patrol(RaidProject2 __instance, Labor labor, ref Motus.Behavior.Node __result)
	{
		if (labor == null || !TreasureChests.IsGuardRaid(__instance.gameObject))
		{
			return true;
		}
		__result = Brix.Game.AI.Nodes.NavigationNodes.WalkToExactly(labor, Location.RandomAround(__instance.BalisePosition, RoamRadius - 1f, labor));
		return false;
	}
}
