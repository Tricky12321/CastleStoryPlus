using System;
using System.Collections.Generic;
using System.Diagnostics;
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

	// After a speed change, how long game_speed lets the game run before it measures the speed reached.
	private const int SpeedSettleMs = 2500;

	// Widest screenshot game_screenshot sends: a 1920 x 1080 PNG of the game, a third larger as base64, can pass the
	// 10 MB an MCP client takes in one message.
	private const int DefaultImageWidth = 1280;

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
			Get(api, "game_objects", "/api/objects", "Things in the world whose type name contains q (buildings, blueprints, resources, units), with counts per type. Trees are listed as Nature.Tree (q=tree or arbre) with root voxel and size, nearest x,z (default the home crystal) first.",
				Prop("q", "string", "Part of the type name, e.g. Palette, Wood, Workbench, Tree", required: true),
				Prop("max", "integer", "Most objects to list (default 100)"),
				Prop("x", "integer", "Trees: nearest this x first"),
				Prop("z", "integer", "Trees: nearest this z first")),
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
			Start(api),
			Get(api, "path_find", "/api/path", "Runs the game's own pathfinding (A*, with the mod's patches) from one voxel to another and compares it with the shortest route (Dijkstra over the same navigation graph). Positions are world/voxel coordinates x,y,z.",
				Prop("from", "string", "Start, 'x,y,z'", required: true),
				Prop("to", "string", "Goal, 'x,y,z'", required: true),
				Prop("enemy", "boolean", "Search as an enemy (AI faction) does"),
				Prop("nodes", "boolean", "Include the route's nodes")),
			Get(api, "path_node", "/api/path/node", "The navigation node at a voxel: walkable or not, its links to neighbours (steps, stairs, ladders, jumps).",
				Prop("at", "string", "Voxel 'x,y,z'", required: true)),
			Get(api, "path_stats", "/api/path/stats", "Measurements of the route searches the game ran since the last reset (path_stats_reset): count per minute, CPU per search and per frame, frames until done, nodes of the coarse and fine search, how often the fine search ran out and kept the coarse route, length per distance, and which targets and origin/target pairs (4-voxel cells) come back again and again.",
				Prop("enemy", "boolean", "true: only enemies, false: only the player's side (default both)"),
				Prop("top", "integer", "Most targets/pairs to list (default 10)"),
				Prop("recent", "integer", "Most recent searches to list (default 20)")),
			Post(api, "path_stats_reset", "/api/path/stats/reset", "Starts the route search measurements again from zero."),
			Get(api, "game_frames", "/api/frames", "Frame rate and frame times of the last seconds (avg, 1% low, p50/p95/p99/max), garbage collections, what takes the time per frame (every Update of the game and the mod, per system; measuring starts at the first call) and the last spikes (frames over [Performance] SpikeMs) with what took their time; also the managed heap (used, reserved, before/after the last garbage collection), the allocation rate and what allocates the most per frame. Use it to find lag spikes, low frame rates and what fills the heap.",
				Prop("seconds", "number", "Window for the frame statistics (default 10)"),
				Prop("top", "integer", "Most systems and allocators to list (default 20)"),
				Prop("spikes", "integer", "Most spikes to list (default 20)"),
				Prop("mod", "boolean", "true: from now on also measure each of the mod's gameplay patches on its own")),
			Post(api, "game_frames_reset", "/api/frames/reset", "Starts the per-system averages and the spike list again from zero."),
			Post(api, "game_heap_scan", "/api/heap/scan", "Starts a scan of what fills the managed heap: every object reachable from the static fields and the game's components, sized and added up per type and per root. Time-sliced (the game keeps running); a full heap takes minutes. Read the progress and result with game_heap.",
				Prop("msPerFrame", "integer", "Work per frame in milliseconds (default 40)")),
			Get(api, "game_heap", "/api/heap", "Progress of the heap scan (game_heap_scan) while it runs, else its result: reachable objects and MB against Mono's own numbers, the types and roots (static field or Unity component type) holding the most MB, the biggest single objects with the field that held them, and the holders: per type, the field (or the field holding the array) and root its objects were found in.",
				Prop("top", "integer", "Most types to list (default 40)"),
				Prop("roots", "integer", "Most roots to list (default 30)"),
				Prop("biggest", "integer", "Most single objects to list (default 40)"),
				Prop("holders", "integer", "Most holders to list (default 30)"),
				Prop("holderType", "string", "Only holders of types whose name contains this text, e.g. System.String")),
			Get(api, "game_workertrace", "/api/workertrace", "Whether the worker trace (every worker state change and why workers reject or fail tasks, to BepInEx/workertrace.log) is on."),
			Post(api, "game_workertrace_set", "/api/workertrace", "Switches the worker trace on or off. It is off by default and costs frame time while on.",
				Prop("on", "boolean", "true: on, false: off", true)),
			GameSpeed(api),
			Get(api, "game_worlds", "/api/worlds", "The maps a new single-player game can start on (codename, name, size) with their modes and presets. Sandbox has no enemies.",
				Prop("q", "string", "Part of the map's name or codename")),
			Post(api, "game_new_world", "/api/world/new", "Starts a new single-player world (leaves the current game without saving). Answers at once; poll game_status until worldStart is null, then worldStartResult says 'started ...' or 'failed: ...'. The gamemode's intro dialog then waits for a click: close it with game_dialog_press. Default mode sandbox: no enemies, a home crystal and 5 bricktrons.",
				Prop("map", "string", "Map codename or name (default: the first official map with the mode)"),
				Prop("gamemode", "string", "Mode codename (default sandbox)"),
				Prop("preset", "string", "Preset codename or name (default: the mode's default)"),
				Prop("waves", "integer", "Invasion only: minutes between waves")),
			Post(api, "game_spawn_enemies", "/api/cheat/enemies", "Cheat (host only): spawns enemies of the enemy faction, by default as a new raid that walks to the player's home crystal and attacks, as an invasion wave does. type healtron is a corruptron that heals its allies (green, with a beam and pulses); game_bricktrons with all=true marks them healtron: true.",
				Prop("type", "string", "corruptron, biftron, minitron, crawler, magitron or healtron (default corruptron)"),
				Prop("count", "integer", "How many (default 1, at most 50)"),
				Prop("at", "string", "Where, 'x,y,z' (default on the ground about 20 voxels from the home crystal)"),
				Prop("level", "integer", "CharacterLevel 1-10 (default 1)"),
				Prop("raid", "boolean", "true (default): hired into a new raid aimed at the home crystal; false: left to their instinct")),
			Get(api, "game_dialog", "/api/dialog", "The dialog open in front of the game (the gamemode's intro, victory or defeat dialog, the mod's dialogs): title, subtitle and buttons, or open false."),
			Post(api, "game_dialog_press", "/api/dialog/press", "Presses a button of the open dialog as a click does, e.g. the intro's \"I'M READY\" that starts the game after game_new_world.",
				Prop("button", "integer", "Button index from game_dialog (default: the one Enter presses, the first)")),
			Get(api, "game_menu", "/api/menu", "The in-game menu: with the C# menu ([LuaUi] CsGameMenu, default) its items by id (bars, sidebars, task popup: label, visible) and the sidebars (open); with the Lua menu its handles (label, open, whether a C# button draws them). Also whether the resource list is on.",
				Prop("q", "string", "Part of an item's id or label (a handle's name or label)"),
				Prop("kind", "string", "mh (menus, default) or bh (buttons)")),
			Post(api, "game_menu_press", "/api/menu/press", "Presses an in-game menu button as a click does. C# menu: an item id (e.g. structures, blocks, resourceList, taskActionsPause) or a sidebar id to toggle (newTask, structures, blocks, blueprintsMenu, settings). Lua menu: a handle name (buildStructures, newTask...).",
				Prop("handle", "string", "Item or sidebar id (C# menu), or handle name in _m.mh or _m.bh (Lua menu)", required: true),
				Prop("kind", "string", "mh or bh (default: whichever has it)")),
			Post(api, "game_ui_reload", "/api/ui/reload", "Reloads the in-game menu's code from the newest build (CastleStoryPlus.UI.dll; build it with dotnet build first) and builds the menu again in the running game, without restarting it. Patches and the rest of the mod are not reloaded. Follow with game_screenshot to see the result."),
			Post(api, "game_test_world", "/api/testworld", "Lays out a test world in the running game (start a sandbox first with game_new_world): near the home crystal on a flat stone floor, one of every structure in rows by the build menu's groups, every block by group from small to big, and a stockpile for every resource, filled. Built at once; follow it with game_test_world_status. Save the game afterwards to keep it.",
				Prop("width", "integer", "Width of the site in voxels (default 120)")),
			Get(api, "game_test_world_status", "/api/testworld", "How the test world's building goes: state, site, placed and refused pieces, items stored, log."),
			Post(api, "game_camera", "/api/camera", "Points the game camera at a place (then game_screenshot), from a distance, pitch and yaw.",
				Prop("at", "string", "What to look at 'x,y,z'", required: true),
				Prop("distance", "number", "Distance (default 14)"),
				Prop("pitch", "number", "Degrees down (default 50)"),
				Prop("yaw", "number", "Degrees round the vertical (default 45)")),
			Post(api, "game_select", "/api/select", "Selects a building as a click does, so its menu opens (then game_screenshot): by object id, or the nearest of a type, e.g. q=MarketStation, ResearchStation, SmithyStation, ArmouryStation, Loom.",
				Prop("id", "integer", "Object id"),
				Prop("q", "string", "Part of the station's type name")),
			Get(api, "game_base", "/api/base", "The player's base: faction, home crystal (position, ground height, energy, bricktrons it can spawn; isHome false: the game has not marked it yet, before the intro is closed, so the faction's crystal nearest its bricktrons), bricktrons by job and the projects."),
			Get(api, "game_ground", "/api/ground", "Terrain heights around a point (default the home crystal): the top solid voxel per column (build on y+1), the trees in the area (count and the 10 nearest root voxels), and flat squares to build on, nearest first.",
				Prop("x", "integer", "Center x"),
				Prop("z", "integer", "Center z"),
				Prop("radius", "integer", "Radius in voxels (default 8, at most 40)"),
				Prop("flat", "integer", "Side of the flat squares to look for, e.g. 6")),
			Get(api, "game_blueprint_keys", "/api/blueprints/keys", "The blueprints the build menu offers (stone, wood, structures such as Pallet = stockpile and the workshops) with their footprint.",
				Prop("all", "boolean", "Every blueprint key, not only the build menu's")),
			Post(api, "game_place", "/api/blueprints/place", "Places blueprints like the player does into a build project (made if none) and commits it. With 'to' it fills the box at-to. Answers per piece: placed or refused (obstructed, unsupported, invalid).",
				Prop("key", "string", "Blueprint key, e.g. Stone_DoubleBrick, Stone_Brick, Wood_Ladder, Pallet", required: true),
				Prop("at", "string", "The empty voxel 'x,y,z' it goes in (on the ground: groundY + 1)", required: true),
				Prop("to", "string", "Other corner 'x,y,z' of a box to fill"),
				Prop("turn", "integer", "Degrees: 0, 90, 180 or 270"),
				Prop("project", "integer", "Build project id (default: the one used last)"),
				Prop("force", "boolean", "Also place unsupported blueprints (they wait for support)")),
			Get(api, "game_projects", "/api/projects", "The player's projects (task bar): id, type, priority, crew and crew size, goals (harvest: the things of its accepted kinds in its circle, harvest.inRange per kind; gameGoals: the game's own count, filled a few frames after create; quarry.toDig: voxels left to dig), marker position and radius."),
			Post(api, "game_project_create", "/api/projects/create", "Makes a build project, or a quarry digging out the box from-to.",
				Prop("type", "string", "build or quarry", required: true),
				Prop("at", "string", "Marker position 'x,y,z'"),
				Prop("from", "string", "Quarry: one corner 'x,y,z'"),
				Prop("to", "string", "Quarry: the opposite corner 'x,y,z'")),
			Post(api, "game_project_priority", "/api/projects/priority", "Sets a project's priority.",
				Prop("id", "integer", "Project id", required: true),
				Prop("priority", "string", "locked, low, medium or high", required: true)),
			Post(api, "game_project_crew", "/api/projects/crew", "Sets how many bricktrons may work on a project (0 to 8).",
				Prop("id", "integer", "Project id", required: true),
				Prop("size", "integer", "Crew size 0 to 8", required: true)),
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
				Prop("speed", "number", "Game speed for the run (0.25-20, e.g. 10, 15, 20; default 1), back to 1x when the run ends. The tests' waits count game time and their timeouts real time, so they finish sooner. Single player/host only"),
				Prop("wait", "boolean", "Wait for the run to finish (default true)"),
				Prop("timeoutSeconds", "integer", "Longest wait (default 600)")
			}),
			Handler = async (JsonObject arguments) =>
			{
				string started = await api.CallAsync("POST", "/api/tests/run", Query(arguments, "filter", "speed"));
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

	// Reads or sets the game speed. After a change it lets the game run a moment and measures the speed reached, which
	// can be lower than asked when the computer cannot run that many physics steps per second.
	private static McpTool GameSpeed(GameApi api)
	{
		return new McpTool
		{
			Name = "game_speed",
			Description = "Game speed for testing: fast-forward the running game up to 20x (e.g. 10, 15, 20) so things play out much faster than real time; returns the achieved speed (game seconds per real second, measured), fps and fixed steps per frame. Without speed it only reads the state. Single player/host only. Goes back to 1x by itself after forSeconds (real time, kept in the game, so it also happens if not reset), when a map loads or on leaving to the menu. Pausing keeps the speed. Set speed 1 when done.",
			InputSchema = Schema(new[]
			{
				Prop("speed", "number", "New speed: 1, 2, 3, 5, 10, 15, 20 or any from 0.25 to 20. Leave out to only read the speed"),
				Prop("forSeconds", "number", "Real seconds after which the game goes back to 1x by itself (default: stays until changed)")
			}),
			Handler = async (JsonObject arguments) =>
			{
				if (arguments["speed"] == null)
				{
					return McpTool.Text(await api.CallAsync("GET", "/api/speed"));
				}
				JsonObject set = await api.CallJsonAsync("POST", "/api/speed", Query(arguments, "speed", "forSeconds"));
				// Paused or back at 1x: nothing to measure.
				if (set == null || (bool)set["paused"] || !(bool)set["setByDevTools"])
				{
					return McpTool.Text(set.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
				}
				await Task.Delay(SpeedSettleMs);
				Dictionary<string, string> window = new Dictionary<string, string> { ["seconds"] = (SpeedSettleMs / 1000.0 - 0.5).ToString(System.Globalization.CultureInfo.InvariantCulture) };
				return McpTool.Text(await api.CallAsync("GET", "/api/speed", window));
			}
		};
	}

	// The game writes the screenshot at the end of its frame; waits for the file and answers it as an image.
	// Starts the game through Steam and the game's launcher, which presses Play by itself (a one-time flag file the
	// mod's launcher fix looks for), then waits until the DevTools API answers.
	private static McpTool Start(GameApi api)
	{
		return new McpTool
		{
			Name = "game_start",
			Description = "Starts Castle Story through Steam and its launcher (Play is pressed by itself) and waits until the game's API answers. Does nothing when the game already runs. The game folder: environment variable CSP_GAME_DIR (default the Steam library's 'Castle Story').",
			InputSchema = Schema(new[]
			{
				Prop("wait", "boolean", "Wait until the API answers (default true, at most 3 minutes)")
			}),
			Handler = async (JsonObject arguments) =>
			{
				try
				{
					JsonObject running = await api.CallJsonAsync("GET", "/api/status");
					return McpTool.Text("The game already runs: " + running.ToJsonString());
				}
				catch (InvalidOperationException)
				{
				}
				string gameDir = GameDir();
				if (!Directory.Exists(gameDir))
				{
					return McpTool.Text("No game folder at " + gameDir + " (set CSP_GAME_DIR)", isError: true);
				}
				string flagDir = Path.Combine(gameDir, "BepInEx", "devtools");
				Directory.CreateDirectory(flagDir);
				await File.WriteAllTextAsync(Path.Combine(flagDir, "autoplay"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
				if (OperatingSystem.IsWindows())
				{
					Process.Start(new ProcessStartInfo("steam://rungameid/227860") { UseShellExecute = true });
				}
				else
				{
					Process.Start(new ProcessStartInfo("steam", "-applaunch 227860") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
				}
				if (arguments?["wait"] != null && !(bool)arguments["wait"])
				{
					return McpTool.Text("Started through Steam; the launcher presses Play by itself.");
				}
				DateTime until = DateTime.Now.AddMinutes(3);
				while (DateTime.Now < until)
				{
					await Task.Delay(2000);
					try
					{
						JsonObject status = await api.CallJsonAsync("GET", "/api/status");
						return McpTool.Text("The game is up: " + status.ToJsonString());
					}
					catch (InvalidOperationException)
					{
					}
				}
				return McpTool.Text("Started, but the API did not answer within 3 minutes (is the launcher's Play pressed? the mod's launcher fix is applied on the game's first start)", isError: true);
			}
		};
	}

	private static string GameDir()
	{
		string dir = Environment.GetEnvironmentVariable("CSP_GAME_DIR");
		if (!string.IsNullOrEmpty(dir))
		{
			return dir;
		}
		if (OperatingSystem.IsWindows())
		{
			return @"C:\Program Files (x86)\Steam\steamapps\common\Castle Story";
		}
		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".steam", "steam", "steamapps", "common", "Castle Story");
	}

	private static McpTool Screenshot(GameApi api)
	{
		return new McpTool
		{
			Name = "game_screenshot",
			Description = "Takes a screenshot of the game window and shows it, at most 1280 pixels wide (an MCP client closes a connection that sends more than 10 MB in one message); the full-size file stays at the path given.",
			InputSchema = Schema(new[]
			{
				Prop("maxWidth", "number", "Widest picture sent, in pixels (default 1280)")
			}),
			Handler = async (JsonObject arguments) =>
			{
				int maxWidth = arguments["maxWidth"] != null ? (int)arguments["maxWidth"].GetValue<double>() : DefaultImageWidth;
				JsonObject answer = await api.CallJsonAsync("POST", "/api/screenshot", new Dictionary<string, string> { ["maxWidth"] = maxWidth.ToString() });
				string path = (string)answer["path"];
				// The small copy is written a frame after the capture; an older game answers without one.
				string sent = (string)answer["smallPath"] ?? path;
				for (int i = 0; i < 50; i++)
				{
					await Task.Delay(100);
					if (File.Exists(sent) && new FileInfo(sent).Length > 0)
					{
						await Task.Delay(200);
						return McpTool.Image(Convert.ToBase64String(await File.ReadAllBytesAsync(sent)), path);
					}
				}
				return McpTool.Text("The screenshot was not written within 5 s: " + sent, isError: true);
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
