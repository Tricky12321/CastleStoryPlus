using System;
using System.IO;
using BepInEx.Configuration;
using Brix.Assets;
using Brix.Engine;
using Brix.Network;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Weather;

// Rain as a natural event. The host rolls it: every in-game hour (the game's clock, Journee.Heure, so a pause or a
// faster game speed counts) there is a chance that makes it start on average every [Rain] DaysBetween days
// (counting from the end of the last one, with a rest of 6 hours after each), and it lasts about [Rain] Hours game
// hours (60% to 140% of that). What falls follows the ground: a world whose ground is mostly sand has no rain, one
// whose ground is mostly snow has snow, any other has rain. While it rains grass spreads and worn paths grow over
// faster ([Rain] GrassBoost: WornPaths) and plants seed and grow faster ([Rain] PlantBoost: WildPlants); snow only
// shows. The game has no fire to put out, and rain does not slow bricktrons. The host tells the other players
// (message WeatherMessage, on every change and every few seconds for a player who joined) and saves the weather
// beside the world's save. RainVisuals shows it.
[Feature(Features.Rain, Features.RainInfo)]
internal sealed class Rain : MonoBehaviour
{
	// The game's own messages end at 63; Castle Story Plus uses 200-208.
	private const short WeatherMessage = 209;

	private const string SaveFile = "castlestoryplus_weather.json";

	private const float ResendSeconds = 30f;

	private static ConfigEntry<float> _daysBetween;

	private static ConfigEntry<float> _hours;

	private static ConfigEntry<float> _grassBoost;

	private static ConfigEntry<float> _plantBoost;

	private static readonly System.Random Random = new System.Random();

	private static bool _active;

	// The weather's state and roll (pure, see Logic/RainWeather.cs); this class is the adapter to the game.
	private static readonly RainWeather State = new RainWeather();

	private static WeatherKind _fall = WeatherKind.Rain;

	private static WeatherKind _sent;

	private static float _nextSend;

	// Whether a game is loaded and its weather runs.
	internal static bool Active => _active;

	internal static WeatherKind Current => State.Current;

	// Game hours left of what falls now.
	internal static float HoursLeft => State.HoursLeft;

	// What falls in this world when the weather comes by itself: Rain, Snow, or None (desert).
	internal static WeatherKind Fall => _fall;

	// How much faster grass spreads and paths grow over, and plants seed and grow, now.
	internal static float GrassBoost => RainRules.Boost(Current, (_grassBoost != null) ? _grassBoost.Value : (float?)null);

	internal static float PlantBoost => RainRules.Boost(Current, (_plantBoost != null) ? _plantBoost.Value : (float?)null);

	// A change of weather: from, to, and whether the players are told (not for the weather a save was loaded with).
	internal static event Action<WeatherKind, WeatherKind, bool> Changed;

	private sealed class WeatherNetMessage : MessageBase
	{
		public byte Kind;

		public float Hours;

		public override void Serialize(NetworkWriter writer)
		{
			writer.Write(Kind);
			writer.Write(Hours);
		}

		public override void Deserialize(NetworkReader reader)
		{
			Kind = reader.ReadByte();
			Hours = reader.ReadSingle();
		}
	}

	private static void Enable()
	{
		Plugin.Root.AddComponent<Rain>();
	}

	// The settings, bound whether the feature is on or not (the boosts are read by WornPaths and WildPlants either way).
	internal static void BindConfig()
	{
		_daysBetween = Plugin.Cfg.Bind("Rain", "DaysBetween", 3f, new ConfigDescription("Game days between rains on average (the host rolls it; 0: no rain by itself).", new AcceptableValueRange<float>(0f, 30f)));
		_hours = Plugin.Cfg.Bind("Rain", "Hours", 3f, new ConfigDescription("Game hours a rain lasts on average (each lasts 60% to 140% of this).", new AcceptableValueRange<float>(0.5f, 24f)));
		_grassBoost = Plugin.Cfg.Bind("Rain", "GrassBoost", 3f, new ConfigDescription("While it rains, grass spreads onto bare earth and worn paths grow over this many times faster (WornPaths).", new AcceptableValueRange<float>(1f, 20f)));
		_plantBoost = Plugin.Cfg.Bind("Rain", "PlantBoost", 2f, new ConfigDescription("While it rains, plants seed and grow this many times faster (WildPlants).", new AcceptableValueRange<float>(1f, 20f)));
	}

