using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Brix.Lua;
using HarmonyLib;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Loaders;

namespace CastleStoryPlus.Core;

// Lets features change the game's Lua UI without shipping the game's own Lua files:
// - text patches are applied to a game Lua file when it is loaded (anchor + inserted mod code);
// - files in the plugin's Lua folder (BepInEx/plugins/CastleStoryPlus/Lua, same layout as Info/Lua)
//   are found first, so the mod can add new Lua files;
// - every script gets a global table "CastleStoryPlus" with the C# functions features register.
[Feature]
internal static class LuaInjection
{
	public enum Mode
	{
		InsertAfter,
		InsertBefore,
		Replace
	}

	private class TextPatch
	{
		public string File;

		public string Anchor;

		public string Text;

		public Mode Mode;

		public string Owner;

		// Set for a range patch: everything from Anchor to the first EndAnchor after it is replaced.
		public string EndAnchor;
	}

	public const string TableName = "CastleStoryPlus";

	private static readonly List<TextPatch> Patches = new List<TextPatch>();

	private static readonly Dictionary<string, Func<ScriptExecutionContext, CallbackArguments, DynValue>> Functions = new Dictionary<string, Func<ScriptExecutionContext, CallbackArguments, DynValue>>();

	public static readonly string PluginLuaDir = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "Lua");

	// file: path relative to Info/Lua, e.g. "LUI/Menus/GameMenu.lua". Line endings in anchor/text may be "\n".
	public static void AddPatch(string owner, string file, string anchor, Mode mode, string text)
	{
		Patches.Add(new TextPatch
		{
			Owner = owner,
			File = file.Replace('\\', '/'),
			Anchor = anchor,
			Text = text,
			Mode = mode
		});
	}

	// Replaces a whole block, from the start of anchor to the end of the first endAnchor after it. Range patches
	// are applied after all other patches, so a patch that inserts next to the block (for example after its last
	// line) still finds its anchor; a patch that inserted inside the block goes away with it.
	public static void AddRangePatch(string owner, string file, string anchor, string endAnchor, string text)
	{
		Patches.Add(new TextPatch
		{
			Owner = owner,
			File = file.Replace('\\', '/'),
			Anchor = anchor,
			EndAnchor = endAnchor,
			Text = text,
			Mode = Mode.Replace
		});
	}

	public static void AddFunction(string name, Func<ScriptExecutionContext, CallbackArguments, DynValue> function)
	{
		Functions[name] = function;
	}

	public static void AddAction(string name, Action action)
	{
		AddFunction(name, (ScriptExecutionContext context, CallbackArguments args) =>
		{
			action();
			return DynValue.Void;
		});
	}

	internal static string ApplyPatches(string file, string code)
	{
		if (string.IsNullOrEmpty(code))
		{
			return code;
		}
		string normalized = file.Replace('\\', '/');
		List<TextPatch> ordered = new List<TextPatch>();
		foreach (TextPatch patch in Patches)
		{
			if (patch.EndAnchor == null)
			{
				ordered.Add(patch);
			}
		}
		foreach (TextPatch patch in Patches)
		{
			if (patch.EndAnchor != null)
			{
				ordered.Add(patch);
			}
		}
		foreach (TextPatch patch in ordered)
		{
			if (!normalized.EndsWith(patch.File, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			// The game's Lua files use CRLF; match and insert with the file's own line endings.
			string newline = code.Contains("\r\n") ? "\r\n" : "\n";
			string anchor = patch.Anchor.Replace("\r\n", "\n").Replace("\n", newline);
			string text = patch.Text.Replace("\r\n", "\n").Replace("\n", newline);
			int index = code.IndexOf(anchor, StringComparison.Ordinal);
			if (index < 0)
			{
				Plugin.Log.LogWarning("Lua patch '" + patch.Owner + "' not applied: anchor not found in " + patch.File);
				continue;
			}
			if (patch.EndAnchor != null)
			{
				string endAnchor = patch.EndAnchor.Replace("\r\n", "\n").Replace("\n", newline);
				int end = code.IndexOf(endAnchor, index + anchor.Length, StringComparison.Ordinal);
				if (end < 0)
				{
					Plugin.Log.LogWarning("Lua patch '" + patch.Owner + "' not applied: end anchor not found in " + patch.File);
					continue;
				}
				end += endAnchor.Length;
				code = code.Remove(index, end - index).Insert(index, text);
				continue;
			}
			switch (patch.Mode)
			{
			case Mode.InsertAfter:
				code = code.Insert(index + anchor.Length, text);
				break;
			case Mode.InsertBefore:
				code = code.Insert(index, text);
				break;
			case Mode.Replace:
				code = code.Remove(index, anchor.Length).Insert(index, text);
				break;
			}
		}
		return code;
	}

	internal static string ResolveInPlugin(string name)
	{
		if (string.IsNullOrEmpty(name) || !Directory.Exists(PluginLuaDir))
		{
			return null;
		}
		string relative = name.Replace('\\', '/');
		if (relative.StartsWith("Info/Lua/", StringComparison.OrdinalIgnoreCase))
		{
			relative = relative.Substring("Info/Lua/".Length);
		}
		foreach (string candidate in new[] { relative, relative + ".lua", "LUI/" + relative, "LUI/" + relative + ".lua" })
		{
			string path = Path.Combine(PluginLuaDir, candidate);
			if (File.Exists(path))
			{
				return path;
			}
		}
		return null;
	}

	internal static void Register(Script script)
	{
		if (Functions.Count == 0)
		{
			return;
		}
		Table table = new Table(script);
		foreach (KeyValuePair<string, Func<ScriptExecutionContext, CallbackArguments, DynValue>> function in Functions)
		{
			Func<ScriptExecutionContext, CallbackArguments, DynValue> callback = function.Value;
			table[function.Key] = DynValue.NewCallback((ScriptExecutionContext context, CallbackArguments args) =>
			{
				try
				{
					return callback(context, args);
				}
				catch (Exception ex)
				{
					Plugin.Log.LogError("Lua call " + TableName + "." + function.Key + " failed: " + ex);
					return DynValue.Void;
				}
			});
		}
		script.Globals[TableName] = table;
	}
}

[Feature]
[HarmonyPatch]
internal static class LuaLoadFilePatch
{
	private static MethodBase TargetMethod()
	{
		return InterfaceMethod(nameof(IScriptLoader.LoadFile));
	}

	internal static MethodBase InterfaceMethod(string name)
	{
		InterfaceMapping map = typeof(BrixScriptLoader).GetInterfaceMap(typeof(IScriptLoader));
		for (int i = 0; i < map.InterfaceMethods.Length; i++)
		{
			if (map.InterfaceMethods[i].Name == name)
			{
				return map.TargetMethods[i];
			}
		}
		return null;
	}

	private static void Postfix(string file, ref object __result)
	{
		if (__result is string code)
		{
			__result = LuaInjection.ApplyPatches(file, code);
		}
	}
}

[Feature]
[HarmonyPatch]
internal static class LuaResolveFilePatch
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return LuaLoadFilePatch.InterfaceMethod(nameof(IScriptLoader.ResolveFileName));
		yield return LuaLoadFilePatch.InterfaceMethod(nameof(IScriptLoader.ResolveModuleName));
	}

	private static bool Prefix(object[] __args, ref string __result)
	{
		string path = LuaInjection.ResolveInPlugin(__args[0] as string);
		if (path == null)
		{
			return true;
		}
		__result = path;
		return false;
	}
}

[Feature]
[HarmonyPatch(typeof(LuaLoader), nameof(LuaLoader.Load_Impl))]
internal static class LuaGlobalsPatch
{
	private static void Prefix(ref Action<Script> preloadAction)
	{
		Action<Script> original = preloadAction;
		preloadAction = (Script script) =>
		{
			LuaInjection.Register(script);
			original?.Invoke(script);
		};
	}
}
