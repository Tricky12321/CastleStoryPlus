#if UNITY6
using BepInEx.Configuration;
using Brix.NewUI.UserSettings_;
using Brix.UI.Builder.Menu;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Core;

// The refresh rate picked in Options > Screen resolution is kept and used. The game lists every width x height x rate
// the screen offers, but saved only the width and height and set them without a rate, so the rate was whatever the
// system chose; picking another rate at the same size did not even set the resolution again. And the list showed the
// first entry of that size (the lowest rate), not the one picked. The rate is kept in [Display] RefreshRate (the
// game's settings file has no place for it) and set with the resolution in full screen. On Windows a rate other than
// the desktop's needs exclusive full screen; elsewhere (Linux, Proton's fallback) the system's own full screen mode
// decides the rate.
[Feature]
internal static class RefreshRateSetting
{
	private static ConfigEntry<string> _rate;

	private static void Enable()
	{
		_rate = Plugin.Cfg.Bind("Display", "RefreshRate", string.Empty, "Refresh rate picked in Options > Screen resolution, as numerator/denominator (e.g. 240000/1000). Empty: the system's.");
	}

	internal static bool TryGet(out RefreshRate rate)
	{
		rate = default(RefreshRate);
		if (_rate == null || string.IsNullOrEmpty(_rate.Value))
		{
			return false;
		}
		string[] parts = _rate.Value.Split('/');
		if (parts.Length != 2 || !uint.TryParse(parts[0], out uint numerator) || !uint.TryParse(parts[1], out uint denominator) || numerator == 0 || denominator == 0)
		{
			return false;
		}
		rate = new RefreshRate { numerator = numerator, denominator = denominator };
		return true;
	}

	internal static void Set(RefreshRate rate)
	{
		if (_rate != null)
		{
			_rate.Value = (rate.numerator == 0 || rate.denominator == 0) ? string.Empty : rate.numerator + "/" + rate.denominator;
		}
	}

	// Rates that differ by less than 0.05 Hz are the same (239.96 and 239.957...).
	internal static bool Same(RefreshRate a, RefreshRate b)
	{
		return System.Math.Abs(a.value - b.value) < 0.05;
	}

	// The option to show for a size: the kept rate, else the rate the screen runs at, else the first of that size.
	[HarmonyPatch(typeof(LegacyResolutionMenu), "FindResolution")]
	private static class FindPatch
	{
		private static bool Prefix(LegacyResolutionMenu __instance, int w, int h, ref int __result)
		{
			RefreshRate wanted;
			if (!TryGet(out wanted))
			{
				wanted = Screen.currentResolution.refreshRateRatio;
			}
			for (int i = 0; i < __instance._options.Count; i++)
			{
				Resolution resolution = __instance._optionMap[__instance._options[i]];
				if (resolution.width == w && resolution.height == h && Same(resolution.refreshRateRatio, wanted))
				{
					__result = i;
					return false;
				}
			}
			return true;
		}
	}

	[HarmonyPatch(typeof(LegacyResolutionMenu), "SetValueAtSettings")]
	private static class PickPatch
	{
		private static void Postfix(LegacyResolutionMenu __instance, int optionIndex)
		{
			if (optionIndex >= 0 && optionIndex < __instance._options.Count)
			{
				Set(__instance._optionMap[__instance._options[optionIndex]].refreshRateRatio);
			}
		}
	}

	// After the game's own SetResolution (size and full screen only): the kept rate, when full screen and not already
	// on it.
	[HarmonyPatch(typeof(UserSettingManager), nameof(UserSettingManager.SetSettingIntoGame))]
	private static class ApplyPatch
	{
		private static void Postfix(UserSettings settings)
		{
			if (settings == null || settings.graphic == null)
			{
				return;
			}
			int width = settings.graphic.screenResolutionX;
			int height = settings.graphic.screenResolutionY;
			if (width == 0 || height == 0 || !settings.graphic.fullscreen || !TryGet(out RefreshRate wanted))
			{
				return;
			}
			Resolution current = Screen.currentResolution;
			if (current.width == width && current.height == height && Same(current.refreshRateRatio, wanted))
			{
				return;
			}
			bool exclusive = Application.platform == RuntimePlatform.WindowsPlayer && !Same(Screen.mainWindowDisplayInfo.refreshRate, wanted);
			FullScreenMode mode = exclusive ? FullScreenMode.ExclusiveFullScreen : FullScreenMode.FullScreenWindow;
			Plugin.Log.LogInfo("RefreshRate: " + width + " x " + height + " @ " + wanted.value.ToString("0.##") + " Hz, " + mode);
			Screen.SetResolution(width, height, mode, wanted);
		}
	}
}
#endif
