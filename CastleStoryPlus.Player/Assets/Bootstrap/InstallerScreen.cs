using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace CastleStoryPlus.Player
{
	// The first start, while there is no converted content: asks where Castle Story is installed, runs the installer
	// (CastleStoryPlus.Port) on a worker thread and starts the player again, which then loads the game's patched
	// assemblies and settings. The installer and its libraries (Cecil, AssetsTools.NET) are kept in Installer/ next to
	// the player, not in Managed, where BepInEx would find this Cecil in place of its own, and run in a domain of their
	// own (InstallerDomain).
	public sealed class InstallerScreen : MonoBehaviour
	{
		public const string InstallerFolder = "Installer";
		// The install's own log, next to the player: the player's log is started over by the new start after it.
		private const string LogName = "install.log";
		private const int ShownLines = 9;
		private static readonly string[] DoorstopState =
		{
			"DOORSTOP_INITIALIZED", "DOORSTOP_INVOKE_DLL_PATH", "DOORSTOP_PROCESS_PATH", "DOORSTOP_MANAGED_FOLDER_DIR",
			"DOORSTOP_DLL_SEARCH_DIRS", "DOORSTOP_MONO_LIB_PATH", "DOORSTOP_DISABLE",
		};

		private static readonly Color Background = new Color(0.09f, 0.10f, 0.12f);
		private static readonly Color PanelColor = new Color(0.15f, 0.16f, 0.19f);
		private static readonly Color FieldColor = new Color(0.07f, 0.08f, 0.09f);
		private static readonly Color Accent = new Color(0.85f, 0.62f, 0.25f);
		private static readonly Color TextColor = new Color(0.92f, 0.92f, 0.90f);
		private static readonly Color DimText = new Color(0.62f, 0.63f, 0.66f);
		private static readonly Color ErrorText = new Color(0.95f, 0.45f, 0.40f);

		private readonly ConcurrentQueue<string> logged = new ConcurrentQueue<string>();
		private readonly List<string> lines = new List<string>();
		private string contentDir;
		// The game's build this player is made from (its shaders are for the player's graphics API).
		private string platform;
		private Font font;
		private InputField pathField;
		private Button installButton;
		private RectTransform progressFill;
		private Text stepText;
		private Text percentText;
		private Text logText;

		// Written by the worker thread, read by Update.
		private volatile string step = "";
		private volatile float progress;
		private volatile bool running;
		private volatile bool succeeded;
		private volatile string failure;
		private float restartAt = -1f;

		public static string PlayerRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

		public static void Show(string contentDir)
		{
			var screen = new GameObject("CastleStoryPlus Installer").AddComponent<InstallerScreen>();
			screen.contentDir = contentDir;
		}

		private void Start()
		{
			font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
			platform = Application.platform == RuntimePlatform.WindowsPlayer ? "Windows" : "Linux";
			Build();
			string found = GameLocator.Find();
			pathField.text = found ?? "";
			if (!InstallerDomain.Exists(Path.Combine(PlayerRoot, InstallerFolder)))
			{
				Missing("The installer is missing: " + Path.Combine(PlayerRoot, InstallerFolder));
				installButton.interactable = false;
				return;
			}
			SetStep(found != null ? "Castle Story found. Install to make the game from it." : "Castle Story was not found in Steam's libraries. Enter its folder.", 0f);
			// Development: install without a click (CSP_AUTO_INSTALL=1, with CSP_GAME_DATA or a Steam install).
			if (found != null && Environment.GetEnvironmentVariable("CSP_AUTO_INSTALL") == "1")
			{
				Install();
			}
		}

		private void Update()
		{
			while (logged.TryDequeue(out string line))
			{
				AddLine(line, false);
			}
			if (running)
			{
				SetStep(step, progress);
				return;
			}
			if (failure != null)
			{
				AddLine(failure, true);
				SetStep("The install failed. Nothing of the game was changed.", progress);
				failure = null;
				installButton.interactable = true;
				pathField.interactable = true;
			}
			if (succeeded && restartAt < 0f)
			{
				SetStep("Installed. Starting the game...", 1f);
				restartAt = Time.realtimeSinceStartup + 1f;
			}
			if (restartAt >= 0f && Time.realtimeSinceStartup >= restartAt)
			{
				restartAt = float.MaxValue;
				if (!Restart())
				{
					SetStep("Installed. Start Castle Story Plus again to play.", 1f);
				}
			}
		}

		private void Install()
		{
			string gameData = GameLocator.DataFolderOf(pathField.text);
			if (gameData == null)
			{
				AddLine("No Castle Story install in " + pathField.text + " (looked for " + GameLocator.DataFolder + ").", true);
				return;
			}
			installButton.interactable = false;
			pathField.interactable = false;
			lines.Clear();
			running = true;
			succeeded = false;
			string playerData = Application.dataPath;
			var worker = new Thread(() => RunInstaller(gameData, playerData)) { Name = "CastleStoryPlus installer", IsBackground = true };
			worker.Start();
		}

		// On the worker thread; the installer runs in a domain of its own (InstallerDomain).
		private void RunInstaller(string gameData, string playerData)
		{
			string dir = Path.Combine(Path.GetDirectoryName(playerData), InstallerFolder);
			// The player's assembly from its file: with BepInEx it is loaded patched, from memory, without a location.
			string playerAssembly = Path.Combine(playerData, "Managed", "CastleStoryPlus.Player.dll");
			string[] problems = null;
			StreamWriter file = null;
			try
			{
				file = new StreamWriter(Path.Combine(Path.GetDirectoryName(playerData), LogName), false) { AutoFlush = true };
				file.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " installing from " + gameData);
				StreamWriter log = file;
				var sink = new InstallerDomain.Sink(
					line =>
					{
						Debug.Log("[CSP install] " + line);
						log.WriteLine(line);
						logged.Enqueue(line);
					},
					(name, done) =>
					{
						if (name != step)
						{
							log.WriteLine("[" + (done * 100f).ToString("0") + " %] " + name);
						}
						step = name;
						progress = done;
					},
					files => problems = files);
				string error = InstallerDomain.Run(dir, playerAssembly, gameData, playerData, contentDir, sink);
				if (error == null)
				{
					log.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " installed");
					succeeded = true;
					return;
				}
				Fail(error, problems, file);
			}
			catch (Exception e)
			{
				Fail(e.ToString(), null, file);
			}
			finally
			{
				file?.Dispose();
				running = false;
			}
		}

		// On the worker thread: error is the exception as text.
		private void Fail(string error, string[] problems, StreamWriter file)
		{
			Debug.LogError("[CSP] install failed: " + error);
			file?.WriteLine(error);
			if (problems != null && problems.Length > 0)
			{
				foreach (string problem in problems)
				{
					logged.Enqueue(problem);
				}
				failure = problems.Length + " game files are missing or not the version this port is made for. It needs the " + platform + " version of Castle Story; verify the game's files in Steam and try again.";
			}
			else
			{
				failure = error.Split('\n')[0];
			}
		}

		internal const string RestartedPidFile = "restarted.pid";

		// The game's assemblies and settings are read when the player starts, so the installed game needs a new start.
		private static bool Restart()
		{
			string data = Application.dataPath;
			string executable = data.Substring(0, data.Length - "_Data".Length) + (Application.platform == RuntimePlatform.WindowsPlayer ? ".exe" : ".x86_64");
			try
			{
				string[] args = Environment.GetCommandLineArgs();
				var start = new ProcessStartInfo(executable, string.Join(" ", Array.ConvertAll(args, Quote), 1, args.Length - 1))
				{
					UseShellExecute = false,
					WorkingDirectory = PlayerRoot,
				};
				// doorstop leaves its state in the environment (started, where it found Mono, and on Windows disabled for
				// child processes); the new player is a new start, BepInEx and all. Its settings (DOORSTOP_ENABLED,
				// ...TARGET_ASSEMBLY from run_bepinex.sh) stay.
				foreach (string state in DoorstopState)
				{
					start.EnvironmentVariables.Remove(state);
				}
				Process child = Process.Start(start);
				try
				{
					// The original game's launcher (CastleStoryPlus.PortLauncher) waits for the new player too.
					File.WriteAllText(Path.Combine(PlayerRoot, RestartedPidFile), child.Id.ToString());
				}
				catch (Exception e)
				{
					Debug.LogWarning("[CSP] Could not write " + RestartedPidFile + ": " + e.Message);
				}
				Application.Quit();
				return true;
			}
			catch (Exception e)
			{
				Debug.LogWarning("[CSP] Could not start " + executable + " again: " + e.Message);
				return false;
			}
		}

		private static string Quote(string arg)
		{
			return arg.IndexOf(' ') >= 0 ? "\"" + arg + "\"" : arg;
		}

		private void Missing(string message)
		{
			AddLine(message, true);
			SetStep("The install can not run.", 0f);
		}

		private void SetStep(string text, float done)
		{
			stepText.text = text;
			percentText.text = (done * 100f).ToString("0") + " %";
			progressFill.anchorMax = new Vector2(Mathf.Clamp01(done), 1f);
		}

		private void AddLine(string line, bool error)
		{
			lines.Add(error ? "<color=#" + ColorUtility.ToHtmlStringRGB(ErrorText) + ">" + line + "</color>" : line);
			int first = Math.Max(0, lines.Count - ShownLines);
			logText.text = string.Join("\n", lines.GetRange(first, lines.Count - first));
		}

		private void Build()
		{
			var cameraObject = new GameObject("Camera");
			cameraObject.transform.SetParent(transform, false);
			Camera view = cameraObject.AddComponent<Camera>();
			view.clearFlags = CameraClearFlags.SolidColor;
			view.backgroundColor = Background;
			view.cullingMask = 0;

			if (EventSystem.current == null)
			{
				var events = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
				events.transform.SetParent(transform, false);
			}

			var canvasObject = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
			canvasObject.transform.SetParent(transform, false);
			canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
			var scaler = canvasObject.GetComponent<CanvasScaler>();
			scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
			scaler.referenceResolution = new Vector2(1600f, 900f);
			scaler.matchWidthOrHeight = 0.5f;

			RectTransform panel = Box("Panel", canvasObject.transform, PanelColor);
			panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f);
			panel.sizeDelta = new Vector2(1000f, 600f);
			var layout = panel.gameObject.AddComponent<VerticalLayoutGroup>();
			layout.padding = new RectOffset(40, 40, 36, 36);
			layout.spacing = 14f;
			layout.childControlWidth = true;
			layout.childControlHeight = true;
			layout.childForceExpandWidth = true;
			layout.childForceExpandHeight = false;

			Label(panel, "Castle Story Plus", 40, FontStyle.Bold, TextColor, 52f);
			Label(panel, "This is Castle Story on Unity 6. It is made from your own Castle Story install the first time it starts; nothing of the game comes with it. Your install is only read, never changed.", 20, FontStyle.Normal, DimText, 56f);
			Label(panel, "Castle Story folder", 18, FontStyle.Bold, TextColor, 26f);

			RectTransform row = Area("Path", panel, 46f);
			var rowLayout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
			rowLayout.spacing = 12f;
			rowLayout.childControlWidth = true;
			rowLayout.childControlHeight = true;
			rowLayout.childForceExpandWidth = false;
			rowLayout.childForceExpandHeight = true;
			pathField = Field(row);
			installButton = ButtonWith(row, "Install", Install);

			RectTransform bar = Area("Progress", panel, 34f);
			var barLayout = bar.gameObject.AddComponent<HorizontalLayoutGroup>();
			barLayout.spacing = 12f;
			barLayout.childControlWidth = true;
			barLayout.childControlHeight = true;
			barLayout.childForceExpandWidth = false;
			barLayout.childForceExpandHeight = true;
			RectTransform track = Box("Track", bar, FieldColor);
			track.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
			progressFill = Box("Fill", track, Accent);
			progressFill.anchorMin = Vector2.zero;
			progressFill.anchorMax = new Vector2(0f, 1f);
			progressFill.offsetMin = progressFill.offsetMax = Vector2.zero;
			percentText = Label(bar, "0 %", 20, FontStyle.Bold, TextColor, 34f);
			percentText.alignment = TextAnchor.MiddleRight;
			percentText.GetComponent<LayoutElement>().preferredWidth = 80f;

			stepText = Label(panel, "", 20, FontStyle.Normal, TextColor, 30f);
			RectTransform logBox = Box("Log", panel, FieldColor);
			logBox.gameObject.AddComponent<LayoutElement>().flexibleHeight = 1f;
			logText = Label(logBox, "", 16, FontStyle.Normal, DimText, 0f);
			RectTransform logRect = logText.rectTransform;
			logRect.anchorMin = Vector2.zero;
			logRect.anchorMax = Vector2.one;
			logRect.offsetMin = new Vector2(14f, 10f);
			logRect.offsetMax = new Vector2(-14f, -10f);
			logText.alignment = TextAnchor.LowerLeft;
			logText.verticalOverflow = VerticalWrapMode.Truncate;
		}

		private static RectTransform Area(string name, Transform parent, float height)
		{
			var area = new GameObject(name, typeof(RectTransform));
			area.transform.SetParent(parent, false);
			var element = area.AddComponent<LayoutElement>();
			element.preferredHeight = height;
			element.minHeight = height;
			// Its own layout group would make it flexible; the log below takes the room that is left.
			element.flexibleHeight = 0f;
			return (RectTransform)area.transform;
		}

		private static RectTransform Box(string name, Transform parent, Color color)
		{
			var box = new GameObject(name, typeof(RectTransform), typeof(Image));
			box.transform.SetParent(parent, false);
			box.GetComponent<Image>().color = color;
			return (RectTransform)box.transform;
		}

		private Text Label(Transform parent, string text, int size, FontStyle style, Color color, float height)
		{
			var label = new GameObject("Text", typeof(RectTransform), typeof(Text));
			label.transform.SetParent(parent, false);
			Text component = label.GetComponent<Text>();
			component.font = font;
			component.text = text;
			component.fontSize = size;
			component.fontStyle = style;
			component.color = color;
			component.supportRichText = true;
			component.horizontalOverflow = HorizontalWrapMode.Wrap;
			component.alignment = TextAnchor.MiddleLeft;
			if (height > 0f)
			{
				var element = label.AddComponent<LayoutElement>();
				element.preferredHeight = height;
				element.minHeight = height;
			}
			return component;
		}

		private InputField Field(Transform parent)
		{
			RectTransform box = Box("Field", parent, FieldColor);
			box.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
			Text text = Label(box, "", 18, FontStyle.Normal, TextColor, 0f);
			Text placeholder = Label(box, "/path/to/Castle Story", 18, FontStyle.Italic, DimText, 0f);
			foreach (Text inner in new[] { text, placeholder })
			{
				inner.horizontalOverflow = HorizontalWrapMode.Overflow;
				inner.rectTransform.anchorMin = Vector2.zero;
				inner.rectTransform.anchorMax = Vector2.one;
				inner.rectTransform.offsetMin = new Vector2(12f, 4f);
				inner.rectTransform.offsetMax = new Vector2(-12f, -4f);
			}
			text.supportRichText = false;
			var field = box.gameObject.AddComponent<InputField>();
			field.textComponent = text;
			field.placeholder = placeholder;
			field.targetGraphic = box.GetComponent<Image>();
			return field;
		}

		private Button ButtonWith(Transform parent, string caption, UnityEngine.Events.UnityAction onClick)
		{
			RectTransform box = Box("Button", parent, Accent);
			box.gameObject.AddComponent<LayoutElement>().preferredWidth = 160f;
			Text text = Label(box, caption, 20, FontStyle.Bold, Background, 0f);
			text.alignment = TextAnchor.MiddleCenter;
			text.rectTransform.anchorMin = Vector2.zero;
			text.rectTransform.anchorMax = Vector2.one;
			text.rectTransform.offsetMin = text.rectTransform.offsetMax = Vector2.zero;
			var button = box.gameObject.AddComponent<Button>();
			button.targetGraphic = box.GetComponent<Image>();
			button.onClick.AddListener(onClick);
			return button;
		}
	}
}
