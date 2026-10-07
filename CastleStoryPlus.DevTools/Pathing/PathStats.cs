using System.Collections.Generic;
using System.Diagnostics;
using Brix.Engine;
using Brix.Game.AI;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Pathing;

// Measures the route searches the game runs (SearchPathRequest): how many, how long they take (CPU per search and per
// frame, frames until done), how many nodes the coarse search and the fine (improve) search visit, how often the fine
// search runs out and the coarse route is kept, and which targets and origin/target pairs come back again and again
// (what a route cache or distance maps would save). The searches PathProbe runs for the API are not counted.
internal static class PathStats
{
	internal sealed class Record
	{
		public float Started;

		public string Unit;

		public bool Enemy;

		public bool Away;

		public bool Precise;

		public string Target;

		public XYZ From;

		public XYZ To;

		public int Distance;

		public int Frames;

		public double Milliseconds;

		public int Coarse;

		public int Improve;

		public int OptimisticLod;

		public int ImproveLod;

		public int LodMax;

		// "improved" (fine route found), "kept coarse" (the fine search ran out at every level), "not precise",
		// "failed" (no route), "abandoned" (the search was stopped or restarted before it ended).
		public string Outcome;

		public int Length;

		public bool Ended;

		public int LastFrame;
	}

	// Pairs closer than this many voxels (each axis) count as the same origin or target when finding repeats.
	internal const int Cell = 4;

	private const int RecentMax = 200;

	// Set while PathProbe runs a search for the API.
	internal static bool Paused;

	private static readonly Dictionary<SearchPathRequest, Record> Running = new Dictionary<SearchPathRequest, Record>();

	internal static readonly List<Record> Done = new List<Record>();

	internal static readonly List<Record> Recent = new List<Record>();

	internal static float Since;

	private static readonly Stopwatch Watch = new Stopwatch();

	private static int _frame = -1;

	private static double _frameMs;

	internal static double MaxFrameMs;

	internal static int BusyFrames;

	internal static double BusyFrameMs;

	internal static void Reset()
	{
		Running.Clear();
		Done.Clear();
		Recent.Clear();
		Since = Time.realtimeSinceStartup;
		MaxFrameMs = 0;
		BusyFrames = 0;
		BusyFrameMs = 0;
		_frame = -1;
		_frameMs = 0;
	}

	// Searches that never ended (their unit's task stopped and the request went back to the pool unused) are counted
	// as abandoned after this many seconds.
	private const float StaleSeconds = 60f;

	internal static void Sweep()
	{
		float now = Time.realtimeSinceStartup;
		List<SearchPathRequest> stale = new List<SearchPathRequest>();
		foreach (KeyValuePair<SearchPathRequest, Record> pair in Running)
		{
			if (now - pair.Value.Started > StaleSeconds)
			{
				stale.Add(pair.Key);
			}
		}
		foreach (SearchPathRequest request in stale)
		{
			End(request, Running[request], "abandoned");
		}
	}

	internal static int RunningCount => Running.Count;

	internal static void Start(SearchPathRequest request)
	{
		if (Paused || request == null)
		{
			return;
		}
		if (Running.TryGetValue(request, out Record old))
		{
			End(request, old, "abandoned");
		}
		Navigation navigation = request.Navigation;
		Record record = new Record
		{
			Started = Time.realtimeSinceStartup,
			Unit = (navigation != null) ? navigation.gameObject.name : "?",
			Enemy = navigation != null && navigation.faction != null && navigation.faction.isAI,
			Away = request.SearchAway,
			Precise = request.Precise,
			Target = TargetName(request),
			From = (request.Origin != null) ? request.Origin.RepresentativePosition : request.From,
			To = request.Destination,
			Outcome = "abandoned",
			ImproveLod = -1
		};
		record.Distance = XYZ.ManhattanMagnitude(record.To - record.From);
		Running[request] = record;
	}

	private static string TargetName(SearchPathRequest request)
	{
		Location location = request.Location;
		if (location == null)
		{
			return "?";
		}
		GameObject target = location.Object;
		string name = (target != null) ? target.name : location.GetType().Name;
		// "Brick (12)" and "Brick(Clone)" are the same kind of target.
		int cut = name.IndexOf('(');
		if (cut > 0)
		{
			name = name.Substring(0, cut);
		}
		return name.Trim();
	}

	internal static Record Of(SearchPathRequest request)
	{
		if (Paused || request == null)
		{
			return null;
		}
		Running.TryGetValue(request, out Record record);
		return record;
	}

