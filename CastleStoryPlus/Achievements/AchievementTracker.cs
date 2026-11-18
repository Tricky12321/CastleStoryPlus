using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Brix.Assets;
using Brix.Engine;
using Brix.Game;
using Brix.Input;
using Brix.Network;
using CastleStoryPlus.Core;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Achievements;

// The mod's own achievements (no Steam, no rewards). Hooks in the game's code report what a team did (Add, Max: on
// the host, where the game's logic runs, with the team that did it; Local and LocalMax: on every peer, for the
// player's own deeds). A report counts only for the local player's own team: the host tells the other players
// the reports of every team (message AchievementMessage, batched once a second) and each player counts the ones of
// his own team, so in multiplayer everyone has his own progress.
//
// Nothing counts (Why) while no game runs, in a sandbox game, in a world editor or debug map, or in a game where
// cheats were used: the debug menu, the game's own debug sidebar (instant build, spawning), the developer API's
// cheats (Cheated); nor in a multiplayer game where the host allows cheats (CheatGuard), as another player's cheats
// cannot be seen from here. A cheated game stays cheated: the folder of its save is remembered (in the achievements file), so
// loading it again does not count either; the save itself is not touched, so it loads without the mod.
//
// Kept in the mod's own file (BepInEx/config/castlestoryplus_achievements.json): the counters, when each achievement
// was unlocked and the cheated saves. Written whole to a temporary file and moved over the old one, so a crash never
// leaves half a file.
[Feature(Features.Achievements, Features.AchievementsInfo)]
internal sealed class AchievementTracker : MonoBehaviour
{
	// The game's own messages end at 63; Castle Story Plus uses 200-209.
	private const short AchievementMessage = 210;

	private const string FileName = "castlestoryplus_achievements.json";

	private const float FlushSeconds = 1f;

	private const float SaveSeconds = 15f;

	private const int Limit = 1000000000;

	private static readonly Dictionary<string, int> Counters = new Dictionary<string, int>();

	// When each achievement was unlocked (the player's own clock).
	private static readonly Dictionary<string, DateTime> Unlocked = new Dictionary<string, DateTime>();

	private static readonly HashSet<string> CheatedSaves = new HashSet<string>();

	// What the host has seen done and not yet counted or told: team id and counter.
	private static readonly Dictionary<KeyValuePair<int, string>, int> Pending = new Dictionary<KeyValuePair<int, string>, int>();

	private static AchievementTracker _instance;

	private static bool _loaded;

	private static bool _dirty;

	private static bool _levelActive;

	private static bool _cheated;

	private static string _cheatReason;

	private static string _saveFolder;

	// A loaded game sets its objects up as the level becomes ready: what the host sees done in the first moments is
	// not counted (real seconds).
	private const float SettleSeconds = 3f;

	private static float _settledAt;

	private static float _nextFlush;

	private static float _nextSave;

	// Another file than the player's own (the tests).
	internal static string FilePathOverride;

	// For the tests: counts whatever the game is (a sandbox is what a test world is) and whoever did it.
	internal static bool IgnoreRules;

	// For the tests: tells whether the game is a sandbox.
	internal static Func<bool> SandboxProbe = Upgrades.SandboxResearch.IsSandbox;

	// Told of every unlock (the toast).
	internal static event Action<AchievementInfo> Unlock;

	internal static string FilePath => FilePathOverride ?? Path.Combine(BepInEx.Paths.ConfigPath, FileName);

	internal static Faction LocalFaction => (User.LocalUser != null) ? User.LocalUser.faction : null;

	private sealed class CounterMessage : MessageBase
	{
		public int Team;

		public string Counter = string.Empty;

		public int Value;

		public bool Max;

		public override void Serialize(NetworkWriter writer)
		{
			writer.Write(Team);
			writer.Write(Counter);
			writer.Write(Value);
			writer.Write(Max);
		}

		public override void Deserialize(NetworkReader reader)
		{
			Team = reader.ReadInt32();
			Counter = reader.ReadString();
			Value = reader.ReadInt32();
			Max = reader.ReadBoolean();
		}
	}

	private static void Enable()
	{
		Load();
		_instance = Plugin.Root.AddComponent<AchievementTracker>();
		GameSession.OnLevelReady(LevelReady);
		GameSession.OnLeave(Leave);
	}

