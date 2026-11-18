using System.Collections;
using System.Collections.Generic;
using System.IO;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using CastleStoryPlus.Achievements;
using CastleStoryPlus.DevTools.Api;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.Weather;
using UnityEngine;
using FactoryBlocks = Brix.External.Factories.Blocks;

namespace CastleStoryPlus.DevTools.Tests;

// The mod's achievements: what counts and unlocks, that a sandbox or a cheated game does not count, the file they are
// kept in, the message with its chime, and the list in the menu. Every test works on a file of its own in the temp
// folder (never the player's achievements file) and puts the tracker's rules back afterwards.
internal static class AchievementTests
{
	// Points the tracker at an empty file of its own, counting whatever the game is (a test world is a sandbox).
	private static string Isolate(TestContext t, bool ignoreRules = true)
	{
		string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "csp-achievements-" + System.Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		string file = System.IO.Path.Combine(folder, "achievements.json");
		// What the player's own achievements were: written out first, read back afterwards; and the game's cheated state.
		AchievementTracker.Flush();
		AchievementTracker.Save();
		KeyValuePair<bool, string> cheatState = AchievementTracker.CheatState;
		AchievementTracker.FilePathOverride = file;
		AchievementTracker.IgnoreRules = ignoreRules;
		AchievementTracker.ResetForTest();
		AchievementToast.ResetForTest();
		t.Cleanup(() =>
		{
			AchievementTracker.FilePathOverride = null;
			AchievementTracker.IgnoreRules = false;
			AchievementTracker.SandboxProbe = CastleStoryPlus.Upgrades.SandboxResearch.IsSandbox;
			AchievementTracker.Load();
			AchievementTracker.CheatState = cheatState;
			AchievementToast.ResetForTest();
			Directory.Delete(folder, true);
		});
		return file;
	}

	[GameTest("Achievements", "Counters add up, an achievement unlocks at its target and the best value is kept", TimeoutSeconds = 30f)]
	private static IEnumerator CountsAndUnlocks(TestContext t)
	{
		t.True(Brix.Game.User.LocalUser != null && AchievementTracker.LocalFaction != null, "No local team");
		Isolate(t);
		AchievementInfo trade10 = AchievementList.Find("trade10");
		AchievementInfo trade100 = AchievementList.Find("trade100");
		AchievementTracker.Local(AchievementCounter.Trades, 9);
		t.Equal(9, AchievementTracker.Progress(trade10), "Progress after 9 trades");
		t.False(AchievementTracker.IsUnlocked(trade10), "Pedlar unlocked at 9 trades");
		// The host's own team, reported where the game runs: counted once the batch is flushed.
		AchievementTracker.Add(AchievementTracker.LocalFaction, AchievementCounter.Trades, 1);
		t.Equal(9, AchievementTracker.Value(AchievementCounter.Trades), "A host report is counted at once");
		AchievementTracker.Flush();
		t.Equal(10, AchievementTracker.Value(AchievementCounter.Trades), "Trades after the flush");
		t.True(AchievementTracker.IsUnlocked(trade10), "Pedlar not unlocked at 10 trades");
		t.False(AchievementTracker.IsUnlocked(trade100), "Merchant unlocked at 10 trades");
		string at = AchievementTracker.UnlockedAt(trade10);
		t.True(at != null && at.Length == 16 && at[2] == '-' && at[5] == '-' && at[10] == ' ' && at[13] == ':', "The unlock date is not dd-MM-yyyy HH:mm: " + at);
		// An enemy's deeds, and a team that is not the player's, are not the player's.
		AchievementTracker.Add(Scenario.EnemyFaction(), AchievementCounter.Trades, 50);
		AchievementTracker.Flush();
		t.Equal(10, AchievementTracker.Value(AchievementCounter.Trades), "Trades after the enemy's report");
		// A best value only goes up.
		AchievementTracker.LocalMax(AchievementCounter.Wave, 12);
		AchievementTracker.LocalMax(AchievementCounter.Wave, 5);
		t.Equal(12, AchievementTracker.Value(AchievementCounter.Wave), "Best wave");
		t.True(AchievementTracker.IsUnlocked(AchievementList.Find("wave10")), "Holding the line at wave 12");
		t.False(AchievementTracker.IsUnlocked(AchievementList.Find("wave20")), "Unbroken at wave 12");
		yield return null;
	}

