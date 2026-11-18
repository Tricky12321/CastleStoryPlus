using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using Brix.Assets;
using Brix.Engine;
using Brix.Engine.Nature;
using Brix.Game;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using CastleStoryPlus.Pathfinding;
using CastleStoryPlus.Weather;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Nature;

// Paths wear into the grass: every second each of the players' units that stepped onto another ground column (the
// map's own top, not a block, a bridge or the floor of a dug pit) leaves a step there. The steps fade with a half
// life of [WornPaths] HalfLifeMinutes; a grass column (grass, thick grass, clover) holding [WornPaths] Steps of them
// turns to earth. A worn column nobody walks any more grows its grass back over time, a few columns at a time so the
// path's edge goes ragged first. Columns with a plant, boulder or ore on them are left as they are. Grass also
// spreads onto bare earth beside it that nobody walks: the map is gone through a strip at a time (all of it every
// SweepSeconds), and an earth column next to grass up to a block above or below it (a slope, not a cliff) grows over in [WornPaths] GrassSpreadMinutes on
// average, also under plants and trees standing on it.
// Every peer works it out from the units it sees (the change is only the ground's look); the host saves the steps and
// the worn columns' old grass beside the save, and the game saves the ground itself with the map.
[Feature(Features.WornPaths, Features.WornPathsInfo)]
internal sealed class WornPaths : MonoBehaviour
{
	private const string SaveFile = "castlestoryplus_paths.json";

	private const float SampleSeconds = 1f;

	private const float TickSeconds = 30f;

	// A worn column with fewer steps than this may grow over again, with this chance each tick.
	private const float RegrowBelow = 1.5f;

	private const double RegrowChance = 0.2;

	private const float ForgetBelow = 0.05f;

	private const float SweepSeconds = 120f;

	// Grass spreads to a column at most this much higher or lower than its own (a slope, not a cliff).
	private const float MaxSpreadStep = 1f;

	private static ConfigEntry<int> _steps;

	private static ConfigEntry<float> _halfLife;

	private static ConfigEntry<float> _spreadMinutes;

	// Earth columns that grass grows onto, with its type, until the next tick puts them in the ground.
	private static readonly Dictionary<int, VoxelTerrainType> Spread = new Dictionary<int, VoxelTerrainType>();

	// Where the sweep for spreading grass is: the index of the next column in the world's box.
	private static int _cursor;

	private static float _sweepCarry;

	// Steps per column (x | z << 16), and the grass type each worn column had.
	private static readonly Dictionary<int, float> Wear = new Dictionary<int, float>();

	private static readonly Dictionary<int, VoxelTerrainType> Worn = new Dictionary<int, VoxelTerrainType>();

	private static readonly Dictionary<Labor, int> LastColumn = new Dictionary<Labor, int>();

	private static readonly List<Labor> Units = new List<Labor>();

	private static readonly System.Random Random = new System.Random();

	private static bool _active;

	private float _nextSample;

	private float _nextTick;

	private static void Enable()
	{
		_steps = Plugin.Cfg.Bind("WornPaths", "Steps", 25, new ConfigDescription("Steps (fading with the half life) on a grass column that wear it down to earth.", new AcceptableValueRange<int>(5, 500)));
		_halfLife = Plugin.Cfg.Bind("WornPaths", "HalfLifeMinutes", 7f, new ConfigDescription("Game minutes in which the steps on a column fade to half; worn paths nobody walks grow over in about five half lives.", new AcceptableValueRange<float>(1f, 120f)));
		_spreadMinutes = Plugin.Cfg.Bind("WornPaths", "GrassSpreadMinutes", 10f, new ConfigDescription("Game minutes (on average) in which bare earth beside grass that nobody walks grows over; 0: grass does not spread.", new AcceptableValueRange<float>(0f, 600f)));
		GameSession.OnLevelReady(LevelReady);
		GameSession.OnLeave(Clear);
		Plugin.Root.AddComponent<WornPaths>();
	}

	private static void Clear()
	{
		Wear.Clear();
		Worn.Clear();
		LastColumn.Clear();
		Spread.Clear();
		_cursor = 0;
		_sweepCarry = 0f;
		_active = false;
	}

	private static void LevelReady()
	{
		Clear();
		Asset_Map map = GameParam.Map;
		if (map == null || !VoxelEngine.Ready)
		{
			return;
		}
		if (NetworkServer.active && !map.IsNewGame() && !string.IsNullOrEmpty(map.path))
		{
			string file = System.IO.Path.Combine(map.path, SaveFile);
			if (File.Exists(file))
			{
				try
				{
					Load(file);
				}
				catch (Exception ex)
				{
					Plugin.Log.LogWarning("WornPaths: could not read the worn paths: " + ex.Message);
				}
			}
		}
		_active = true;
	}

