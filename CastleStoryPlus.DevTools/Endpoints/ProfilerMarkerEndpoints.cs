#if UNITY6
using System;
using System.Collections.Generic;
using CastleStoryPlus.DevTools.Api;
using CastleStoryPlus.DevTools.Frames;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// Unity's own profiler markers, timed (Frames/ProfilerMarkers): the native work inside a player loop step.
internal static class ProfilerMarkerEndpoints
{
	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/frames/markers", "Unity's own profiler markers (culling, drawing, skinning, each script type's Update and so on) since the last reset: ms per frame, worst frame and in how many frames it ran; all of them in a development player, few in a release one; the first call starts the recorders", Get)
		{
			Parameters = "categories (comma list, e.g. Render,Scripts,Animation,Physics,GUI,Internal; default all), top (most markers, default 60), filter (part of a marker's name), reset (true: start again from zero), stop (true: take the recorders away again)"
		});
	}

	private static object Get(ApiRequest request)
	{
		if (request.Bool("stop", false))
		{
			ProfilerMarkers.Stop();
			return Obj("running", false);
		}
		bool started = !ProfilerMarkers.Running;
		if (started)
		{
			HashSet<string> categories = new HashSet<string>();
			foreach (string category in request.String("categories", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
			{
				categories.Add(category.Trim());
			}
			ProfilerMarkers.Start(categories);
		}
		long frames = ProfilerMarkers.Frames;
		double seconds = ProfilerMarkers.Since.Elapsed.TotalSeconds;
		string filter = request.String("filter", "");
		List<ProfilerMarkers.Marker> markers = new List<ProfilerMarkers.Marker>(ProfilerMarkers.Markers);
		markers.Sort((a, b) => b.Ms.CompareTo(a.Ms));
		int top = request.Int("top", 60);
		List<object> rows = new List<object>();
		foreach (ProfilerMarkers.Marker marker in markers)
		{
			if (rows.Count >= top || marker.Count == 0)
			{
				break;
			}
			if (filter.Length > 0 && marker.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
			{
				continue;
			}
			rows.Add(Obj(
				"marker", marker.Name,
				"category", marker.Category,
				"msPerFrame", (frames > 0) ? (float)Math.Round(marker.Ms / frames, 3) : 0f,
				"maxMsInFrame", Round((float)marker.MaxMs),
				"framesSeen", marker.Count));
		}
		List<object> counters = new List<object>();
		foreach (ProfilerMarkers.Counter counter in ProfilerMarkers.Counters)
		{
			if (frames > 0 && counter.Sum > 0)
			{
				counters.Add(Obj("counter", counter.Name, "perFrame", (float)Math.Round(counter.Sum / frames, 1), "maxInFrame", counter.Max));
			}
		}
		if (request.Bool("reset", false))
		{
			ProfilerMarkers.Reset();
		}
		return Obj(
			"startedNow", started,
			"recorders", ProfilerMarkers.Markers.Count,
			"frames", frames,
			"fps", (seconds > 0) ? Round((float)(frames / seconds)) : 0f,
			"markers", rows,
			"counters", counters);
	}
}
#endif
