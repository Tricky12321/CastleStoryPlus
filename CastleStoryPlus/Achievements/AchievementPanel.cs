using System.Collections.Generic;
using Brix.UI.Icons;
using CastleStoryPlus.Core;
using CastleStoryPlus.GameUi;
using CastleStoryPlus.LuaUi;
using UnityEngine;

namespace CastleStoryPlus.Achievements;

// The list in the in-game menu: a sidebar opened from the right bar (a crown button), one block for each achievement
// with its picture, category, name, text and progress (37/100 and a bar; the date it was unlocked once it is);
// locked ones are greyed out. A line at the top says when nothing counts in this game (sandbox, cheats).
[Feature(Features.Achievements, Features.AchievementsInfo)]
internal static class AchievementPanel
{
	private static UiPanel _panel;

	private static void Enable()
	{
		GameMenuItems.Extend(Build);
	}

	// Counters changed: the list shows the new progress at once while it is open.
	internal static void Changed()
	{
		if (_panel != null && _panel.IsOpen)
		{
			GameUiState.Announce();
		}
	}

	private static void Build(GameMenuModel menu)
	{
		UiPanel panel = new UiPanel
		{
			Id = "achievements",
			Title = () => "Achievements " + AchievementTracker.UnlockedCount + "/" + AchievementList.All.Count,
			Group = menu.Right
		};
		panel.Section("state", null).Add(new UiText
		{
			Id = "achievementsState",
			Text = StateText,
			Color = () => AchievementTracker.Counting ? Color.white : LuiStyle.CastleYellow,
			FontSize = LuiStyle.SmallFontSize
		});
		Dictionary<string, UiSection> sections = new Dictionary<string, UiSection>();
		foreach (AchievementInfo achievement in AchievementList.All)
		{
			if (!sections.TryGetValue(achievement.Category, out UiSection section))
			{
				string category = achievement.Category;
				section = panel.Section("achievements" + category, () => category);
				sections[category] = section;
			}
			AchievementInfo info = achievement;
			section.Add(new UiStatus
			{
				Id = "achievement" + info.Id,
				Icon = () => GameUiState.Icon(info.Icon),
				Caption = () => info.Category,
				Title = () => info.Name,
				Detail = () => DetailText(info),
				Progress = () => Mathf.Clamp01(AchievementTracker.Progress(info) / (float)info.Goal),
				Dimmed = () => !AchievementTracker.IsUnlocked(info),
				FillPanel = true,
				Value = () => AchievementTracker.Progress(info) + "/" + info.Goal,
				Tooltip = () => info.Text
			});
		}
		menu.ExtraPanels.Add(panel);
		_panel = panel;
		// In the right bar above the spacer that pushes the task buttons down.
		int at = menu.RightBar.FindIndex((UiItem item) => item == null);
		menu.RightBar.Insert((at < 0) ? menu.RightBar.Count : at, new UiButton
		{
			Id = "achievementsButton",
			Label = () => "Achievements",
			Icon = () => GameUiState.Icon(IconKeys.UI_Crown),
			Selected = () => panel.IsOpen,
			Tooltip = () => "Achievements: " + AchievementTracker.UnlockedCount + " of " + AchievementList.All.Count + " unlocked",
			OnClick = (bool shift, bool ctrl) => panel.Toggle()
		});
	}

	// What the player sees under an achievement's name: its text and, below it, how far he has come.
	internal static string DetailText(AchievementInfo info)
	{
		if (AchievementTracker.IsUnlocked(info))
		{
			return info.Text + "\n<color=#FFCC40>Unlocked " + AchievementTracker.UnlockedAt(info) + "</color>";
		}
		return info.Text;
	}

	private static string StateText()
	{
		string why = AchievementTracker.Why();
		return (why == null) ? "Counting in this game." : "Not counting in this game: " + why + ".";
	}
}
