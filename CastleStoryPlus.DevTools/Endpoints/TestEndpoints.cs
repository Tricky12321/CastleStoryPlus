using System.Collections.Generic;
using Brix.Engine;
using CastleStoryPlus.DevTools.Api;
using CastleStoryPlus.DevTools.Speed;
using CastleStoryPlus.DevTools.Testing;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// The in-game tests: list them, start a run, follow it and read the results.
internal static class TestEndpoints
{
	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/tests", "The in-game tests", List)
		{
			Parameters = "filter (part of a test name or category)"
		});
		routes.Add(new Route("POST", "/api/tests/run", "Starts a run of the matching tests (answers at once; follow it with /api/tests/results)", Run)
		{
			Parameters = "filter (part of a test name or category; empty: all), speed (game speed for the run, 0.25-20, back to 1x when it ends; single player/host only), parallel (tests at the same time, 1-8, default 1: those of [ParallelTests] classes, one per group; the others alone), clearUnits (true: take the world's own units, the player's bricktrons and roaming enemies, away first and keep the base's repair and cleanup tasks without crew during the run)"
		});
		routes.Add(new Route("GET", "/api/tests/results", "The current or last run: progress and each test's outcome, message, log and recorded values", Results)
		{
			Parameters = "log (false: leave out each test's log lines)"
		});
		routes.Add(new Route("POST", "/api/tests/stop", "Stops the run after the current test's clean-up", Stop));
	}

	private static object List(ApiRequest request)
	{
		List<object> tests = new List<object>();
		foreach (TestRunner.TestInfo test in TestRunner.Matching(request.String("filter", null)))
		{
			tests.Add(Obj("name", test.Name, "category", test.Category, "description", test.Description, "timeoutSeconds", test.TimeoutSeconds, "needsHostGame", test.NeedsHostGame, "parallel", test.Parallel, "group", test.Group));
		}
		return Obj("count", tests.Count, "tests", tests);
	}

	private static object Run(ApiRequest request)
	{
		List<TestRunner.TestInfo> tests = TestRunner.Matching(request.String("filter", null));
		if (tests.Count == 0)
		{
			throw new ApiException(404, "No test matches '" + request.String("filter", string.Empty) + "'");
		}
		float speed = request.Float("speed", 1f);
		if (speed < GameSpeed.MinSpeed || speed > GameSpeed.MaxSpeed)
		{
			throw new ApiException(400, "'speed' must be from " + GameSpeed.MinSpeed + " to " + GameSpeed.MaxSpeed);
		}
		if (speed != 1f && Neo.multiplayer)
		{
			throw new ApiException(409, "The game speed can only change in single player");
		}
		int parallel = request.Int("parallel", 1);
		if (parallel < 1 || parallel > 8)
		{
			throw new ApiException(400, "'parallel' must be from 1 to 8");
		}
		bool clearUnits = request.Bool("clearUnits", false);
		if (!TestRunner.Instance.StartRun(tests, speed, parallel, clearUnits))
		{
			throw new ApiException(409, "A test run is already going (POST /api/tests/stop stops it)");
		}
		return Obj("run", TestRunner.Instance.RunNumber, "started", tests.Count, "inHostGame", TestRunner.InHostGame(), "speed", speed, "parallel", parallel, "clearUnits", clearUnits);
	}

	private static object Results(ApiRequest request)
	{
		TestRunner runner = TestRunner.Instance;
		bool withLog = request.Bool("log", true);
		Dictionary<string, int> totals = new Dictionary<string, int>();
		List<object> results = new List<object>();
		foreach (TestRunner.Result result in runner.Results)
		{
			string outcome = result.Outcome.ToString();
			totals.TryGetValue(outcome, out int count);
			totals[outcome] = count + 1;
			results.Add(Obj(
				"name", result.Name,
				"category", result.Category,
				"outcome", outcome,
				"message", result.Message,
				"seconds", result.Seconds,
				"data", result.Data,
				"log", withLog ? result.Log : null));
		}
		return Obj(
			"run", runner.RunNumber,
			"running", runner.Running,
			"startedAt", (runner.RunNumber > 0) ? runner.StartedAt.ToString("dd-MM-yyyy HH:mm:ss") : null,
			"finishedAt", (runner.RunNumber > 0 && !runner.Running) ? runner.FinishedAt.ToString("dd-MM-yyyy HH:mm:ss") : null,
			"totals", totals,
			"results", results);
	}

	private static object Stop(ApiRequest request)
	{
		TestRunner.Instance.Stop();
		return Obj("stopping", TestRunner.Instance.Running);
	}
}
