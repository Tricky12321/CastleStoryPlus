using Brix.Engine;
using Brix.Network;
using Brix.UI.Builder.Menu;
using CastleStoryPlus.Core;
using CastleStoryPlus.WorkerAI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static CastleStoryPlus.UI.UiKit;

namespace CastleStoryPlus.UI;

// The lobby's "Bricktrons per task" choice (TaskCrewSize), in the left column above "Cheats": the host steps it with
// - and + (1 to 31, the game's 8 to start with: the host's [Workers] BricktronsPerTask), the other players see what
// the host chose. Single player has the same value in the Castle Story Plus settings.
[Feature(Features.TaskCrewSize, Features.TaskCrewSizeInfo)]
internal class TaskCrewSizeLobby : MonoBehaviour
{
	// Set by the lobby menu opening and closing (TaskCrewSizeLobbyPatch).
	internal static bool LobbyOpen;

	private const float Bottom = 200f;

	private const float Gap = 8f;

	private GameObject _canvas;

	private RectTransform _panel;

	private Text _value;

	private Text _info;

	private Button _less;

	private Button _more;

	private static void Enable()
	{
		Plugin.Root.AddComponent<TaskCrewSizeLobby>();
	}

	private void Awake()
	{
		_canvas = CreateCanvas("TaskCrewSizeLobbyCanvas", transform, 905);
		GameObject panel = CreatePanel("CrewSizePanel", _canvas.transform);
		panel.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);
		_panel = panel.GetComponent<RectTransform>();
		_panel.anchorMin = _panel.anchorMax = Vector2.zero;
		_panel.pivot = Vector2.zero;
		_panel.anchoredPosition = new Vector2(0f, Bottom);
		_panel.sizeDelta = new Vector2(364f, 0f);
		VerticalLayoutGroup layout = panel.AddComponent<VerticalLayoutGroup>();
		layout.padding = new RectOffset(14, 14, 10, 12);
		layout.spacing = 6f;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = false;
		panel.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

		Transform header = CreateRow(panel.transform, 28f);
		// Smaller than "CHEATS": the name fits on one line next to the buttons.
		Text title = CreateText(header, "BRICKTRONS PER TASK", 17, Yellow, TextAnchor.MiddleLeft);
		title.fontStyle = FontStyle.Bold;
		Flexible(title.gameObject);
		_less = CreateButton(header, "-", 28f, () => Step(-1));
		_value = CreateText(header, string.Empty, 18, Yellow, TextAnchor.MiddleCenter);
		Fixed(_value.gameObject, 36f, 24f);
		_more = CreateButton(header, "+", 28f, () => Step(1));
		_info = CreateText(panel.transform, string.Empty, 15, TextColor, TextAnchor.UpperLeft);
		_canvas.SetActive(false);
	}

	private void Step(int direction)
	{
		if (TaskCrewSize.CanChoose)
		{
			TaskCrewSize.Set(TaskCrewSize.Value + direction);
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
			// Above the "Cheats" choice while that shows.
			RectTransform cheats = CheatGuardLobby.ShownPanel;
			float y = (cheats != null) ? cheats.anchoredPosition.y + cheats.rect.height + Gap : Bottom;
			if (!Mathf.Approximately(_panel.anchoredPosition.y, y))
			{
				_panel.anchoredPosition = new Vector2(0f, y);
			}
			Refresh();
		}
	}

	private void Refresh()
	{
		int value = TaskCrewSize.Value;
		bool host = TaskCrewSize.CanChoose;
		string text = value.ToString();
		if (_value.text != text)
		{
			_value.text = text;
		}
		_less.interactable = host && value > 1;
		_more.interactable = host && value < TaskCrewSize.Most;
		_info.text = "A task's crew holds up to " + value + " bricktrons (the game: " + TaskCrewSize.GameValue + ")." + (host ? " Host only." : " The host decides.");
	}
}

[Feature(Features.TaskCrewSize, Features.TaskCrewSizeInfo)]
[HarmonyPatch(typeof(LobbyMenu), "OpenSelf")]
internal static class TaskCrewSizeLobbyOpenPatch
{
	private static void Postfix()
	{
		TaskCrewSizeLobby.LobbyOpen = true;
	}
}

[Feature(Features.TaskCrewSize, Features.TaskCrewSizeInfo)]
[HarmonyPatch(typeof(LobbyMenu), "CloseSelf")]
internal static class TaskCrewSizeLobbyClosePatch
{
	private static void Postfix()
	{
		TaskCrewSizeLobby.LobbyOpen = false;
	}
}
