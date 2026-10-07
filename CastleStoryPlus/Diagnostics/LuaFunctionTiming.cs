using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using MoonSharp.Interpreter;

namespace CastleStoryPlus.Diagnostics;

// Which Lua function C# called: "lua: called from C#" says only that Lua ran, so each outermost call is also
// charged to a bucket of its own, named after the function and where it starts ("lua fn OnClick
// LUI/Menus/GameMenu.lua:812"). Only the outermost call (Lua that C# called while no Lua runs), so a spike's top
// names the Lua that C# started. Timed on its own, not on FrameMonitor's stack: Harmony runs this finalizer and the
// "called from C#" one in the same order as their prefixes, not nested.
internal static class LuaFunctionTiming
{
	private static readonly Dictionary<Script, Dictionary<int, FrameMonitor.Bucket>> Buckets = new Dictionary<Script, Dictionary<int, FrameMonitor.Bucket>>();

	private static FieldInfo _byteCode;

	private static FieldInfo _code;

	private static FieldInfo _name;

	private static FieldInfo _source;

	private static int _depth;

	internal static void Patch(Harmony harmony, Type processor)
	{
		_byteCode = AccessTools.Field(typeof(Script), "m_ByteCode");
		HarmonyMethod prefix = new HarmonyMethod(typeof(LuaFunctionTiming), nameof(Prefix));
		HarmonyMethod finalizer = new HarmonyMethod(typeof(LuaFunctionTiming), nameof(Finalizer));
		int patched = 0;
		foreach (MethodInfo method in processor.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
		{
			ParameterInfo[] parameters = method.GetParameters();
			if (method.Name != "Call" || method.IsGenericMethodDefinition || parameters.Length == 0 || parameters[0].Name != "function" || parameters[0].ParameterType != typeof(DynValue))
			{
				continue;
			}
			try
			{
				harmony.Patch(method, prefix: prefix, finalizer: finalizer);
				patched++;
			}
			catch (Exception ex)
			{
				Plugin.Log.LogWarning("Performance: could not measure Lua functions in " + method + ": " + ex.Message);
			}
		}
		Plugin.Log.LogInfo("Performance: Lua functions measured one by one (" + patched + " entry points)" + ((_byteCode == null) ? ", without their names (no Script.m_ByteCode)" : string.Empty));
	}

	private static void Prefix(DynValue function, out long __state)
	{
		__state = -1L;
		_depth++;
		if (_depth == 1 && function.Type == DataType.Function && function.Function != null)
		{
			__state = Stopwatch.GetTimestamp();
		}
	}

	private static void Finalizer(DynValue function, long __state)
	{
		_depth--;
		if (_depth < 0)
		{
			_depth = 0;
		}
		if (__state < 0L)
		{
			return;
		}
		long ticks = Stopwatch.GetTimestamp() - __state;
		try
		{
			FrameMonitor.Bucket bucket = BucketOf(function.Function);
			bucket.FrameTicks += ticks;
			bucket.FrameCalls++;
		}
		catch (Exception)
		{
		}
	}

	private static FrameMonitor.Bucket BucketOf(Closure closure)
	{
		Script script = closure.OwnerScript;
		if (!Buckets.TryGetValue(script, out Dictionary<int, FrameMonitor.Bucket> byEntry))
		{
			byEntry = new Dictionary<int, FrameMonitor.Bucket>();
			Buckets[script] = byEntry;
		}
		int entry = closure.EntryPointByteCodeLocation;
		if (!byEntry.TryGetValue(entry, out FrameMonitor.Bucket bucket))
		{
			bucket = FrameMonitor.Named("lua fn " + Describe(script, entry));
			byEntry[entry] = bucket;
		}
		return bucket;
	}

	// The function's name (the Meta instruction at its entry point) and the first source line after it.
	private static string Describe(Script script, int entry)
	{
		try
		{
			object byteCode = _byteCode?.GetValue(script);
			if (byteCode == null)
			{
				return "@" + entry;
			}
			_code ??= AccessTools.Field(byteCode.GetType(), "Code");
			System.Collections.IList code = (System.Collections.IList)_code.GetValue(byteCode);
			if (entry < 0 || entry >= code.Count)
			{
				return "@" + entry;
			}
			object first = code[entry];
			_name ??= AccessTools.Field(first.GetType(), "Name");
			_source ??= AccessTools.Field(first.GetType(), "SourceCodeRef");
			string name = (_name.GetValue(first) as string) ?? "?";
			for (int i = entry; i < code.Count && i < entry + 8; i++)
			{
				object reference = _source.GetValue(code[i]);
				if (reference is MoonSharp.Interpreter.Debugging.SourceRef sourceRef && sourceRef.FromLine > 0)
				{
					return name + " " + script.GetSourceCode(sourceRef.SourceIdx).Name + ":" + sourceRef.FromLine;
				}
			}
			return name + " @" + entry;
		}
		catch (Exception)
		{
			return "@" + entry;
		}
	}
}
