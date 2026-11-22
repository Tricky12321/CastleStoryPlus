#if ESSENTIALS
using System.Collections.Generic;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Locomotion;
using CastleStoryPlus.Core;
using CastleStoryPlus.Workers;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Saving;

// Essentials edition only: a save made with the Full edition can hold the treasure chests' guardian beasts (Treasure,
// left out of Essentials). They are enemy units the game saves with a mod variant (Beasts: 10 and up); without the
// Treasure code they would come back as plain corruptrons and biftrons standing round the ruins and attack whoever
// comes near (#182). The host takes them away once they are loaded, so no guard spawns in Essentials. The ruins
// themselves are ordinary stone blocks of the world and stay; the chests were never part of the save.
[Feature]
internal static class FullSaveGuards
{
	// Beasts.VariantBase in the Full edition.
	private const int BeastVariantBase = 10;

	private static readonly List<CharacterState> Loaded = new List<CharacterState>();

	private static void Enable()
	{
		WorkerStats.VariantChanged += Changed;
		Plugin.Root.AddComponent<FullSaveGuardsDriver>();
		GameSession.OnLeave(Loaded.Clear);
	}

	private static void Changed(CharacterState state)
	{
		WorkerStats stats = WorkerStats.Peek(state);
		if (stats != null && stats.Variant >= BeastVariantBase && !Loaded.Contains(state))
		{
			Loaded.Add(state);
		}
	}

	// The host, every frame: the loaded guards are killed once their unit is in the world.
	internal static void Update()
	{
		if (Loaded.Count == 0 || !NetworkServer.active)
		{
			return;
		}
		for (int i = Loaded.Count - 1; i >= 0; i--)
		{
			CharacterState state = Loaded[i];
			if (state == null)
			{
				Loaded.RemoveAt(i);
				continue;
			}
			Locomotion4 unit = state.GetComponent<Locomotion4>();
			if (unit == null || !unit.gameObject.activeInHierarchy)
			{
				continue;
			}
			Loaded.RemoveAt(i);
			if (unit.IsCorpse)
			{
				continue;
			}
			Plugin.Log.LogInfo("FullSaveGuards: " + unit.name + " was a treasure guardian of the Full edition; taken away (Essentials has no treasure)");
			unit.Kill(unit.transform.position, Vector3.up);
		}
	}
}

internal sealed class FullSaveGuardsDriver : MonoBehaviour
{
	private void Update()
	{
		try
		{
			FullSaveGuards.Update();
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogWarning("FullSaveGuards: " + ex.Message);
		}
	}
}
#endif