	internal static void Clear()
	{
		bool wasRaining = Current != WeatherKind.None;
		_active = false;
		State.Reset();
		_sent = WeatherKind.None;
		_fall = WeatherKind.Rain;
		if (wasRaining)
		{
			Changed?.Invoke(WeatherKind.Rain, WeatherKind.None, false);
		}
	}

	internal static void LevelReady()
	{
		Clear();
		Asset_Map map = GameParam.Map;
		if (map == null)
		{
			return;
		}
		_fall = VoxelEngine.Ready ? DetectFall() : WeatherKind.Rain;
		NetworkClient client = (UNetManager.instance != null) ? UNetManager.instance.LocalNetworkClient : null;
		if (client != null)
		{
			if (client.handlers.ContainsKey(WeatherMessage))
			{
				client.UnregisterHandler(WeatherMessage);
			}
			client.RegisterHandler(WeatherMessage, Received);
		}
		if (NetworkServer.active && Plugin.IsEnabled(Features.Rain) && !map.IsNewGame() && !string.IsNullOrEmpty(map.path))
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
					Plugin.Log.LogWarning("Rain: could not read the weather: " + ex.Message);
				}
			}
		}
		_active = true;
	}

	// Rain on any ground but mostly sand (desert: none) or mostly snow (snow instead).
	private static WeatherKind DetectFall()
	{
		if (!(VoxelEngine.Impl is Brix.Engine.FlatWorld.Layers.LayerVoxelEngine engine))
		{
			return WeatherKind.Rain;
		}
		XYZ start = engine.test.worldStart;
		XYZ size = engine.test.worldSize;
		if (size.x <= 0 || size.z <= 0)
		{
			return WeatherKind.Rain;
		}
		System.Random pick = new System.Random(RainRules.SampleSeed(size.x, size.z));
		int sand = 0;
		int snow = 0;
		int other = 0;
		// On a sparse map (islands in the sky) most columns are empty: keep looking until enough have ground.
		for (int i = 0; RainRules.KeepSampling(i, sand + snow + other); i++)
		{
			VoxelTerrainType type = VoxelEngine.GetColumnType(start.x + pick.Next(size.x), start.z + pick.Next(size.z));
			if (type == VoxelTerrainType.Empty || type == VoxelTerrainType.Internal)
			{
				continue;
			}
			if (type == VoxelTerrainType.Sand)
			{
				sand++;
			}
			else if (type == VoxelTerrainType.Snow)
			{
				snow++;
			}
			else
			{
				other++;
			}
		}
		return RainRules.DetectFall(sand, snow, other);
	}

	// Switched off while it rains: the weather ends (the host tells the others), so no boost is left behind.
	private void OnDisable()
	{
		if (!_active || !NetworkServer.active || Current == WeatherKind.None)
		{
			return;
		}
		Set(WeatherKind.None, 0f, announce: false);
		State.Rest = RainRules.RestHours;
		Send(true);
	}

	private void Update()
	{
		if (!_active)
		{
			return;
		}
		float hours = Advance();
		if (!NetworkServer.active)
		{
			return;
		}
		try
		{
			Roll(hours);
			Send();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("Rain: " + ex.Message);
		}
	}

	// The game hours that went by since the last look (0 when the game is paused or the clock was set).
	private static float Advance()
	{
		if (Journee.instance == null)
		{
			return 0f;
		}
		return State.Advance(Journee.instance.Heure);
	}

	private static void Roll(float hours)
	{
		WeatherKind before = State.Current;
		State.Roll(hours, _daysBetween.Value, _hours.Value, _fall, Random.NextDouble);
		if (State.Current != before)
		{
			Changed?.Invoke(before, State.Current, true);
		}
	}

	// Host: starts a rain or snow of this many game hours (None: ends it).
	internal static void Begin(WeatherKind kind, float hours)
	{
		if (!_active || !NetworkServer.active)
		{
			return;
		}
		WeatherKind from = State.Begin(kind, hours);
		if (from != kind)
		{
			Changed?.Invoke(from, kind, true);
		}
	}

	private static void Set(WeatherKind kind, float hours, bool announce)
	{
		WeatherKind from = State.Set(kind, hours);
		if (from != kind)
		{
			Changed?.Invoke(from, kind, announce);
		}
	}

	// Host: the weather to the other players when it changed, and once in a while for a player who joined.
	private static void Send(bool force = false)
	{
		if (!Neo.multiplayer)
		{
			return;
		}
		if (!force && _sent == Current && Time.unscaledTime < _nextSend)
		{
			return;
		}
		_sent = Current;
		_nextSend = Time.unscaledTime + ResendSeconds;
		NetworkServer.SendToAll(WeatherMessage, new WeatherNetMessage { Kind = (byte)Current, Hours = HoursLeft });
	}

	// A client: the host's weather.
	private static void Received(NetworkMessage netMsg)
	{
		WeatherNetMessage message = netMsg.ReadMessage<WeatherNetMessage>();
		if (NetworkServer.active || !_active || !RainWire.TryValidate(message.Kind, message.Hours, out WeatherKind kind, out float hours))
		{
			return;
		}
		Set(kind, hours, announce: true);
	}

	private static void Load(string file)
	{
		RainWire.ReadSave(File.ReadAllText(file), out WeatherKind kind, out float hours, out float rest);
		State.Rest = rest;
		if (kind != WeatherKind.None)
		{
			Set(kind, hours, announce: false);
		}
		Plugin.Log.LogInfo("Rain: weather from the save: " + Current + ", " + HoursLeft.ToString("0.0") + " h left");
	}

	// Host: called when the game has written a save into its folder.
	internal static void Write(string folder)
	{
		if (!_active)
		{
			return;
		}
		string file = System.IO.Path.Combine(folder, SaveFile);
		if (!Plugin.IsEnabled(Features.Rain))
		{
			// Off: no old rain left in the save to come back when it is switched on.
			try
			{
				File.Delete(file);
			}
			catch (Exception ex)
			{
				Plugin.Log.LogWarning("Rain: could not remove the old weather: " + ex.Message);
			}
			return;
		}
		try
		{
			File.WriteAllText(file, RainWire.SaveText(Current, HoursLeft, State.Rest));
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("Rain: could not save the weather: " + ex.Message);
		}
	}

	// For the tests: the weather read back from a folder written by Write (as the load of a save does).
	internal static void ReadFrom(string folder)
	{
		Load(System.IO.Path.Combine(folder, SaveFile));
	}
}

// What always runs, also with the feature off: the settings (the boosts are read by WornPaths and WildPlants), the
// weather's start and end with a game, and the host's weather, which every player takes (message and save).
[Feature]
internal static class RainSetup
{
	private static void Enable()
	{
		Rain.BindConfig();
		GameSession.OnLevelReady(Rain.LevelReady);
		GameSession.OnLeave(Rain.Clear);
	}
}

// Saves the weather with the game (with the feature off the old weather file is removed).
[Feature]
[HarmonyPatch(typeof(Asset_Map), "Disk_Save")]
internal static class RainSavePatch
{
	private static void Postfix(Asset_Map __instance)
	{
		if (NetworkServer.active && !string.IsNullOrEmpty(__instance.path))
		{
			Rain.Write(__instance.path);
		}
	}
}
