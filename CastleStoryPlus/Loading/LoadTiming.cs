using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Brix.Engine;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Loading;

// Profiles map loading and writes where the time went to the BepInEx log, so slow loads can be measured and
// fixed. GameLoader.Load is run as one flat coroutine: nested steps (yielded IEnumerators) are stepped here
// instead of by Unity, which behaves the same, and each step's time is booked under its own name: "cpu" is time
// spent in its code, "wall" also counts the frames it waited (for the voxel engine, worker threads, the network).
[Feature(Features.FasterLoading, Features.FasterLoadingInfo)]
[HarmonyPatch(typeof(GameLoader), "Load")]
internal static class LoadTiming
{
	private const int Shown = 20;

	private class Entry
	{
		public string Name;

		public long Cpu;

		public long Wait;

		public int Frames;

		public int Runs;
	}

	private static void Postfix(ref IEnumerator __result)
	{
		if (__result != null)
		{
			__result = Profiled(__result);
		}
	}

	private static IEnumerator Profiled(IEnumerator root)
	{
		Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
		Stack<IEnumerator> stack = new Stack<IEnumerator>();
		stack.Push(root);
		Stopwatch total = Stopwatch.StartNew();
		int startFrame = Time.frameCount;
		LoadProfiler.Start();
		LuaChunkReuse.Start();
		LuaMenuBatchPatch.Resumes = 0;
		LuaMenuBatchPatch.Ticks = 0;
		LoadWatchdog.Started();
		LoadingStatus.Started();
		while (stack.Count > 0)
		{
			IEnumerator top = stack.Peek();
			Entry entry = EntryFor(entries, top, true);
			long start = Stopwatch.GetTimestamp();
			bool more;
			try
			{
				more = top.MoveNext();
			}
			catch (System.Exception e)
			{
				// The game's own handler logs nothing in this build and leaves the loading screen up.
				Plugin.Log.LogError("Load failed in " + Describe(stack) + ": " + e);
				LoadWatchdog.Finished();
				LoadingStatus.Failed(e);
				throw;
			}
			finally
			{
				entry.Cpu += Stopwatch.GetTimestamp() - start;
			}
			if (!more)
			{
				stack.Pop();
				continue;
			}
			object current = top.Current;
			if (current is IEnumerator nested)
			{
				stack.Push(nested);
				EntryFor(entries, nested, false).Runs++;
				continue;
			}
			long waitStart = Stopwatch.GetTimestamp();
			int waitFrame = Time.frameCount;
			yield return current;
			entry.Wait += Stopwatch.GetTimestamp() - waitStart;
			entry.Frames += Time.frameCount - waitFrame;
			LoadWatchdog.Resumed(entry.Name, current, () => Describe(stack));
			LoadingStatus.Update(stack);
		}
		LoadWatchdog.Finished();
		LoadingStatus.Finished();
		LuaChunkReuse.Stop();
		Log(entries, total.Elapsed.TotalSeconds, Time.frameCount - startFrame);
	}

	// GameLoader's own steps are split by resume point ("#2" = the code after its second yield), so a long
	// step shows which of its loops or signals took the time.
	private static Entry EntryFor(Dictionary<string, Entry> entries, IEnumerator enumerator, bool byState)
	{
		string name = byState ? EntryName(enumerator) : NameOf(enumerator);
		if (!entries.TryGetValue(name, out Entry entry))
		{
			entry = new Entry { Name = name };
			entries[name] = entry;
		}
		return entry;
	}

	private static string EntryName(IEnumerator enumerator)
	{
		string name = NameOf(enumerator);
		if (enumerator.GetType().DeclaringType == typeof(GameLoader))
		{
			FieldInfo state = AccessTools.Field(enumerator.GetType(), "$PC");
			if (state != null)
			{
				name += "#" + state.GetValue(enumerator);
			}
		}
		return name;
	}

	// The nested steps from the outermost, with GameLoader's resume points.
	private static string Describe(Stack<IEnumerator> stack)
	{
		IEnumerator[] steps = stack.ToArray();
		StringBuilder text = new StringBuilder();
		for (int i = steps.Length - 1; i >= 0; i--)
		{
			if (text.Length > 0)
			{
				text.Append(" > ");
			}
			text.Append(EntryName(steps[i]));
		}
		return text.ToString();
	}

	// "<LoadBasicTerrain>c__Iterator3" (compiler-made iterator class) -> "GameLoader.LoadBasicTerrain".
	private static string NameOf(IEnumerator enumerator)
	{
		System.Type type = enumerator.GetType();
		string name = type.Name;
		int open = name.IndexOf('<');
		int close = name.IndexOf('>');
		if (open >= 0 && close > open)
		{
			name = name.Substring(open + 1, close - open - 1);
		}
		System.Type owner = type.DeclaringType;
		return (owner != null) ? (owner.Name + "." + name) : name;
	}

	private static void Log(Dictionary<string, Entry> entries, double totalSeconds, int frames)
	{
		List<Entry> list = new List<Entry>(entries.Values);
		list.Sort((Entry a, Entry b) => (b.Cpu + b.Wait).CompareTo(a.Cpu + a.Wait));
		StringBuilder text = new StringBuilder();
		text.Append("Load timing: total ").Append(totalSeconds.ToString("0.00")).Append(" s, ").Append(frames).Append(" frames. Own time per step (cpu = its code, wait = frames it waited):");
		for (int i = 0; i < list.Count && i < Shown; i++)
		{
			Entry entry = list[i];
			text.Append("\n  ").Append(entry.Name).Append(": ").Append(Seconds(entry.Cpu + entry.Wait)).Append(" (cpu ").Append(Seconds(entry.Cpu)).Append(", wait ").Append(Seconds(entry.Wait)).Append(", ").Append(entry.Frames).Append(" frames");
			if (entry.Runs > 1)
			{
				text.Append(", ").Append(entry.Runs).Append(" runs");
			}
			text.Append(')');
		}
		text.Append("\n  Lua menus: ").Append(LuaMenuBatchPatch.Resumes).Append(" resumes, ").Append(Seconds(LuaMenuBatchPatch.Ticks));
		text.Append("\n  Lua files: ").Append(LuaChunkReuse.Compiled).Append(" compiled, ").Append(LuaChunkReuse.Reused).Append(" reused");
		LoadProfiler.Stop(text);
		Plugin.Log.LogInfo(text.ToString());
	}

	private static string Seconds(long ticks)
	{
		return ((double)ticks / Stopwatch.Frequency).ToString("0.00") + " s";
	}
}
