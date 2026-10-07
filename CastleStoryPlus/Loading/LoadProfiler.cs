using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Brix.Engine;
using Brix.External.Signals;
using Brix.Utils.Closures;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Loading;

// Named time buckets filled while a map loads (Lua menu per script, game signal per listener), written to the
// log with the load timing. Only active during a load, so the hooks cost nothing in game.
internal static class LoadProfiler
{
	private const int Shown = 60;

	private class Bucket
	{
		public string Name;

		public long Ticks;

		public long Longest;

		public int Count;

		public int Gcs;
	}

	private static readonly Dictionary<string, Bucket> Buckets = new Dictionary<string, Bucket>();

	public static bool Active;

	private static int _gcStart;

	public static void Start()
	{
		_gcStart = GC.CollectionCount(0);
		Buckets.Clear();
		Active = true;
	}

	public static void Add(string name, long ticks)
	{
		Add(name, ticks, 0);
	}

	public static void Add(string name, long ticks, int gcs)
	{
		if (!Buckets.TryGetValue(name, out Bucket bucket))
		{
			bucket = new Bucket { Name = name };
			Buckets[name] = bucket;
		}
		bucket.Ticks += ticks;
		bucket.Longest = Math.Max(bucket.Longest, ticks);
		bucket.Count++;
		bucket.Gcs += gcs;
	}

	public static void Stop(StringBuilder text)
	{
		Active = false;
		List<Bucket> list = new List<Bucket>(Buckets.Values);
		list.Sort((Bucket a, Bucket b) => b.Ticks.CompareTo(a.Ticks));
		text.Append("\n Garbage collections during the load: ").Append(GC.CollectionCount(0) - _gcStart);
		text.Append("\n Slowest parts (total, longest single call, calls):");
		for (int i = 0; i < list.Count && i < Shown; i++)
		{
			Bucket bucket = list[i];
			text.Append("\n  ").Append(bucket.Name).Append(": ").Append(Seconds(bucket.Ticks)).Append(" (longest ").Append(Seconds(bucket.Longest)).Append(", ").Append(bucket.Count).Append(" calls").Append((bucket.Gcs > 0) ? (", " + bucket.Gcs + " garbage collections") : string.Empty).Append(")");
		}
		Buckets.Clear();
	}

	public static string Seconds(long ticks)
	{
		return ((double)ticks / Stopwatch.Frequency).ToString("0.00") + " s";
	}
}

// Times each listener of the game's parameterless signals (LoadUI, LevelSaveReady, LevelReady, ...) while loading.
// Same order and exception behaviour as Signal.Invoke.
[Feature(Features.FasterLoading, Features.FasterLoadingInfo)]
[HarmonyPatch(typeof(GameSignals), nameof(GameSignals.Invoke), new Type[] { typeof(GameSignals.GameSignal) })]
internal static class LoadSignalTiming
{
	private static Dictionary<GameSignals.GameSignal, string> _names;

	private static bool Prefix(GameSignals.GameSignal gs)
	{
		if (!LoadProfiler.Active || gs == null)
		{
			return true;
		}
		string signal = NameOf(gs);
		Signal.SignalInvokeListEnumerator listeners = new Signal.SignalInvokeListEnumerator(gs._signal);
		try
		{
			while (listeners.MoveNext())
			{
				IAction action = listeners.Current.Delegate;
				long start = Stopwatch.GetTimestamp();
				try
				{
					action.Invoke();
				}
				finally
				{
					long ticks = Stopwatch.GetTimestamp() - start;
					if (ticks * 1000 >= Stopwatch.Frequency * 5)
					{
						LoadProfiler.Add("signal " + signal + " -> " + Describe(action), ticks);
					}
				}
			}
		}
		finally
		{
			listeners.Dispose();
		}
		return false;
	}

