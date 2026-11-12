using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Brix.Game;
using Brix.Game.Components;
using Brix.Game.Components.Validators.Graphs;
using Brix.Game.Network;
using Brix.IO.Serialization.Contracts;
using CastleStoryPlus.Core;
using HarmonyLib;
using Newtonsoft.Json.Serialization;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Economy;

// A workshop's fetch range goes along with it as a blueprint: copying an area (CopyPaste), the game's duplicate and
// move of blueprints and the blueprint library keep each workshop's range, and the workshop built from the blueprint
// starts with it.
//   - A placed workshop blueprint holds the range (synced to clients, saved with the blueprint); the finished workshop
//     takes it (Blueprint.MorphToObject).
//   - The game's compound blueprint is a graph of nodes, each made from a blueprint: a node holds the range of the
//     blueprint it was made from, and the blueprint library saves it with the node (an extra "fetchRange").
//   - The compound's own (local) blueprints follow the cursor; placing them asks the host for one real blueprint each
//     (UNetBlueprint.CmdSpawnPlacedBlueprint, which carries no more than kind, place and turn): just before, the range
//     for that place is sent on the same channel, so the host has it when the blueprint is made.
// Only a range set on the workshop goes along; one on the default stays on the default.
[Feature(Features.WorkshopFetchRange, Features.WorkshopFetchRangeInfo)]
internal static class WorkshopFetchRangeBlueprints
{
	private const int PlaceHash = 1129595238;

	private const float PendingSeconds = 30f;

	private sealed class Box
	{
		public int Raw;
	}

	private sealed class Pending
	{
		public GameObject User;

		public Vector3 Position;

		public int Raw;

		public float Time;
	}

	// Placed (networked) blueprints with a range.
	private static readonly Dictionary<Blueprint, int> Ranges = new Dictionary<Blueprint, int>();

	// The compound's own blueprints (pooled and reused: every one is set or cleared when a compound is made).
	private static readonly Dictionary<GameObject, int> Previews = new Dictionary<GameObject, int>();

	private static readonly ConditionalWeakTable<ValidationNode, Box> Nodes = new ConditionalWeakTable<ValidationNode, Box>();

	// The places about to be asked for (this player), and the host's ranges waiting for their blueprint.
	private static readonly List<Pending> Placing = new List<Pending>();

	private static readonly List<Pending> Waiting = new List<Pending>();

	private static void Enable()
	{
		GameSession.OnLeave(() =>
		{
			Ranges.Clear();
			Previews.Clear();
			Placing.Clear();
			Waiting.Clear();
		});
		NetworkBehaviour.RegisterCommandDelegate(typeof(UNetBlueprint), PlaceHash, InvokePlace);
	}

	// -1: none.
	internal static int Raw(Blueprint blueprint)
	{
		return (blueprint != null && Ranges.TryGetValue(blueprint, out int raw)) ? raw : -1;
	}

	internal static void Set(Blueprint blueprint, int raw)
	{
		if (blueprint == null)
		{
			return;
		}
		bool had = Ranges.TryGetValue(blueprint, out int current);
		if (raw < 0)
		{
			if (!had)
			{
				return;
			}
			Ranges.Remove(blueprint);
		}
		else
		{
			if (had && current == raw)
			{
				return;
			}
			Ranges[blueprint] = raw;
		}
		if (NetworkServer.active)
		{
			blueprint.SetDirtyBit(WorkshopFetchRange.DirtyBit);
		}
	}

	internal static void Forget(Blueprint blueprint)
	{
		if (blueprint != null)
		{
			Ranges.Remove(blueprint);
		}
	}

	// The range a thing copied as a blueprint carries: a workshop's own range, or a workshop blueprint's.
	internal static int RawOf(GameObject go)
	{
		if (go == null)
		{
			return -1;
		}
		if (Previews.TryGetValue(go, out int preview))
		{
			return preview;
		}
		Blueprint blueprint = go.GetComponent<Blueprint>();
		if (blueprint != null)
		{
			return Raw(blueprint);
		}
		return WorkshopFetchRange.Raw(go.GetComponent<CraftingState>());
	}

	internal static int NodeRaw(ValidationNode node)
	{
		return (node != null && Nodes.TryGetValue(node, out Box box)) ? box.Raw : -1;
	}

	internal static void SetNode(ValidationNode node, int raw)
	{
		if (node == null)
		{
			return;
		}
		Nodes.Remove(node);
		if (raw >= 0)
		{
			Nodes.Add(node, new Box { Raw = raw });
		}
	}

