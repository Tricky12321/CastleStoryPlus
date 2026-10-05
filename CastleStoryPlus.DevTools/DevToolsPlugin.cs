using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using CastleStoryPlus.DevTools.Api;
using CastleStoryPlus.DevTools.Testing;

namespace CastleStoryPlus.DevTools;

// Developer tools for Castle Story Plus, a plugin of its own that is never part of a release:
// - an HTTP API on localhost into the running game ([Api] Port), used by the MCP server (CastleStoryPlus.Mcp) so
//   Claude can read the game's state, run the in-game tests and look at the results;
// - the in-game test runner (Testing) with the tests (Tests) that check the mod in a real game: pathfinding, ...
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
		gameObject.AddComponent<TestRunner>();
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