	private static string NameOf(GameSignals.GameSignal gs)
	{
		if (_names == null)
		{
			_names = new Dictionary<GameSignals.GameSignal, string>();
			foreach (FieldInfo field in typeof(GameSignals).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
			{
				if (field.GetValue(null) is GameSignals.GameSignal value && !_names.ContainsKey(value))
				{
					_names[value] = field.Name;
				}
			}
		}
		return _names.TryGetValue(gs, out string name) ? name : "?";
	}

	// The closure wraps a delegate somewhere in its fields; name it by the method it calls.
	internal static string Describe(object holder)
	{
		Delegate target = FindDelegate(holder, 0);
		if (target == null)
		{
			return holder.GetType().Name;
		}
		MethodInfo method = target.Method;
		return ((method.DeclaringType != null) ? (method.DeclaringType.FullName + ".") : string.Empty) + method.Name;
	}

	private static Delegate FindDelegate(object holder, int depth)
	{
		if (holder == null || depth > 6)
		{
			return null;
		}
		if (holder is Delegate direct)
		{
			return direct;
		}
		for (Type type = holder.GetType(); type != null && type != typeof(object); type = type.BaseType)
		{
			foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
			{
				if (field.FieldType.IsPrimitive || field.FieldType.IsEnum || field.FieldType == typeof(string) || field.FieldType.IsArray)
				{
					continue;
				}
				Delegate found = FindDelegate(field.GetValue(holder), depth + 1);
				if (found != null)
				{
					return found;
				}
			}
		}
		return null;
	}
}

// Times building a menu from its finished Lua table (creating its panels and widgets), per script.
[Feature(Features.FasterLoading, Features.FasterLoadingInfo)]
[HarmonyPatch(typeof(Brix.UI.Builder.Menu.LuaMenu), nameof(Brix.UI.Builder.Menu.LuaMenu.LoadMenuTable), new Type[] { typeof(MoonSharp.Interpreter.Table), typeof(Func<UnityEngine.RectTransform>), typeof(Brix.Lua.LuaHook), typeof(string), typeof(bool), typeof(bool), typeof(bool) })]
internal static class LoadMenuTableTiming
{
	private static void Prefix(out long __state)
	{
		__state = LoadProfiler.Active ? Stopwatch.GetTimestamp() : 0;
	}

	private static void Postfix(long __state, string path)
	{
		if (__state != 0)
		{
			LoadProfiler.Add("lua menu build " + path, Stopwatch.GetTimestamp() - __state);
		}
	}
}

// Times reading and compiling each Lua file (loadfile, dofile, require all end here), per file name, to see
// which files are compiled how often. Measurement only.
[Feature(Features.FasterLoading, Features.FasterLoadingInfo)]
[HarmonyPatch(typeof(MoonSharp.Interpreter.Script), nameof(MoonSharp.Interpreter.Script.LoadFile))]
internal static class LuaFileTiming
{
	private static void Prefix(out long __state)
	{
		__state = LoadProfiler.Active ? Stopwatch.GetTimestamp() : 0;
	}

	private static void Postfix(long __state, string filename)
	{
		if (__state != 0)
		{
			LoadProfiler.Add("lua file " + filename, Stopwatch.GetTimestamp() - __state);
		}
	}
}

// Menus that Lua builds from a closure (LoadMenuFromClosure), named by their game object.
[Feature(Features.FasterLoading, Features.FasterLoadingInfo)]
[HarmonyPatch(typeof(Brix.UI.Builder.Menu.Component.LuaMenuComponent), "LoadMenuTable", new Type[] { typeof(MoonSharp.Interpreter.ScriptExecutionContext), typeof(MoonSharp.Interpreter.CallbackArguments) })]
internal static class LuaClosureMenuTiming
{
	private static void Prefix(out long __state)
	{
		__state = LoadProfiler.Active ? Stopwatch.GetTimestamp() : 0;
	}

	private static void Postfix(long __state, MoonSharp.Interpreter.CallbackArguments args)
	{
		if (__state == 0)
		{
			return;
		}
		string name = "?";
		try
		{
			UnityEngine.Component component = Brix.Lua.CallbackArgumentExtensions.AsComponent<Brix.UI.Builder.Menu.Component.LuaMenuComponent>(args, 0, "LoadMenuFromClosure");
			name = (component != null) ? component.gameObject.name : "?";
		}
		catch (Exception)
		{
		}
		LoadProfiler.Add("lua closure menu " + name, Stopwatch.GetTimestamp() - __state);
	}
}
