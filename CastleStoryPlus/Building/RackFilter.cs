using System;
using System.Collections.Generic;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Network;
using Brix.Game.Semantique;
using Brix.IO.Serialization.Contracts;
using Brix.Network;
using Brix.UI.GameSelector;
using CastleStoryPlus.Core;
using HarmonyLib;
using Newtonsoft.Json.Serialization;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Building;

// A weapon stand can be limited to some soldier classes: the gear of the other classes is no longer brought to it
// (its room for those items is 0, and the hard room check refuses them), what is already on it stays. Picked in a
// small window while the stand is selected (CsRackFilterWindow). Kept per stand as the classes left out (none: every
// class, the game's way), saved with the stand's storage (an extra "rackFilter") and synced to the other players:
// the host sends every stand's filter when one changes and every few seconds (players who join get it then);
// clients send their changes to the host.
[Feature(Features.RackFilter, Features.RackFilterInfo)]
internal static class RackFilter
{
	private const int SetHash = 1129595239;

	private const short SyncMessage = 205;

	private const float BroadcastSeconds = 5f;

	// In the order of ToolRack.AllClasses.
	internal static readonly string[] ClassNames = { "Knight", "Archer", "Arbalist", "Alchemist", "Artificer", "Halberdier" };

	// The classes left out per stand (bit i: ToolRack.AllClasses[i]); a stand not in here takes every class.
	private static readonly Dictionary<ToolRack, int> Excluded = new Dictionary<ToolRack, int>();

	private static bool _changed;

	private static float _nextBroadcast;

	private static NetworkClient _client;

	private sealed class FilterMessage : MessageBase
	{
		public readonly List<uint> Ids = new List<uint>();

		public readonly List<int> Masks = new List<int>();

		public override void Serialize(NetworkWriter writer)
		{
			writer.Write(Ids.Count);
			for (int i = 0; i < Ids.Count; i++)
			{
				writer.WritePackedUInt32(Ids[i]);
				writer.WritePackedUInt32((uint)Masks[i]);
			}
		}

		public override void Deserialize(NetworkReader reader)
		{
			int count = reader.ReadInt32();
			for (int i = 0; i < count; i++)
			{
				Ids.Add(reader.ReadPackedUInt32());
				Masks.Add((int)reader.ReadPackedUInt32());
			}
		}
	}

	private static void Enable()
	{
		GameSession.OnLeave(() =>
		{
			Excluded.Clear();
			_changed = false;
			_client = null;
		});
		NetworkBehaviour.RegisterCommandDelegate(typeof(UNetProjectCmd), SetHash, InvokeSet);
		Plugin.Root.AddComponent<RackFilterSync>();
	}

	internal static int Mask(ToolRack rack)
	{
		return (rack != null && Excluded.TryGetValue(rack, out int mask)) ? mask : 0;
	}

	internal static bool Allows(ToolRack rack, int index)
	{
		return (Mask(rack) & (1 << index)) == 0;
	}

	// The weapon stand selected now, or null.
	internal static ToolRack Selected()
	{
		List<GameObject> selected = new List<GameObject>();
		UIGameSelector.GetSelectedInTrack(ref selected, SelectionTrackID.Storage);
		foreach (GameObject go in selected)
		{
			ToolRack rack = (go != null) ? go.GetComponent<ToolRack>() : null;
			if (rack != null && !rack.Released)
			{
				return rack;
			}
		}
		return null;
	}

	internal static void Set(ToolRack rack, int mask)
	{
		if (rack == null)
		{
			return;
		}
		mask &= (1 << ClassNames.Length) - 1;
		if (Mask(rack) == mask)
		{
			return;
		}
		if (mask == 0)
		{
			Excluded.Remove(rack);
		}
		else
		{
			Excluded[rack] = mask;
		}
		if (rack.recepteur != null)
		{
			rack.recepteur.ResetDescriptions();
		}
		if (NetworkServer.active)
		{
			_changed = true;
		}
	}

