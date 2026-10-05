using System.Collections.Generic;
using Brix.External.Factories;
using Brix.Game.AI;
using CastleStoryPlus.DevTools.Api;
using CastleStoryPlus.DevTools.Pathing;
using CastleStoryPlus.Diagnostics;
using CastleStoryPlus.WorkerAI;
using UnityEngine;
using UnityEngine.Networking;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// Why workers do or do not do things: the problems the mod's system log collected (failed tasks with their
// reason, errors), and what the idle workers' automatic cleanup sees.
internal static class DiagnosticsEndpoints
{
	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/problems", "The system log's problems (F9): bricktron tasks that failed and why, errors and warnings of the game and the mod", Problems)
		{
			Parameters = "count (default 100), q (filter on source or message)"
		});
		routes.Add(new Route("GET", "/api/cleanup", "What a worker's automatic cleanup would do: each loose item near the base or the worker and why it would not be picked up", Cleanup)
		{
			Parameters = "unit (bricktron id; default: the player's first builder), max (default 50)"
		});
	}

	private static object Problems(ApiRequest request)
	{
		int count = request.Int("count", 100);
		string filter = request.String("q", string.Empty);
		List<object> result = new List<object>();
		List<SystemLog.Entry> entries = SystemLog.Problems();
		for (int i = entries.Count - 1; i >= 0 && result.Count < count; i--)
		{
			SystemLog.Entry entry = entries[i];
			if (filter.Length > 0 && (entry.Source + " " + entry.Message).IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) < 0)
			{
				continue;
			}
			result.Add(Obj(
				"time", entry.Time.ToString("HH:mm:ss"),
				"severity", entry.Severity.ToString(),
				"source", entry.Source,
				"message", entry.Message,
				"details", entry.Details,
				"count", entry.Count));
		}
		return Obj("count", result.Count, "problems", result);
	}

	private static object Cleanup(ApiRequest request)
	{
		if (!NetworkServer.active)
		{
			throw new ApiException(409, "Only on the host");
		}
		if (AutoCleanup.Radius == null)
		{
			throw new ApiException(409, "The AutoCleanup feature is switched off");
		}
		Labor labor = Units.Find(request.Int("unit", 0), false);
		if (labor == null)
		{
			throw new ApiException(404, "No bricktron");
		}
		int max = request.Int("max", 50);
		Dictionary<string, int> reasons = new Dictionary<string, int>();
		List<object> items = new List<object>();
		foreach (KeyValuePair<GameObject, string> pair in AutoCleanup.Explain(labor))
		{
			string why = pair.Value ?? "would pick it up";
			reasons.TryGetValue(why, out int n);
			reasons[why] = n + 1;
			if (items.Count < max)
			{
				FactoryImprint imprint = pair.Key.GetComponent<FactoryImprint>();
				items.Add(Obj(
					"id", pair.Key.GetInstanceID(),
					"type", (imprint != null && imprint.AssetKey != null) ? (imprint.AssetKey.Factory + "." + imprint.AssetKey.Name) : pair.Key.name,
					"position", Vec(pair.Key.transform.position),
					"why", why));
			}
		}
		return Obj(
			"unit", labor.name,
			"radius", AutoCleanup.Radius.Value,
			"carrying", !labor.recepteur.IsEmpty(),
			"reasons", reasons,
			"items", items);
	}
}
