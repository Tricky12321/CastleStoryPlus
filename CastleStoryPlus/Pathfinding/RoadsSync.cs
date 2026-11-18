using Brix.Engine;
using Brix.Network;
using CastleStoryPlus.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Pathfinding;

// The road bonus is the host's: units walk on the host and are shown on the clients, and a route search of a client
// must weigh a road as the host does. The host sends [Roads] SpeedBonus to the other players (message BonusMessage)
// every few seconds, and a client takes the host's value in place of its own setting.
[Feature(Features.Roads, Features.RoadsInfo)]
internal sealed class RoadsSync : MonoBehaviour
{
	// The game's own messages end at 63; Castle Story Plus uses 200-210.
	private const short BonusMessage = 211;

	private const float ResendSeconds = 10f;

	// The host's bonus (percent) on a client; null on the host and in single player.
	internal static int? HostBonus;

	private static int _sent = int.MinValue;

	private float _next;

	private sealed class BonusNetMessage : MessageBase
	{
		public int Percent;

		public override void Serialize(NetworkWriter writer)
		{
			writer.Write(Percent);
		}

		public override void Deserialize(NetworkReader reader)
		{
			Percent = reader.ReadInt32();
		}
	}

	private static void Enable()
	{
		GameSession.OnLevelReady(LevelReady);
		GameSession.OnLeave(Clear);
		Plugin.Root.AddComponent<RoadsSync>();
	}

	private static void Clear()
	{
		HostBonus = null;
		_sent = int.MinValue;
	}

	private static void LevelReady()
	{
		Clear();
		NetworkClient client = (UNetManager.instance != null) ? UNetManager.instance.LocalNetworkClient : null;
		if (client == null)
		{
			return;
		}
		if (client.handlers.ContainsKey(BonusMessage))
		{
			client.UnregisterHandler(BonusMessage);
		}
		client.RegisterHandler(BonusMessage, Received);
	}

	private void Update()
	{
		if (!NetworkServer.active || !Neo.multiplayer || Roads.SpeedBonus == null)
		{
			return;
		}
		int bonus = Roads.SpeedBonus.Value;
		if (bonus == _sent && Time.unscaledTime < _next)
		{
			return;
		}
		_sent = bonus;
		_next = Time.unscaledTime + ResendSeconds;
		NetworkServer.SendToAll(BonusMessage, new BonusNetMessage { Percent = bonus });
	}

	// A client: the host's bonus.
	private static void Received(NetworkMessage netMsg)
	{
		BonusNetMessage message = netMsg.ReadMessage<BonusNetMessage>();
		if (!NetworkServer.active)
		{
			HostBonus = Mathf.Clamp(message.Percent, 0, 200);
		}
	}
}