	// A class let in or left out, here and, on a client, on the host.
	internal static void Toggle(ToolRack rack, int index)
	{
		if (rack == null || index < 0 || index >= ClassNames.Length)
		{
			return;
		}
		int mask = Mask(rack) ^ (1 << index);
		Set(rack, mask);
		UNetProjectCmd cmd = (User.LocalUser != null) ? User.LocalUser.GetComponent<UNetProjectCmd>() : null;
		if (cmd == null || cmd.isServer)
		{
			return;
		}
		NetworkWriter writer = new NetworkWriter();
		writer.Write((short)0);
		writer.Write((short)5);
		writer.WritePackedUInt32((uint)SetHash);
		writer.Write(cmd.GetComponent<NetworkIdentity>().netId);
		writer.Write(rack.gameObject);
		writer.WritePackedUInt32((uint)mask);
		cmd.SendCommandInternal(writer, 0, "CmdSetRackFilter");
	}

	private static void InvokeSet(NetworkBehaviour obj, NetworkReader reader)
	{
		if (!NetworkServer.active)
		{
			return;
		}
		GameObject go = reader.ReadGameObject();
		int mask = (int)reader.ReadPackedUInt32();
		if (go == null || !Affiliation.IsOwnedBy(go, obj.gameObject))
		{
			return;
		}
		Set(go.GetComponent<ToolRack>(), mask);
	}

	// Whether the stand takes these items: none of a class left out.
	internal static bool Takes(Recepteur recepteur, Description description)
	{
		ToolRack rack = (recepteur != null) ? recepteur.GetComponent<ToolRack>() : null;
		int mask = Mask(rack);
		if (mask == 0 || description == null)
		{
			return true;
		}
		for (int i = 0; i < ToolRack.AllClasses.Count && i < ClassNames.Length; i++)
		{
			if ((mask & (1 << i)) == 0)
			{
				continue;
			}
			foreach (Ressource item in ToolRack.AllClasses[i])
			{
				if (description.Value(item) > 0)
				{
					return false;
				}
			}
		}
		return true;
	}

	// No room on the stand for the items of a class left out.
	internal static void Cap(Recepteur recepteur)
	{
		ToolRack rack = recepteur.GetComponent<ToolRack>();
		int mask = Mask(rack);
		if (mask == 0)
		{
			return;
		}
		Description capacity = recepteur.CurrentCapacity;
		for (int i = 0; i < ToolRack.AllClasses.Count && i < ClassNames.Length; i++)
		{
			if ((mask & (1 << i)) == 0)
			{
				continue;
			}
			foreach (Ressource item in ToolRack.AllClasses[i])
			{
				if (capacity.Value(item) > 0)
				{
					capacity.Set(item, 0);
				}
			}
		}
	}

	// Every frame: the client's handler, and on the host the filters to the other players.
	internal static void Update()
	{
		NetworkClient client = (UNetManager.instance != null) ? UNetManager.instance.LocalNetworkClient : null;
		if (client != null && client != _client)
		{
			_client = client;
			if (client.handlers.ContainsKey(SyncMessage))
			{
				client.UnregisterHandler(SyncMessage);
			}
			client.RegisterHandler(SyncMessage, Received);
		}
		if (!NetworkServer.active || (!_changed && (Excluded.Count == 0 || Time.unscaledTime < _nextBroadcast)))
		{
			return;
		}
		_changed = false;
		_nextBroadcast = Time.unscaledTime + BroadcastSeconds;
		FilterMessage message = new FilterMessage();
		foreach (KeyValuePair<ToolRack, int> pair in Excluded)
		{
			NetworkIdentity identity = (pair.Key != null) ? pair.Key.GetComponent<NetworkIdentity>() : null;
			if (identity != null && !identity.netId.IsEmpty())
			{
				message.Ids.Add(identity.netId.Value);
				message.Masks.Add(pair.Value);
			}
		}
		NetworkServer.SendToAll(SyncMessage, message);
	}