	private void Update()
	{
		if (!_active || !VoxelEngine.Ready)
		{
			return;
		}
		try
		{
			if (Time.time >= _nextSample)
			{
				_nextSample = Time.time + SampleSeconds;
				Sample();
			}
			Sweep();
			if (Time.time >= _nextTick)
			{
				_nextTick = Time.time + TickSeconds;
				Tick();
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("WornPaths: " + ex.Message);
		}
	}

	// A step for every player's unit that came onto another column of the ground since the last look.
	private static void Sample()
	{
		Live<Labor>.Active(Units);
		foreach (Labor labor in Units)
		{
			Faction faction = Faction.GetFaction(labor.gameObject);
			if (faction == null || faction.isAI)
			{
				continue;
			}
			XYZ feet = XYZ.FromVector3(labor.transform.position);
			int key = Key(feet.x, feet.z);
			// A road keeps its ground from wearing: nobody's steps on it count.
			if (Roads.IsOn(labor.transform))
			{
				LastColumn[labor] = key;
				continue;
			}
			if (LastColumn.TryGetValue(labor, out int last) && last == key)
			{
				continue;
			}
			LastColumn[labor] = key;
			int top = VoxelEngine.GetColumnTop(feet.x, feet.z);
			if (feet.y < top - 1 || feet.y > top)
			{
				continue;
			}
			Wear.TryGetValue(key, out float steps);
			Wear[key] = steps + 1f;
		}
		Units.Clear();
		if (LastColumn.Count > 1000)
		{
			List<Labor> gone = new List<Labor>();
			foreach (Labor labor in LastColumn.Keys)
			{
				if (labor == null)
				{
					gone.Add(labor);
				}
			}
			foreach (Labor labor in gone)
			{
				LastColumn.Remove(labor);
			}
		}
	}

	// The steps fade; much trodden grass wears to earth, and worn columns nobody walks grow over again.
	private static void Tick()
	{
		float fade = Mathf.Pow(0.5f, TickSeconds / 60f / _halfLife.Value);
		List<int> keys = new List<int>(Wear.Keys);
		List<KeyValuePair<int, VoxelTerrainType>> changes = new List<KeyValuePair<int, VoxelTerrainType>>();
		foreach (int key in keys)
		{
			float steps = Wear[key] * fade;
			if (steps < ForgetBelow && !Worn.ContainsKey(key))
			{
				Wear.Remove(key);
				continue;
			}
			Wear[key] = steps;
			if (steps >= _steps.Value && !Worn.ContainsKey(key))
			{
				int x = key & 0xFFFF;
				int z = (key >> 16) & 0xFFFF;
				VoxelTerrainType type = VoxelEngine.GetColumnType(x, z);
				if (IsGrass(type) && NatureRegistry.NatureAt(x, z).Type == NatureType.none)
				{
					Worn[key] = type;
					changes.Add(new KeyValuePair<int, VoxelTerrainType>(key, VoxelTerrainType.Earth));
				}
			}
		}
		foreach (KeyValuePair<int, VoxelTerrainType> worn in new List<KeyValuePair<int, VoxelTerrainType>>(Worn))
		{
			Wear.TryGetValue(worn.Key, out float steps);
			if (steps >= RegrowBelow || Random.NextDouble() >= RegrowChance * Rain.GrassBoost)
			{
				continue;
			}
			Worn.Remove(worn.Key);
			Wear.Remove(worn.Key);
			int x = worn.Key & 0xFFFF;
			int z = (worn.Key >> 16) & 0xFFFF;
			// Only earth this made grows over (not ground that was turned to something else since).
			if (VoxelEngine.GetColumnType(x, z) == VoxelTerrainType.Earth)
			{
				changes.Add(new KeyValuePair<int, VoxelTerrainType>(worn.Key, worn.Value));
			}
		}
		foreach (KeyValuePair<int, VoxelTerrainType> grass in Spread)
		{
			if (!Worn.ContainsKey(grass.Key))
			{
				changes.Add(grass);
			}
		}
		Spread.Clear();
		if (changes.Count == 0)
		{
			return;
		}
		VoxelEngine.BeginChanges();
		try
		{
			foreach (KeyValuePair<int, VoxelTerrainType> change in changes)
			{
				VoxelEngine.SetColumnType(change.Key & 0xFFFF, (change.Key >> 16) & 0xFFFF, change.Value);
			}
		}
		finally
		{
			VoxelEngine.ApplyChanges();
		}
	}

	// Goes through the next strip of the world's columns: bare earth beside grass on its level, that nobody walks, may
	// grow over.
	private static void Sweep()
	{
		if (_spreadMinutes.Value <= 0f || !(VoxelEngine.Impl is Brix.Engine.FlatWorld.Layers.LayerVoxelEngine engine))
		{
			return;
		}
		XYZ start = engine.test.worldStart;
		XYZ size = engine.test.worldSize;
		int area = size.x * size.z;
		if (area <= 0)
		{
			return;
		}
		_sweepCarry += area * Time.deltaTime / SweepSeconds;
		int count = Mathf.Min((int)_sweepCarry, 20000);
		_sweepCarry -= count;
		// Rain makes it GrassBoost times more likely.
		double chance = SweepSeconds / 60.0 / _spreadMinutes.Value * Rain.GrassBoost;
		for (int i = 0; i < count; i++)
		{
			if (_cursor >= area)
			{
				_cursor = 0;
			}
			int x = start.x + _cursor % size.x;
			int z = start.z + _cursor / size.x;
			_cursor++;
			if (VoxelEngine.GetColumnType(x, z) != VoxelTerrainType.Earth || Random.NextDouble() >= chance)
			{
				continue;
			}
			int key = Key(x, z);
			if (Worn.ContainsKey(key) || Spread.ContainsKey(key) || (Wear.TryGetValue(key, out float steps) && steps >= RegrowBelow))
			{
				continue;
			}
			int top = VoxelEngine.GetColumnTop(x, z);
			// The map's own top must be there and open (not dug away, not under a block); plants and trees on it are fine.
			if (Voxel.IsEmpty(new XYZ(x, top - 1, z)) || !Voxel.IsEmpty(new XYZ(x, top, z)))
			{
				continue;
			}
			VoxelTerrainType grass = GrassBeside(x, z);
			if (grass != VoxelTerrainType.Empty)
			{
				Spread[key] = grass;
			}
		}
	}

	// The grass of a neighbouring column whose ground is at most a block higher or lower (up and down a slope of up to
	// 45 degrees, not up a cliff), or Empty.
	private static VoxelTerrainType GrassBeside(int x, int z)
	{
		float height = Surface(x, z);
		for (int d = 0; d < 4; d++)
		{
			int nx = x + ((d == 0) ? 1 : (d == 1) ? -1 : 0);
			int nz = z + ((d == 2) ? 1 : (d == 3) ? -1 : 0);
			VoxelTerrainType type = VoxelEngine.GetColumnType(nx, nz);
			if (IsGrass(type) && Math.Abs(Surface(nx, nz) - height) <= MaxSpreadStep)
			{
				return type;
			}
		}
		return VoxelTerrainType.Empty;
	}

	// The height of the ground's smooth top in the column: its top voxel and how full that is.
	private static float Surface(int x, int z)
	{
		VoxelEngine.GetColumnData(x, z, out int _, out int top, out VoxelTerrainType _, out float crust);
		return top - 1 + crust;
	}

	// Whether the players' units walk the column (its steps have not faded below a step and a half).
	internal static bool Trodden(int x, int z)
	{
		return Wear.TryGetValue(Key(x, z), out float steps) && steps >= RegrowBelow;
	}

	internal static bool IsGrass(VoxelTerrainType type)
	{
		return type == VoxelTerrainType.Grass || type == VoxelTerrainType.ThickGrass || type == VoxelTerrainType.Clover;
	}

	private static int Key(int x, int z)
	{
		return (x & 0xFFFF) | ((z & 0xFFFF) << 16);
	}

	private static void Load(string file)
	{
		JObject data = JObject.Parse(File.ReadAllText(file));
		foreach (JToken token in (JArray)data["wear"] ?? new JArray())
		{
			Wear[Key((int)token[0], (int)token[1])] = (float)token[2];
		}
		foreach (JToken token in (JArray)data["worn"] ?? new JArray())
		{
			Worn[Key((int)token[0], (int)token[1])] = (VoxelTerrainType)(int)token[2];
		}
		Plugin.Log.LogInfo("WornPaths: " + Worn.Count + " worn columns and " + Wear.Count + " trodden ones from the save");
	}

	// Host: called when the game has written a save into its folder.
	internal static void Write(string folder)
	{
		if (!_active)
		{
			return;
		}
		JArray wear = new JArray();
		foreach (KeyValuePair<int, float> entry in Wear)
		{
			if (entry.Value >= 1f || Worn.ContainsKey(entry.Key))
			{
				wear.Add(new JArray(entry.Key & 0xFFFF, (entry.Key >> 16) & 0xFFFF, Math.Round(entry.Value, 2)));
			}
		}
		JArray worn = new JArray();
		foreach (KeyValuePair<int, VoxelTerrainType> entry in Worn)
		{
			worn.Add(new JArray(entry.Key & 0xFFFF, (entry.Key >> 16) & 0xFFFF, (int)entry.Value));
		}
		try
		{
			File.WriteAllText(System.IO.Path.Combine(folder, SaveFile), new JObject { ["wear"] = wear, ["worn"] = worn }.ToString(Newtonsoft.Json.Formatting.None));
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("WornPaths: could not save the worn paths: " + ex.Message);
		}
	}
}

// Saves the worn paths with the game.
[Feature(Features.WornPaths, Features.WornPathsInfo)]
[HarmonyPatch(typeof(Asset_Map), "Disk_Save")]
internal static class WornPathsSavePatch
{
	private static void Postfix(Asset_Map __instance)
	{
		if (NetworkServer.active && !string.IsNullOrEmpty(__instance.path))
		{
			WornPaths.Write(__instance.path);
		}
	}
}