	// The compound's blueprints were made from its nodes.
	internal static void Spawned(BlueprintGraph graph)
	{
		foreach (ValidationNode node in graph.Nodes)
		{
			if (node == null || node.Target == null)
			{
				continue;
			}
			int raw = NodeRaw(node);
			if (raw >= 0)
			{
				Previews[node.Target] = raw;
			}
			else
			{
				Previews.Remove(node.Target);
			}
		}
	}

	// The compound is placed: the places of its blueprints with a range.
	internal static void Placed(BlueprintCompoundComponent compound)
	{
		Placing.Clear();
		if (compound == null || compound.Graph == null)
		{
			return;
		}
		foreach (ValidationNode node in compound.Graph.Nodes)
		{
			if (node == null || node.Target == null || !Previews.TryGetValue(node.Target, out int raw))
			{
				continue;
			}
			Placing.Add(new Pending { Position = node.Target.transform.position, Raw = raw, Time = Time.realtimeSinceStartup });
		}
	}

	// About to ask the host for a blueprint at this place: its range first.
	internal static void Asking(UNetBlueprint sender, Vector3 position)
	{
		Pending found = Take(Placing, null, position);
		if (found == null || sender == null)
		{
			return;
		}
		if (sender.isServer)
		{
			Wait(sender.gameObject, position, found.Raw);
			return;
		}
		NetworkWriter writer = new NetworkWriter();
		writer.Write((short)0);
		writer.Write((short)5);
		writer.WritePackedUInt32((uint)PlaceHash);
		writer.Write(sender.GetComponent<NetworkIdentity>().netId);
		writer.Write(position);
		writer.WritePackedUInt32((uint)found.Raw);
		sender.SendCommandInternal(writer, 0, "CmdWorkshopFetchRangePlace");
	}

	private static void InvokePlace(NetworkBehaviour obj, NetworkReader reader)
	{
		if (!NetworkServer.active)
		{
			return;
		}
		Vector3 position = reader.ReadVector3();
		int raw = WorkshopFetchRange.FromRaw((int)reader.ReadPackedUInt32());
		if (raw >= 0)
		{
			Wait(obj.gameObject, position, raw);
		}
	}

	private static void Wait(GameObject user, Vector3 position, int raw)
	{
		float now = Time.realtimeSinceStartup;
		Waiting.RemoveAll((Pending pending) => now - pending.Time > PendingSeconds);
		Waiting.Add(new Pending { User = user, Position = position, Raw = raw, Time = now });
	}

	// The host made a blueprint asked for by this player at this place.
	internal static void Made(UNetBlueprint sender, Vector3 position, Blueprint blueprint)
	{
		if (blueprint == null || sender == null)
		{
			return;
		}
		Pending found = Take(Waiting, sender.gameObject, position);
		Set(blueprint, (found != null) ? found.Raw : -1);
	}

	private static Pending Take(List<Pending> list, GameObject user, Vector3 position)
	{
		for (int i = 0; i < list.Count; i++)
		{
			Pending pending = list[i];
			if ((user == null || pending.User == user) && (pending.Position - position).sqrMagnitude < 0.0001f)
			{
				list.RemoveAt(i);
				return pending;
			}
		}
		return null;
	}

	// A workshop blueprint was finished into this workshop (host).
	internal static void Finished(Blueprint blueprint, GameObject result)
	{
		int raw = Raw(blueprint);
		if (raw >= 0 && result != null)
		{
			WorkshopFetchRange.Set(result.GetComponent<CraftingState>(), raw);
		}
	}
}

// A node made from a blueprint (the game's duplicate and move, the library's save, a compound rebuilt from its own
// blueprints) takes that blueprint's range.
[Feature(Features.WorkshopFetchRange, Features.WorkshopFetchRangeInfo)]
[HarmonyPatch(typeof(BlueprintGraph), nameof(BlueprintGraph.AddNode))]
internal static class WorkshopFetchRangeNodePatch
{
	private static void Postfix(ValidationNode node)
	{
		if (node != null && node.Target != null)
		{
			WorkshopFetchRangeBlueprints.SetNode(node, WorkshopFetchRangeBlueprints.RawOf(node.Target));
		}
	}
}

[Feature(Features.WorkshopFetchRange, Features.WorkshopFetchRangeInfo)]
[HarmonyPatch(typeof(BlueprintGraph), nameof(BlueprintGraph.SpawnBlueprintsUnder))]
internal static class WorkshopFetchRangeSpawnPatch
{
	private static void Postfix(BlueprintGraph __instance)
	{
		WorkshopFetchRangeBlueprints.Spawned(__instance);
	}
}

[Feature(Features.WorkshopFetchRange, Features.WorkshopFetchRangeInfo)]
[HarmonyPatch(typeof(BlueprintCompoundComponent), nameof(BlueprintCompoundComponent.StartPlaceBlueprints))]
internal static class WorkshopFetchRangePlacePatch
{
	private static void Prefix(BlueprintCompoundComponent __instance)
	{
		WorkshopFetchRangeBlueprints.Placed(__instance);
	}
}

