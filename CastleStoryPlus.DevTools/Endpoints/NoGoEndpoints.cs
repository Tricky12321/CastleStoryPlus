using System.Collections.Generic;
using Brix.Game;
using CastleStoryPlus.DevTools.Api;
using CastleStoryPlus.Pathfinding;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// The no-go zones (NoGoZones): every faction's rectangles, and drawing one as the local player would (on a client it
// goes to the host, as the tool's drawing does).
internal static class NoGoEndpoints
{
	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/nogo", "The no-go zones: every faction's rectangles (minX, minZ, maxX, maxZ), whether the faction is the local player's, the strength routes use and how many route searches paid for zones", Get));
		routes.Add(new Route("POST", "/api/nogo", "Draws, erases or clears a no-go zone of the local player's faction, as the tool does (on a client it is sent to the host)", Set)
		{
			Parameters = "op (add, erase or clear; default add), x0, z0, x1, z1 (the rectangle's corners; not for clear)"
		});
	}

	private static object Get(ApiRequest request)
	{
		Faction local = (User.LocalUser != null) ? User.LocalUser.faction : null;
		List<object> zones = new List<object>();
		foreach (KeyValuePair<Faction, List<NoGoRect>> entry in NoGoZones.All)
		{
			List<object> rects = new List<object>();
			foreach (NoGoRect rect in entry.Value)
			{
				rects.Add(new int[4] { rect.MinX, rect.MinZ, rect.MaxX, rect.MaxZ });
			}
			zones.Add(Obj("faction", (entry.Key != null) ? entry.Key.name : "?", "local", entry.Key != null && entry.Key == local, "rects", rects));
		}
		object searches = 0;
#if UNITY6
		searches = NoGoZonesSearchPatch.Searches;
#endif
		return Obj("active", NoGoZones.Active, "strength", NoGoZones.CurrentStrength, "localFaction", (local != null) ? local.name : null, "zones", zones, "searches", searches, "lastRefusal", NoGoZones.LastRefusal);
	}

	private static object Set(ApiRequest request)
	{
		if (!NoGoZones.Active)
		{
			throw new ApiException(409, "The NoGoZones feature is off");
		}
		if (User.LocalUser == null || User.LocalUser.faction == null)
		{
			throw new ApiException(409, "No game is running");
		}
		string op = request.String("op", "add").ToLowerInvariant();
		byte code;
		switch (op)
		{
			case "add":
				code = NoGoZones.OpAdd;
				break;
			case "erase":
				code = NoGoZones.OpErase;
				break;
			case "clear":
				code = NoGoZones.OpClear;
				break;
			default:
				throw new ApiException(400, "op must be add, erase or clear");
		}
		NoGoRect rect = (code == NoGoZones.OpClear) ? default : NoGoRules.Drawn(int.Parse(request.Required("x0")), int.Parse(request.Required("z0")), int.Parse(request.Required("x1")), int.Parse(request.Required("z1")));
		NoGoZones.Request(code, rect);
		return Get(request);
	}
}