	[GameTest("Achievements", "The game's own events count: research, rain, kills, guardians, digging and building", TimeoutSeconds = 60f)]
	private static IEnumerator HooksCount(TestContext t)
	{
		Isolate(t);
		Faction mine = AchievementTracker.LocalFaction;
		// Research: the tiers of the team change (the hook sees it on every peer).
		t.Cleanup(() => Rain.Begin(WeatherKind.None, 0f));
		int line = 0;
		int tier = CastleStoryPlus.Upgrades.TeamUpgrades.Tier(mine, line);
		CastleStoryPlus.Upgrades.TeamUpgrades.Unlock(mine, line, tier + 1);
		t.True(AchievementTracker.Value(AchievementCounter.Research) >= 1, "Research after a finished research: " + AchievementTracker.Value(AchievementCounter.Research));
		t.True(AchievementTracker.IsUnlocked(AchievementList.Find("research1")), "Curious mind");
		// Rain: each rain that starts counts once.
		Rain.Begin(WeatherKind.None, 0f);
		Rain.Begin(WeatherKind.Rain, 1f);
		Rain.Begin(WeatherKind.None, 0f);
		Rain.Begin(WeatherKind.Rain, 1f);
		t.Equal(2, AchievementTracker.Value(AchievementCounter.Rains), "Rains counted");
		Rain.Begin(WeatherKind.None, 0f);
		// An enemy killed by the player's team, and a guardian.
		Labor enemy = Scenario.EnsureEnemy(t);
		AchievementHooks.Killed(enemy, mine);
		AchievementTracker.Flush();
		t.Equal(1, AchievementTracker.Value(AchievementCounter.Kills), "Kills after one enemy");
		t.Equal(0, AchievementTracker.Value(AchievementCounter.Guardians), "Guardians after a plain enemy");
		Labor builder = Scenario.EnsureBuilder(t);
		AchievementHooks.Killed(builder, mine);
		AchievementTracker.Flush();
		t.Equal(1, AchievementTracker.Value(AchievementCounter.Kills), "Kills after one of the team's own units died");
		Labor guardian = CastleStoryPlus.Treasure.Beasts.Spawn(CastleStoryPlus.Treasure.BeastKind.Ironhorn, Scenario.EnemyFaction(), builder.transform.position + Vector3.right * 5f, 1);
		t.NotNull(guardian, "A guardian");
		t.Cleanup(() => Scenario.Discard(guardian.gameObject));
		AchievementHooks.Killed(guardian, mine);
		AchievementTracker.Flush();
		t.Equal(2, AchievementTracker.Value(AchievementCounter.Kills), "Kills after a guardian");
		t.Equal(1, AchievementTracker.Value(AchievementCounter.Guardians), "Guardians");
		t.True(AchievementTracker.IsUnlocked(AchievementList.Find("guardian1")), "Beast slayer");
		// The game's own counts of ground dug and blocks built, as it reports them.
		StatsTracker.DugVoxel(builder.gameObject);
		AchievementTracker.Flush();
		t.Equal(1, AchievementTracker.Value(AchievementCounter.Dug), "Voxels dug");
		yield return null;
	}

