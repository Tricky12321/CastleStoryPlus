using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CastleStoryPlus.Mcp.Protocol;
using CastleStoryPlus.Mcp.Services;

namespace CastleStoryPlus.Mcp.Tools;

// The tools Claude gets. Most pass their arguments straight to one API endpoint and answer its JSON.
public static class GameTools
{
	private const int TestPollMs = 1000;

	public static List<McpTool> Build(GameApi api)
	{
		return new List<McpTool>
		{
			Get(api, "game_status", "/api/status", "State of the running game and mod: in a game, host/client, paused, speed, scene, fps, whether tests run."),
			Get(api, "game_log", "/api/log", "BepInEx log lines (mod, Harmony, Unity), newest last. Pass 'after' (the 'last' id of a previous call) to get only newer lines.",
				Prop("after", "integer", "Only lines after this id"),
				Prop("count", "integer", "Most lines to return (default 200)"),
				Prop("problems", "boolean", "Warnings and errors only")),
			Get(api, "game_features", "/api/features", "The mod's features and whether each is on."),
			Get(api, "game_bricktrons", "/api/bricktrons", "Bricktrons: name, job, activity, current task and how long, position, carrying.",
				Prop("all", "boolean", "Every faction (enemies too), not only the player's"),
				Prop("q", "string", "Filter on name, job or kind")),
			Get(api, "game_objects", "/api/objects", "Things in the world whose type name contains q (buildings, blueprints, resources, units), with counts per type.",
				Prop("q", "string", "Part of the type name, e.g. Palette, Wood, Workbench", required: true),
				Prop("max", "integer", "Most objects to list (default 100)")),
			Get(api, "game_storage", "/api/storage", "Storages (stockpiles, warehouses, racks) and what they hold: amount per resource with its type and the name the UI shows, and the stored objects by type.",
				Prop("q", "string", "Part of the storage's type name, e.g. Warehouse, Palette"),
				Prop("empty", "boolean", "Also list empty storages"),
				Prop("max", "integer", "Most storages to list (default 50)")),
			Get(api, "game_selection", "/api/selection", "What the player has selected (bricktrons, wards, objects, workshop, project) with ids, positions, turn and whether each can be moved; also the build project a move would use."),
			Post(api, "game_move", "/api/move", "Moves a building as the mod's move key does: a blueprint at the new place, the old one demolished by the workers of a build project (costs work, no materials).",
				Prop("id", "integer", "Object id (from game_selection or game_objects)", required: true),
				Prop("to", "string", "New world position of the building, 'x,y,z'", required: true),
				Prop("turn", "number", "Degrees round the vertical (default: as it is)"),
				Prop("project", "integer", "Build project id (default: the last selected build project)")),
			Get(api, "game_problems", "/api/problems", "The system log's problems (F9): bricktron tasks that failed and why, and errors/warnings of the game and the mod, newest first.",
				Prop("count", "integer", "Most problems (default 100)"),
				Prop("q", "string", "Filter on source or message")),
			Get(api, "game_cleanup", "/api/cleanup", "What an idle worker's automatic cleanup sees: loose items near the base or the worker, and why each would not be picked up.",
				Prop("unit", "integer", "Bricktron id (default: the player's first builder)"),
				Prop("max", "integer", "Most items to list (default 50)")),
			Screenshot(api),
			Get(api, "path_find", "/api/path", "Runs the game's own pathfinding (A*, with the mod's patches) from one voxel to another and compares it with the shortest route (Dijkstra over the same navigation graph). Positions are world/voxel coordinates x,y,z.",
				Prop("from", "string", "Start, 'x,y,z'", required: true),
				Prop("to", "string", "Goal, 'x,y,z'", required: true),
				Prop("enemy", "boolean", "Search as an enemy (AI faction) does"),
				Prop("nodes", "boolean", "Include the route's nodes")),
			Get(api, "path_node", "/api/path/node", "The navigation node at a voxel: walkable or not, its links to neighbours (steps, stairs, ladders, jumps).",
				Prop("at", "string", "Voxel 'x,y,z'", required: true)),
			Get(api, "tests_list", "/api/tests", "The in-game tests.",
				Prop("filter", "string", "Part of a test name or category")),
			RunTests(api),
			Get(api, "tests_results", "/api/tests/results", "Results of the current or last test run: each test's outcome, message, log and recorded values.",
				Prop("log", "boolean", "Include each test's log lines (default true)")),
			Post(api, "tests_stop", "/api/tests/stop", "Stops the test run after the current test's clean-up."),
			ApiCall(api)
		};
	}

	private static McpTool Get(GameApi api, string name, string path, string description, params (string Name, JsonObject Schema, bool Required)[] properties)
	{
		return Pass(api, "GET", name, path, description, properties);
	}

	private static McpTool Post(GameApi api, string name, string path, string description, params (string Name, JsonObject Schema, bool Required)[] properties)
	{
		return Pass(api, "POST", name, path, description, properties);
	}

	private static McpTool Pass(GameApi api, string method, string name, string path, string description, (string Name, JsonObject Schema, bool Required)[] properties)
	{
		return new McpTool
		{
			Name = name,
			Description = description,
			InputSchema = Schema(properties),
			Handler = async (JsonObject arguments) => McpTool.Text(await api.CallAsync(method, path, Query(arguments)))
		};
	}

