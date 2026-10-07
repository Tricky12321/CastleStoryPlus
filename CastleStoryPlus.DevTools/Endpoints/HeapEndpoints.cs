using System;
using System.Collections.Generic;
using System.Linq;
using CastleStoryPlus.DevTools.Api;
using CastleStoryPlus.DevTools.Memory;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// What fills the managed heap: a scan of every object reachable from the static fields and the game's components
// (Memory/HeapScan), totals per type and per root, and the biggest single objects. The scan runs a few milliseconds
// per frame, so it takes a while on a full heap; ask GET /api/heap until it is done.
internal static class HeapEndpoints
{
	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("POST", "/api/heap/scan", "Starts a scan of the managed heap (time-sliced, the game keeps running; a full heap takes minutes) and answers at once with its state", Scan)
		{
			Parameters = "msPerFrame (work per frame, default 40)"
		});
		routes.Add(new Route("GET", "/api/heap", "The heap scan's progress while it runs, else the last result: reachable objects and MB, the types and roots (static field or Unity component type) holding the most, and the biggest single objects with the field that held them", Heap)
		{
			Parameters = "top (most types to list, default 40), roots (most roots to list, default 30), biggest (most single objects to list, default 40)"
		});
	}

	private static object Scan(ApiRequest request)
	{
		bool started = HeapScan.StartScan(request.Int("msPerFrame", 40));
		return Obj("started", started, "state", State());
	}

	private static object Heap(ApiRequest request)
	{
		HeapScan.Result result = HeapScan.Last;
		if (HeapScan.Running || result == null)
		{
			return Obj("state", State(), "last", (result != null) ? Summary(result) : null);
		}
		return Obj(
			"state", State(),
			"result", Summary(result),
			"types", result.Types.Take(request.Int("top", 40)).Select((HeapScan.TypeRow t) => Obj(
				"type", t.Name,
				"count", t.Count,
				"mb", Mb(t.Bytes))).ToList(),
			"roots", result.RootRows.Take(request.Int("roots", 30)).Select((HeapScan.RootRow r) => Obj(
				"root", r.Label,
				"objects", r.Count,
				"mb", Mb(r.Bytes))).ToList(),
			"biggest", result.Biggest.Take(request.Int("biggest", 40)).Select((HeapScan.BigObject b) => Obj(
				"type", b.Type,
				"mb", Mb(b.Bytes),
				"length", (b.Length >= 0) ? (object)b.Length : null,
				"via", b.Via,
				"root", b.Root)).ToList());
	}

	private static object State()
	{
		return Obj(
			"running", HeapScan.Running,
			"phase", HeapScan.Phase,
			"objects", HeapScan.Objects,
			"mb", Mb(HeapScan.Bytes),
			"roots", HeapScan.RootsDone + " / " + HeapScan.RootsTotal,
			"stack", HeapScan.StackDepth,
			"failures", HeapScan.Failures,
			"elapsedSeconds", Math.Round(HeapScan.ElapsedSeconds, 1),
			"msPerFrame", HeapScan.MsPerFrame);
	}

	private static object Summary(HeapScan.Result result)
	{
		return Obj(
			"finished", result.Finished.ToString("dd-MM-yyyy HH:mm:ss"),
			"seconds", Math.Round(result.Seconds, 1),
			"objects", result.Objects,
			"reachableMb", Mb(result.Bytes),
			"roots", result.Roots,
			"types", result.TypesSeen,
			"failures", result.Failures,
			"gcTotalMemoryStartMb", Mb(result.GcStart),
			"gcTotalMemoryEndMb", Mb(result.GcEnd),
			"monoUsedStartMb", Mb(result.MonoUsedStart),
			"monoUsedEndMb", Mb(result.MonoUsedEnd),
			"monoHeapMb", Mb(result.MonoHeapEnd));
	}

	private static double Mb(long bytes)
	{
		return Math.Round(bytes / 1048576.0, 2);
	}
}
