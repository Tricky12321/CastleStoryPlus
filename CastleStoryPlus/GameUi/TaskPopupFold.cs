using BepInEx.Configuration;
using CastleStoryPlus.Core;

namespace CastleStoryPlus.GameUi;

// The selected task's popup over the task list can be minimized, and remembers that per task (#198): minimizing the
// crystal's popup (the idle group, selected whenever no other task is) leaves the popup of every other task open,
// and going back to the crystal shows it minimized again. The crystal's choice is kept in [Menus]
// CrystalPopupMinimized (it is the same crystal in every game); the other tasks' and workshops' for the game being
// played. Kept here in the plugin, not in the reloadable UI assembly, so a UI reload keeps it.
[Feature(Features.CsMenus, Features.CsMenusInfo)]
internal static class TaskPopupFold
{
	private static readonly FoldMemory<object> Memory = new FoldMemory<object>();

	internal static ConfigEntry<bool> Crystal;

	private static void Enable()
	{
		Crystal = Plugin.Cfg.Bind("Menus", "CrystalPopupMinimized", false, "The crystal's (idle group's) popup over the task list is minimized. Set with the popup's minimize button; other tasks keep their own choice for the game.");
		GameSession.OnLeave(Memory.Clear);
	}

	// What the popup shows now: the selected workshop, else the selected task; null for nothing.
	internal static object Current
	{
		get
		{
			if (GameUiState.Station != null)
			{
				return GameUiState.Station;
			}
			return GameUiState.Selected;
		}
	}

	internal static bool IsCrystal(object key)
	{
		return key is IdleProject;
	}

	internal static bool IsMinimized(object key)
	{
		if (key == null)
		{
			return false;
		}
		if (IsCrystal(key))
		{
			return Crystal != null && Crystal.Value;
		}
		return Memory.IsFolded(key);
	}

	internal static void SetMinimized(object key, bool minimized)
	{
		if (key == null)
		{
			return;
		}
		if (IsCrystal(key))
		{
			if (Crystal != null)
			{
				Crystal.Value = minimized;
			}
		}
		else
		{
			Memory.Set(key, minimized);
		}
		GameUiState.Announce();
	}

	// The popup's minimize button: for what it shows now.
	internal static void Toggle()
	{
		object key = Current;
		SetMinimized(key, !IsMinimized(key));
	}

	internal static bool CurrentMinimized
	{
		get { return IsMinimized(Current); }
	}
}
