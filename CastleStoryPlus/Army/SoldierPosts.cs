using System.Collections.Generic;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Network;
using CastleStoryPlus.Core;
using CastleStoryPlus.Workers;
using UnityEngine;

namespace CastleStoryPlus.Army;

// Posts: a saved place for each unit, like a rally point of its own. The player puts the soldiers where they should
// stand (on the walls, at the gate), selects them and picks "Set post" in their action wheel: each one keeps the place
// it stands on as its post. "Back to post" sends the selected units to their posts, and a call to arms sends every
// unit with a post there instead of to its class's rally point (a builder takes its kit first). "Clear post" forgets
// it. The posts belong to the units (saved with them, synced to every player; the host decides, like the call to arms
// roles).
[Feature(Features.SoldierPosts, Features.SoldierPostsInfo)]
internal static class SoldierPosts
{
	internal static bool Enabled => Plugin.IsEnabled(Features.SoldierPosts);

	// The unit's post, if it has one (on every peer: the posts are synced).
	public static bool TryGet(Labor labor, out Vector3 post)
	{
		post = Vector3.zero;
		WorkerStats stats = (Enabled && labor != null) ? WorkerStats.Peek(labor.state) : null;
		if (stats == null || stats.HasPost == 0)
		{
			return false;
		}
		post = new Vector3(stats.PostX / 100f, stats.PostY / 100f, stats.PostZ / 100f);
		return true;
	}

	public static bool Has(Labor labor)
	{
		return TryGet(labor, out _);
	}

	// Server: sets (or clears) the unit's post.
	public static void Set(Labor labor, bool set, Vector3 position)
	{
		if (labor == null || labor.state == null)
		{
			return;
		}
		WorkerStats.Modify(labor.state, (WorkerStats s) =>
		{
			s.HasPost = set ? 1 : 0;
			s.PostX = set ? Mathf.RoundToInt(position.x * 100f) : 0;
			s.PostY = set ? Mathf.RoundToInt(position.y * 100f) : 0;
			s.PostZ = set ? Mathf.RoundToInt(position.z * 100f) : 0;
		});
	}

	// Client: each unit's post becomes the place it stands on.
	public static int SetHere(IEnumerable<Labor> units)
	{
		int count = 0;
		foreach (Labor labor in units)
		{
			if (labor == null)
			{
				continue;
			}
			SoldierOrdersNetwork.SendSetPost(labor.gameObject, true, labor.transform.position);
			count++;
		}
		return count;
	}

	public static int Clear(IEnumerable<Labor> units)
	{
		int count = 0;
		foreach (Labor labor in units)
		{
			if (Has(labor))
			{
				SoldierOrdersNetwork.SendSetPost(labor.gameObject, false, Vector3.zero);
				count++;
			}
		}
		return count;
	}

	// Client: sends the units that have a post there (a move order, as if the player clicked there).
	public static int SendBack(IEnumerable<Labor> units)
	{
		UNetProjectCmd cmd = (User.LocalUser != null) ? User.LocalUser.GetComponent<UNetProjectCmd>() : null;
		if (cmd == null)
		{
			return 0;
		}
		int count = 0;
		foreach (Labor labor in units)
		{
			if (!TryGet(labor, out Vector3 post))
			{
				continue;
			}
			labor.AssignToIdle();
			cmd.CallCmdAddMoveToGoal(labor.gameObject, post, drop: false, queued: false);
			count++;
		}
		return count;
	}
}
