using System;
using System.Collections.Generic;
using CastleStoryPlus.DevTools.Api;
using CastleStoryPlus.Weather;
using UnityEngine.Networking;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// The weather (Rain): what falls now, and starting or stopping it for a test.
internal static class WeatherEndpoints
{
	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/weather", "The weather: active, what falls now (None, Rain or Snow), game hours left, what falls in this world by itself, the boosts, and what is shown (strength, particles, veil, sound, banner)", Get));
		routes.Add(new Route("POST", "/api/weather", "Starts or stops the weather (host)", Set)
		{
			Parameters = "state (rain, snow or none; required), hours (game hours it lasts, default 3)"
		});
	}

	private static object Get(ApiRequest request)
	{
		RainVisuals visuals = RainVisuals.Instance;
		return Obj("active", Rain.Active, "state", Rain.Current.ToString(), "hoursLeft", Rain.HoursLeft, "fall", Rain.Fall.ToString(), "grassBoost", Rain.GrassBoost, "plantBoost", Rain.PlantBoost, "shown", (visuals != null) ? visuals.Describe() : "(feature off)");
	}

	private static object Set(ApiRequest request)
	{
		if (!NetworkServer.active)
		{
			throw new ApiException(409, "Only on the host");
		}
		if (!Rain.Active)
		{
			throw new ApiException(409, "No game is running (or the Rain feature is off)");
		}
		WeatherKind kind = (WeatherKind)Enum.Parse(typeof(WeatherKind), request.Required("state"), true);
		Rain.Begin(kind, request.Float("hours", 3f));
		return Get(request);
	}
}
