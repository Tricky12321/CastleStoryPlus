using System.Collections.Generic;
using Brix.Input;
using CastleStoryPlus.DevTools.Api;
using UnityEngine;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// Pointing the game's camera at a place, to look at it in a screenshot.
internal static class CameraEndpoints
{
	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("POST", "/api/camera", "Points the camera at a place, as the player's camera looks: from a distance, a pitch down and a turn round the vertical. Answers where it stands. hold=true keeps it there every frame (the game scrolls the view while the mouse is at the screen's edge) until the next call without hold or the game is left", Look)
		{
			Parameters = "at (x,y,z: what to look at, required), distance (default 14), pitch (degrees down, default 50), yaw (degrees round the vertical, default 45), hold (true: put back every frame)"
		});
	}

	private static object Look(ApiRequest request)
	{
		CameraScript camera = CameraScript.Instance;
		if (camera == null)
		{
			throw new ApiException(409, "No game camera (start a game first)");
		}
		Vector3 at = request.Vector("at");
		float distance = Mathf.Clamp(request.Float("distance", 14f), 2f, 300f);
		Quaternion rotation = Quaternion.Euler(Mathf.Clamp(request.Float("pitch", 50f), 5f, 89f), request.Float("yaw", 45f), 0f);
		Vector3 position = at - rotation * Vector3.forward * distance;
		camera.Positionner(position, rotation, distance);
		CameraHold.Set(request.Bool("hold", false), position, rotation, distance);
		return Obj("position", position, "target", at, "distance", distance, "held", CameraHold.Holding);
	}
}

// Puts the camera back where the last /api/camera?hold=true left it, after the game's camera script has moved it
// (at the screen's edge the game scrolls the view; in a benchmark the mouse may well sit there). Let go with a camera
// call without hold, or when the game's camera is gone (the game left).
internal sealed class CameraHold : MonoBehaviour
{
	private static bool _holding;

	private static Vector3 _position;

	private static Quaternion _rotation;

	private static float _distance;

	internal static bool Holding => _holding;

	internal static void Set(bool hold, Vector3 position, Quaternion rotation, float distance)
	{
		_holding = hold;
		_position = position;
		_rotation = rotation;
		_distance = distance;
	}

	private void LateUpdate()
	{
		if (!_holding)
		{
			return;
		}
		CameraScript camera = CameraScript.Instance;
		if (camera == null)
		{
			_holding = false;
			return;
		}
		camera.Positionner(_position, _rotation, _distance);
	}
}
