using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using CastleStoryPlus.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace CastleStoryPlus.Updates;

// Asks GitHub for the latest Castle Story Plus release at startup. When it is newer than this plugin,
// the main menu shows a notice with "Update and restart": that runs the installer shipped in
// plugins/CastleStoryPlus/installer/, which waits for the game to close, installs the release and
// starts the game again through Steam. Uses Unity's WWW, because the game's Mono 2.6 cannot do TLS 1.2.
[Feature(Features.UpdateCheck, Features.UpdateCheckInfo)]
internal class UpdateCheck : MonoBehaviour
{
	private const string Repo = "Tricky12321/CastleStoryPlus";

	private const string MenuScene = "SceneMenu";

	private static readonly Regex TagPattern = new Regex("\"tag_name\"\\s*:\\s*\"([^\"]+)\"");

	private string _latest;

	private GameObject _notice;

	private bool _dismissed;

	private static void Enable()
	{
		Plugin.Root.AddComponent<UpdateCheck>();
	}

	private IEnumerator Start()
	{
		SceneManager.sceneLoaded += (Scene scene, LoadSceneMode mode) => ShowIfMenu();
		Dictionary<string, string> headers = new Dictionary<string, string> { { "Accept", "application/vnd.github+json" } };
		WWW www = new WWW("https://api.github.com/repos/" + Repo + "/releases/latest", null, headers);
		yield return www;
		if (!string.IsNullOrEmpty(www.error))
		{
			Plugin.Log.LogInfo("Update check failed: " + www.error);
			yield break;
		}
		Match match = TagPattern.Match(www.text);
		if (!match.Success)
		{
			yield break;
		}
		string tag = match.Groups[1].Value;
		if (!IsNewer(tag, Plugin.Version))
		{
			Plugin.Log.LogInfo("Castle Story Plus is up to date (latest release " + tag + ")");
			yield break;
		}
		Plugin.Log.LogInfo("Castle Story Plus " + tag + " is available (installed " + Plugin.Version + ")");
		_latest = tag;
		ShowIfMenu();
	}

	internal static bool IsNewer(string tag, string current)
	{
		try
		{
			return new Version(tag.TrimStart('v', 'V')) > new Version(current.TrimStart('v', 'V'));
		}
		catch (Exception)
		{
			return false;
		}
	}

	private void ShowIfMenu()
	{
		if (_latest == null || _dismissed || _notice != null || SceneManager.GetActiveScene().name != MenuScene)
		{
			return;
		}
		_notice = BuildNotice();
	}

	private GameObject BuildNotice()
	{
		Text any = FindObjectOfType<Text>();
		Font font = any != null ? any.font : Resources.GetBuiltinResource<Font>("Arial.ttf");

		GameObject root = new GameObject("CastleStoryPlusUpdate", typeof(RectTransform));
		Canvas canvas = root.AddComponent<Canvas>();
		canvas.renderMode = RenderMode.ScreenSpaceOverlay;
		canvas.sortingOrder = 500;
		CanvasScaler scaler = root.AddComponent<CanvasScaler>();
		scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
		scaler.referenceResolution = new Vector2(1920f, 1080f);
		scaler.matchWidthOrHeight = 0.5f;
		root.AddComponent<GraphicRaycaster>();

		RectTransform panel = NewRect("Panel", root.transform);
		panel.anchorMin = new Vector2(1f, 1f);
		panel.anchorMax = new Vector2(1f, 1f);
		panel.pivot = new Vector2(1f, 1f);
		panel.sizeDelta = new Vector2(440f, 132f);
		panel.anchoredPosition = new Vector2(-32f, -32f);
		panel.gameObject.AddComponent<Image>().color = new Color(0.08f, 0.08f, 0.1f, 0.92f);

		Text title = NewText(panel, font, "Castle Story Plus " + _latest + " is available", 20, FontStyle.Bold, new Color(1f, 0.76f, 0.06f));
		Place(title.rectTransform, 16f, -14f, 408f, 28f);
		Text body = NewText(panel, font, "You have v" + Plugin.Version + ". The update closes the game, installs and starts it again.", 15, FontStyle.Normal, new Color(0.85f, 0.85f, 0.85f));
		Place(body.rectTransform, 16f, -44f, 408f, 40f);

		NewButton(panel, font, "Update and restart", new Color(0.85f, 0.6f, 0.05f), 16f, 200f, RunUpdate);
		NewButton(panel, font, "Later", new Color(0.3f, 0.3f, 0.33f), 228f, 120f, () =>
		{
			_dismissed = true;
			Destroy(_notice);
		});
		return root;
	}