	[GameTest("Achievements", "Nothing counts in a sandbox game, in a game where cheats were used, or with instant build on", TimeoutSeconds = 30f)]
	private static IEnumerator SandboxAndCheatsDoNotCount(TestContext t)
	{
		Isolate(t, ignoreRules: false);
		AchievementTracker.ClearCheatedForTest();
		// A sandbox game (the test world is one).
		AchievementTracker.SandboxProbe = () => true;
		t.True(AchievementTracker.Why() != null && AchievementTracker.Why().Contains("sandbox"), "Why in a sandbox: " + AchievementTracker.Why());
		AchievementTracker.Local(AchievementCounter.Trades, 5);
		AchievementTracker.LocalMax(AchievementCounter.Wave, 30);
		t.Equal(0, AchievementTracker.Value(AchievementCounter.Trades), "Trades in a sandbox");
		t.Equal(0, AchievementTracker.Value(AchievementCounter.Wave), "Wave in a sandbox");
		// Any other game counts.
		AchievementTracker.SandboxProbe = () => false;
		t.Log("Why in another game: " + (AchievementTracker.Why() ?? "(counts)"));
		t.True(AchievementTracker.Why() == null, "Nothing may stop a normal game from counting: " + AchievementTracker.Why());
		AchievementTracker.Local(AchievementCounter.Trades, 5);
		t.Equal(5, AchievementTracker.Value(AchievementCounter.Trades), "Trades in a normal game");
		// The developer API's cheats.
		ApiRequest harmless = new ApiRequest { Method = "GET", Path = "/api/status" };
		CheatRoutes.Check(harmless);
		t.False(AchievementTracker.IsCheated, "A GET marked the game as cheated");
		CheatRoutes.Check(new ApiRequest { Method = "POST", Path = "/api/cheat/items" });
		t.True(AchievementTracker.IsCheated, "A cheat of the developer API did not mark the game");
		t.True(AchievementTracker.Why() != null && AchievementTracker.Why().Contains("cheats"), "Why after a cheat: " + AchievementTracker.Why());
		AchievementTracker.Local(AchievementCounter.Trades, 5);
		AchievementTracker.Add(AchievementTracker.LocalFaction, AchievementCounter.Trades, 5);
		AchievementTracker.Flush();
		t.Equal(5, AchievementTracker.Value(AchievementCounter.Trades), "Trades after a cheat");
		// The game's own instant build is a cheat as well.
		AchievementTracker.ClearCheatedForTest();
		t.True(AchievementTracker.Why() == null, "A new game counts again: " + AchievementTracker.Why());
		t.Cleanup(() => Architecte.editorMode = false);
		Architecte.editorMode = true;
		yield return t.Frames(3);
		t.True(AchievementTracker.IsCheated, "Instant build did not mark the game");
		Architecte.editorMode = false;
		AchievementTracker.Local(AchievementCounter.Trades, 5);
		t.Equal(5, AchievementTracker.Value(AchievementCounter.Trades), "Trades after instant build");
	}

