#if UNITY6
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using Brix.NewUI.UserSettings_;
using Brix.UI.Builder.Menu;
using Brix.Utils;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Core;

// Screen resolution and refresh rate are two choices in Options > Graphics. The game listed every width x height x
// rate the screen offers in one list, saved only the width and height and set them without a rate, so the rate was
// whatever the system chose. Now the resolution list has each size once, and a "Refresh rate" row under it (a copy of
// the resolution row, opening the same list window) lists the rates the screen offers at the chosen size, or Auto
// (the system's). The rate is kept in [Display] RefreshRate (the game's settings file has no place for it) and set
// with the resolution in full screen. On Windows a rate other than the desktop's needs exclusive full screen;
// elsewhere (Linux) the system's own full screen mode can still decide the rate.
[Feature]
internal static class RefreshRateSetting
{
	// Not one of the game's operations: only the refresh rate row and menu use it.
	internal const LegacyBaseMenu.Operation Operation = (LegacyBaseMenu.Operation)7302;

	internal const string Title = "&&Refresh rate";

	private static ConfigEntry<string> _rate;

	private static void Enable()
	{
		_rate = Plugin.Cfg.Bind("Display", "RefreshRate", string.Empty, "Refresh rate picked in Options > Graphics > Refresh rate, as numerator/denominator (e.g. 240000/1000). Empty: the system's.");
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

	// A rate with numerator 0: the system's.
	internal static void Set(RefreshRate rate)
	{
		if (_rate != null)
		{
			_rate.Value = (rate.numerator == 0 || rate.denominator == 0) ? string.Empty : rate.numerator + "/" + rate.denominator;
		}
	}

	// Rates that differ by less than half a hertz are the same (239.96 and 240, 143.93 and 144: the same screen mode as
	// reported by the mode list and by the display).
	internal static bool Same(RefreshRate a, RefreshRate b)
	{
		return System.Math.Abs(a.value - b.value) < 0.5;
	}

	internal static string Label(RefreshRate rate)
	{
		double rounded = System.Math.Round(rate.value);
		return (System.Math.Abs(rate.value - rounded) < 0.05 ? rounded.ToString("0") : rate.value.ToString("0.##")) + " HZ";
	}

	// The rates the screen offers at a size, lowest first, each once; at a size it does not list (a window's size, as
	// the game saves it while windowed), every rate it offers.
	internal static List<RefreshRate> RatesAt(int width, int height)
	{
		List<RefreshRate> rates = Rates(width, height, true);
		return (rates.Count > 0) ? rates : Rates(width, height, false);
	}

	private static List<RefreshRate> Rates(int width, int height, bool sized)
	{
		List<RefreshRate> rates = new List<RefreshRate>();
		foreach (Resolution resolution in Screen.resolutions)
		{
			if (sized && (resolution.width != width || resolution.height != height))
			{
				continue;
			}
			RefreshRate rate = resolution.refreshRateRatio;
			if (rate.numerator == 0 || rates.Exists((RefreshRate r) => Same(r, rate)))
			{
				continue;
			}
			rates.Add(rate);
		}
		// The rate the screen runs at now: a Wayland desktop lists only 60 Hz modes to the game, also on a faster screen.
		foreach (RefreshRate current in new RefreshRate[] { Screen.currentResolution.refreshRateRatio, Screen.mainWindowDisplayInfo.refreshRate })
		{
			if (current.numerator != 0 && current.denominator != 0 && !rates.Exists((RefreshRate r) => Same(r, current)))
			{
				rates.Add(current);
			}
		}
		rates.Sort((RefreshRate a, RefreshRate b) => a.value.CompareTo(b.value));
		return rates;
	}

	// The resolution list: each size once, "W x H" (the game's list had one entry per rate).
	[Feature]
	[HarmonyPatch(typeof(LegacyResolutionMenu), "InitOptions")]
	private static class SizesPatch
	{
		private static bool Prefix(LegacyResolutionMenu __instance)
		{
			__instance._options.Clear();
			__instance._optionMap.Clear();
			foreach (Resolution resolution in Screen.resolutions)
			{
				string text = resolution.width + " x " + resolution.height;
				if (!__instance._options.Contains(text))
				{
					__instance._options.Add(text);
					__instance._optionMap.Add(text, resolution);
				}
			}
			return false;
		}
	}

	// The refresh rate menu beside the resolution menu (on the same object, using the same row container and list
	// window), and its row on the Graphics page.
	[Feature]
	[HarmonyPatch(typeof(LegacyResolutionMenu), "Init")]
	private static class AddMenuPatch
	{
		private static void Postfix(LegacyResolutionMenu __instance)
		{
			if (__instance == null || __instance.preModalContainer == null || __instance.enumContainer == null)
			{
				return;
			}
			RefreshRateMenu menu = __instance.GetComponent<RefreshRateMenu>();
			if (menu == null)
			{
				menu = __instance.gameObject.AddComponent<RefreshRateMenu>();
				menu.CopyFrom(__instance);
				Plugin.Log.LogInfo("RefreshRate: refresh rate option added to " + __instance.name);
			}
			RefreshRateRow.Ensure(__instance.preModalContainer);
			menu.Setup();
		}
	}

	// A page shown again builds its rows again: the refresh rate row too.
	[Feature]
	[HarmonyPatch(typeof(Container), "OnEnable")]
	private static class RowPatch
	{
		private static void Postfix(Container __instance)
		{
			if (__instance != null && __instance.GetItem(LegacyBaseMenu.Operation.Resolution) != null)
			{
				RefreshRateRow.Ensure(__instance);
			}
		}
	}

	// After the game's own SetResolution (size and full screen only): the kept rate, when full screen and not already
	// on it.
	[Feature]
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

// The "Refresh rate" row of the Graphics page: a copy of the resolution row (look, layout, the click that opens the
// list window), with its own operation and title.
internal static class RefreshRateRow
{
	internal static void Ensure(Container container)
	{
		Container.Item source = container.GetItem(LegacyBaseMenu.Operation.Resolution);
		if (source == null || source.instance == null)
		{
			return;
		}
		Container.Item item = container.GetItem(RefreshRateSetting.Operation);
		if (item != null && item.instance != null)
		{
			return;
		}
		if (item == null)
		{
			item = new Container.Item();
			foreach (FieldInfo field in typeof(Container.Item).GetFields(BindingFlags.Public | BindingFlags.Instance))
			{
				field.SetValue(item, field.GetValue(source));
			}
			item.containerPopulatorOperation = RefreshRateSetting.Operation;
			item.titleText = RefreshRateSetting.Title;
			item.localizeTitle = RefreshRateSetting.Title;
			item.subtitleText = "&&Set your screen's refresh rate (full screen)";
			item.localizeSubtitle = item.subtitleText;
			item.subsubtitleText = string.Empty;
			item.isDefaultInGroup = false;
			item.actionItems = new List<Container.Item.ActionItem>(source.actionItems);
			container.items.Add(item);
		}
		GameObject go = Object.Instantiate(source.instance.gameObject, source.instance.transform.parent, false);
		go.name = "CSP_RefreshRateOption";
		go.hideFlags = HideFlags.DontSave;
		go.transform.SetSiblingIndex(source.instance.transform.GetSiblingIndex() + 1);
		Containee containee = go.GetComponent<Containee>();
		containee.container = container;
		go.SetActive(source.instance.gameObject.activeSelf);
		item.instance = containee;
		if (containee.title != null)
		{
			string title = I2Helper.TryGet(item.titleText);
			containee.title.text = item.IsTitleCaps ? title.ToUpper() : title;
		}
		if (containee.subtitle != null)
		{
			string subtitle = I2Helper.TryGet(item.subtitleText);
			containee.subtitle.text = item.IsSubtitleCaps ? subtitle.ToUpper() : subtitle;
		}
		if (containee.subsubtitle != null)
		{
			containee.subsubtitle.text = string.Empty;
		}
	}
}

// The refresh rate list: Auto (the system's rate), then every rate the screen offers at the resolution chosen in the
// options. Applying it keeps the rate ([Display] RefreshRate) and sets the options into the game, which sets it.
internal class RefreshRateMenu : LegacyBaseEnumMenu
{
	private readonly List<RefreshRate> _rates = new List<RefreshRate>();

	private int _width = -1;

	private int _height = -1;

	private bool _ready;

	public override Operation TargetOperation
	{
		get { return RefreshRateSetting.Operation; }
	}

	public override int OptionCount
	{
		get
		{
			Rates();
			return _rates.Count + 1;
		}
	}

	// The resolution menu's containers, texts and colours: the same row container and list window.
	internal void CopyFrom(LegacyBaseEnumMenu source)
	{
		foreach (FieldInfo field in typeof(LegacyBaseEnumMenu).GetFields(BindingFlags.Public | BindingFlags.Instance))
		{
			field.SetValue(this, field.GetValue(source));
		}
		_ready = true;
	}

	// Connected again with the copied containers (the first OnEnable ran before they were set).
	internal void Setup()
	{
		Disconnect();
		Connect();
		Init();
	}

	public override void Connect()
	{
		if (_ready)
		{
			base.Connect();
		}
	}

	public override void Init()
	{
		if (_ready)
		{
			_width = -1;
			base.Init();
		}
	}

	// The rates at the size chosen in the options (not yet applied), else at the screen's size.
	private void Rates()
	{
		UserSettings settings = UserSettingManager.TempSettings.Settings;
		int width = (settings != null && settings.graphic != null && settings.graphic.screenResolutionX > 0) ? settings.graphic.screenResolutionX : Screen.currentResolution.width;
		int height = (settings != null && settings.graphic != null && settings.graphic.screenResolutionY > 0) ? settings.graphic.screenResolutionY : Screen.currentResolution.height;
		if (width == _width && height == _height)
		{
			return;
		}
		_width = width;
		_height = height;
		_rates.Clear();
		_rates.AddRange(RefreshRateSetting.RatesAt(width, height));
	}

	public override bool CompareWithSettings(UserSettings settings, int optionIndex)
	{
		GetValueFromSettings(settings, out int current);
		return optionIndex == current;
	}

	public override void GetValueFromSettings(UserSettings settings, out int optionIndex)
	{
		Rates();
		optionIndex = 0;
		if (RefreshRateSetting.TryGet(out RefreshRate kept))
		{
			int found = _rates.FindIndex((RefreshRate r) => RefreshRateSetting.Same(r, kept));
			if (found >= 0)
			{
				optionIndex = found + 1;
			}
		}
	}

	public override void SetValueAtSettings(UserSettings settings, int optionIndex)
	{
		Rates();
		RefreshRateSetting.Set((optionIndex > 0 && optionIndex <= _rates.Count) ? _rates[optionIndex - 1] : default(RefreshRate));
	}

	public override string GetTitleLabel()
	{
		return I2Helper.TryGet(RefreshRateSetting.Title).ToUpper();
	}

	public override string GetOptionLabel(int optionIndex)
	{
		Rates();
		if (optionIndex > 0 && optionIndex <= _rates.Count)
		{
			return RefreshRateSetting.Label(_rates[optionIndex - 1]);
		}
		return "AUTO";
	}

	public override string GetOptionDescription(int optionIndex)
	{
		return string.Empty;
	}
}
#endif
