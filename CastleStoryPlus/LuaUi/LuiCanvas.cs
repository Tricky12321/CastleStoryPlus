using System;
using Brix.Engine;
using Brix.External.Signals;
using Brix.NewUI.UserSettings_;
using Brix.UI.Builder.Menu.Component;
using CastleStoryPlus.Core;
using CastleStoryPlus.UI;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace CastleStoryPlus.LuaUi;

// The one canvas every C# menu is drawn on. It scales like the game's own menus (a constant pixel size times the
// player's UI scale setting, Neo.canvasScale, as CanvasScalerDriver does), so the sizes the Lua menus use (48
// pixel buttons...) carry over as they are. It sorts just above the canvas of the game's in-game menus and, where
// the game's dialog menu (GS_Modal) has a canvas of its own above them, below that, so dialogs stay on top.
// Tooltips go on a layer above the menus. Views are children of Menus and are dropped when the game is left.
internal class LuiCanvas : MonoBehaviour
{
	private const int FallbackOrder = 100;

	private static LuiCanvas _instance;

	private Canvas _canvas;

	private CanvasScaler _scaler;

	private SignalConnection _settings;

	private int _orderedForScene = -1;

	// Every frame, for views that must keep reading while hidden (an inactive object gets no Update).
	public event Action Tick;

	// Parent of the menus.
	public RectTransform Menus { get; private set; }

	// Parent of tooltips, above the menus.
	public RectTransform Tooltips { get; private set; }

	public static LuiCanvas Instance
	{
		get
		{
			if (_instance == null)
			{
				GameObject go = new GameObject("CSP_LuiCanvas", typeof(RectTransform));
				go.transform.SetParent(Plugin.Root.transform, false);
				_instance = go.AddComponent<LuiCanvas>();
				_instance.Build();
			}
			_instance.UpdateOrder();
			return _instance;
		}
	}

	public static bool Exists
	{
		get { return _instance != null; }
	}

	private void Update()
	{
		try
		{
			Tick?.Invoke();
		}
		catch (Exception ex)
		{
			LuaBridge.LogOnce("view update", ex.ToString());
		}
	}

	private void Build()
	{
		_canvas = gameObject.AddComponent<Canvas>();
		_canvas.renderMode = RenderMode.ScreenSpaceOverlay;
		_canvas.sortingOrder = FallbackOrder;
		_scaler = gameObject.AddComponent<CanvasScaler>();
		_scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
		_scaler.scaleFactor = Neo.canvasScale;
		// Only images and texts marked as raycast targets catch the mouse (backgrounds, buttons); the rest of the
		// screen stays the world's, since the game's picking asks the EventSystem whether the mouse is over UI.
		gameObject.AddComponent<GraphicRaycaster>();
		Menus = Layer("Menus");
		Tooltips = Layer("Tooltips");
		_settings = GameSignals.Connect(UserSettingManager.OnSetSettingIntoGameSignal, OnSettings);
		GameSession.OnLeave(Clear);
	}

	private RectTransform Layer(string name)
	{
		RectTransform rect = UiKit.CreateRect(name, transform);
		UiKit.SetRect(rect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
		return rect;
	}

	private void OnSettings()
	{
		if (_scaler != null)
		{
			_scaler.scaleFactor = Neo.canvasScale;
		}
	}

	private void OnDestroy()
	{
		if (_settings.IsConnected)
		{
			_settings.Disconnect();
		}
	}

	// The game's menus live in the scene, so their canvases are looked at once per scene.
	private void UpdateOrder()
	{
		// Scene.GetHashCode is the scene's handle (Scene.handle is not public in Unity 5.6).
		int scene = SceneManager.GetActiveScene().GetHashCode();
		if (scene == _orderedForScene)
		{
			return;
		}
		int menus = int.MinValue;
		int modal = int.MinValue;
		foreach (LuaMenuComponent component in FindObjectsOfType<LuaMenuComponent>())
		{
			Canvas canvas = component.GetComponentInParent<Canvas>();
			if (canvas == null)
			{
				continue;
			}
			int order = canvas.rootCanvas.sortingOrder;
			if (component.gameObject.name.Contains("Modal"))
			{
				modal = Mathf.Max(modal, order);
			}
			else
			{
				menus = Mathf.Max(menus, order);
			}
		}
		if (menus == int.MinValue)
		{
			// Not in a game yet: ask again next time.
			return;
		}
		_orderedForScene = scene;
		int ours = menus + 1;
		if (modal != int.MinValue && modal > menus && ours >= modal)
		{
			ours = modal - 1;
		}
		_canvas.sortingOrder = ours;
		Plugin.Log.LogInfo("CsMenus: canvas order " + ours + " (game menus " + menus + ", dialogs " + ((modal == int.MinValue) ? "none" : modal.ToString()) + ")");
	}

	// Leaving a game: its menus go, their Lua tables with them.
	private void Clear()
	{
		foreach (RectTransform layer in new RectTransform[] { Menus, Tooltips })
		{
			if (layer == null)
			{
				continue;
			}
			for (int i = layer.childCount - 1; i >= 0; i--)
			{
				Destroy(layer.GetChild(i).gameObject);
			}
		}
		_orderedForScene = -1;
	}
}
