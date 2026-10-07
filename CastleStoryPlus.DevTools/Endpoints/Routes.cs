using System.Collections.Generic;
using CastleStoryPlus.DevTools.Api;

namespace CastleStoryPlus.DevTools.Endpoints;

// Every endpoint of the API (GET /api lists them with their parameters).
internal static class Routes
{
	internal static List<Route> Build()
	{
		List<Route> routes = new List<Route>();
		GameEndpoints.Add(routes);
		StorageEndpoints.Add(routes);
		SelectionEndpoints.Add(routes);
		DiagnosticsEndpoints.Add(routes);
		PathEndpoints.Add(routes);
		PathStatsEndpoints.Add(routes);
		FrameEndpoints.Add(routes);
		HeapEndpoints.Add(routes);
		TestEndpoints.Add(routes);
		return routes;
	}
}
