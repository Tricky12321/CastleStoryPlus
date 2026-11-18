using System;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Network;
using Brix.Input;
using Brix.UI.Icons;
using CastleStoryPlus.LuaUi;
using UnityEngine;

namespace CastleStoryPlus.GameUi;

// The player's debug sidebar (GameMenu.lua's creative handle, CreativeSidebar.lua): beside the right bar while the
// map allows the debug menu, in a single-player game. Instant build, double speed, stopping the sun and the time of
// day, and spawning bricktrons of each job and the four kinds of corruptron at the camera's target, through the
// game's own spawn commands (UNetProjectCmd.CmdSpawn...), as the Lua's fy_Bricktron:New did.
internal static class GameDebugSidebar
{
	internal static bool Available
	{
		get { return Architecte.allowDebugMenu && !Neo.IsMultiplayer; }
	}

	internal static UiPanel Build(GameMenuModel menu)
	{
		UiPanel panel = new UiPanel { Id = "creative", Title = () => GameUiState.Text("##gamemenu_debug_title"), Group = menu.Right };
		panel.Section("disclaimer", null).Add(new UiText
		{
			Id = "debugDisclaimer",
			Text = () => GameUiState.Text("##gamemenu_debug_disclaimer"),
			Color = () => LuiStyle.CastleYellow,
			FontSize = LuiStyle.SmallFontSize
		});
		UiSection global = panel.Section("global", null);
		global.Add(new UiButton
		{
			Id = "debugInstantBuild",
			Label = () => GameUiState.Text("##gamemenu_debug_instantbuild"),
			Icon = () => GameUiState.Icon(IconKeys._Build_Brick),
			Highlight = () => Architecte.editorMode,
			OnClick = (bool shift, bool ctrl) =>
			{
				Achievements.AchievementTracker.Cheated("instant build");
				Architecte.editorMode = !Architecte.editorMode;
				GameSignals.Invoke(GameSignals.OnEditorModeChanged, Architecte.editorMode);
			}
		});
		global.Add(new UiButton
		{
			Id = "debugDoubleSpeed",
			Label = () => GameUiState.Text("##gamemenu_debug_timex2"),
			Icon = () => GameUiState.Icon(IconKeys.UI_Play),
			Highlight = () => ClockConfig.GetClockSpeedFromTimeScale() == ClockConfig.ClockSpeed.Double,
			OnClick = (bool shift, bool ctrl) => ClockConfig.ToggleClockSpeed(ClockConfig.ClockSpeed.Double)
		});
		global.Add(new UiButton
		{
			Id = "debugPauseDaytime",
			Label = () => GameUiState.Text("##gamemenu_debug_pausedaytime"),
			Icon = () => GameUiState.Icon(IconKeys.UI_Pause),
			Highlight = () => Journee.instance != null && Journee.instance.IsPaused,
			OnClick = (bool shift, bool ctrl) =>
			{
				Achievements.AchievementTracker.Cheated("the debug sidebar's pause of the day");
				if (Journee.instance != null)
				{
					Journee.instance.IsPaused = !Journee.instance.IsPaused;
				}
			}
		});
		panel.Section("timeOfDay", () => GameUiState.Text("##gamemenu_debug_timeofday")).Add(new UiStepper
		{
			Id = "debugTimeOfDay",
			Label = () => GameUiState.Text("##gamemenu_debug_currenttime"),
			Value = () => (Journee.instance != null) ? Journee.instance.Heure.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " h" : string.Empty,
			// An hour at a time (Shift: ten), round the clock.
			Step = (int delta) =>
			{
				if (Journee.instance != null)
				{
					Achievements.AchievementTracker.Cheated("the debug sidebar's time of day");
					float time = Mathf.Repeat(Mathf.Round(Journee.instance.Heure) + delta, 24f);
					Journee.instance.SetTime(time);
				}
			}
		});
		// The mod's developer reload: the menu's code from the newest build, without restarting the game.
		panel.Section("developer", () => "Developer").Add(new UiButton
		{
			Id = "debugReloadUi",
			Label = () => "Reload UI",
			Tooltip = () => "Reload UI: loads the menu's code again from the newest build (CastleStoryPlus.UI.dll) and builds the menu again.",
			Icon = () => GameUiState.Icon(IconKeys._UI_Wheel),
			IconColor = () => LuiStyle.CastleYellow,
			OnClick = (bool shift, bool ctrl) => UiAssembly.RequestReload()
		});
		UiSection bricktrons = panel.Section("spawnBricktrons", () => GameUiState.Text("##gamemenu_debug_spawnbricktrons"));
		bricktrons.Add(Spawn("debugWorker", "##gamemenu_debug_worker", IconKeys._Bricktron, (UNetProjectCmd cmd, Vector3 at, GameObject faction) => cmd.CmdSpawn(Test.Bricktron.Factory, Test.Bricktron.Name, at, Quaternion.identity, faction, Occupation.Type.Builder, false)));
		bricktrons.Add(Spawn("debugHalberdier", "##gamemenu_debug_halberdier", IconKeys._Halberdier, (UNetProjectCmd cmd, Vector3 at, GameObject faction) => cmd.CmdSpawnHalbardier(Test.Bricktron.Factory, Test.Bricktron.Name, at, Quaternion.identity, faction)));
		bricktrons.Add(Spawn("debugKnight", "##gamemenu_debug_knight", IconKeys._Knight, (UNetProjectCmd cmd, Vector3 at, GameObject faction) => cmd.CmdSpawn(Test.Bricktron.Factory, Test.Bricktron.Name, at, Quaternion.identity, faction, Occupation.Type.Knight, true)));
		bricktrons.Add(Spawn("debugArcher", "##gamemenu_debug_archer", IconKeys._Archer, (UNetProjectCmd cmd, Vector3 at, GameObject faction) => cmd.CmdSpawn(Test.Bricktron.Factory, Test.Bricktron.Name, at, Quaternion.identity, faction, Occupation.Type.Archer, true)));
		bricktrons.Add(Spawn("debugArbalist", "##gamemenu_debug_arbalist", IconKeys._Arbalist, (UNetProjectCmd cmd, Vector3 at, GameObject faction) => cmd.CmdSpawnCrossbowTron(Test.Bricktron.Factory, Test.Bricktron.Name, at, Quaternion.identity, faction)));
		bricktrons.Add(Spawn("debugAlchemist", "##gamemenu_debug_alchemist", IconKeys._Alchemist, (UNetProjectCmd cmd, Vector3 at, GameObject faction) => cmd.CmdSpawnAlchemist(Test.Bricktron.Factory, Test.Bricktron.Name, at, Quaternion.identity, faction)));
		bricktrons.Add(Spawn("debugArtificer", "##gamemenu_debug_artificer", IconKeys._Artificer, (UNetProjectCmd cmd, Vector3 at, GameObject faction) => cmd.CmdSpawnGeomancer(Test.Bricktron.Factory, Test.Bricktron.Name, at, Quaternion.identity, faction)));
		UiSection enemies = panel.Section("spawnCorruptrons", () => GameUiState.Text("##gamemenu_debug_spawncorruptrons"));
		enemies.Add(Enemy("debugMinitron", "##gamemenu_debug_minitron", IconKeys._Minitron, Test.Minitron, Occupation.Type.Minitron));
		enemies.Add(Enemy("debugCorruptron", "##gamemenu_debug_corruptron", IconKeys._Corruptron, Test.Corruptron, Occupation.Type.Corruptron));
		enemies.Add(Enemy("debugBiftron", "##gamemenu_debug_biftron", IconKeys._Biftron, Test.Biftron, Occupation.Type.Biftron));
		enemies.Add(Enemy("debugMagitron", "##gamemenu_debug_magitron", IconKeys._Warlock, Test.Magitron, Occupation.Type.Magitron));
		return panel;
	}