	// Starts a run and, unless told not to, waits for it and answers the results.
	private static McpTool RunTests(GameApi api)
	{
		return new McpTool
		{
			Name = "tests_run",
			Description = "Runs the in-game tests matching the filter (all when empty) in the running game. Needs a game in progress on the host (single player is fine). Waits for the results unless wait is false.",
			InputSchema = Schema(new[]
			{
				Prop("filter", "string", "Part of a test name or category, e.g. Pathfinding"),
				Prop("wait", "boolean", "Wait for the run to finish (default true)"),
				Prop("timeoutSeconds", "integer", "Longest wait (default 600)")
			}),
			Handler = async (JsonObject arguments) =>
			{
				string started = await api.CallAsync("POST", "/api/tests/run", Query(arguments, "filter"));
				if (arguments["wait"] != null && !(bool)arguments["wait"])
				{
					return McpTool.Text(started);
				}
				int timeout = (arguments["timeoutSeconds"] != null) ? (int)arguments["timeoutSeconds"] : 600;
				DateTime end = DateTime.Now.AddSeconds(timeout);
				while (DateTime.Now < end)
				{
					await Task.Delay(TestPollMs);
					JsonObject results = await api.CallJsonAsync("GET", "/api/tests/results");
					if (results != null && !(bool)results["running"])
					{
						return McpTool.Text(results.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
					}
				}
				return McpTool.Text("The run still goes after " + timeout + " s; see tests_results.", isError: true);
			}
		};
	}

	// The game writes the screenshot at the end of its frame; waits for the file and answers it as an image.
	private static McpTool Screenshot(GameApi api)
	{
		return new McpTool
		{
			Name = "game_screenshot",
			Description = "Takes a screenshot of the game window and shows it.",
			InputSchema = Schema(Array.Empty<(string, JsonObject, bool)>()),
			Handler = async (JsonObject arguments) =>
			{
				JsonObject answer = await api.CallJsonAsync("POST", "/api/screenshot");
				string path = (string)answer["path"];
				for (int i = 0; i < 50; i++)
				{
					await Task.Delay(100);
					if (File.Exists(path) && new FileInfo(path).Length > 0)
					{
						await Task.Delay(200);
						return McpTool.Image(Convert.ToBase64String(await File.ReadAllBytesAsync(path)), path);
					}
				}
				return McpTool.Text("The screenshot was not written within 5 s: " + path, isError: true);
			}
		};
	}

	// Any endpoint (GET /api lists them all).
	private static McpTool ApiCall(GameApi api)
	{
		return new McpTool
		{
			Name = "api_call",
			Description = "Calls any endpoint of the in-game DevTools API directly. GET /api lists every endpoint with its parameters.",
			InputSchema = Schema(new[]
			{
				Prop("method", "string", "GET or POST", required: true),
				Prop("path", "string", "e.g. /api/status", required: true),
				(Name: "query", Schema: new JsonObject { ["type"] = "object", ["description"] = "Query parameters", ["additionalProperties"] = new JsonObject { ["type"] = "string" } }, Required: false),
				(Name: "body", Schema: new JsonObject { ["type"] = "object", ["description"] = "JSON body (POST)" }, Required: false)
			}),
			Handler = async (JsonObject arguments) =>
			{
				Dictionary<string, string> query = new Dictionary<string, string>();
				if (arguments["query"] is JsonObject queryObject)
				{
					foreach (KeyValuePair<string, JsonNode> pair in queryObject)
					{
						query[pair.Key] = Value(pair.Value);
					}
				}
				string method = ((string)arguments["method"] ?? "GET").ToUpperInvariant();
				return McpTool.Text(await api.CallAsync(method, (string)arguments["path"], query, arguments["body"] as JsonObject));
			}
		};
	}

	private static (string Name, JsonObject Schema, bool Required) Prop(string name, string type, string description, bool required = false)
	{
		return (name, new JsonObject { ["type"] = type, ["description"] = description }, required);
	}

	private static JsonObject Schema((string Name, JsonObject Schema, bool Required)[] properties)
	{
		JsonObject props = new JsonObject();
		JsonArray required = new JsonArray();
		foreach ((string name, JsonObject schema, bool isRequired) in properties)
		{
			props[name] = schema;
			if (isRequired)
			{
				required.Add(name);
			}
		}
		JsonObject result = new JsonObject { ["type"] = "object", ["properties"] = props };
		if (required.Count > 0)
		{
			result["required"] = required;
		}
		return result;
	}

	// The tool's arguments as query parameters (only the named ones, when names are given).
	private static Dictionary<string, string> Query(JsonObject arguments, params string[] only)
	{
		Dictionary<string, string> query = new Dictionary<string, string>();
		foreach (KeyValuePair<string, JsonNode> pair in arguments)
		{
			if (only.Length > 0 && Array.IndexOf(only, pair.Key) < 0)
			{
				continue;
			}
			query[pair.Key] = Value(pair.Value);
		}
		return query;
	}

	private static string Value(JsonNode node)
	{
		if (node == null)
		{
			return null;
		}
		if (node is JsonValue value && value.TryGetValue(out string text))
		{
			return text;
		}
		return node.ToJsonString();
	}
}
