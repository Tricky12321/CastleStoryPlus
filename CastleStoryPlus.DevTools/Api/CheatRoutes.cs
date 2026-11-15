using System;
using System.Collections.Generic;
using CastleStoryPlus.Multiplayer;

namespace CastleStoryPlus.DevTools.Api;

// The API's cheats, refused in a multiplayer game whose host did not allow cheats (CheatGuard, the lobby's "Cheats").
// Single player and a multiplayer game with cheats allowed are not affected; POST /api/net/cheats lets the host allow
// them for a test.
internal static class CheatRoutes
{
	// Cheats whatever their parameters: things out of nothing, buildings finished at once, whole test worlds and test
	// runs (they spawn units and buildings), switching the game's components or cameras off, the x-ray.
	private static readonly HashSet<string> Always = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"/api/cheat/bricktrons",
		"/api/cheat/items",
		"/api/cheat/enemies",
		"/api/blueprints/complete",
		"/api/testworld",
		"/api/studio",
		"/api/benchmark/build",
		"/api/tests/run",
		"/api/treasure/beast",
		"/api/components",
		"/api/experiment",
		"/api/xray"
	};

	// Cheats only with instant=true: placing or taking down at once.
	private static readonly HashSet<string> WhenInstant = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"/api/blueprints/place",
		"/api/demolish"
	};

	internal static bool IsCheat(ApiRequest request)
	{
		if (request.Method != "POST")
		{
			return false;
		}
		if (Always.Contains(request.Path))
		{
			return true;
		}
		return WhenInstant.Contains(request.Path) && request.Bool("instant", false);
	}

	// Throws 403 for a cheat while cheats are off. On the main thread (CheatGuard reads the network state).
	internal static void Check(ApiRequest request)
	{
		if (IsCheat(request) && CheatGuard.Restricted)
		{
			throw new ApiException(403, request.Path + " is a cheat: " + CheatGuard.Explanation + " The host can allow them with POST /api/net/cheats?value=true (in the lobby or the game).");
		}
	}
}
