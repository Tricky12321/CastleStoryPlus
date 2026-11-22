using System;
using System.Collections;
using Brix.Engine;
using Brix.Network;
using Brix.UI.Builder.Menu;
using CastleStoryPlus.Core;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using static CastleStoryPlus.UI.UiKit;

namespace CastleStoryPlus.Multiplayer;

// The Full and the Essentials edition (docs/Editions.md) cannot play together: Essentials has none of Full's buildings,
// blocks, items or messages, so the worlds would differ from the first placed market on. The host tells every player its
// edition and mod version with the cheat setting it sends every few seconds (CheatGuard's message 207: these fields
// come after the setting, so an older client reads the setting and leaves the rest). A client of the other edition
// leaves the lobby or game at once and says why in a window, and tells the host (message 215, sent only to a host that
// sent its edition, so never to an older host that has no handler for it), whose chat then says why the player left.
// A host from before 0.10.0 sends no edition: nothing is checked then (the log says so once).
internal static class EditionCheck
{
	// The client's "I left: other edition" to the host.
	internal const short RefusedMessage = 215;

	// Before the edition in message 207, so the fields can be told from anything else a later version adds there.
	private const string Marker = "CSPEdition";

	private static NetworkClient _checked;

	private static bool _toldOld;

	private static bool _registered;

	// What the host adds to its message 207.
	internal static void Write(NetworkWriter writer)
	{
		writer.Write(Marker);
		writer.Write(Edition.Id);
		writer.Write(Plugin.Version);
	}

	// A client, on the host's message 207 after the cheat setting: the host's edition, or nothing from an older host.
	internal static void Read(NetworkReader reader)
	{
		if (NetworkServer.active)
		{
			return;
		}
		NetworkClient client = (UNetManager.instance != null) ? UNetManager.instance.LocalNetworkClient : null;
		if (client == null || client == _checked)
		{
			return;
		}
		string hostEdition = null;
		string hostVersion = null;
		try
		{
			if (reader.Position < reader.Length && reader.ReadString() == Marker)
			{
				hostEdition = reader.ReadString();
				hostVersion = reader.ReadString();
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("EditionCheck: could not read the host's edition: " + ex.Message);
		}
		if (hostEdition == null)
		{
			if (!_toldOld)
			{
				_toldOld = true;
				Plugin.Log.LogInfo("EditionCheck: the host's mod does not say its edition (Castle Story Plus before 0.10.0); not checked");
			}
			return;
		}
		_checked = client;
		if (hostEdition == Edition.Id)
		{
			Plugin.Log.LogInfo("EditionCheck: the host has the " + hostEdition + " edition (" + hostVersion + "), as this player");
			return;
		}
		string text = "This game's host plays " + NameOf(hostEdition) + " " + hostVersion + ", you have " + Plugin.Name + " " + Plugin.Version + ". The two editions cannot play together: every player needs the same edition.";
		Plugin.Log.LogWarning("EditionCheck: " + text + " Leaving the game.");
		try
		{
			client.Send(RefusedMessage, new RefusedNetMessage { Edition = Edition.Id, Version = Plugin.Version });
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("EditionCheck: could not tell the host: " + ex.Message);
		}
		Plugin.Root.AddComponent<EditionNotice>().Show(text);
	}

	// The host: the handler for a client that left for the other edition (in a lobby or game hosted by this player).
	internal static void Update()
	{
		if (!NetworkServer.active)
		{
			_registered = false;
			return;
		}
		if (!_registered)
		{
			_registered = true;
			NetworkServer.RegisterHandler(RefusedMessage, Refused);
		}
	}

	private static void Refused(NetworkMessage netMsg)
	{
		RefusedNetMessage message = netMsg.ReadMessage<RefusedNetMessage>();
		string text = "A player with " + NameOf(message.Edition) + " " + message.Version + " left: this game is " + Plugin.Name + ", and every player needs the same edition.";
		Plugin.Log.LogWarning("EditionCheck: " + text);
		GameSignals.Invoke(UNetManager.LobbyNewChatMessage, "Castle Story Plus: " + text);
	}

	private static string NameOf(string edition)
	{
		return (edition == "Essentials") ? "Castle Story Plus - Essentials" : "Castle Story Plus (" + edition + ")";
	}

	private sealed class RefusedNetMessage : MessageBase
	{
		public string Edition;

		public string Version;

		public override void Serialize(NetworkWriter writer)
		{
			writer.Write(Edition ?? string.Empty);
			writer.Write(Version ?? string.Empty);
		}

		public override void Deserialize(NetworkReader reader)
		{
			Edition = reader.ReadString();
			Version = reader.ReadString();
		}
	}
}

// Leaves the lobby or game (a moment after the host was told) and shows why, until OK is pressed.
internal sealed class EditionNotice : MonoBehaviour
{
	private GameObject _canvas;

	internal void Show(string text)
	{
		StartCoroutine(Leave(text));
	}

	private IEnumerator Leave(string text)
	{
		yield return new WaitForSecondsRealtime(0.5f);
		try
		{
			if (Architecte.instance != null)
			{
				Architecte.QuitToMainMenu();
			}
			else
			{
				LobbyMenu.Leave();
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("EditionCheck: could not leave: " + ex.Message);
		}
		_canvas = CreateCanvas("CastleStoryPlusEditionNotice", transform, 30000);
		GameObject window = CreateWindow(_canvas.transform, 520f);
		RectTransform rect = window.GetComponent<RectTransform>();
		rect.anchorMin = new Vector2(0.5f, 0.5f);
		rect.anchorMax = new Vector2(0.5f, 0.5f);
		rect.pivot = new Vector2(0.5f, 0.5f);
		rect.anchoredPosition = Vector2.zero;
		CreateText(window.transform, "Different Castle Story Plus edition", 18, Yellow, TextAnchor.MiddleLeft);
		Text body = CreateText(window.transform, text, 14, TextColor, TextAnchor.UpperLeft);
		body.verticalOverflow = VerticalWrapMode.Overflow;
		CreateButton(window.transform, "OK", 0f, Close);
	}

	private void Close()
	{
		if (_canvas != null)
		{
			Destroy(_canvas);
		}
		Destroy(this);
	}
}
