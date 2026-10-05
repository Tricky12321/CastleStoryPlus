using System;

namespace CastleStoryPlus.DevTools.Api;

// One API endpoint. Handlers run on the game's main thread unless OffMainThread is set (for ones that only read
// the API's own data, such as the log, so they also answer while the game is loading).
internal class Route
{
	public string Method;

	public string Path;

	public string Description;

	public string Parameters = string.Empty;

	public bool OffMainThread;

	public int TimeoutMs = 15000;

	public Func<ApiRequest, object> Handler;

	public Route(string method, string path, string description, Func<ApiRequest, object> handler)
	{
		Method = method;
		Path = path;
		Description = description;
		Handler = handler;
	}
}
