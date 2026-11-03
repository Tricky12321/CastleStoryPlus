using System;
using System.Collections.Generic;
using Brix.Game;
using Brix.Game.Components;
using Brix.Game.Network;
using CastleStoryPlus.DevTools.Api;
using HarmonyLib;
using UnityEngine;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// The fog of war, object by object: whether the local player's faction sees it and each reason
// LocalFactionVision.IsVisibleForFaction can give (always visible, owned, allied, a faction seeing it, the object
// carrying it seen), whether its renderers are on as the host's last visibility says, and what tracks it. To find
// what shows through the fog (the scarves of enemy bricktrons hidden by it showed on the host).
internal static class VisionEndpoints
{
	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/vision", "Fog of war per object: visible for the local faction and why (always visible, owned, allied, factions seeing it, the carrier seen), the host's last visibility, renderers on, the tracking parent", Get)
		{
			Parameters = "path (only objects whose path holds this text, e.g. Scarf), max (default 50)"
		});
	}

	private static object Get(ApiRequest request)
	{
		string path = request.String("path", null);
		int max = request.Int("max", 50);
		User user = User.LocalUser;
		Faction local = user != null ? user.faction : null;
		if (local == null)
		{
			throw new ApiException(409, "Not in a game");
		}
		List<object> found = new List<object>();
		int total = 0;
		foreach (LocalFactionVision vision in UnityEngine.Object.FindObjectsOfType<LocalFactionVision>())
		{
			string where = ComponentEndpoints.Path(vision.transform);
			if (path != null && where.IndexOf(path, StringComparison.OrdinalIgnoreCase) < 0)
			{
				continue;
			}
			total++;
			if (found.Count >= max)
			{
				continue;
			}
			List<int> seeing = new List<int>();
			foreach (int id in vision.FactionsSeeingMe)
			{
				seeing.Add(id);
			}
			int renderersOn = 0;
			int renderers = 0;
			foreach (Renderer renderer in vision.GetComponentsInChildren<Renderer>(true))
			{
				renderers++;
				if (renderer.enabled)
				{
					renderersOn++;
				}
			}
			GenericParentTracker tracker = vision.GetComponent<GenericParentTracker>();
			GameObject parent = tracker != null && tracker.IsTracked() ? tracker.TrackingParent() : null;
			LocalFactionVision parentVision = parent != null ? parent.GetComponent<LocalFactionVision>() : null;
			Faction own = Faction.GetFaction(vision.gameObject);
			found.Add(Obj(
				"path", where,
				"faction", own != null ? own.name : null,
				"visible", vision.IsVisibleForFaction(local),
				"hostVisibility", Traverse.Create(vision).Field("_lastHostVisibility").GetValue<bool>(),
				"renderers", renderersOn + "/" + renderers,
				"alwaysVisible", vision.AlwaysVisible,
				"ignoreParentVision", vision.IgnoreParentVision,
				"owned", Affiliation.IsOwnedBy(vision.gameObject, local.gameObject),
				"allied", Affiliation.IsAllied(vision.gameObject, local.gameObject),
				"factionsSeeing", seeing,
				"localFactionId", local.ID,
				"trackedBy", parent != null ? ComponentEndpoints.Path(parent.transform) : null,
				"trackerType", tracker != null ? tracker.GetType().Name : null,
				"carrierVisible", parentVision != null ? (object)parentVision.IsVisibleForFaction(local) : null,
				"responsible", LocalFactionVision.IsResponsibleForVision(vision)));
		}
		return Obj("count", total, "objects", found);
	}
}
