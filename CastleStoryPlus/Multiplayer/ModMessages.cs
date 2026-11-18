using System;
using CastleStoryPlus.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Multiplayer;

// The mod's network messages are numbered 200 to 211 (the game's own end at 63). A message that reaches a player with
// no handler for its number makes UNet stop reading the packet and throw the rest of it away, and other messages (the
// game's own among them) were in it. So every client gets a handler that ignores the message as soon as it connects
// (before the level loads), whatever features are on; a feature that wants its message registers its own handler
// after it, which replaces this one. The host gets the same for the one message a client sends to it.
[Feature]
internal static class ModMessages
{
	internal const short First = 200;

	internal const short Last = 211;

	// A client's message to the host (DesyncProtection).
	private const short ToHost = 203;

	private static NetworkClient _client;

	private static bool _server;

	private static void Enable()
	{
		Plugin.Root.AddComponent<ModMessagesDriver>();
	}

	internal static void Update()
	{
		NetworkClient client = (Brix.Network.UNetManager.instance != null) ? Brix.Network.UNetManager.instance.LocalNetworkClient : null;
		if (client == null)
		{
			_client = null;
		}
		else if (client != _client)
		{
			_client = client;
			for (short id = First; id <= Last; id++)
			{
				if (!client.handlers.ContainsKey(id))
				{
					client.RegisterHandler(id, Ignore);
				}
			}
		}
		if (!NetworkServer.active)
		{
			_server = false;
		}
		else if (!_server)
		{
			_server = true;
			if (!NetworkServer.handlers.ContainsKey(ToHost))
			{
				NetworkServer.RegisterHandler(ToHost, Ignore);
			}
		}
	}

	private static void Ignore(NetworkMessage message)
	{
	}
}

internal sealed class ModMessagesDriver : MonoBehaviour
{
	private void Update()
	{
		try
		{
			ModMessages.Update();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("ModMessages: " + ex.Message);
		}
	}
}
