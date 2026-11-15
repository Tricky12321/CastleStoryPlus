using Brix.Engine;
using Brix.Network;
using Brix.UI.Builder.Menu;
using CastleStoryPlus.Core;
using CastleStoryPlus.Multiplayer;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static CastleStoryPlus.UI.UiKit;

namespace CastleStoryPlus.UI;

// The lobby's "Cheats" choice (CheatGuard), in the left column under the player's name, colour and team: the host
// switches it between off (the default) and allowed, the other players see what the host chose.
[Feature(Features.CheatGuard, Features.CheatGuardInfo)]
internal class CheatGuardLobby : MonoBehaviour
{
	// Set by the lobby menu opening and closing (CheatGuardLobbyPatch).
	internal static bool LobbyOpen;

	private GameObject _canvas;

	private Text _value;

	private Text _info;

	private Button _button;

	private static void Enable()
	{
		Plugin.Root.AddComponent<CheatGuardLobby>();
	}

	private void Awake()
	{
		_canvas = CreateCanvas("CheatGuardLobbyCanvas", transform, 905);
		GameObject panel = CreatePanel("CheatsPanel", _canvas.transform);
		panel.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);
		RectTransform rect = panel.GetComponent<RectTransform>();
		rect.anchorMin = rect.anchorMax = Vector2.zero;
		rect.pivot = Vector2.zero;
		rect.anchoredPosition = new Vector2(0f, 200f);
		rect.sizeDelta = new Vector2(364f, 0f);
		VerticalLayoutGroup layout = panel.AddComponent<VerticalLayoutGroup>();
		layout.padding = new RectOffset(14, 14, 10, 12);
		layout.spacing = 6f;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = false;
		panel.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

		Transform header = CreateRow(panel.transform, 28f);
		Text title = CreateText(header, "CHEATS", 22, Yellow, TextAnchor.MiddleLeft);
		title.fontStyle = FontStyle.Bold;
		Flexible(title.gameObject);
		_button = CreateButton(header, string.Empty, 120f, Toggle);
		_value = _button.GetComponentInChildren<Text>();
		_value.fontSize = 15;
		_info = CreateText(panel.transform, string.Empty, 15, TextColor, TextAnchor.UpperLeft);
		_canvas.SetActive(false);
	}

	private void Toggle()
	{
		if (CheatGuard.CanChoose)
		{
			CheatGuard.SetAllowed(!CheatGuard.HostAllows);
			Refresh();
		}
	}

	private void Update()
	{
		bool shown = LobbyOpen && Neo.IsMultiplayer && UNetManager.instance != null && UNetManager.instance.LocalLobbyPlayer != null && SceneManager.GetActiveScene().name == "SceneMenu";
		if (_canvas.activeSelf != shown)
		{
			_canvas.SetActive(shown);
		}
		if (shown)
		{
			Refresh();
		}
	}

	private void Refresh()
	{
		bool allowed = CheatGuard.HostAllows;
		bool host = CheatGuard.CanChoose;
		_value.text = allowed ? "ALLOWED" : "OFF";
		_value.color = allowed ? Yellow : TextColor;
		_button.interactable = host;
		_info.text = (allowed ? "Every player may use the debug menu's cheats, instant build and spawning in this game." : "No debug cheats for anyone in this game: instant build, spawning, resources, energy, waves, fog of war and x-ray are refused.")
			+ (host ? " Click to change (host only)." : " The host decides.");
	}
}

[Feature(Features.CheatGuard, Features.CheatGuardInfo)]
[HarmonyPatch(typeof(LobbyMenu), "OpenSelf")]
internal static class CheatGuardLobbyOpenPatch
{
	private static void Postfix()
	{
		CheatGuardLobby.LobbyOpen = true;
	}
}

[Feature(Features.CheatGuard, Features.CheatGuardInfo)]
[HarmonyPatch(typeof(LobbyMenu), "CloseSelf")]
internal static class CheatGuardLobbyClosePatch
{
	private static void Postfix()
	{
		CheatGuardLobby.LobbyOpen = false;
	}
}