	// A client: the host's filters replace its own.
	private static void Received(NetworkMessage netMsg)
	{
		if (NetworkServer.active)
		{
			return;
		}
		FilterMessage message = netMsg.ReadMessage<FilterMessage>();
		HashSet<ToolRack> seen = new HashSet<ToolRack>();
		for (int i = 0; i < message.Ids.Count; i++)
		{
			GameObject go = ClientScene.FindLocalObject(new NetworkInstanceId(message.Ids[i]));
			ToolRack rack = (go != null) ? go.GetComponent<ToolRack>() : null;
			if (rack != null)
			{
				seen.Add(rack);
				Set(rack, message.Masks[i]);
			}
		}
		foreach (ToolRack rack in new List<ToolRack>(Excluded.Keys))
		{
			if (!seen.Contains(rack))
			{
				Set(rack, 0);
			}
		}
	}
}

internal class RackFilterSync : MonoBehaviour
{
	private void Update()
	{
		try
		{
			RackFilter.Update();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("RackFilter: " + ex.Message);
			enabled = false;
		}
	}
}

// The stand's room for a class left out is 0 (after MixedRacks' slot room), so workers do not bring that gear.
[Feature(Features.RackFilter, Features.RackFilterInfo)]
[HarmonyPatch(typeof(Recepteur), "RegenerateDescriptionsWithExclusions")]
internal static class RackFilterCapacityPatch
{
	[HarmonyPriority(Priority.Low)]
	private static void Postfix(Recepteur __instance)
	{
		if (MixedRacks.IsRack(__instance))
		{
			RackFilter.Cap(__instance);
		}
	}
}

// And the hard room check refuses it.
[Feature(Features.RackFilter, Features.RackFilterInfo)]
[HarmonyPatch]
internal static class RackFilterRoomPatch
{
	[HarmonyPatch(typeof(Recepteur), nameof(Recepteur.HasRoomFor), new Type[] { typeof(IDescriptor) })]
	[HarmonyPostfix]
	private static void DescriptorRoom(Recepteur __instance, IDescriptor d, ref bool __result)
	{
		if (__result && d != null && MixedRacks.IsRack(__instance) && !RackFilter.Takes(__instance, d.Description))
		{
			__result = false;
		}
	}

	[HarmonyPatch(typeof(Recepteur), nameof(Recepteur.HasRoomFor), new Type[] { typeof(GameObject) })]
	[HarmonyPostfix]
	private static void ObjectRoom(Recepteur __instance, GameObject o, ref bool __result)
	{
		if (!__result || o == null || !MixedRacks.IsRack(__instance) || __instance.Carries(o))
		{
			return;
		}
		IDescriptor d = o.GetComponent<IDescriptor>();
		if (d != null && !RackFilter.Takes(__instance, d.Description))
		{
			__result = false;
		}
	}
}

// Saves: an extra "rackFilter" on the stand's storage (the classes left out), only on a filtered stand.
[Feature(Features.RackFilter, Features.RackFilterInfo)]
[HarmonyPatch(typeof(DefaultGameContractResolver), nameof(DefaultGameContractResolver.GenerateDefaultMonoBehaviourContract))]
internal static class RackFilterSave
{
	private const string Property = "rackFilter";

	private class MaskProvider : IValueProvider
	{
		public object GetValue(object target)
		{
			Recepteur recepteur = target as Recepteur;
			return RackFilter.Mask((recepteur != null) ? recepteur.GetComponent<ToolRack>() : null);
		}

		public void SetValue(object target, object value)
		{
			Recepteur recepteur = target as Recepteur;
			RackFilter.Set((recepteur != null) ? recepteur.GetComponent<ToolRack>() : null, Convert.ToInt32(value));
		}
	}

	private static void Postfix(Type objectType, JsonObjectContract __result)
	{
		if (objectType == null || !typeof(Recepteur).IsAssignableFrom(objectType) || __result == null || __result.Properties.Contains(Property))
		{
			return;
		}
		__result.Properties.AddProperty(new JsonProperty
		{
			PropertyName = Property,
			UnderlyingName = Property,
			PropertyType = typeof(int),
			DeclaringType = objectType,
			ValueProvider = new MaskProvider(),
			ShouldSerialize = (object target) =>
			{
				Recepteur recepteur = target as Recepteur;
				return RackFilter.Mask((recepteur != null) ? recepteur.GetComponent<ToolRack>() : null) != 0;
			},
			Readable = true,
			Writable = true
		});
	}
}
