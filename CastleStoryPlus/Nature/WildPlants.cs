using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Engine;
using Brix.Components;
using Brix.Engine.Nature;
using Brix.Legacy;
using Brix.Network;
using CastleStoryPlus.Core;
using CastleStoryPlus.Weather;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Nature;

// Plants (the ones that give fibre when harvested) seed themselves in open grass: now and then (every SeedSeconds of
// game time) the host tries [WildPlants] Tries columns of the world at random, and a column gets a young plant when
// the 5x5 square round it is open grass (no block on it, no tree, nobody walking it: WornPaths), and there is no plant
// next to it and at most MaxNear in the 7x7 round it, so meadows fill up thinly and stop. The plant kinds are drawn as
// the random world draws them on grass. The game saves the plants with the map; the host sends each new plant to the
// other players (message PlantMessage, which the game itself does not use). The game's plants never grew: they kept
// the size they were made with. Now every plant smaller than full size grows to full size in [WildPlants] GrowMinutes
// of game time; every peer grows its own plants (the same ones), and the game saves their size with the map.
[Feature(Features.WildPlants, Features.WildPlantsInfo)]
internal sealed class WildPlants : MonoBehaviour
{
	private const short PlantMessage = 200;

	private const float SeedSeconds = 60f;

	// The open grass round a new plant: 2 columns to every side (5x5).
	private const int OpenRadius = 2;

	private const int NearRadius = 3;

	private const int MaxNear = 2;

	// The plant kinds 0-5 and how often each is drawn (the random world's grass weights).
	private static readonly int[] Weights = new[] { 60, 15, 5, 3, 15, 2 };

	private const float GrowSeconds = 30f;

	private static ConfigEntry<int> _tries;

	private static ConfigEntry<float> _growMinutes;

	private float _nextGrow;

	private static readonly System.Random Random = new System.Random();

	private static bool _active;

	private float _next;

	private sealed class PlantsMessage : MessageBase
	{
		public readonly List<XYZ> Positions = new List<XYZ>();

		public readonly List<int> Kinds = new List<int>();

		public readonly List<float> Growths = new List<float>();

		public override void Serialize(NetworkWriter writer)
		{
			writer.Write(Positions.Count);
			for (int i = 0; i < Positions.Count; i++)
			{
				writer.Write(Positions[i].x);
				writer.Write(Positions[i].y);
				writer.Write(Positions[i].z);
				writer.Write(Kinds[i]);
				writer.Write(Growths[i]);
			}
		}

		public override void Deserialize(NetworkReader reader)
		{
			int count = reader.ReadInt32();
			for (int i = 0; i < count; i++)
			{
				Positions.Add(new XYZ(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()));
				Kinds.Add(reader.ReadInt32());
				Growths.Add(reader.ReadSingle());
			}
		}
	}

	private static void Enable()
	{
		_tries = Plugin.Cfg.Bind("WildPlants", "Tries", 300, new ConfigDescription("Columns tried each game minute for a new plant (more: meadows fill up faster); 0: no new plants.", new AcceptableValueRange<int>(0, 5000)));
		_growMinutes = Plugin.Cfg.Bind("WildPlants", "GrowMinutes", 10f, new ConfigDescription("Game minutes a plant takes from nothing to full size; 0: plants keep their size.", new AcceptableValueRange<float>(0f, 600f)));
		GameSession.OnLevelReady(LevelReady);
		GameSession.OnLeave(() => _active = false);
		Plugin.Root.AddComponent<WildPlants>();
	}

	private static void LevelReady()
	{
		_active = VoxelEngine.Ready;
		NetworkClient client = UNetManager.instance?.LocalNetworkClient;
		if (client != null)
		{
			// The host's own client gets the message too (it has added the plants already).
			if (client.handlers.ContainsKey(PlantMessage))
			{
				client.UnregisterHandler(PlantMessage);
			}
			client.RegisterHandler(PlantMessage, Received);
		}
	}

	private void Update()
	{
		if (!_active)
		{
			return;
		}
		if (Time.time >= _nextGrow)
		{
			_nextGrow = Time.time + GrowSeconds;
			try
			{
				Grow();
			}
			catch (Exception ex)
			{
				Plugin.Log.LogWarning("WildPlants: " + ex.Message);
			}
		}
		if (!NetworkServer.active || Time.time < _next)
		{
			return;
		}
		_next = Time.time + SeedSeconds;
		try
		{
			Seed();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("WildPlants: " + ex.Message);
		}
	}

