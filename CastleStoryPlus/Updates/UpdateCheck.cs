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

	private static UpdateCheck _instance;

	// [Updates] SkippedVersion: the release the player chose "Don't show again" for; the title screen does not offer it
	// again (a newer one is offered). The in-game settings still have the update button.
	private static BepInEx.Configuration.ConfigEntry<string> _skipped;

	private static void Enable()
	{
		_skipped = Plugin.Cfg.Bind("Updates", "SkippedVersion", string.Empty, "The release the title screen's update notice is not shown for (\"Don't show again\"). Empty: every new release is offered.");
		_instance = Plugin.Root.AddComponent<UpdateCheck>();
		// In a game too: the settings sidebar offers the update once one is found. The game is saved first.
		GameUi.GameMenuItems.Extend((GameUi.GameMenuModel menu) => menu.Settings.Find("settings").Add(new GameUi.UiButton
		{
			Id = "update",
			Visible = () => _instance != null && _instance._latest != null,
			Label = () => "Update to " + _instance._latest,
			Tooltip = () => "Castle Story Plus " + _instance._latest + " is available (you have v" + Plugin.Version + "). Saves the game, closes it, installs the update and starts the game again.",
			Icon = () => GameUi.GameUiState.Icon(Brix.UI.Icons.IconKeys.UI_Down),
			Enabled = () => !_instance._updating,
			OnClick = (bool shift, bool ctrl) => _instance.UpdateNow()
		}));
	}

	// The settings sidebar's update button: a game being played is saved first (as "save and quit" does).
	private void UpdateNow()
	{
		if (_updating || _latest == null)
		{
			return;
		}
		if (Brix.Engine.Architecte.instance != null && GameParam.Map != null && !Menus.QuitDialogPatch.SaveBeforeLeaving())
		{
			Plugin.Log.LogWarning("Update: the game could not be saved; not updating");
			return;
		}
		_updating = true;
		StartCoroutine(RunUpdate());
	}

	// The original game's folder, which the installer installs into: in the Unity 6 port, the folder that holds the
	// port's own (CastleStoryPlus-Unity6, where the installer put it); null when the port was unpacked elsewhere by hand.
	private static string GameDir
	{
		get
		{
#if UNITY6_PLAYER
			DirectoryInfo port = new DirectoryInfo(BepInEx.Paths.GameRootPath);
			if (port.Name == "CastleStoryPlus-Unity6" && port.Parent != null && Directory.Exists(Path.Combine(port.Parent.FullName, "Castle Story_Data")))
			{
				return port.Parent.FullName;
			}
			return null;
#else
			return BepInEx.Paths.GameRootPath;
#endif
		}
	}

	// For the mod settings' UPDATES section.
	internal static string Latest
	{
		get { return (_instance != null) ? _instance._latest : null; }
	}

	internal static string Status
	{
		get { return (_instance != null) ? _instance._status : "off"; }
	}

	internal static bool Busy
	{
		get { return _instance != null && (_instance._checking || _instance._updating); }
	}

	internal static string Skipped
	{
		get { return (_skipped != null) ? _skipped.Value : string.Empty; }
		set
		{
			if (_skipped != null)
			{
				_skipped.Value = value ?? string.Empty;
			}
		}
	}

	internal static void CheckNow()
	{
		if (_instance != null && !_instance._checking)
		{
			_instance.StartCoroutine(_instance.Check());
		}
	}

	internal static void Install()
	{
		_instance?.UpdateNow();
	}

	private string _status = "not checked yet";

	private bool _checking;

	private IEnumerator Start()
	{
		SceneManager.sceneLoaded += (Scene scene, LoadSceneMode mode) => ShowIfMenu();
		yield return Check();
	}

	private IEnumerator Check()
	{
		_checking = true;
		_status = "checking...";
		try
		{
			IEnumerator inner = CheckReleases();
			while (inner.MoveNext())
			{
				yield return inner.Current;
			}
		}
		finally
		{
			_checking = false;
		}
	}

	private IEnumerator CheckReleases()
	{
		WWW www = new WWW("https://api.github.com/repos/" + Repo + "/releases?per_page=30", null, GitHubHeaders());
		yield return www;
		// WWW holds native buffers until disposed.
		string error = www.error;
		string json = string.IsNullOrEmpty(error) ? www.text : null;
		www.Dispose();
		if (!string.IsNullOrEmpty(error))
		{
			Plugin.Log.LogInfo("Update check failed: " + error);
			_status = "could not check (" + error + ")";
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
			_status = "could not check (" + ex.Message + ")";
			yield break;
		}
		if (best == null || !IsNewer(best, Plugin.Version))
		{
			Plugin.Log.LogInfo("Castle Story Plus is up to date (installed v" + Plugin.Version + ", newest release " + (best ?? "none") + ")");
			_status = "up to date (checked " + DateTime.Now.ToString("HH:mm") + ")";
			yield break;
		}
		_status = best + " is available";
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
		if (_latest == null || _dismissed || _notice != null || SceneManager.GetActiveScene().name != MenuScene || (_skipped != null && _skipped.Value == _latest))
		{
			return;
		}
		_notice = BuildNotice();
	}

	private GameObject BuildNotice()
	{
		Text any = FindObjectOfType<Text>();
		Font font = any != null ? any.font : UnityCompat.BuiltinFont();

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

		NewButton(panel, font, "Update and restart", new Color(0.85f, 0.6f, 0.05f), 16f, 170f, () =>
		{
			if (!_updating)
			{
				_updating = true;
				body.text = "Downloading the installer...";
				StartCoroutine(RunUpdate());
			}
		});
		NewButton(panel, font, "Later", new Color(0.3f, 0.3f, 0.33f), 194f, 80f, () =>
		{
			_dismissed = true;
			Destroy(_notice);
		});
		NewButton(panel, font, "Don't show again", new Color(0.3f, 0.3f, 0.33f), 282f, 142f, () =>
		{
			if (_skipped != null)
			{
				_skipped.Value = _latest;
			}
			_dismissed = true;
			Destroy(_notice);
		});
		return root;
	}

	private IEnumerator RunUpdate()
	{
		// Prefer the installer of the release being installed, so installer fixes apply to this update already.
		string installer = Path.Combine(Path.Combine(Plugin.Directory, "installer"), InstallerName);
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
		string gameDir = GameDir;
		if (!File.Exists(installer) || gameDir == null)
		{
			Application.OpenURL("https://github.com/" + Repo + "/releases/tag/" + _latest);
			_updating = false;
			yield break;
		}
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
		// As the game's own quit: a save still being written finishes first.
		Brix.Utils.ApplicationQuitter.Quit();
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
