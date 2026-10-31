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
		BlueprintEndpoints.Add(routes);
		BuildSearchEndpoints.Add(routes);
		PlaceEndpoints.Add(routes);
		WorldEndpoints.Add(routes);
		ProjectEndpoints.Add(routes);
		CheatEndpoints.Add(routes);
		WorkshopEndpoints.Add(routes);
		ReachEndpoints.Add(routes);
		DemolishEndpoints.Add(routes);
		SelectionEndpoints.Add(routes);
		DiagnosticsEndpoints.Add(routes);
		PathEndpoints.Add(routes);
		PathStatsEndpoints.Add(routes);
#if UNITY6_PLAYER
		NavMeshEndpoints.Add(routes);
#endif
		FrameEndpoints.Add(routes);
		FramePhaseEndpoints.Add(routes);
		BenchmarkEndpoints.Add(routes);
#if UNITY6
		PlayerLoopEndpoints.Add(routes);
		ProfilerMarkerEndpoints.Add(routes);
#endif
		HeapEndpoints.Add(routes);
#if !UNITY6_PLAYER
		// The allocation profiler is CastleStoryPlus.Bootstrap's, which the port's player does not have.
		AllocEndpoints.Add(routes);
#endif
		SpeedEndpoints.Add(routes);
		QuitEndpoints.Add(routes);
		RenderEndpoints.Add(routes);
		DialogEndpoints.Add(routes);
		MenuEndpoints.Add(routes);
		TestEndpoints.Add(routes);
		TestWorldEndpoints.Add(routes);
		CameraEndpoints.Add(routes);
		RandomWorldEndpoints.Add(routes);
		TreasureEndpoints.Add(routes);
		SpawnEndpoints.Add(routes);
		VolumeDumpEndpoints.Add(routes);
		TemplateEndpoints.Add(routes);
		AnimatorEndpoints.Add(routes);
		UiEndpoints.Add(routes);
		ComponentEndpoints.Add(routes);
		return routes;
	}
}
