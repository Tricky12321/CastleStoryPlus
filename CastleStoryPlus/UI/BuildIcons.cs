using System.Collections.Generic;
using System.IO;
using CastleStoryPlus.Core;
using MoonSharp.Interpreter;
using UnityEngine;

namespace CastleStoryPlus.UI;

// Build menu icons of the mod's buildings and blocks (Assets/Build/<name>.png, made by tools/make_build_icons.py),
// shown through CastleStoryPlus.BuildIcon("<name>") in the build menu's Lua in place of a game icon. Registered by
// each feature that adds menu entries.
internal static class BuildIcons
{
	private static readonly Dictionary<string, Sprite> Sprites = new Dictionary<string, Sprite>();

	internal static void Register()
	{
		LuaInjection.AddFunction("BuildIcon", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			Sprite sprite = SpriteFor((args.Count > 0) ? args[0].CastToString() : null);
			return (sprite != null) ? DynValue.FromObject(context.OwnerScript, sprite) : DynValue.Nil;
		});
	}

	// The Lua for a menu entry's icon: the mod's icon, or the game's icon when the file is missing.
	internal static string Lua(string name, string fallback)
	{
		return "||(CastleStoryPlus.BuildIcon(\"" + name + "\") or IconKeys." + fallback + ":Get64())";
	}

	private static Sprite SpriteFor(string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return null;
		}
		if (Sprites.TryGetValue(name, out Sprite sprite))
		{
			return sprite;
		}
		string path = Path.Combine(Path.Combine(Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "Assets"), "Build"), name + ".png");
		Texture2D texture = new Texture2D(2, 2, TextureFormat.ARGB32, mipmap: false);
		if (File.Exists(path) && texture.LoadImage(File.ReadAllBytes(path)))
		{
			texture.name = "Build " + name;
			texture.filterMode = FilterMode.Bilinear;
			sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
			Object.DontDestroyOnLoad(texture);
		}
		else
		{
			Plugin.Log.LogWarning("BuildIcons: " + path + " not found");
			Object.Destroy(texture);
			sprite = null;
		}
		Sprites[name] = sprite;
		return sprite;
	}
}