[Feature(Features.WorkshopFetchRange, Features.WorkshopFetchRangeInfo)]
[HarmonyPatch(typeof(UNetBlueprint), nameof(UNetBlueprint.CallCmdSpawnPlacedBlueprint))]
internal static class WorkshopFetchRangeAskPatch
{
	private static void Prefix(UNetBlueprint __instance, Vector3 position)
	{
		WorkshopFetchRangeBlueprints.Asking(__instance, position);
	}
}

[Feature(Features.WorkshopFetchRange, Features.WorkshopFetchRangeInfo)]
[HarmonyPatch(typeof(UNetBlueprint), "_PrepareSpawnBlueprint")]
internal static class WorkshopFetchRangeMadePatch
{
	private static void Postfix(UNetBlueprint __instance, Vector3 position, Blueprint __result)
	{
		WorkshopFetchRangeBlueprints.Made(__instance, position, __result);
	}
}

[Feature(Features.WorkshopFetchRange, Features.WorkshopFetchRangeInfo)]
[HarmonyPatch(typeof(Blueprint), nameof(Blueprint.MorphToObject))]
internal static class WorkshopFetchRangeFinishPatch
{
	private static void Postfix(Blueprint __instance, GameObject __result)
	{
		if (NetworkServer.active)
		{
			WorkshopFetchRangeBlueprints.Finished(__instance, __result);
		}
	}
}

[Feature(Features.WorkshopFetchRange, Features.WorkshopFetchRangeInfo)]
[HarmonyPatch(typeof(Blueprint), nameof(Blueprint.ResetComponent))]
internal static class WorkshopFetchRangeReleasePatch
{
	private static void Postfix(Blueprint __instance)
	{
		WorkshopFetchRangeBlueprints.Forget(__instance);
	}
}

// Network: the blueprint's range after the game's data, in the initial state and every update.
[Feature(Features.WorkshopFetchRange, Features.WorkshopFetchRangeInfo)]
[HarmonyPatch(typeof(Blueprint), nameof(Blueprint.OnSerialize))]
internal static class WorkshopFetchRangeBlueprintWritePatch
{
	[HarmonyPriority(Priority.Low)]
	private static void Postfix(Blueprint __instance, NetworkWriter writer, ref bool __result)
	{
		writer.WritePackedUInt32((uint)(WorkshopFetchRangeBlueprints.Raw(__instance) + 1));
		__result = true;
	}
}

[Feature(Features.WorkshopFetchRange, Features.WorkshopFetchRangeInfo)]
[HarmonyPatch(typeof(Blueprint), nameof(Blueprint.OnDeserialize))]
internal static class WorkshopFetchRangeBlueprintReadPatch
{
	[HarmonyPriority(Priority.Low)]
	private static void Postfix(Blueprint __instance, NetworkReader reader)
	{
		WorkshopFetchRangeBlueprints.Set(__instance, WorkshopFetchRange.FromRaw((int)reader.ReadPackedUInt32() - 1));
	}
}

// The blueprint library: a node's range is saved with it (an extra "fetchRange", only on nodes that have one; a game
// without the mod skips it).
[Feature(Features.WorkshopFetchRange, Features.WorkshopFetchRangeInfo)]
[HarmonyPatch(typeof(DefaultGameContractResolver), "CreateContract")]
internal static class WorkshopFetchRangeNodeSave
{
	private const string Property = "fetchRange";

	private class RangeProvider : IValueProvider
	{
		public object GetValue(object target)
		{
			return WorkshopFetchRangeBlueprints.NodeRaw(target as ValidationNode);
		}

		public void SetValue(object target, object value)
		{
			WorkshopFetchRangeBlueprints.SetNode(target as ValidationNode, WorkshopFetchRange.FromRaw(Convert.ToInt32(value)));
		}
	}

	private static void Postfix(Type objectType, JsonContract __result)
	{
		if (objectType == null || !typeof(ValidationNode).IsAssignableFrom(objectType) || !(__result is JsonObjectContract contract) || contract.Properties.Contains(Property))
		{
			return;
		}
		contract.Properties.AddProperty(new JsonProperty
		{
			PropertyName = Property,
			UnderlyingName = Property,
			PropertyType = typeof(int),
			DeclaringType = objectType,
			ValueProvider = new RangeProvider(),
			ShouldSerialize = (object target) => WorkshopFetchRangeBlueprints.NodeRaw(target as ValidationNode) >= 0,
			Readable = true,
			Writable = true
		});
	}
}