	// ---- state of the game

	// Why nothing counts now, or null when it does.
	internal static string Why()
	{
		if (!Plugin.IsEnabled(Features.Achievements))
		{
			return "the Achievements feature is off";
		}
		if (IgnoreRules)
		{
			return null;
		}
		if (!_levelActive || GameParam.Map == null)
		{
			return "no game is running";
		}
		if (InputModeController.DefaultMode == InputMode.worldEditor)
		{
			return "this is the world editor";
		}
		if (SandboxProbe())
		{
			return "this is a sandbox game";
		}
		if (_cheated)
		{
			return "cheats were used in this game (" + _cheatReason + ")";
		}
		if (Multiplayer.CheatGuard.InMultiplayerGame && Multiplayer.CheatGuard.Allowed)
		{
			// Another player's cheats cannot be seen from here: where the host lets cheats be used, nobody counts.
			return "cheats are allowed in this multiplayer game";
		}
		if (GameParam.Map.isDebug)
		{
			return "this is a debug map";
		}
		if (LocalFaction == null)
		{
			return "you have no team in this game";
		}
		return null;
	}

	internal static bool Counting => Why() == null;

	internal static bool IsCheated => _cheated;

	// A cheat was used in this game: nothing counts in it from now on (nor when its save is loaded again).
	internal static void Cheated(string reason)
	{
		if (!_levelActive || _cheated)
		{
			return;
		}
		_cheated = true;
		_cheatReason = reason;
		Plugin.Log.LogInfo("Achievements: nothing counts in this game from now on (" + reason + ")");
		Remember(_saveFolder);
	}

	private static void Remember(string folder)
	{
		if (!string.IsNullOrEmpty(folder) && CheatedSaves.Add(folder))
		{
			_dirty = true;
		}
	}

	private static void LevelReady()
	{
		_levelActive = true;
		_settledAt = Time.unscaledTime + SettleSeconds;
		_cheated = false;
		_cheatReason = null;
		Pending.Clear();
		Asset_Map map = GameParam.Map;
		_saveFolder = (map != null && !map.IsNewGame()) ? map.path : null;
		if (!string.IsNullOrEmpty(_saveFolder) && CheatedSaves.Contains(_saveFolder))
		{
			_cheated = true;
			_cheatReason = "it was cheated in before";
		}
		NetworkClient client = (UNetManager.instance != null) ? UNetManager.instance.LocalNetworkClient : null;
		if (client != null)
		{
			if (client.handlers.ContainsKey(AchievementMessage))
			{
				client.UnregisterHandler(AchievementMessage);
			}
			client.RegisterHandler(AchievementMessage, Received);
		}
	}

	private static void Leave()
	{
		Flush();
		_levelActive = false;
		_cheated = false;
		_cheatReason = null;
		_saveFolder = null;
		Save();
	}

	// The game was saved into the folder: if cheats were used it is remembered as cheated.
	internal static void Saved(string folder)
	{
		if (_levelActive && _cheated)
		{
			Remember(folder);
			_saveFolder = folder;
		}
		else if (_levelActive && !string.IsNullOrEmpty(folder))
		{
			_saveFolder = folder;
		}
	}

	// ---- reports

	// Host: the team did something (adds to the counter). Told to the other players, who count it if it is theirs.
	internal static void Add(Faction team, string counter, int amount = 1)
	{
		if (team == null || team.isAI || amount <= 0 || !NetworkServer.active || !Plugin.IsEnabled(Features.Achievements))
		{
			return;
		}
		if (!IgnoreRules && Time.unscaledTime < _settledAt)
		{
			return;
		}
		KeyValuePair<int, string> key = new KeyValuePair<int, string>(team.ID, counter);
		Pending.TryGetValue(key, out int sum);
		Pending[key] = Math.Min(Limit, sum + amount);
	}

	// Host: the team reached a value (kept when it is the best so far).
	internal static void Max(Faction team, string counter, int value)
	{
		if (team == null || team.isAI || !NetworkServer.active || !Plugin.IsEnabled(Features.Achievements))
		{
			return;
		}
		Deliver(team.ID, counter, value, max: true);
	}

