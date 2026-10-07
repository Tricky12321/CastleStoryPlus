using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using Brix.Game;
using Brix.Game.Components;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Diagnostics;

// Measures every frame, to find what makes the game stutter. Always on and cheap: each frame's time and the garbage
// collections in it, kept for the last minute. A frame over [Performance] SpikeMs is a spike: logged with what took
// the time, the last 50 kept for the performance window (F7) and the developer API (/api/frames).
// What took the time ("systems") is measured once systems are measured: from the start with [Performance]
// MeasureAtStart, else when the F7 window or the API first asks. That patches, while the game runs, every Update,
// LateUpdate and FixedUpdate of the game's and the mod's MonoBehaviours, and the game's own update loop (GameClock)
// per component type, each into a bucket of its own; patching ~300 methods freezes the game for a moment once.
// What no bucket covers (rendering, Unity's own work, coroutines, Lua menus, waiting for the screen) is "unmeasured".
// A frame starts when FrameMonitorDriver updates, so a bucket's time can belong to the frame before; nested buckets
// (an Update that calls another measured one) count in both, but only once towards the measured total.
// Unity's "referenced script is missing" warnings are counted per frame, and the first ones are logged with the C#
// call that caused them (the warning itself says nothing about where it comes from).
// Memory: with the systems, also what each one allocates ([Performance] MeasureAllocations), read from the managed
// heap's used size before and after it (GC.GetTotalMemory, cheap on Unity's Mono). Boehm's collector hands out memory
// in blocks of 4 KB, so a single call's bytes are coarse: a bucket is charged when it takes a new block, which over
// many frames is in proportion to what it allocates. A call with a collection inside is left out. Per frame: how much
// was allocated (frames with a collection left out), the heap's used and reserved size, and what each collection
// freed and left, since every collection scans the whole heap and stops the game while it does.
// Extra buckets cover what the Updates do not show on their own: Lua called from C# and Lua coroutines, the workers'
// task search, pathfinding; with [Performance] MeasureModPatches (or /api/frames?mod=true) also every Harmony patch of
// the mod's gameplay features, one bucket each.
[Feature(Features.Performance, Features.PerformanceInfo)]
internal static class FrameMonitor
{
	internal sealed class Bucket
	{
		public string Name;

		public long FrameTicks;

		public int FrameCalls;

		public long TotalTicks;

		public long TotalCalls;

		public long MaxTicks;

		public long FrameBytes;

		public long TotalBytes;

		// Calls of this bucket open on the stack: a recursive call (Lua calling C# calling Lua) counts once.
		public int Open;
	}

	internal sealed class Spike
	{
		public int Frame;

		public float At;

		public DateTime Clock;

		public float Milliseconds;

		public int Gcs;

		public int MissingScripts;

		public float UnmeasuredMs;

		public string[] Names;

		public float[] BucketMs;

		public int[] Calls;
	}

	// Frames kept for the statistics (about a minute at 60 fps).
	internal const int History = 4096;

	private const int SpikesKept = 50;

	private const int TopBuckets = 5;

	private const int MaxDepth = 64;

	// Distinct call stacks logged for missing-script warnings.
	private const int MissingScriptStacks = 8;

	private const string MissingScriptWarning = "The referenced script on this Behaviour";

	internal static ConfigEntry<float> SpikeMs;

	private static ConfigEntry<bool> _measureAtStart;

	private static ConfigEntry<bool> _measureAllocations;

	private static ConfigEntry<bool> _measureModPatches;

	// Whether the buckets also count the bytes they allocate.
	internal static bool Allocations;

	internal static bool ModPatches;

	private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

	// Ring buffers of the last frames: time, when, garbage collections.
	internal static readonly float[] FrameMs = new float[History];

	internal static readonly float[] FrameAt = new float[History];

	internal static readonly byte[] FrameGcs = new byte[History];

	internal static readonly bool[] FrameSpike = new bool[History];

	// Kilobytes allocated in each frame, -1 for a frame with a collection (its allocation is unknown).
	internal static readonly float[] FrameKb = new float[History];

