using System.Collections.Generic;
using BepInEx.Configuration;
using CastleStoryPlus.Core;
using HarmonyLib;
using MoonSharp.Interpreter;

namespace CastleStoryPlus.Loading;

// The game UI's Lua runs dofile/loadfile on the same panel files hundreds of times while a map loads (one per
// button, divider, key binding row, ...), and MoonSharp parses and compiles the file every time. While a map
// loads, a file loaded again into the same script, with the same environment and the same text, returns the
// function compiled the first time. A file is a plain function of its environment, so calling it again runs
// it exactly like a fresh load (new locals, new tables).
// Only within one script: compiled code belongs to its script and is never moved to another one (restoring
// dumped bytecode into other scripts broke the game's Lua).
[Feature(Features.FasterLoading, Features.FasterLoadingInfo)]
[HarmonyPatch(typeof(Script), nameof(Script.LoadFile))]
internal static class LuaChunkReuse
{
	private class Loaded
	{
		public string Code;

		public Table Environment;

		public DynValue Function;
	}

	internal static ConfigEntry<bool> Enabled;

	private static readonly Dictionary<Script, Dictionary<string, Loaded>> Functions = new Dictionary<Script, Dictionary<string, Loaded>>();

	private static bool _active;

	public static int Compiled;

	public static int Reused;

	private static void Enable()
	{
		Enabled = Plugin.Cfg.Bind("Loading", "LuaChunkReuse", true, "While a map loads, a Lua file loaded again into the same script reuses its compiled code instead of parsing it again.");
	}

	public static void Start()
	{
		Functions.Clear();
		Compiled = 0;
		Reused = 0;
		_active = Enabled == null || Enabled.Value;
	}

	public static void Stop()
	{
		_active = false;
		Functions.Clear();
	}

	private static bool Prefix(Script __instance, string filename, Table globalContext, string friendlyFilename, ref DynValue __result)
	{
		if (!_active || __instance.Options.ScriptLoader == null)
		{
			return true;
		}
		Table environment = globalContext ?? __instance.Globals;
		string resolved = __instance.Options.ScriptLoader.ResolveFileName(filename, environment);
		if (!(__instance.Options.ScriptLoader.LoadFile(resolved, environment) is string code))
		{
			return true;
		}
		if (!Functions.TryGetValue(__instance, out Dictionary<string, Loaded> files))
		{
			files = new Dictionary<string, Loaded>();
			Functions[__instance] = files;
		}
		if (files.TryGetValue(resolved, out Loaded loaded) && ReferenceEquals(loaded.Environment, environment) && loaded.Code == code)
		{
			Reused++;
			__result = loaded.Function;
			return false;
		}
		// Same as the game's own path for text files.
		__result = __instance.LoadString(code, globalContext, friendlyFilename ?? resolved);
		files[resolved] = new Loaded { Code = code, Environment = environment, Function = __result };
		Compiled++;
		return false;
	}
}
