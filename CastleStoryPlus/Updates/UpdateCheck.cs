using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using CastleStoryPlus.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace CastleStoryPlus.Updates;

// Asks GitHub for Castle Story Plus releases at startup. Every release is a version tag (v0.1.0) made by
// tools/release.sh. The newest published (not draft, not pre-release) tag that is newer than this plugin and
// has a package for this system is offered in the main menu with "Update and restart": that downloads the
// installer from that tag's source (falling back to the one shipped in plugins/CastleStoryPlus/installer/), which waits
// for the game to close, installs that tag and starts the game again through Steam.
// Uses Unity's WWW, because the game's Mono 2.6 cannot do TLS 1.2.
[Feature(Features.UpdateCheck, Features.UpdateCheckInfo)]
internal class UpdateCheck : MonoBehaviour
{
	private const string Repo = "Tricky12321/CastleStoryPlus";

	private const string MenuScene = "SceneMenu";

	private string _latest;

	private string _installerUrl;

	private GameObject _notice;

	private bool _dismissed;

	private bool _updating;

	private static bool IsWindows => Application.platform == RuntimePlatform.WindowsPlayer;

	private static string InstallerName => IsWindows ? "install.ps1" : "install.sh";

	private static void Enable()
	{
		Plugin.Root.AddComponent<UpdateCheck>();
	}

	private IEnumerator Start()
	{
		SceneManager.sceneLoaded += (Scene scene, LoadSceneMode mode) => ShowIfMenu();
		WWW www = new WWW("https://api.github.com/repos/" + Repo + "/releases?per_page=30", null, GitHubHeaders());
		yield return www;
		// WWW holds native buffers until disposed.
		string error = www.error;
		string json = string.IsNullOrEmpty(error) ? www.text : null;
		www.Dispose();
		if (!string.IsNullOrEmpty(error))
		{
			Plugin.Log.LogInfo("Update check failed: " + error);
			yield break;
		}
		string best = null;
		try
		{
			foreach (JToken release in JArray.Parse(json))
			{
				string tag = (string)release["tag_name"];
				if (tag == null || (bool?)release["draft"] == true || (bool?)release["prerelease"] == true || ParseVersion(tag) == null)
				{
					continue;
				}
				bool hasPackage = false;
				foreach (JToken asset in release["assets"] ?? new JArray())
				{
					if (IsPackageForThisSystem((string)asset["name"] ?? ""))
					{
						hasPackage = true;
					}
				}
				if (hasPackage && (best == null || ParseVersion(tag) > ParseVersion(best)))
				{
					best = tag;
				}
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogInfo("Update check failed: " + ex.Message);
			yield break;
		}
		if (best == null || !IsNewer(best, Plugin.Version))
		{
			Plugin.Log.LogInfo("Castle Story Plus is up to date (installed v" + Plugin.Version + ", newest release " + (best ?? "none") + ")");
			yield break;
		}
		Plugin.Log.LogInfo("Castle Story Plus " + best + " is available (installed v" + Plugin.Version + ")");
		_latest = best;
		// The installer as it is in that tag's source, so installer fixes already apply to this update.
		_installerUrl = "https://raw.githubusercontent.com/" + Repo + "/" + best + "/installer/" + InstallerName;
		ShowIfMenu();
	}

	private static Dictionary<string, string> GitHubHeaders()
	{
		return new Dictionary<string, string> { { "Accept", "application/vnd.github+json" } };
	}

	// CastleStoryPlus-v0.1.0-windows.zip / -linux.zip
	private static bool IsPackageForThisSystem(string name)
	{
		return name.StartsWith("CastleStoryPlus-") && name.EndsWith(IsWindows ? "-windows.zip" : "-linux.zip");
	}

	private static Version ParseVersion(string tag)
	{
		try
		{
			return new Version(tag.TrimStart('v', 'V'));
		}
		catch (Exception)
		{
			return null;
		}
	}

	internal static bool IsNewer(string tag, string current)
	{
		Version latest = ParseVersion(tag);
		Version installed = ParseVersion(current);
		return latest != null && installed != null && latest > installed;
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

		NewButton(panel, font, "Update and restart", new Color(0.85f, 0.6f, 0.05f), 16f, 200f, () =>
		{
			if (!_updating)
			{
				_updating = true;
				body.text = "Downloading the installer...";
				StartCoroutine(RunUpdate());
			}
		});
		NewButton(panel, font, "Later", new Color(0.3f, 0.3f, 0.33f), 228f, 120f, () =>
		{
			_dismissed = true;
			Destroy(_notice);
		});
		return root;
	}

	private IEnumerator RunUpdate()
	{
		// Prefer the installer of the release being installed, so installer fixes apply to this update already.
		string installer = Path.Combine(Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "installer"), InstallerName);
		if (_installerUrl != null)
		{
			WWW www = new WWW(_installerUrl);
			yield return www;
			string error = www.error;
			byte[] bytes = string.IsNullOrEmpty(error) ? www.bytes : null;
			www.Dispose();
			if (bytes != null && bytes.Length > 0)
			{
				string downloaded = Path.Combine(BepInEx.Paths.BepInExRootPath, "CastleStoryPlus.Updater" + Path.GetExtension(InstallerName));
				try
				{
					File.WriteAllBytes(downloaded, bytes);
					installer = downloaded;
				}
				catch (Exception ex)
				{
					Plugin.Log.LogWarning("Could not save the downloaded installer: " + ex.Message);
				}
			}
			else
			{
				Plugin.Log.LogWarning("Could not download the " + _latest + " installer (" + error + "); using the shipped one");
			}
		}
		if (!File.Exists(installer))
		{
			Application.OpenURL("https://github.com/" + Repo + "/releases/tag/" + _latest);
			_updating = false;
			yield break;
		}
		string gameDir = BepInEx.Paths.GameRootPath;
		int pid = Process.GetCurrentProcess().Id;
		string log = Path.Combine(BepInEx.Paths.BepInExRootPath, "CastleStoryPlus.Update.log");
		ProcessStartInfo start;
		if (IsWindows)
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
			Application.OpenURL("https://github.com/" + Repo + "/releases/tag/" + _latest);
			_updating = false;
			yield break;
		}
		Plugin.Log.LogInfo("Updating to " + _latest + " with " + installer + "; closing the game");
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