	// The managed heap: used now, reserved by Mono (0 when Unity does not tell), and around the last collection.
	internal static long HeapUsed;

	internal static long HeapReserved;

	internal static long UsedBeforeLastGc;

	internal static long UsedAfterLastGc;

	internal static float LastGcFrameMs;

	// Since the last reset: bytes allocated in frames without a collection and their time, for the rate; frames
	// with a collection and their time, for what a collection costs.
	internal static double AllocBytes;

	internal static double AllocSeconds;

	internal static int GcFrames;

	internal static double GcFramesMs;

	// Frames recorded since the start (the newest is at (Recorded - 1) % History).
	internal static int Recorded;

	internal static readonly List<Spike> Spikes = new List<Spike>();

	internal static int SpikeCount;

	internal static int GcTotal;

	internal static int MissingScriptTotal;

	// Frames measured with systems since the last reset (for the buckets' averages).
	internal static int MeasuredFrames;

	internal static double MeasuredFrameMs;

	internal static double UnmeasuredTotalMs;

	internal static readonly List<Bucket> Buckets = new List<Bucket>();

	internal static bool Detailed;

	internal static int PatchedMethods;

	private static readonly Dictionary<MethodBase, Bucket> ByMethod = new Dictionary<MethodBase, Bucket>();

	// GameClock buckets per component type: update, fixed update, late update, slow update.
	private static readonly Dictionary<Type, Bucket>[] ByType = new Dictionary<Type, Bucket>[4]
	{
		new Dictionary<Type, Bucket>(),
		new Dictionary<Type, Bucket>(),
		new Dictionary<Type, Bucket>(),
		new Dictionary<Type, Bucket>()
	};

	private static readonly string[] KindNames = new string[4] { "update: ", "fixed: ", "late: ", "slow: " };

	private static readonly Bucket[] Stack = new Bucket[MaxDepth];

	private static readonly long[] StackBytes = new long[MaxDepth];

	private static readonly int[] StackGcs = new int[MaxDepth];

	// Names of the extra buckets (methods measured that are not an Update), by method.
	private static readonly Dictionary<MethodBase, string> ExtraNames = new Dictionary<MethodBase, string>();

	// Methods of the same name (overloads) share a bucket.
	private static readonly Dictionary<string, Bucket> ByName = new Dictionary<string, Bucket>();

	private static long _usedAtFrameStart;

	private static int _depth;

	// Ticks of the outermost measured calls this frame.
	private static long _measuredTicks;

	private static long _frameStart;

	private static int _gcAtFrameStart;

	private static int _missingScriptsThisFrame;

	private static readonly HashSet<string> LoggedStacks = new HashSet<string>();

	private static readonly Bucket[] Top = new Bucket[TopBuckets];

	private static void Enable()
	{
		SpikeMs = Plugin.Cfg.Bind("Performance", "SpikeMs", 100f, new ConfigDescription("A frame longer than this many milliseconds is a spike: logged with what took the time.", new AcceptableValueRange<float>(20f, 2000f)));
		_measureAtStart = Plugin.Cfg.Bind("Performance", "MeasureAtStart", false, "Measure what takes the time in each frame (every Update of the game and the mod) from the start, rather than when the performance window (F7) or the developer API first asks. Costs a little time every frame.");
		_measureAllocations = Plugin.Cfg.Bind("Performance", "MeasureAllocations", true, "While systems are measured, also measure how much memory each one allocates (every allocation leads towards a garbage collection, which stops the game).");
		_measureModPatches = Plugin.Cfg.Bind("Performance", "MeasureModPatches", false, "While systems are measured, also measure each of the mod's gameplay patches on its own (several hundred more measured methods, some of them called very often).");
		Allocations = _measureAllocations.Value;
		Application.logMessageReceived += OnLog;
		Plugin.Root.AddComponent<FrameMonitorDriver>();
		if (_measureAtStart.Value)
		{
			StartDetailed();
		}
	}

