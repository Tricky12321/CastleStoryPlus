#if UNITY6
using System.Collections.Generic;
using Brix.Game.Network;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Multiplayer;

// An item delivered to a blueprint is held by the blueprint's receptor; on the clients the item's tracker names the
// blueprint and the receptor hides the item. A blueprint is only sent to its own faction's players, so an allied
// player's game had the item but not the blueprint: the tracker found nothing to hold it, put it on the ground and
// left it there, a heap that could not be picked up or cleaned (the host still counted it for the building). Such an
// item is hidden as the receptor would, and shown again when it is tracked by something else or let go.
[Feature(Features.HiddenDeliveries, Features.HiddenDeliveriesInfo)]
[HarmonyPatch(typeof(GenericParentTracker), "UpdateTrackers")]
internal static class HiddenDeliveries
{
	// TrackingPair.type of a resource receptor (IResourceTracker).
	private const int ResourceTracker = 2;

	private static readonly HashSet<GameObject> Hidden = new HashSet<GameObject>();

	private static void Enable()
	{
		GameSession.OnLeave(Hidden.Clear);
	}

	private static void Prefix(GenericParentTracker __instance, TrackingPair ___currentTracker, TrackingPair ___previousTracker)
	{
		if (NetworkServer.active || ___currentTracker.IsSame(___previousTracker))
		{
			return;
		}
		// Ours to show again before the new tracker takes it (a receptor hides it again itself).
		if (Hidden.Remove(__instance.gameObject))
		{
			__instance.gameObject.SetActive(true);
		}
	}

	private static void Postfix(GenericParentTracker __instance, TrackingPair ___currentTracker)
	{
		if (NetworkServer.active || ___currentTracker.type != ResourceTracker || ___currentTracker.IsValid)
		{
			return;
		}
		Hidden.Add(__instance.gameObject);
		__instance.gameObject.SetActive(false);
	}
}
#endif