	private static void Seed()
	{
		if (_tries.Value <= 0 || !(VoxelEngine.Impl is Brix.Engine.FlatWorld.Layers.LayerVoxelEngine engine))
		{
			return;
		}
		XYZ start = engine.test.worldStart;
		XYZ size = engine.test.worldSize;
		if (size.x <= 2 * NearRadius || size.z <= 2 * NearRadius)
		{
			return;
		}
		PlantsMessage message = new PlantsMessage();
		// Rain brings more tries.
		int tries = (int)(_tries.Value * Rain.PlantBoost);
		for (int i = 0; i < tries; i++)
		{
			int x = start.x + NearRadius + Random.Next(size.x - 2 * NearRadius);
			int z = start.z + NearRadius + Random.Next(size.z - 2 * NearRadius);
			int top = VoxelEngine.GetColumnTop(x, z);
			if (!WornPaths.IsGrass(VoxelEngine.GetColumnType(x, z)) || !OpenGrass(x, z, top) || Crowded(x, z, top))
			{
				continue;
			}
			XYZ at = new XYZ(x, top - 1, z);
			if (NatureRegistry.HasNatureAt(at))
			{
				continue;
			}
			int kind = Kind();
			float growth = (float)Math.Round(0.1 + Random.NextDouble() * 0.4, 3);
			PlantRegistry.Add(at, kind, growth);
			message.Positions.Add(at);
			message.Kinds.Add(kind);
			message.Growths.Add(growth);
		}
		if (message.Positions.Count == 0)
		{
			return;
		}
		Plugin.Log.LogInfo("WildPlants: " + message.Positions.Count + " new plant(s) in open grass");
		NetworkServer.SendToAll(PlantMessage, message);
	}

	// Every plant below full size grows a step.
	private static void Grow()
	{
		if (_growMinutes.Value <= 0f)
		{
			return;
		}
		float step = GrowSeconds / 60f / _growMinutes.Value * Rain.PlantBoost;
		List<KeyValuePair<XYZ, PlantData>> growing = new List<KeyValuePair<XYZ, PlantData>>();
		foreach (KeyValuePair<XYZ, PlantData> plant in BrixSingleton<PlantRegistry>.Instance._data)
		{
			if (plant.Value != null && plant.Value.Growth < 1f)
			{
				growing.Add(plant);
			}
		}
		foreach (KeyValuePair<XYZ, PlantData> plant in growing)
		{
			PlantRegistry.AddOrGrow(plant.Key, plant.Value.PlantType, Mathf.Min(step, 1f - plant.Value.Growth));
		}
	}

	// The 5x5 round the column: grass about level with it (a slope, not a cliff), open on top, no tree, nobody walking.
	private static bool OpenGrass(int x, int z, int top)
	{
		for (int dz = -OpenRadius; dz <= OpenRadius; dz++)
		{
			for (int dx = -OpenRadius; dx <= OpenRadius; dx++)
			{
				int cx = x + dx;
				int cz = z + dz;
				int ctop = VoxelEngine.GetColumnTop(cx, cz);
				if (Math.Abs(ctop - top) > 1 || !WornPaths.IsGrass(VoxelEngine.GetColumnType(cx, cz)) || WornPaths.Trodden(cx, cz))
				{
					return false;
				}
				XYZ crust = new XYZ(cx, ctop - 1, cz);
				if (Voxel.IsEmpty(crust) || !Voxel.IsEmpty(new XYZ(cx, ctop, cz)) || TreeRegistry.Instance.HasTree(crust))
				{
					return false;
				}
			}
		}
		return true;
	}

	// A plant right next to the column, or more than MaxNear in the 7x7 round it.
	private static bool Crowded(int x, int z, int top)
	{
		int near = 0;
		for (int dz = -NearRadius; dz <= NearRadius; dz++)
		{
			for (int dx = -NearRadius; dx <= NearRadius; dx++)
			{
				int cx = x + dx;
				int cz = z + dz;
				if (!PlantRegistry.IsPlant(new XYZ(cx, VoxelEngine.GetColumnTop(cx, cz) - 1, cz)))
				{
					continue;
				}
				if (Math.Abs(dx) <= 1 && Math.Abs(dz) <= 1)
				{
					return true;
				}
				near++;
				if (near > MaxNear - 1)
				{
					return true;
				}
			}
		}
		return false;
	}

	private static int Kind()
	{
		int total = 0;
		foreach (int weight in Weights)
		{
			total += weight;
		}
		int roll = Random.Next(total);
		int kind = 0;
		while (roll >= Weights[kind])
		{
			roll -= Weights[kind];
			kind++;
		}
		return kind;
	}

	// A client: the host's new plants.
	private static void Received(NetworkMessage netMsg)
	{
		if (NetworkServer.active)
		{
			return;
		}
		PlantsMessage message = netMsg.ReadMessage<PlantsMessage>();
		for (int i = 0; i < message.Positions.Count; i++)
		{
			XYZ at = message.Positions[i];
			if (!PlantRegistry.IsPlant(at))
			{
				PlantRegistry.Add(at, message.Kinds[i], message.Growths[i]);
			}
		}
	}
}
