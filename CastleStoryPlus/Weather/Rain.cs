using System;
using System.IO;
using BepInEx.Configuration;
using Brix.Assets;
using Brix.Engine;
using Brix.Network;
using CastleStoryPlus.Core;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Weather;

internal enum WeatherKind
{
	None = 0,
	Rain = 1,
	Snow = 2
}

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

	// Game hours of rest after a rain before the next may start.
	private const float RestHours = 6f;

	private const float ResendSeconds = 30f;

	// Columns looked at to see what the ground of the world is.
	private const int GroundSamples = 600;

	private static ConfigEntry<float> _daysBetween;

	private static ConfigEntry<float> _hours;

	private static ConfigEntry<float> _grassBoost;

	private static ConfigEntry<float> _plantBoost;

	private static readonly System.Random Random = new System.Random();

	private static bool _active;

	private static float _lastClock = float.NaN;

	private static float _hourCarry;

	private static float _rest;

	private static WeatherKind _fall = WeatherKind.Rain;

	private static WeatherKind _sent;

	private static float _nextSend;

	// Whether a game is loaded and its weather runs.
	internal static bool Active => _active;

	internal static WeatherKind Current { get; private set; }

	// Game hours left of what falls now.
	internal static float HoursLeft { get; private set; }

	// What falls in this world when the weather comes by itself: Rain, Snow, or None (desert).
	internal static WeatherKind Fall => _fall;

	// How much faster grass spreads and paths grow over, and plants seed and grow, now.
	internal static float GrassBoost => (Current == WeatherKind.Rain && _grassBoost != null) ? _grassBoost.Value : 1f;

	internal static float PlantBoost => (Current == WeatherKind.Rain && _plantBoost != null) ? _plantBoost.Value : 1f;

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
		Current = WeatherKind.None;
		HoursLeft = 0f;
		_lastClock = float.NaN;
		_hourCarry = 0f;
		_rest = RestHours;
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
		System.Random pick = new System.Random(size.x * 31 + size.z);
		int sand = 0;
		int snow = 0;
		int other = 0;
		// On a sparse map (islands in the sky) most columns are empty: keep looking until enough have ground.
		for (int i = 0; i < GroundSamples * 10 && sand + snow + other < GroundSamples; i++)
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
		int total = sand + snow + other;
		if (total < 20)
		{
			return WeatherKind.Rain;
		}
		if (sand * 2 > total)
		{
			return WeatherKind.None;
		}
		return (snow * 2 > total) ? WeatherKind.Snow : WeatherKind.Rain;
	}

	// Switched off while it rains: the weather ends (the host tells the others), so no boost is left behind.
	private void OnDisable()
	{
		if (!_active || !NetworkServer.active || Current == WeatherKind.None)
		{
			return;
		}
		Set(WeatherKind.None, 0f, announce: false);
		_rest = RestHours;
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
		float clock = Journee.instance.Heure;
		float passed = float.IsNaN(_lastClock) ? 0f : Mathf.Repeat(clock - _lastClock, 24f);
		_lastClock = clock;
		return (passed > 12f) ? 0f : passed;
	}

	private static void Roll(float hours)
	{
		if (Current != WeatherKind.None)
		{
			HoursLeft -= hours;
			if (HoursLeft <= 0f)
			{
				Set(WeatherKind.None, 0f, announce: true);
				_rest = RestHours;
			}
			return;
		}
		if (_rest > 0f)
		{
			_rest -= hours;
			return;
		}
		if (_daysBetween.Value <= 0f || _fall == WeatherKind.None)
		{
			return;
		}
		// One roll for every game hour, with the chance that makes the wait (after the rest and the rain's own hours)
		// average the days between.
		_hourCarry += hours;
		while (_hourCarry >= 1f)
		{
			_hourCarry -= 1f;
			float wait = Mathf.Max(1f, _daysBetween.Value * 24f - RestHours - _hours.Value);
			if (Random.NextDouble() < 1.0 / wait)
			{
				Begin(_fall, _hours.Value * (0.6f + 0.8f * (float)Random.NextDouble()));
				return;
			}
		}
	}

	// Host: starts a rain or snow of this many game hours (None: ends it).
	internal static void Begin(WeatherKind kind, float hours)
	{
		if (!_active || !NetworkServer.active)
		{
			return;
		}
		Set(kind, (kind == WeatherKind.None) ? 0f : Mathf.Max(0.05f, hours), announce: true);
		if (kind == WeatherKind.None)
		{
			_rest = RestHours;
		}
	}

	private static void Set(WeatherKind kind, float hours, bool announce)
	{
		WeatherKind from = Current;
		Current = kind;
		HoursLeft = hours;
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
		if (NetworkServer.active || !_active || !Enum.IsDefined(typeof(WeatherKind), (int)message.Kind))
		{
			return;
		}
		Set((WeatherKind)message.Kind, message.Hours, announce: true);
	}

	private static void Load(string file)
	{
		JObject data = JObject.Parse(File.ReadAllText(file));
		_rest = (float?)data["rest"] ?? RestHours;
		WeatherKind kind = (WeatherKind)((int?)data["kind"] ?? 0);
		float hours = (float?)data["hoursLeft"] ?? 0f;
		if (kind != WeatherKind.None && hours > 0f)
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
			File.WriteAllText(file, new JObject { ["kind"] = (int)Current, ["hoursLeft"] = Math.Round(HoursLeft, 3), ["rest"] = Math.Round(_rest, 3) }.ToString(Newtonsoft.Json.Formatting.None));
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
