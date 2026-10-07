using System.Collections.Generic;
using System.Linq;
using CastleStoryPlus.DevTools.Api;
using CastleStoryPlus.DevTools.Pathing;
using UnityEngine;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// What the game's route searches cost while it plays (PathStats): totals, spread, how the fine search ends, and how
// often the same targets and origin/target pairs come back.
internal static class PathStatsEndpoints
{
	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/path/stats", "Measurements of the route searches the game ran since the last reset: count, CPU per search and per frame, frames until done, nodes of the coarse and fine search, how the fine search ended, repeated targets and origin/target pairs", Stats)
		{
			Parameters = "enemy (true: only enemies, false: only the player's side; default both), top (most targets/pairs to list, default 10), recent (most recent searches to list, default 20)"
		});
		routes.Add(new Route("POST", "/api/path/stats/reset", "Starts the route search measurements again from zero", Reset));
	}

	private static object Reset(ApiRequest request)
	{
		PathStats.Reset();
		return Obj("reset", true);
	}

	private static object Stats(ApiRequest request)
	{
		PathStats.Sweep();
		string side = request.Get("enemy");
		List<PathStats.Record> records = PathStats.Done.Where((PathStats.Record r) => side == null || r.Enemy == (side == "true")).ToList();
		int top = request.Int("top", 10);
		float seconds = Time.realtimeSinceStartup - PathStats.Since;
		List<PathStats.Record> ended = records.Where((PathStats.Record r) => r.Outcome != "abandoned").ToList();
		List<PathStats.Record> precise = ended.Where((PathStats.Record r) => r.Outcome == "improved" || r.Outcome == "kept coarse").ToList();
		return Obj(
			"seconds", Round(seconds),
			"searches", records.Count,
			"perMinute", (seconds > 0) ? Round(records.Count * 60f / seconds) : 0f,
			"running", PathStats.RunningCount,
			"outcomes", records.GroupBy((PathStats.Record r) => r.Outcome).ToDictionary((IGrouping<string, PathStats.Record> g) => g.Key, (IGrouping<string, PathStats.Record> g) => g.Count()),
			"enemy", records.Count((PathStats.Record r) => r.Enemy),
			"away", records.Count((PathStats.Record r) => r.Away),
			"cpu", Obj(
				"totalMs", Round((float)records.Sum((PathStats.Record r) => r.Milliseconds)),
				"perSearchMs", Spread(records.Select((PathStats.Record r) => r.Milliseconds)),
				"busyFrames", PathStats.BusyFrames,
				"perBusyFrameMs", (PathStats.BusyFrames > 0) ? Round((float)(PathStats.BusyFrameMs / PathStats.BusyFrames)) : 0f,
				"maxFrameMs", Round((float)PathStats.MaxFrameMs)),
			"frames", Spread(ended.Select((PathStats.Record r) => (double)r.Frames)),
			"coarseNodes", Spread(ended.Select((PathStats.Record r) => (double)r.Coarse)),
			"improveNodes", Spread(precise.Select((PathStats.Record r) => (double)r.Improve)),
			"improve", Obj(
				"firstLevel", precise.Count((PathStats.Record r) => r.Outcome == "improved" && r.ImproveLod <= r.OptimisticLod),
				"higherLevel", precise.Count((PathStats.Record r) => r.Outcome == "improved" && r.ImproveLod > r.OptimisticLod),
				"keptCoarse", precise.Count((PathStats.Record r) => r.Outcome == "kept coarse")),
			"distance", Spread(ended.Select((PathStats.Record r) => (double)r.Distance)),
			"lengthPerDistance", Spread(ended.Where((PathStats.Record r) => r.Distance > 0 && r.Length > 0).Select((PathStats.Record r) => (double)r.Length / r.Distance)),
			"repeats", Repeats(records),
			"targets", records.GroupBy((PathStats.Record r) => r.Target).OrderByDescending((IGrouping<string, PathStats.Record> g) => g.Count()).Take(top).Select((IGrouping<string, PathStats.Record> g) => Obj("target", g.Key, "count", g.Count(), "avgMs", Round((float)g.Average((PathStats.Record r) => r.Milliseconds)), "avgCoarse", Round((float)g.Average((PathStats.Record r) => r.Coarse)))).ToList(),
			"targetCells", records.GroupBy((PathStats.Record r) => PathStats.CellKey(r.To)).OrderByDescending((IGrouping<string, PathStats.Record> g) => g.Count()).Take(top).Select((IGrouping<string, PathStats.Record> g) => Obj("cell", g.Key, "count", g.Count(), "targets", g.Select((PathStats.Record r) => r.Target).Distinct().Take(3).ToList())).ToList(),
			"pairs", records.GroupBy(PairKey).OrderByDescending((IGrouping<string, PathStats.Record> g) => g.Count()).Take(top).Select((IGrouping<string, PathStats.Record> g) => Obj("pair", g.Key, "count", g.Count(), "target", g.First().Target)).ToList(),
			"recent", PathStats.Recent.Where((PathStats.Record r) => side == null || r.Enemy == (side == "true")).Reverse().Take(request.Int("recent", 20)).Select(RecordJson).ToList());
	}

	private static string PairKey(PathStats.Record r)
	{
		return PathStats.CellKey(r.From) + " > " + PathStats.CellKey(r.To);
	}

	// How many searches went where an earlier search already went (target cell), and from where an earlier one came
	// (the same origin and target cells): what distance maps per target, or a route cache per pair, could answer.
	private static object Repeats(List<PathStats.Record> records)
	{
		HashSet<string> targets = new HashSet<string>();
		HashSet<string> pairs = new HashSet<string>();
		int sameTarget = 0;
		int samePair = 0;
		foreach (PathStats.Record r in records)
		{
			if (!targets.Add(PathStats.CellKey(r.To)))
			{
				sameTarget++;
			}
			if (!pairs.Add(PairKey(r)))
			{
				samePair++;
			}
		}
		return Obj("cell", PathStats.Cell, "targetCells", targets.Count, "sameTarget", sameTarget, "pairs", pairs.Count, "samePair", samePair);
	}

	private static object Spread(IEnumerable<double> values)
	{
		List<double> sorted = values.OrderBy((double v) => v).ToList();
		if (sorted.Count == 0)
		{
			return null;
		}
		return Obj(
			"avg", Round((float)sorted.Average()),
			"p50", Round((float)sorted[sorted.Count / 2]),
			"p95", Round((float)sorted[Mathf.Min(sorted.Count - 1, sorted.Count * 95 / 100)]),
			"max", Round((float)sorted[sorted.Count - 1]));
	}

	private static object RecordJson(PathStats.Record r)
	{
		return Obj(
			"unit", r.Unit,
			"enemy", r.Enemy,
			"target", r.Target,
			"from", PathEndpoints.Voxel(r.From),
			"to", PathEndpoints.Voxel(r.To),
			"distance", r.Distance,
			"length", r.Length,
			"outcome", r.Outcome,
			"ms", Round((float)r.Milliseconds),
			"frames", r.Frames,
			"coarse", r.Coarse,
			"improve", r.Improve,
			"lod", r.OptimisticLod + ">" + r.ImproveLod + "/" + r.LodMax,
			"away", r.Away);
	}
}
