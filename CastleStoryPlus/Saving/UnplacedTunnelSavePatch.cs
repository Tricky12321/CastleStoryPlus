using System.Collections.Generic;
using Brix.Engine;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Saving;

// A tunnel task that was never placed (an inactive "Tunnel (0)" under Jeu/Groupes, its volume never filled) made
// TunnelProject.OnSerializing throw a NullReferenceException on its missing units, and with it every save and
// autosave of the game failed until the game was closed. Such a tunnel now saves with no units, so the rest of the
// game is saved.
[Feature]
[HarmonyPatch(typeof(TunnelProject), "OnSerializing")]
internal static class UnplacedTunnelSavePatch
{
	private static readonly AccessTools.FieldRef<TunnelProject, List<XYZ>> UnitsPositions =
		AccessTools.FieldRefAccess<TunnelProject, List<XYZ>>("UnitsPositionsList");

	private static bool _warned;

	private static bool Prefix(TunnelProject __instance)
	{
		if (__instance.tunnelVolume != null && __instance.tunnelVolume.GetUnits() != null)
		{
			return true;
		}
		UnitsPositions(__instance) = new List<XYZ>();
		if (!_warned)
		{
			_warned = true;
			Plugin.Log.LogWarning("UnplacedTunnelSave: tunnel task '" + __instance.name + "' has no units (never placed); saved without them");
		}
		return false;
	}
}