	// Patches every Update and the game's update loop, once.
	internal static void StartDetailed()
	{
		if (Detailed)
		{
			return;
		}
		Detailed = true;
		Stopwatch watch = Stopwatch.StartNew();
		Harmony harmony = new Harmony(Plugin.Guid + ".performance");
		HarmonyMethod transpiler = new HarmonyMethod(typeof(FrameMonitor), nameof(ClockTranspiler));
		foreach (MethodInfo method in new MethodInfo[4]
		{
			AccessTools.Method(typeof(GameClock.UpdateSignal), nameof(GameClock.UpdateSignal.SafeInvoke)),
			AccessTools.Method(typeof(GameClock.FixedUpdateSignal), nameof(GameClock.FixedUpdateSignal.UnorderedSafeInvoke)),
			AccessTools.Method(typeof(GameClock.LateUpdateSignal), nameof(GameClock.LateUpdateSignal.SafeInvoke)),
			AccessTools.Method(typeof(GameClock.SlowUpdateSignal), nameof(GameClock.SlowUpdateSignal.SafeInvoke))
		})
		{
			try
			{
				harmony.Patch(method, transpiler: transpiler);
				PatchedMethods++;
			}
			catch (Exception ex)
			{
				Plugin.Log.LogWarning("Performance: could not measure " + method.DeclaringType.Name + "." + method.Name + ": " + ex.Message);
			}
		}
		HarmonyMethod prefix = new HarmonyMethod(typeof(FrameMonitor), nameof(TimedPrefix));
		HarmonyMethod finalizer = new HarmonyMethod(typeof(FrameMonitor), nameof(TimedFinalizer));
		int failed = 0;
		foreach (MethodInfo method in UpdateMethods())
		{
			try
			{
				harmony.Patch(method, prefix: prefix, finalizer: finalizer);
				PatchedMethods++;
			}
			catch (Exception)
			{
				failed++;
			}
		}
		foreach (KeyValuePair<MethodBase, string> extra in ExtraMethods())
		{
			try
			{
				ExtraNames[extra.Key] = extra.Value;
				harmony.Patch(extra.Key, prefix: prefix, finalizer: finalizer);
				PatchedMethods++;
			}
			catch (Exception)
			{
				failed++;
			}
		}
		Type processor = AccessTools.TypeByName("MoonSharp.Interpreter.Execution.VM.Processor");
		if (processor != null)
		{
			LuaFunctionTiming.Patch(harmony, processor);
		}
		Plugin.Log.LogInfo("Performance: measuring " + PatchedMethods + " methods" + ((failed > 0) ? (" (" + failed + " could not be patched)") : string.Empty) + ", patched in " + watch.ElapsedMilliseconds + " ms");
		if (_measureModPatches.Value)
		{
			StartModPatches();
		}
	}

