using System;
using System.Collections.Generic;
using System.Linq;
using CastleStoryPlus.DevTools.Api;
using CastleStoryPlus.Diagnostics;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// Frame times and spikes from the mod's FrameMonitor (the same numbers as the performance window, F7). Asking starts
// the measuring of what takes the time in each frame, if it is not running yet.
internal static class FrameEndpoints
{
	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/frames", "Frame rate and frame times of the last seconds (avg, 1% low, p50/p95/p99/max), garbage collections, what takes the time per frame (every Update of the game and the mod, measured from the first call or F7) and the last spikes with what took their time", Frames)
		{
			Parameters = "seconds (window for the frame statistics, default 10), top (most systems and allocators to list, default 20), spikes (most spikes to list, default 20), mod (true: also measure each of the mod's gameplay patches on its own, from now on)"
		});
		routes.Add(new Route("POST", "/api/frames/reset", "Starts the system averages and the spike list again from zero", Reset));
	}

	private static object Reset(ApiRequest request)
	{
		FrameMonitor.Reset();
		return Obj("reset", true);
	}

	private static object Frames(ApiRequest request)
	{
		bool started = !FrameMonitor.Detailed;
		FrameMonitor.StartDetailed();
		if (request.Bool("mod", false))
		{
			FrameMonitor.StartModPatches();
		}
		float seconds = request.Float("seconds", 10f);
		float[] recent = new float[FrameMonitor.History];
		int count = FrameMonitor.Recent(seconds, recent, out int gcs, out int spikes);
		List<float> sorted = recent.Take(count).OrderBy((float v) => v).ToList();
		int top = request.Int("top", 20);
		int measured = FrameMonitor.MeasuredFrames;
		return Obj(
			"measuringStartedNow", started,
			"measuredMethods", FrameMonitor.PatchedMethods,
			"seconds", seconds,
			"frames", count,
			"fps", Obj(
				"avg", (count > 0) ? Round(1000f / sorted.Average()) : 0f,
				"onePercentLow", (count > 0) ? Round(1000f / sorted.Skip(count - Math.Max(1, count / 100)).Average()) : 0f),
			"frameMs", (count > 0) ? Obj(
				"avg", Round(sorted.Average()),
				"p50", Round(sorted[count / 2]),
				"p95", Round(sorted[Math.Min(count - 1, count * 95 / 100)]),
				"p99", Round(sorted[Math.Min(count - 1, count * 99 / 100)]),
				"max", Round(sorted[count - 1])) : null,
			"gcs", gcs,
			"spikes", spikes,
			"spikeMs", FrameMonitor.SpikeMs.Value,
			"memory", Memory(seconds),
			"sinceReset", Obj(
				"measuredFrames", measured,
				"frameMsAvg", (measured > 0) ? Round((float)(FrameMonitor.MeasuredFrameMs / measured)) : 0f,
				"unmeasuredMsAvg", (measured > 0) ? Round((float)(FrameMonitor.UnmeasuredTotalMs / measured)) : 0f,
				"gcs", FrameMonitor.GcTotal,
				"spikes", FrameMonitor.SpikeCount,
				"missingScriptWarnings", FrameMonitor.MissingScriptTotal),
			"systems", FrameMonitor.Ranked(top).Select((FrameMonitor.Bucket b) => Obj(
				"name", b.Name,
				"msPerFrame", (measured > 0) ? Round((float)(FrameMonitor.Milliseconds(b.TotalTicks) / measured)) : 0f,
				"maxMsInFrame", Round((float)FrameMonitor.Milliseconds(b.MaxTicks)),
				"callsPerFrame", (measured > 0) ? Round((float)b.TotalCalls / measured) : 0f)).ToList(),
			"allocators", FrameMonitor.RankedByBytes(top).Select((FrameMonitor.Bucket b) => Obj(
				"name", b.Name,
				"kbPerFrame", (measured > 0) ? Round((float)(b.TotalBytes / 1024.0 / measured)) : 0f,
				"kbPerCall", (b.TotalCalls > 0) ? Round((float)(b.TotalBytes / 1024.0 / b.TotalCalls)) : 0f,
				"msPerFrame", (measured > 0) ? Round((float)(FrameMonitor.Milliseconds(b.TotalTicks) / measured)) : 0f)).ToList(),
			"lastSpikes", FrameMonitor.Spikes.AsEnumerable().Reverse().Take(request.Int("spikes", 20)).Select((FrameMonitor.Spike s) => Obj(
				"time", s.Clock.ToString("HH:mm:ss"),
				"frame", s.Frame,
				"ms", Round(s.Milliseconds),
				"gcs", s.Gcs,
				"missingScripts", s.MissingScripts,
				"unmeasuredMs", Round(s.UnmeasuredMs),
				"top", Enumerable.Range(0, s.Names.Length).Select((int i) => Obj("name", s.Names[i], "ms", Round(s.BucketMs[i]), "calls", s.Calls[i])).ToList())).ToList());
	}

	// The managed heap and how fast it fills: every collection scans all of it and stops the game meanwhile.
	private static object Memory(float seconds)
	{
		float kbPerFrame = FrameMonitor.RecentKbPerFrame(seconds, out float peakKb);
		return Obj(
			"heapUsedMb", Round(FrameMonitor.HeapUsed / 1048576f),
			"heapReservedMb", Round(FrameMonitor.HeapReserved / 1048576f),
			"kbPerFrame", Round(kbPerFrame),
			"peakKbInFrame", Round(peakKb),
			"mbPerSecondSinceReset", Round((float)FrameMonitor.AllocMbPerSecond),
			"lastGc", Obj(
				"usedBeforeMb", Round(FrameMonitor.UsedBeforeLastGc / 1048576f),
				"usedAfterMb", Round(FrameMonitor.UsedAfterLastGc / 1048576f),
				"frameMs", Round(FrameMonitor.LastGcFrameMs)),
			"gcFrameMsAvgSinceReset", Round((float)FrameMonitor.GcFrameMsAverage),
			"gcFramesSinceReset", FrameMonitor.GcFrames,
			"measuringAllocations", FrameMonitor.Allocations,
			"measuringModPatches", FrameMonitor.ModPatches);
	}
}