	internal static UiButton Button(UiPanel panel)
	{
		return new UiButton
		{
			Id = "creativeButton",
			Visible = () => Available,
			Label = () => GameUiState.Text("##gamemenu_debug_title"),
			Icon = () => GameUiState.Icon(IconKeys.UI_Console),
			Selected = () => panel.IsOpen,
			OnClick = (bool shift, bool ctrl) => panel.Toggle()
		};
	}

	// A spawn button: one unit at the camera's target, of the local player's team.
	private static UiButton Spawn(string id, string label, IconKey icon, Action<UNetProjectCmd, Vector3, GameObject> spawn)
	{
		return new UiButton
		{
			Id = id,
			Label = () => GameUiState.Text(label),
			Icon = () => GameUiState.Icon(icon),
			IconColor = () => LuiStyle.White,
			OnClick = (bool shift, bool ctrl) =>
			{
				UNetProjectCmd cmd = (User.LocalUser != null) ? User.LocalUser.GetComponent<UNetProjectCmd>() : null;
				Faction faction = (User.LocalUser != null) ? User.LocalUser.faction : null;
				if (cmd != null && faction != null)
				{
					Achievements.AchievementTracker.Cheated("the debug sidebar's spawning");
					spawn(cmd, CameraScript.Target, faction.gameObject);
				}
			}
		};
	}

	// A corruptron of the enemy team (the AI team, as the Lua took the team with no owner).
	private static UiButton Enemy(string id, string label, IconKey icon, Factory.AssetKey key, Occupation.Type occupation)
	{
		return Spawn(id, label, icon, (UNetProjectCmd cmd, Vector3 at, GameObject own) =>
		{
			Faction enemy = Faction.FindByName("Corruptrons") ?? Faction.DefaultEnemy;
			if (enemy != null)
			{
				cmd.CmdSpawn(key.Factory, key.Name, at, Quaternion.identity, enemy.gameObject, occupation, true);
			}
		});
	}
}
