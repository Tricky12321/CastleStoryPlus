using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Network;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Pathing;

// Finds the bricktron a route search is run for: one by its object id, else the player's first builder (or any of
// theirs), or for an enemy search the first unit of an AI faction.
internal static class Units
{
	internal static Labor Find(int id, bool enemy)
	{
		Faction local = (User.LocalUser != null) ? User.LocalUser.faction : null;
		Labor fallback = null;
		foreach (Labor labor in Object.FindObjectsOfType<Labor>())
		{
			if (labor == null || labor.navigation == null || !labor.gameObject.activeInHierarchy)
			{
				continue;
			}
			if (id != 0)
			{
				if (labor.gameObject.GetInstanceID() == id)
				{
					return labor;
				}
				continue;
			}
			bool isAi = labor.navigation.faction != null && labor.navigation.faction.isAI;
			if (enemy)
			{
				if (isAi)
				{
					return labor;
				}
				continue;
			}
			if (local == null || !local.IsSame(labor.gameObject))
			{
				continue;
			}
			if (labor.Occupation != null && labor.Occupation.CurrentJob == Occupation.Job.Builder)
			{
				return labor;
			}
			if (fallback == null)
			{
				fallback = labor;
			}
		}
		return fallback;
	}

	internal static XYZ VoxelOf(Labor labor)
	{
		return (labor.presence != null) ? labor.presence.CurrentVoxel : XYZ.FromVector3(labor.transform.position);
	}
}