	// Each Harmony patch method of the mod's gameplay features (not the diagnostics, loading, menu or core code), once.
	internal static void StartModPatches()
	{
		if (ModPatches)
		{
			return;
		}
		StartDetailed();
		ModPatches = true;
		Stopwatch watch = Stopwatch.StartNew();
		Harmony harmony = new Harmony(Plugin.Guid + ".performance.mod");
		HarmonyMethod prefix = new HarmonyMethod(typeof(FrameMonitor), nameof(TimedPrefix));
		HarmonyMethod finalizer = new HarmonyMethod(typeof(FrameMonitor), nameof(TimedFinalizer));
		int patched = 0;
		int failed = 0;
		Type[] types;
		try
		{
			types = typeof(Plugin).Assembly.GetTypes();
		}
		catch (ReflectionTypeLoadException ex)
		{
			types = ex.Types;
		}
		string root = typeof(Plugin).Namespace + ".";
		foreach (Type type in types)
		{
			if (type == null || type.Namespace == null || !type.Namespace.StartsWith(root, StringComparison.Ordinal) || type.ContainsGenericParameters)
			{
				continue;
			}
			string area = type.Namespace.Substring(root.Length);
			if (area.StartsWith("Diagnostics", StringComparison.Ordinal) || area.StartsWith("Loading", StringComparison.Ordinal) || area.StartsWith("LuaUi", StringComparison.Ordinal) || area.StartsWith("Core", StringComparison.Ordinal))
			{
				continue;
			}
			if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0)
			{
				continue;
			}
			foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
			{
				if (method.Name != "Prefix" && method.Name != "Postfix" && method.Name != "Finalizer")
				{
					continue;
				}
				try
				{
					ExtraNames[method] = "mod: " + type.Name + "." + method.Name;
					harmony.Patch(method, prefix: prefix, finalizer: finalizer);
					patched++;
				}
				catch (Exception)
				{
					failed++;
				}
			}
		}
		PatchedMethods += patched;
		Plugin.Log.LogInfo("Performance: measuring " + patched + " mod patches" + ((failed > 0) ? (" (" + failed + " could not be patched)") : string.Empty) + ", patched in " + watch.ElapsedMilliseconds + " ms");
	}

	// Work that is not an Update of its own but runs inside one, or in a coroutine: Lua, the workers' task search,
	// pathfinding, saving. Methods not found in this version of the game are left out.
	private static List<KeyValuePair<MethodBase, string>> ExtraMethods()
	{
		List<KeyValuePair<MethodBase, string>> methods = new List<KeyValuePair<MethodBase, string>>();
		Type processor = AccessTools.TypeByName("MoonSharp.Interpreter.Execution.VM.Processor");
		if (processor != null)
		{
			foreach (MethodInfo method in processor.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
			{
				if (method.Name == "Call" && !method.IsGenericMethodDefinition)
				{
					methods.Add(new KeyValuePair<MethodBase, string>(method, "lua: called from C#"));
				}
				else if (method.Name == "Coroutine_Resume")
				{
					methods.Add(new KeyValuePair<MethodBase, string>(method, "lua: coroutine resume"));
				}
			}
		}
		AddMethods(methods, "Brix.Game.AI.Labor", "labor: ", "RefreshAvailableProjects", "TryWorkInProject", "Find", "FindComplete", "FindOneResource", "BestRecepteurToStore", "BestRecepteurToStoreInventory", "BestToolToPickup", "BestToolRackToStore", "ClosestResource");
		AddMethods(methods, "Brix.Game.AI.KnowledgeSpace.Knowledge", "knowledge: ", "ClosestObjectToPickup", "CanReach");
		AddMethods(methods, "Brix.Game.AI.GoalSelector", "goals: ", "ProcessGoalAndReturnIsBest");
		AddMethods(methods, "Brix.Pathfinding.SearchPathRequest", "path: ", "Work", "LoopImprovePath");
		AddMethods(methods, "Brix.Game.Network.GameObjectSerializer", "save: ", "GetJsonGameState");
		// uGUI's layout and graphic rebuilds run from Canvas.willRenderCanvases, outside every Update: the C# menus'
		// cost when they change.
		AddMethods(methods, "UnityEngine.UI.CanvasUpdateRegistry", "ui: ", "PerformUpdate");
		return methods;
	}

	private static void AddMethods(List<KeyValuePair<MethodBase, string>> into, string typeName, string prefix, params string[] names)
	{
		Type type = AccessTools.TypeByName(typeName);
		if (type == null)
		{
			Plugin.Log.LogInfo("Performance: no " + typeName + " to measure");
			return;
		}
		foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
		{
			if (Array.IndexOf(names, method.Name) >= 0 && !method.IsAbstract && !method.IsGenericMethodDefinition && method.GetMethodBody() != null)
			{
				into.Add(new KeyValuePair<MethodBase, string>(method, prefix + method.Name));
			}
		}
	}

	// Update, LateUpdate and FixedUpdate of every MonoBehaviour of the game and the mod. Not GameClock's (its
	// components are measured one by one) nor the monitor's own.
	private static IEnumerable<MethodInfo> UpdateMethods()
	{
		HashSet<string> assemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Assembly-CSharp", "Assembly-CSharp-firstpass", "Brix.Core", typeof(Plugin).Assembly.GetName().Name };
		foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			if (!assemblies.Contains(assembly.GetName().Name))
			{
				continue;
			}
			Type[] types;
			try
			{
				types = assembly.GetTypes();
			}
			catch (ReflectionTypeLoadException ex)
			{
				types = ex.Types;
			}
			foreach (Type type in types)
			{
				if (type == null || type.ContainsGenericParameters || !typeof(MonoBehaviour).IsAssignableFrom(type) || typeof(GameClock).IsAssignableFrom(type) || type == typeof(FrameMonitorDriver) || type == typeof(PerformancePanel))
				{
					continue;
				}
				foreach (string name in new string[3] { "Update", "LateUpdate", "FixedUpdate" })
				{
					MethodInfo method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
					if (method != null && method.ReturnType == typeof(void) && !method.IsAbstract && method.GetMethodBody() != null)
					{
						yield return method;
					}
				}
			}
		}
	}

	private static void TimedPrefix(MethodBase __originalMethod, out long __state)
	{
		if (!ByMethod.TryGetValue(__originalMethod, out Bucket bucket))
		{
			bucket = Named(ExtraNames.TryGetValue(__originalMethod, out string name) ? name : (__originalMethod.DeclaringType.Name + "." + __originalMethod.Name));
			ByMethod[__originalMethod] = bucket;
		}
		__state = Begin(bucket);
	}

	private static void TimedFinalizer(long __state)
	{
		End(__state);
	}

	// The game's update loop calls each component's BrixUpdate (and the others) through this instead, timed per type.
	private static IEnumerable<CodeInstruction> ClockTranspiler(IEnumerable<CodeInstruction> instructions)
	{
		foreach (CodeInstruction instruction in instructions)
		{
			if (instruction.opcode == OpCodes.Callvirt && instruction.operand is MethodInfo called && called.DeclaringType == typeof(IUpdatableComponent))
			{
				MethodInfo timed = null;
				switch (called.Name)
				{
					case nameof(IUpdatableComponent.BrixUpdate):
						timed = AccessTools.Method(typeof(FrameMonitor), nameof(TimedUpdate));
						break;
					case nameof(IUpdatableComponent.BrixFixedUpdate):
						timed = AccessTools.Method(typeof(FrameMonitor), nameof(TimedFixedUpdate));
						break;
					case nameof(IUpdatableComponent.BrixLateUpdate):
						timed = AccessTools.Method(typeof(FrameMonitor), nameof(TimedLateUpdate));
						break;
					case nameof(IUpdatableComponent.BrixSlowUpdate):
						timed = AccessTools.Method(typeof(FrameMonitor), nameof(TimedSlowUpdate));
						break;
				}
				if (timed != null)
				{
					CodeInstruction call = new CodeInstruction(OpCodes.Call, timed);
					call.labels.AddRange(instruction.labels);
					call.blocks.AddRange(instruction.blocks);
					yield return call;
					continue;
				}
			}
			yield return instruction;
		}
	}

	private static void TimedUpdate(IUpdatableComponent component)
	{
		long start = Begin(TypeBucket(0, component));
		try
		{
			component.BrixUpdate();
		}
		finally
		{
			End(start);
		}
	}

	private static void TimedFixedUpdate(IUpdatableComponent component)
	{
		long start = Begin(TypeBucket(1, component));
		try
		{
			component.BrixFixedUpdate();
		}
		finally
		{
			End(start);
		}
	}

	private static void TimedLateUpdate(IUpdatableComponent component)
	{
		long start = Begin(TypeBucket(2, component));
		try
		{
			component.BrixLateUpdate();
		}
		finally
		{
			End(start);
		}
	}

	private static void TimedSlowUpdate(IUpdatableComponent component)
	{
		long start = Begin(TypeBucket(3, component));
		try
		{
			component.BrixSlowUpdate();
		}
		finally
		{
			End(start);
		}
	}

	private static Bucket TypeBucket(int kind, IUpdatableComponent component)
	{
		Type type = component.GetType();
		if (!ByType[kind].TryGetValue(type, out Bucket bucket))
		{
			bucket = NewBucket(KindNames[kind] + type.Name);
			ByType[kind][type] = bucket;
		}
		return bucket;
	}

	private static Bucket NewBucket(string name)
	{
		Bucket bucket = new Bucket { Name = name };
		Buckets.Add(bucket);
		return bucket;
	}

	internal static Bucket Named(string name)
	{
		if (!ByName.TryGetValue(name, out Bucket bucket))
		{
			bucket = NewBucket(name);
			ByName[name] = bucket;
		}
		return bucket;
	}

	private static long Begin(Bucket bucket)
	{
		if (_depth < MaxDepth)
		{
			Stack[_depth] = bucket;
			if (Allocations)
			{
				StackGcs[_depth] = GC.CollectionCount(0);
				StackBytes[_depth] = GC.GetTotalMemory(false);
			}
		}
		bucket.Open++;
		_depth++;
		return Stopwatch.GetTimestamp();
	}

	private static void End(long start)
	{
		long ticks = Stopwatch.GetTimestamp() - start;
		_depth--;
		if (_depth < 0)
		{
			_depth = 0;
			return;
		}
		if (_depth < MaxDepth)
		{
			Bucket bucket = Stack[_depth];
			bucket.Open--;
			if (bucket.Open <= 0)
			{
				bucket.Open = 0;
				bucket.FrameTicks += ticks;
				bucket.FrameCalls++;
				if (Allocations && GC.CollectionCount(0) == StackGcs[_depth])
				{
					long bytes = GC.GetTotalMemory(false) - StackBytes[_depth];
					if (bytes > 0)
					{
						bucket.FrameBytes += bytes;
					}
				}
			}
		}
		if (_depth == 0)
		{
			_measuredTicks += ticks;
		}
	}

	// From FrameMonitorDriver.Update, once per frame: closes the frame before.
	internal static void NextFrame()
	{
		long now = Stopwatch.GetTimestamp();
		int gcNow = GC.CollectionCount(0);
		long used = GC.GetTotalMemory(false);
		if (_frameStart == 0)
		{
			_frameStart = now;
			_gcAtFrameStart = gcNow;
			_usedAtFrameStart = used;
			return;
		}
		long frameTicks = now - _frameStart;
		float ms = (float)(frameTicks * MsPerTick);
		int gcs = gcNow - _gcAtFrameStart;
		_frameStart = now;
		_gcAtFrameStart = gcNow;
		GcTotal += gcs;
		int slot = Recorded % History;
		if (gcs == 0)
		{
			long allocated = Math.Max(0L, used - _usedAtFrameStart);
			FrameKb[slot] = allocated / 1024f;
			AllocBytes += allocated;
			AllocSeconds += ms / 1000.0;
		}
		else
		{
			FrameKb[slot] = -1f;
			UsedBeforeLastGc = _usedAtFrameStart;
			UsedAfterLastGc = used;
			LastGcFrameMs = ms;
			GcFrames++;
			GcFramesMs += ms;
		}
		_usedAtFrameStart = used;
		HeapUsed = used;
		if ((Recorded & 63) == 0)
		{
			HeapReserved = ReservedHeap();
		}
		FrameMs[slot] = ms;
		FrameAt[slot] = Time.realtimeSinceStartup;
		FrameGcs[slot] = (byte)Mathf.Min(gcs, 255);
		bool spike = ms >= SpikeMs.Value && !CastleStoryPlus.Loading.LoadProfiler.Active;
		FrameSpike[slot] = spike;
		Recorded++;
		if (spike)
		{
			RecordSpike(ms, frameTicks, gcs);
		}
		if (Detailed)
		{
			MeasuredFrames++;
			MeasuredFrameMs += ms;
			UnmeasuredTotalMs += Mathf.Max(0f, (float)((frameTicks - _measuredTicks) * MsPerTick));
			foreach (Bucket bucket in Buckets)
			{
				if (bucket.FrameCalls == 0)
				{
					continue;
				}
				bucket.TotalTicks += bucket.FrameTicks;
				bucket.TotalCalls += bucket.FrameCalls;
				bucket.TotalBytes += bucket.FrameBytes;
				if (bucket.FrameTicks > bucket.MaxTicks)
				{
					bucket.MaxTicks = bucket.FrameTicks;
				}
				bucket.FrameTicks = 0;
				bucket.FrameCalls = 0;
				bucket.FrameBytes = 0;
			}
		}
		_measuredTicks = 0;
		_missingScriptsThisFrame = 0;
	}

	private static void RecordSpike(float ms, long frameTicks, int gcs)
	{
		SpikeCount++;
		Spike spike = new Spike
		{
			Frame = Time.frameCount - 1,
			At = Time.realtimeSinceStartup,
			Clock = DateTime.Now,
			Milliseconds = ms,
			Gcs = gcs,
			MissingScripts = _missingScriptsThisFrame,
			UnmeasuredMs = Detailed ? Mathf.Max(0f, (float)((frameTicks - _measuredTicks) * MsPerTick)) : ms
		};
		int found = 0;
		if (Detailed)
		{
			Array.Clear(Top, 0, Top.Length);
			foreach (Bucket bucket in Buckets)
			{
				if (bucket.FrameTicks <= 0)
				{
					continue;
				}
				for (int i = 0; i < Top.Length; i++)
				{
					if (Top[i] == null || bucket.FrameTicks > Top[i].FrameTicks)
					{
						for (int j = Top.Length - 1; j > i; j--)
						{
							Top[j] = Top[j - 1];
						}
						Top[i] = bucket;
						break;
					}
				}
			}
			while (found < Top.Length && Top[found] != null)
			{
				found++;
			}
		}
		spike.Names = new string[found];
		spike.BucketMs = new float[found];
		spike.Calls = new int[found];
		string text = string.Empty;
		for (int i = 0; i < found; i++)
		{
			spike.Names[i] = Top[i].Name;
			spike.BucketMs[i] = (float)(Top[i].FrameTicks * MsPerTick);
			spike.Calls[i] = Top[i].FrameCalls;
			text += ", " + Top[i].Name + " " + spike.BucketMs[i].ToString("0.0") + " ms (" + Top[i].FrameCalls + "x)";
		}
		Spikes.Add(spike);
		if (Spikes.Count > SpikesKept)
		{
			Spikes.RemoveAt(0);
		}
		Plugin.Log.LogWarning("Performance: frame " + spike.Frame + " took " + ms.ToString("0") + " ms, " + gcs + " garbage collections" + ((spike.MissingScripts > 0) ? (", " + spike.MissingScripts + " missing-script warnings") : string.Empty) + (Detailed ? (", unmeasured " + spike.UnmeasuredMs.ToString("0") + " ms" + text) : " (systems not measured yet: open the performance window, F7)"));
	}

	// Unity names the object of a missing script but not who made it: the managed call stack does.
	private static void OnLog(string condition, string stackTrace, LogType type)
	{
		if (type != LogType.Warning || condition == null || !condition.StartsWith(MissingScriptWarning, StringComparison.Ordinal))
		{
			return;
		}
		_missingScriptsThisFrame++;
		MissingScriptTotal++;
		if (LoggedStacks.Count >= MissingScriptStacks)
		{
			return;
		}
		string stack = new StackTrace(1, false).ToString();
		string inside = (_depth > 0 && _depth <= MaxDepth) ? Stack[_depth - 1].Name : "-";
		if (LoggedStacks.Add(stack))
		{
			Plugin.Log.LogWarning("Performance: \"" + condition + "\" (frame " + Time.frameCount + ", inside " + inside + ") came from:\n" + stack);
		}
	}

	internal static void Reset()
	{
		Spikes.Clear();
		SpikeCount = 0;
		GcTotal = 0;
		MissingScriptTotal = 0;
		MeasuredFrames = 0;
		MeasuredFrameMs = 0.0;
		UnmeasuredTotalMs = 0.0;
		AllocBytes = 0.0;
		AllocSeconds = 0.0;
		GcFrames = 0;
		GcFramesMs = 0.0;
		foreach (Bucket bucket in Buckets)
		{
			bucket.TotalTicks = 0;
			bucket.TotalCalls = 0;
			bucket.MaxTicks = 0;
			bucket.TotalBytes = 0;
		}
	}

	// Mono's reserved heap, 0 when this Unity does not tell.
	private static long ReservedHeap()
	{
		try
		{
			return UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong();
		}
		catch (Exception)
		{
			return 0;
		}
	}

	// Megabytes allocated per second (frames without a collection), 0 before any.
	internal static double AllocMbPerSecond
	{
		get { return (AllocSeconds > 0.0) ? (AllocBytes / AllocSeconds / (1024.0 * 1024.0)) : 0.0; }
	}

	// Average time of a frame with a collection in it.
	internal static double GcFrameMsAverage
	{
		get { return (GcFrames > 0) ? (GcFramesMs / GcFrames) : 0.0; }
	}

	// Kilobytes allocated per frame over the last seconds (frames with a collection left out).
	internal static float RecentKbPerFrame(float seconds, out float peakKb)
	{
		peakKb = 0f;
		float since = Time.realtimeSinceStartup - seconds;
		int count = Mathf.Min(Recorded, History);
		double total = 0.0;
		int frames = 0;
		for (int i = 0; i < count; i++)
		{
			int slot = (Recorded - 1 - i) % History;
			if (FrameAt[slot] < since)
			{
				break;
			}
			if (FrameKb[slot] < 0f)
			{
				continue;
			}
			total += FrameKb[slot];
			frames++;
			if (FrameKb[slot] > peakKb)
			{
				peakKb = FrameKb[slot];
			}
		}
		return (frames > 0) ? (float)(total / frames) : 0f;
	}

	// The buckets by the bytes they allocate per measured frame, the most first.
	internal static List<Bucket> RankedByBytes(int top)
	{
		List<Bucket> ranked = new List<Bucket>();
		foreach (Bucket bucket in Buckets)
		{
			if (bucket.TotalBytes > 0)
			{
				ranked.Add(bucket);
			}
		}
		ranked.Sort((Bucket a, Bucket b) => b.TotalBytes.CompareTo(a.TotalBytes));
		if (ranked.Count > top)
		{
			ranked.RemoveRange(top, ranked.Count - top);
		}
		return ranked;
	}

	internal static double Milliseconds(long ticks)
	{
		return ticks * MsPerTick;
	}

	// The frame times of the last seconds, oldest first, into the given array; answers how many.
	internal static int Recent(float seconds, float[] into, out int gcs, out int spikes)
	{
		gcs = 0;
		spikes = 0;
		float since = Time.realtimeSinceStartup - seconds;
		int count = Mathf.Min(Mathf.Min(Recorded, History), into.Length);
		int taken = 0;
		for (int i = 0; i < count; i++)
		{
			int slot = (Recorded - 1 - i) % History;
			if (FrameAt[slot] < since)
			{
				break;
			}
			taken++;
		}
		for (int i = 0; i < taken; i++)
		{
			int slot = (Recorded - taken + i) % History;
			into[i] = FrameMs[slot];
			gcs += FrameGcs[slot];
			if (FrameSpike[slot])
			{
				spikes++;
			}
		}
		return taken;
	}

	// The buckets by their average time per measured frame, the most first.
	internal static List<Bucket> Ranked(int top)
	{
		List<Bucket> ranked = new List<Bucket>();
		foreach (Bucket bucket in Buckets)
		{
			if (bucket.TotalCalls > 0)
			{
				ranked.Add(bucket);
			}
		}
		ranked.Sort((Bucket a, Bucket b) => b.TotalTicks.CompareTo(a.TotalTicks));
		if (ranked.Count > top)
		{
			ranked.RemoveRange(top, ranked.Count - top);
		}
		return ranked;
	}
}

// Marks the frames for FrameMonitor.
internal class FrameMonitorDriver : MonoBehaviour
{
	private void Update()
	{
		FrameMonitor.NextFrame();
	}
}