	// Every peer: the local player did something (a click, a message every peer gets).
	internal static void Local(string counter, int amount = 1)
	{
		Apply(counter, amount, max: false);
	}

	internal static void LocalMax(string counter, int value)
	{
		Apply(counter, value, max: true);
	}

	// Counts what was reported on the host, and tells the other players.
	internal static void Flush()
	{
		if (Pending.Count == 0)
		{
			return;
		}
		List<KeyValuePair<KeyValuePair<int, string>, int>> batch = new List<KeyValuePair<KeyValuePair<int, string>, int>>(Pending);
		Pending.Clear();
		foreach (KeyValuePair<KeyValuePair<int, string>, int> entry in batch)
		{
			Deliver(entry.Key.Key, entry.Key.Value, entry.Value, max: false);
		}
	}

	private static void Deliver(int team, string counter, int value, bool max)
	{
		Faction local = LocalFaction;
		if (local != null && local.ID == team)
		{
			Apply(counter, value, max);
		}
		if (Neo.IsMultiplayer && NetworkServer.active)
		{
			NetworkServer.SendToAll(AchievementMessage, new CounterMessage { Team = team, Counter = counter, Value = value, Max = max });
		}
	}

	// A client: what the host reports; counted when it is the player's own team's.
	private static void Received(NetworkMessage netMsg)
	{
		CounterMessage message = netMsg.ReadMessage<CounterMessage>();
		Faction local = LocalFaction;
		if (NetworkServer.active || local == null || local.ID != message.Team)
		{
			return;
		}
		Apply(message.Counter, message.Value, message.Max);
	}

	private static void Apply(string counter, int value, bool max)
	{
		if (value <= 0 || Why() != null)
		{
			return;
		}
		Counters.TryGetValue(counter, out int before);
		int now = max ? Math.Max(before, value) : Math.Min(Limit, before + value);
		if (now == before)
		{
			return;
		}
		Counters[counter] = now;
		_dirty = true;
		foreach (AchievementInfo achievement in AchievementList.ForCounter(counter))
		{
			if (now >= achievement.Goal && !Unlocked.ContainsKey(achievement.Id))
			{
				Unlocked[achievement.Id] = DateTime.Now;
				_dirty = true;
				Plugin.Log.LogInfo("Achievements: unlocked " + achievement.Name + " (" + achievement.Id + ")");
				Save();
				Unlock?.Invoke(achievement);
			}
		}
		AchievementPanel.Changed();
	}

	// ---- what the menu shows

	internal static int Value(string counter)
	{
		return Counters.TryGetValue(counter, out int value) ? value : 0;
	}

	internal static int Progress(AchievementInfo achievement)
	{
		return Math.Min(Value(achievement.Counter), achievement.Goal);
	}

	internal static bool IsUnlocked(AchievementInfo achievement)
	{
		return Unlocked.ContainsKey(achievement.Id);
	}