	internal static void Begin()
	{
		Watch.Reset();
		Watch.Start();
	}

	internal static void Stop(Record record)
	{
		Watch.Stop();
		double ms = Watch.Elapsed.TotalMilliseconds;
		int frame = Time.frameCount;
		if (record.LastFrame != frame)
		{
			record.LastFrame = frame;
			record.Frames++;
		}
		record.Milliseconds += ms;
		if (frame != _frame)
		{
			_frame = frame;
			_frameMs = 0;
			BusyFrames++;
		}
		_frameMs += ms;
		BusyFrameMs += ms;
		if (_frameMs > MaxFrameMs)
		{
			MaxFrameMs = _frameMs;
		}
	}

	internal static void Coarse(SearchPathRequest request, Record record)
	{
		record.Coarse = request.Searched;
	}

	internal static void Improved(SearchPathRequest request, Record record, bool finished)
	{
		record.OptimisticLod = request.OptimisticLod;
		record.ImproveLod = request.ImproveLod;
		record.LodMax = request.LodMax;
		if (!finished)
		{
			return;
		}
		if (!request.Precise)
		{
			record.Outcome = "not precise";
		}
		else if (request.ImproveLod >= request.LodMax)
		{
			record.Outcome = "kept coarse";
		}
		else
		{
			record.Outcome = "improved";
		}
	}

	internal static void Concluded(SearchPathRequest request)
	{
		Record record = Of(request);
		if (record == null)
		{
			return;
		}
		if (!request.Succeeded)
		{
			record.Outcome = "failed";
		}
		else if (record.Outcome == "abandoned")
		{
			record.Outcome = "improved";
		}
		record.Length = (request.SuccessLink != null) ? request.SuccessLink.length : 0;
		End(request, record, record.Outcome);
	}

	private static void End(SearchPathRequest request, Record record, string outcome)
	{
		Running.Remove(request);
		record.Outcome = outcome;
		record.Ended = true;
		Done.Add(record);
		Recent.Add(record);
		if (Recent.Count > RecentMax)
		{
			Recent.RemoveAt(0);
		}
	}

	internal static string CellKey(XYZ p)
	{
		return Floor(p.x) + "," + Floor(p.y) + "," + Floor(p.z);
	}

	private static int Floor(int v)
	{
		return Mathf.FloorToInt(v / (float)Cell) * Cell;
	}
}

[HarmonyPatch(typeof(SearchPathRequest), "InitSearch")]
internal static class PathStatsStartPatch
{
	private static void Postfix(SearchPathRequest __instance)
	{
		PathStats.Start(__instance);
	}
}

[HarmonyPatch(typeof(SearchPathRequest), "Work")]
internal static class PathStatsWorkPatch
{
	private static void Prefix(SearchPathRequest __instance, out PathStats.Record __state)
	{
		__state = PathStats.Of(__instance);
		if (__state != null)
		{
			PathStats.Begin();
		}
	}

	private static void Postfix(SearchPathRequest __instance, PathStats.Record __state)
	{
		if (__state != null)
		{
			PathStats.Stop(__state);
			PathStats.Coarse(__instance, __state);
		}
	}
}

[HarmonyPatch(typeof(SearchPathRequest), "LoopImprovePath")]
internal static class PathStatsImprovePatch
{
	private static void Prefix(SearchPathRequest __instance, out PathStats.Record __state)
	{
		__state = PathStats.Of(__instance);
		if (__state != null)
		{
			PathStats.Begin();
		}
	}

	private static void Postfix(SearchPathRequest __instance, bool __result, PathStats.Record __state)
	{
		if (__state != null)
		{
			PathStats.Stop(__state);
			PathStats.Improved(__instance, __state, __result);
		}
	}
}

// The fine search's visited nodes: ImproveSearched starts again at each level, so it is added up per step.
[HarmonyPatch(typeof(SearchPathRequest), "WorkImprovePath")]
internal static class PathStatsImproveNodesPatch
{
	private static void Prefix(SearchPathRequest __instance, out int __state)
	{
		__state = __instance.ImproveSearched;
	}

	private static void Postfix(SearchPathRequest __instance, int __state)
	{
		PathStats.Record record = PathStats.Of(__instance);
		if (record != null)
		{
			record.Improve += __instance.ImproveSearched - __state;
		}
	}
}

[HarmonyPatch(typeof(SearchPathRequest), "ConcludeSearch")]
internal static class PathStatsConcludePatch
{
	private static void Postfix(SearchPathRequest __instance)
	{
		PathStats.Concluded(__instance);
	}
}