	[GameTest("Achievements", "A cheated game stays cheated when its save is loaded again", TimeoutSeconds = 30f)]
	private static IEnumerator CheatedSaveIsRemembered(TestContext t)
	{
		string file = Isolate(t, ignoreRules: false);
		AchievementTracker.SandboxProbe = () => false;
		AchievementTracker.ClearCheatedForTest();
		string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "csp-achievements-save-" + System.Guid.NewGuid().ToString("N"));
		AchievementTracker.Cheated("test");
		AchievementTracker.Saved(folder);
		AchievementTracker.Save();
		t.True(File.ReadAllText(file).Contains("cheatedSaves"), "The cheated save is not in the file");
		// Read back, and the game's folder is one of the cheated ones.
		AchievementTracker.Load();
		t.True(AchievementTracker.IsCheatedSave(folder), "The cheated save was not read back");
		t.False(AchievementTracker.IsCheatedSave(folder + "-other"), "Another save is cheated");
		yield return null;
	}

	[GameTest("Achievements", "The file keeps the counters and unlock dates, is written whole, and an unreadable file is kept aside", TimeoutSeconds = 30f)]
	private static IEnumerator FileRoundTrip(TestContext t)
	{
		string file = Isolate(t);
		AchievementTracker.Local(AchievementCounter.Trades, 12);
		AchievementTracker.LocalMax(AchievementCounter.Wave, 21);
		string unlockedAt = AchievementTracker.UnlockedAt(AchievementList.Find("trade10"));
		AchievementTracker.Save();
		t.True(File.Exists(file), "No file written");
		t.False(File.Exists(file + ".tmp"), "The temporary file is left behind");
		// Forgotten in memory, read from the file.
		AchievementTracker.Load();
		t.Equal(12, AchievementTracker.Value(AchievementCounter.Trades), "Trades read back");
		t.Equal(21, AchievementTracker.Value(AchievementCounter.Wave), "Wave read back");
		t.True(AchievementTracker.IsUnlocked(AchievementList.Find("wave20")), "An unlock read back");
		t.Equal(unlockedAt, AchievementTracker.UnlockedAt(AchievementList.Find("trade10")), "The unlock date read back");
		// Written again over the old file (the replace path).
		AchievementTracker.Local(AchievementCounter.Trades, 1);
		AchievementTracker.Save();
		t.False(File.Exists(file + ".tmp"), "The temporary file is left behind after the second write");
		AchievementTracker.Load();
		t.Equal(13, AchievementTracker.Value(AchievementCounter.Trades), "Trades after the second write");
		// A file that is not JSON: no crash, a copy beside it, and what is in memory stays.
		File.WriteAllText(file, "{ this is not json");
		AchievementTracker.Load();
		t.Equal(13, AchievementTracker.Value(AchievementCounter.Trades), "Trades after reading an unreadable file");
		t.True(Directory.GetFiles(System.IO.Path.GetDirectoryName(file), "achievements.json.unreadable-*").Length == 1, "No copy of the unreadable file");
		yield return null;
	}

	[GameTest("Achievements", "An unlock shows its message and plays the chime, with one audio listener at most", TimeoutSeconds = 45f)]
	private static IEnumerator ToastAndChime(TestContext t)
	{
		Isolate(t);
		AchievementInfo chest = AchievementList.Find("chest1");
		int shown = AchievementToast.Shown;
		AchievementTracker.Local(AchievementCounter.Chests, 1);
		t.True(AchievementTracker.IsUnlocked(chest), "Treasure found not unlocked");
		yield return t.Until(() => AchievementToast.Shown > shown, 5f, "the message");
		t.True(AchievementToast.Current == chest, "The message is for " + (AchievementToast.Current != null ? AchievementToast.Current.Name : "nothing"));
		AudioClip chime = AchievementToast.Chime();
		t.True(chime != null && chime.length > 0.5f, "The chime clip");
		// A listener is there while the chime plays (the game's own, or ours), and never two.
		int most = 0;
		float end = Time.realtimeSinceStartup + 4f;
		bool heard = false;
		while (Time.realtimeSinceStartup < end)
		{
			int listeners = Object.FindObjectsOfType<AudioListener>().Length;
			most = Mathf.Max(most, listeners);
			heard |= listeners >= 1;
			yield return null;
		}
		t.Record("mostListeners", most);
		t.Record("ownListener", CastleStoryPlus.Core.ModSound.HasOwnListener);
		t.True(most <= 1, "Two audio listeners: " + most);
		t.True(heard, "No audio listener while the chime plays");
		yield return t.Until(() => !AchievementToast.IsShowing, 10f, "the message to go");
		yield return t.Until(() => Object.FindObjectsOfType<AudioListener>().Length <= 1 && !CastleStoryPlus.Core.ModSound.HasOwnListener, 10f, "our listener to go when nothing plays");
	}

	[GameTest("Achievements", "The in-game menu lists every achievement, locked ones greyed out", NeedsHostGame = true, TimeoutSeconds = 30f)]
	private static IEnumerator MenuLists(TestContext t)
	{
		Isolate(t);
		CastleStoryPlus.GameUi.GameMenuModel model = CastleStoryPlus.GameUi.CsGameMenu.Model;
		t.NotNull(model, "The in-game menu's model");
		CastleStoryPlus.GameUi.UiPanel panel = model.ExtraPanels.Find((CastleStoryPlus.GameUi.UiPanel p) => p.Id == "achievements");
		t.NotNull(panel, "The achievements panel");
		int statuses = 0;
		foreach (CastleStoryPlus.GameUi.UiSection section in panel.Sections)
		{
			foreach (CastleStoryPlus.GameUi.UiItem item in section.Items)
			{
				if (item is CastleStoryPlus.GameUi.UiStatus status)
				{
					statuses++;
					t.True(status.Dimmed(), "A locked achievement is not greyed out: " + status.Title());
				}
			}
		}
		t.Equal(AchievementList.All.Count, statuses, "Achievements in the list");
		AchievementTracker.Local(AchievementCounter.Chests, 3);
		CastleStoryPlus.GameUi.UiStatus unlocked = null;
		foreach (CastleStoryPlus.GameUi.UiSection section in panel.Sections)
		{
			foreach (CastleStoryPlus.GameUi.UiItem item in section.Items)
			{
				if (item.Id == "achievementchest1")
				{
					unlocked = (CastleStoryPlus.GameUi.UiStatus)item;
				}
			}
		}
		t.NotNull(unlocked, "The Treasure found entry");
		t.False(unlocked.Dimmed(), "An unlocked achievement is greyed out");
		t.True(unlocked.Detail().Contains("Unlocked "), "Its detail: " + unlocked.Detail());
		yield return null;
	}

	[GameTest("Achievements", "A best value a game starts with is its baseline: only what goes beyond it counts", TimeoutSeconds = 30f)]
	private static IEnumerator BaselineOfALoadedGame(TestContext t)
	{
		Isolate(t, ignoreRules: false);
		AchievementTracker.SandboxProbe = () => false;
		AchievementTracker.ClearCheatedForTest();
		// The first moments of a game: what it reports is where it stands (a loaded game at wave 12).
		AchievementTracker.SetBaselineOverForTest(false);
		AchievementTracker.LocalMax(AchievementCounter.Wave, 12);
		t.Equal(0, AchievementTracker.Value(AchievementCounter.Wave), "Wave counted from the start of a game");
		t.False(AchievementTracker.IsUnlocked(AchievementList.Find("wave10")), "Holding the line from a loaded game");
		AchievementTracker.SetBaselineOverForTest(true);
		AchievementTracker.LocalMax(AchievementCounter.Wave, 12);
		t.Equal(0, AchievementTracker.Value(AchievementCounter.Wave), "The same wave again");
		AchievementTracker.LocalMax(AchievementCounter.Wave, 13);
		t.Equal(13, AchievementTracker.Value(AchievementCounter.Wave), "A wave survived after the start");
		yield return null;
	}

	[GameTest("Achievements", "A block counts when its blueprint is built, once", TimeoutSeconds = 60f)]
	private static IEnumerator BuiltBlockCounts(TestContext t)
	{
		Isolate(t);
		XYZ site = Scenario.Site(t, 6, 6);
		Brix.External.Factories.Factory.AssetKey blueprint = new Brix.External.Factories.Factory.AssetKey("Blueprints", FactoryBlocks.Stone_Brick.Name);
		Brix.Game.AI.BuildProject build = Scenario.BuildProject(t, site);
		yield return Scenario.Building(t, blueprint, new XYZ(site.x + 2, site.y, site.z), build, (GameObject made) => { });
		AchievementTracker.Flush();
		t.Equal(1, AchievementTracker.Value(AchievementCounter.Blocks), "Blocks after one block was built");
	}
}