	// When it was unlocked, as the menu shows it: dd-MM-yyyy HH:mm.
	internal static string UnlockedAt(AchievementInfo achievement)
	{
		return Unlocked.TryGetValue(achievement.Id, out DateTime at) ? at.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture) : null;
	}

	internal static int UnlockedCount
	{
		get
		{
			int count = 0;
			foreach (AchievementInfo achievement in AchievementList.All)
			{
				if (IsUnlocked(achievement))
				{
					count++;
				}
			}
			return count;
		}
	}

	// ---- the file

	// Reads the file (the counters stay as they are when it is missing or cannot be read).
	internal static void Load()
	{
		Counters.Clear();
		Unlocked.Clear();
		CheatedSaves.Clear();
		_loaded = true;
		_dirty = false;
		string file = FilePath;
		if (!File.Exists(file))
		{
			return;
		}
		try
		{
			// Dates stay text: the reader would turn them into dates of its own.
			JObject data;
			using (Newtonsoft.Json.JsonTextReader reader = new Newtonsoft.Json.JsonTextReader(new StringReader(File.ReadAllText(file))) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })
			{
				data = JObject.Load(reader);
			}
			if (data["counters"] is JObject counters)
			{
				foreach (KeyValuePair<string, JToken> entry in counters)
				{
					Counters[entry.Key] = Math.Max(0, (int?)entry.Value ?? 0);
				}
			}
			if (data["unlocked"] is JObject unlocked)
			{
				foreach (KeyValuePair<string, JToken> entry in unlocked)
				{
					if (DateTime.TryParseExact((string)entry.Value, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime at))
					{
						Unlocked[entry.Key] = at;
					}
				}
			}
			if (data["cheatedSaves"] is JArray cheated)
			{
				foreach (JToken folder in cheated)
				{
					CheatedSaves.Add((string)folder);
				}
			}
		}
		catch (Exception ex)
		{
			// Kept beside the file so the player's progress is not lost to a half-read file being written over.
			Plugin.Log.LogWarning("Achievements: could not read " + file + ": " + ex.Message);
			try
			{
				File.Copy(file, file + ".unreadable", true);
			}
			catch (Exception)
			{
				// Nothing more to do.
			}
		}
	}

	// Writes the file whole to a temporary one and moves it over the old one.
	internal static void Save()
	{
		if (!_loaded || !_dirty)
		{
			return;
		}
		_dirty = false;
		string file = FilePath;
		string temporary = file + ".tmp";
		try
		{
			JObject counters = new JObject();
			foreach (KeyValuePair<string, int> entry in Counters)
			{
				counters[entry.Key] = entry.Value;
			}
			JObject unlocked = new JObject();
			foreach (KeyValuePair<string, DateTime> entry in Unlocked)
			{
				unlocked[entry.Key] = entry.Value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
			}
			JObject data = new JObject
			{
				["version"] = 1,
				["counters"] = counters,
				["unlocked"] = unlocked,
				["cheatedSaves"] = new JArray(CheatedSaves)
			};
			string folder = Path.GetDirectoryName(file);
			if (!string.IsNullOrEmpty(folder))
			{
				Directory.CreateDirectory(folder);
			}
			File.WriteAllText(temporary, data.ToString(Newtonsoft.Json.Formatting.Indented));
			if (File.Exists(file))
			{
				File.Replace(temporary, file, null);
			}
			else
			{
				File.Move(temporary, file);
			}
		}
		catch (Exception ex)
		{
			_dirty = true;
			Plugin.Log.LogWarning("Achievements: could not save " + file + ": " + ex.Message);
		}
	}

	// For the tests: forgets everything (in memory and in the file the path points to).
	internal static void ResetForTest()
	{
		Counters.Clear();
		Unlocked.Clear();
		CheatedSaves.Clear();
		Pending.Clear();
		_dirty = true;
		_loaded = true;
		Save();
	}

	// For the tests: the game is not cheated (a new game).
	internal static void ClearCheatedForTest()
	{
		_cheated = false;
		_cheatReason = null;
	}

	// Whether the folder is the save of a game cheats were used in.
	internal static bool IsCheatedSave(string folder)
	{
		return CheatedSaves.Contains(folder);
	}

	// For the tests: the state of the game as it is now.
	internal static string Describe()
	{
		return "counting " + (Why() ?? "yes") + ", cheated " + _cheated + ", unlocked " + UnlockedCount + "/" + AchievementList.All.Count + ", file " + FilePath;
	}

	// ---- every frame

	private void Update()
	{
		if (_levelActive && !_cheated && Architecte.editorMode && InputModeController.DefaultMode != InputMode.worldEditor && !IgnoreRules)
		{
			// The game's own instant build (its debug sidebar's, or the editor mode).
			Cheated("instant build");
		}
		if (Time.unscaledTime >= _nextFlush)
		{
			_nextFlush = Time.unscaledTime + FlushSeconds;
			Flush();
		}
		if (_dirty && Time.unscaledTime >= _nextSave)
		{
			_nextSave = Time.unscaledTime + SaveSeconds;
			Save();
		}
	}

	private void OnApplicationQuit()
	{
		Flush();
		Save();
	}

	private void OnDisable()
	{
		Flush();
		Save();
	}
}

// The game was saved into a folder: a cheated game's folder is remembered as cheated.
[Feature(Features.Achievements, Features.AchievementsInfo)]
[HarmonyPatch(typeof(Asset_Map), "Disk_Save")]
internal static class AchievementSavePatch
{
	private static void Postfix(Asset_Map __instance)
	{
		if (!string.IsNullOrEmpty(__instance.path))
		{
			AchievementTracker.Saved(__instance.path);
		}
	}
}
