using System.Collections.Generic;
using Brix.Components;
using Brix.Game.Utils;
using CastleStoryPlus.UI;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.LuaUi;

// The look of the game's Lua menus, for the C# menus that replace them. The colours are the ones LUI2.lua defines
// (CastleYellow is the game's LuaColor.castleYellow); fonts come from the game's LayoutPanelManager; the button and
// frame sprites are taken from the game's own live menus the first time they are asked for (the game ships them
// only inside its scene), with plain colour blocks until a menu has been seen.
internal static class LuiStyle
{
	public static readonly Color CastleYellow = new Color(1f, 197f / 255f, 10f / 255f, 1f);

	public static readonly Color CastleYellowLight = new Color(1f, 212f / 255f, 75f / 255f, 1f);

	public static readonly Color CastleRed = new Color(1f, 0f, 100f / 255f, 1f);

	public static readonly Color CastleBlue = new Color(0f, 160f / 255f, 1f, 1f);

	public static readonly Color White = Color.white;

	public static readonly Color Gray = Color.gray;

	public static readonly Color DarkGray = new Color(0.25f, 0.25f, 0.25f, 1f);

	// LUI2.lua's _bgColor* values: button and panel backgrounds.
	public static readonly Color BgNone = new Color(0f, 0f, 0f, 0f);

	public static readonly Color BgDefault = new Color(0.12f, 0.12f, 0.12f, 0.85f);

	public static readonly Color BgDark = new Color(0.07f, 0.07f, 0.07f, 0.98f);

	public static readonly Color BgSelected = new Color(0.10f, 0.10f, 0.10f, 1f);

	// The frame of a selected row or button: the yellow, a little see-through.
	public static readonly Color Highlight = new Color(1f, 197f / 255f, 10f / 255f, 0.8f);

	// The C# menus' panels (bars, sidebars, windows, tooltips): a little see-through.
	public static readonly Color BgPanel = new Color(0.09f, 0.09f, 0.09f, 0.85f);

	public static readonly Color BgHover = new Color(0.12f, 0.12f, 0.12f, 0.85f);

	public static readonly Color FgHover = new Color(1f / 3f, 1f / 3f, 1f / 3f, 0.5f);

	// A background under the mouse: lighter, and a see-through one gets a faint light fill.
	public static Color Hovered(Color background)
	{
		if (background.a < 0.1f)
		{
			return new Color(1f, 1f, 1f, 0.12f);
		}
		Color lighter = Color.Lerp(background, White, 0.15f);
		lighter.a = Mathf.Max(background.a, 0.95f);
		return lighter;
	}

	// The dark strip behind a hover label (ButtonPanel's HoverBgImage).
	public static readonly Color HoverLabelBg = new Color(0f, 0f, 0f, 0.75f);

	// LUI's default sizes: the bars' buttons are 48 pixels, their icons 32.
	public const float ButtonSize = 48f;

	public const float IconSize = 32f;

	public const int FontSize = 14;

	public const int SmallFontSize = 11;

	public const int TitleFontSize = 18;

	private static Sprite _buttonSprite;

	private static Sprite _frameSprite;

	private static bool _scraped;

	private static float _triedAt = -100f;

	public static Font Regular
	{
		get { return UiKit.Font; }
	}

	public static Font Bold
	{
		get
		{
			LayoutPanelManager fonts = BrixSingleton<LayoutPanelManager>.Instance;
			Font font = (fonts != null) ? fonts.ProximaNovaBold : null;
			return (font != null) ? font : UiKit.Font;
		}
	}

	// The titles' font (Korolev, as the game's headers).
	public static Font Title
	{
		get
		{
			LayoutPanelManager fonts = BrixSingleton<LayoutPanelManager>.Instance;
			Font font = (fonts != null) ? fonts.KorolevMedium : null;
			return (font != null) ? font : Bold;
		}
	}

	// A sliced sprite the game's buttons use, or null (plain colour).
	public static Sprite ButtonSprite
	{
		get
		{
			Scrape();
			return _buttonSprite;
		}
	}

	// A sliced sprite the game's framed panels use, or null (plain colour).
	public static Sprite FrameSprite
	{
		get
		{
			Scrape();
			return _frameSprite;
		}
	}

	// Looks through the game's live Lua menus: the sliced sprites most used by images with a Button (buttons) and
	// without (frames). Retried until one is found, so a menu built later still lends its look.
	private static void Scrape()
	{
		// Walking every menu's images is not cheap: at most every few seconds until the game's menus are there.
		if (_scraped || Time.realtimeSinceStartup - _triedAt < 5f)
		{
			return;
		}
		_triedAt = Time.realtimeSinceStartup;
		Dictionary<Sprite, int> buttons = new Dictionary<Sprite, int>();
		Dictionary<Sprite, int> frames = new Dictionary<Sprite, int>();
		foreach (Brix.UI.Builder.Menu.Component.LuaMenuComponent component in Object.FindObjectsOfType<Brix.UI.Builder.Menu.Component.LuaMenuComponent>())
		{
			foreach (Image image in component.GetComponentsInChildren<Image>(true))
			{
				if (image.sprite == null || image.type != Image.Type.Sliced)
				{
					continue;
				}
				Dictionary<Sprite, int> counts = (image.GetComponent<Button>() != null) ? buttons : frames;
				counts.TryGetValue(image.sprite, out int count);
				counts[image.sprite] = count + 1;
			}
		}
		_buttonSprite = MostUsed(buttons);
		_frameSprite = MostUsed(frames);
		if (_buttonSprite != null || _frameSprite != null)
		{
			_scraped = true;
			Plugin.Log.LogInfo("CsMenus: style taken from the game's menus (button sprite " + Name(_buttonSprite) + ", frame sprite " + Name(_frameSprite) + ")");
		}
	}

	private static Sprite MostUsed(Dictionary<Sprite, int> counts)
	{
		Sprite best = null;
		int bestCount = 0;
		foreach (KeyValuePair<Sprite, int> pair in counts)
		{
			if (pair.Value > bestCount)
			{
				best = pair.Key;
				bestCount = pair.Value;
			}
		}
		return best;
	}

	private static string Name(Sprite sprite)
	{
		return (sprite != null) ? sprite.name : "none";
	}

	// Gives an image the game's sliced sprite (when known) and a colour.
	public static void Paint(Image image, Sprite sprite, Color color)
	{
		image.color = color;
		if (sprite != null)
		{
			image.sprite = sprite;
			image.type = Image.Type.Sliced;
		}
	}
}