	private void RunUpdate()
	{
		string installer = Path.Combine(Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "installer"), Application.platform == RuntimePlatform.WindowsPlayer ? "install.ps1" : "install.sh");
		if (!File.Exists(installer))
		{
			Application.OpenURL("https://github.com/" + Repo + "/releases/latest");
			return;
		}
		string gameDir = BepInEx.Paths.GameRootPath;
		int pid = Process.GetCurrentProcess().Id;
		string log = Path.Combine(BepInEx.Paths.BepInExRootPath, "CastleStoryPlus.Update.log");
		ProcessStartInfo start;
		if (Application.platform == RuntimePlatform.WindowsPlayer)
		{
			start = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + installer + "\" -Tag " + _latest + " -GameDir \"" + gameDir + "\" -WaitPid " + pid + " -Restart");
			start.UseShellExecute = true;
		}
		else
		{
			// Detached from the game, so it keeps running when the game quits.
			string command = "setsid bash " + Quote(installer) + " --tag " + Quote(_latest) + " --game-dir " + Quote(gameDir) + " --wait-pid " + pid + " --restart > " + Quote(log) + " 2>&1 < /dev/null &";
			start = new ProcessStartInfo("/bin/bash", "-c " + Quote(command));
			start.UseShellExecute = false;
		}
		try
		{
			Process.Start(start);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("Could not start the updater: " + ex);
			Application.OpenURL("https://github.com/" + Repo + "/releases/latest");
			return;
		}
		Plugin.Log.LogInfo("Updating to " + _latest + "; closing the game");
		Application.Quit();
	}

	private static string Quote(string value)
	{
		return "'" + value.Replace("'", "'\\''") + "'";
	}

	private static RectTransform NewRect(string name, Transform parent)
	{
		RectTransform rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
		rect.SetParent(parent, worldPositionStays: false);
		return rect;
	}

	// Top-left anchored placement inside the panel.
	private static void Place(RectTransform rect, float x, float y, float width, float height)
	{
		rect.anchorMin = new Vector2(0f, 1f);
		rect.anchorMax = new Vector2(0f, 1f);
		rect.pivot = new Vector2(0f, 1f);
		rect.anchoredPosition = new Vector2(x, y);
		rect.sizeDelta = new Vector2(width, height);
	}

	private static Text NewText(Transform parent, Font font, string value, int size, FontStyle style, Color color)
	{
		Text text = NewRect("Text", parent).gameObject.AddComponent<Text>();
		text.font = font;
		text.text = value;
		text.fontSize = size;
		text.fontStyle = style;
		text.color = color;
		text.horizontalOverflow = HorizontalWrapMode.Wrap;
		text.verticalOverflow = VerticalWrapMode.Overflow;
		return text;
	}

	private static void NewButton(Transform parent, Font font, string label, Color color, float x, float width, UnityEngine.Events.UnityAction onClick)
	{
		RectTransform rect = NewRect("Button", parent);
		Place(rect, x, -88f, width, 32f);
		Image image = rect.gameObject.AddComponent<Image>();
		image.color = color;
		Button button = rect.gameObject.AddComponent<Button>();
		button.targetGraphic = image;
		button.onClick.AddListener(onClick);
		Text text = NewText(rect, font, label, 16, FontStyle.Bold, Color.white);
		text.alignment = TextAnchor.MiddleCenter;
		RectTransform textRect = text.rectTransform;
		textRect.anchorMin = Vector2.zero;
		textRect.anchorMax = Vector2.one;
		textRect.offsetMin = Vector2.zero;
		textRect.offsetMax = Vector2.zero;
	}
}
