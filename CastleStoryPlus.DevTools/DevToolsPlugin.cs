using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using CastleStoryPlus.DevTools.Api;
using CastleStoryPlus.DevTools.Memory;
using CastleStoryPlus.DevTools.Pathing;
using CastleStoryPlus.DevTools.Speed;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.DevTools.Worlds;

namespace CastleStoryPlus.DevTools;

// Developer tools for Castle Story Plus, a plugin of its own that is never part of a release:
// - an HTTP API on localhost into the running game ([Api] Port), used by the MCP server (CastleStoryPlus.Mcp) so
//   Claude can read the game's state, run the in-game tests and look at the results;
// - the in-game test runner (Testing) with the tests (Tests) that check the mod in a real game: pathfinding, ...;
// - measurements of the game's route searches (Pathing/PathStats), read at /api/path/stats;
// - a scan of what fills the managed heap (Memory/HeapScan), read at /api/heap;
// - fast-forward up to 20x for testing (Speed/GameSpeed), at /api/speed;
// - each frame split into Unity's phases by time stamps (Frames/FramePhases), at /api/frames/phases;
// - starting new worlds and building through the API (Worlds/WorldStarter, Building/BlueprintPlacer), so Claude can
//   play: /api/worlds, /api/world/new, /api/base, /api/ground, /api/blueprints/place, /api/projects;
// - a test world with one of every building and block and full stockpiles (Worlds/TestWorldBuilder), /api/testworld.
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(CastleStoryPlus.Plugin.Guid)]
public class DevToolsPlugin : BaseUnityPlugin
{
	public const string Guid = "com.tricky12321.castlestoryplus.devtools";

	public const string Name = "Castle Story Plus DevTools";

	public const string Version = "0.1.0";

	internal static ManualLogSource Log;

	internal static ConfigEntry<bool> ApiEnabled;

	internal static ConfigEntry<int> ApiPort;

	private ApiServer _server;

	private void Awake()
	{
		Log = Logger;
		ApiEnabled = Config.Bind("Api", "Enabled", true, "Run the developer API on localhost (only this computer can reach it).");
		ApiPort = Config.Bind("Api", "Port", 27860, "Port of the developer API: http://127.0.0.1:<port>/api/...");
		LogBuffer.Install();
		gameObject.AddComponent<MainThread>();
		gameObject.AddComponent<HeapScan>();
		gameObject.AddComponent<TestRunner>();
		gameObject.AddComponent<GameSpeed>();
		gameObject.AddComponent<Frames.FramePhases>();
		gameObject.AddComponent<WorldStarter>();
		gameObject.AddComponent<Building.InstantBuild>();
		gameObject.AddComponent<TestWorldBuilder>();
		gameObject.AddComponent<BenchmarkWorld>();
		gameObject.AddComponent<Frames.BenchmarkRun>();
		gameObject.AddComponent<Endpoints.CameraHold>();
		PathStats.Reset();
		new Harmony(Guid).PatchAll(typeof(DevToolsPlugin).Assembly);
		if (ApiEnabled.Value)
		{
			_server = new ApiServer(ApiPort.Value, Endpoints.Routes.Build());
			_server.Start();
		}
		Log.LogInfo(Name + " " + Version + " loaded");
	}

	private void OnDestroy()
	{
		if (_server != null)
		{
			_server.Stop();
		}
	}
}
