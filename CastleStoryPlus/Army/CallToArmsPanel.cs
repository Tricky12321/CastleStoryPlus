using System;
using System.Collections.Generic;
using System.Text;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Input;
using Brix.UI.Icons;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CastleStoryPlus.Army;

// Call to arms settings window, built in code: soldiers per class, rally points for ranged and melee,
// and the call to arms role of the selected workers. Also places and shows the rally point markers.
internal class CallToArmsPanel : MonoBehaviour
{
	private enum Placing
	{
		None,
		Ranged,
		Melee
	}

	private class RoleButton
	{
		public int Role;

		public Button Button;

		public Text Label;
	}

	private const float RefreshSeconds = 0.3f;

	private static readonly Color Background = new Color(0.07f, 0.07f, 0.09f, 0.94f);

	private static readonly Color Yellow = new Color(1f, 0.8f, 0.25f, 1f);

	private static readonly Color Grey = new Color(0.6f, 0.6f, 0.6f, 1f);

	private static readonly Color TextColor = new Color(0.88f, 0.88f, 0.88f, 1f);

	private static readonly Color RangedColor = new Color(0.25f, 0.55f, 0.95f, 1f);

	private static readonly Color MeleeColor = new Color(0.9f, 0.3f, 0.22f, 1f);

	private static CallToArmsPanel _instance;

	private Font _font;

	private GameObject _window;

	private GameObject _hint;

	private Text _hintText;

	private readonly Dictionary<Occupation.Job, Text> _quotaTexts = new Dictionary<Occupation.Job, Text>();

	private readonly Dictionary<Occupation.Job, Text> _setTexts = new Dictionary<Occupation.Job, Text>();

	private readonly Dictionary<Occupation.Job, Button> _plusButtons = new Dictionary<Occupation.Job, Button>();

	private readonly List<RoleButton> _roleButtons = new List<RoleButton>();

	private Text _rangedStatus;

	private Text _meleeStatus;

	private Text _selectionText;

	private Text _feedbackText;

	private GameObject _rangedMarker;

	private GameObject _meleeMarker;

	private Placing _placing;

	private int _placingFrame;

	private float _nextRefresh;

	public static void Toggle()
	{
		CallToArmsPanel panel = Ensure();
		if (panel._placing != Placing.None)
		{
			panel.StopPlacing();
			return;
		}
		panel.SetVisible(!panel._window.activeSelf);
	}

	private static CallToArmsPanel Ensure()
	{
		if (_instance == null)
		{
			GameObject go = new GameObject("CallToArmsPanel");
			_instance = go.AddComponent<CallToArmsPanel>();
		}
		return _instance;
	}

	private void Awake()
	{
		Text any = FindObjectOfType<Text>();
		_font = (any != null) ? any.font : Resources.GetBuiltinResource<Font>("Arial.ttf");
		BuildCanvas();
		_rangedMarker = CreateMarker("RANGED", RangedColor, IconKeys._UI_Bow);
		_meleeMarker = CreateMarker("MELEE", MeleeColor, IconKeys._UI_Sword);
		SetVisible(false);
	}

	private void OnDestroy()
	{
		if (_placing != Placing.None && Picking.Instance != null)
		{
			Picking.Instance.UILocked = false;
		}
		if (_instance == this)
		{
			_instance = null;
		}
	}

	private void SetVisible(bool visible)
	{
		_window.SetActive(visible);
		if (visible)
		{
			_feedbackText.text = string.Empty;
			Refresh();
		}
	}

	private void Update()
	{
		if (_placing != Placing.None)
		{
			UpdatePlacing();
		}
		else if (_window.activeSelf && Time.unscaledTime >= _nextRefresh)
		{
			Refresh();
		}
		UpdateMarker(_rangedMarker, CallToArmsSettings.HasRanged, CallToArmsSettings.Ranged);
		UpdateMarker(_meleeMarker, CallToArmsSettings.HasMelee, CallToArmsSettings.Melee);
	}

	// ---- refresh

	private void Refresh()
	{
		_nextRefresh = Time.unscaledTime + RefreshSeconds;
		Dictionary<Occupation.Job, int> sets = CallToArms.CountSets();
		Dictionary<int, int> roles = CallToArms.CountRoles();
		foreach (Occupation.Job job in CallToArms.SoldierJobs)
		{
			int quota = CallToArmsSettings.GetQuota(job);
			_quotaTexts[job].text = quota.ToString();
			_quotaTexts[job].color = (quota > sets[job]) ? MeleeColor : Yellow;
			_setTexts[job].text = sets[job] + " sets";
			_plusButtons[job].interactable = quota < sets[job];
		}
		_rangedStatus.text = CallToArmsSettings.HasRanged ? "Ranged: set" : "Ranged: not set (crystal)";
		_meleeStatus.text = CallToArmsSettings.HasMelee ? "Melee: set" : "Melee: not set (crystal)";
		HashSet<Labor> selected = SelectedWorkers();
		_selectionText.text = SelectionSummary(selected);
		foreach (RoleButton roleButton in _roleButtons)
		{
			int taken = roles.TryGetValue(roleButton.Role, out int count) ? count : 0;
			bool isJob = roleButton.Role != CallToArms.RoleAuto && roleButton.Role != CallToArms.RoleStayAtWork;
			if (isJob)
			{
				int total = sets[(Occupation.Job)roleButton.Role];
				roleButton.Label.text = RoleName(roleButton.Role) + " " + taken + "/" + total;
				roleButton.Button.interactable = selected.Count > 0 && (taken < total || AllHaveRole(selected, roleButton.Role));
			}
			else
			{
				roleButton.Label.text = RoleName(roleButton.Role) + " " + taken;
				roleButton.Button.interactable = selected.Count > 0;
			}
		}
	}

	private static HashSet<Labor> SelectedWorkers()
	{
		HashSet<Labor> result = new HashSet<Labor>();
		if (UIGameObserver.bricktrons == null)
		{
			return result;
		}
		foreach (Labor labor in UIGameObserver.bricktrons.CheckSelection())
		{
			if (labor != null)
			{
				result.Add(labor);
			}
		}
		return result;
	}

	private static bool AllHaveRole(HashSet<Labor> workers, int role)
	{
		foreach (Labor worker in workers)
		{
			if (CallToArms.RoleOf(worker) != role)
			{
				return false;
			}
		}
		return true;
	}

	private static string SelectionSummary(HashSet<Labor> selected)
	{
		if (selected.Count == 0)
		{
			return "Select workers to set their role.";
		}
		Dictionary<int, int> roles = new Dictionary<int, int>();
		foreach (Labor worker in selected)
		{
			int role = CallToArms.RoleOf(worker);
			roles[role] = (roles.TryGetValue(role, out int count) ? count : 0) + 1;
		}
		StringBuilder text = new StringBuilder();
		text.Append(selected.Count).Append(" selected: ");
		bool first = true;
		foreach (KeyValuePair<int, int> pair in roles)
		{
			if (!first)
			{
				text.Append(", ");
			}
			text.Append(pair.Value).Append(' ').Append(RoleName(pair.Key));
			first = false;
		}
		return text.ToString();
	}

	private static string RoleName(int role)
	{
		if (role == CallToArms.RoleAuto)
		{
			return "Auto";
		}
		if (role == CallToArms.RoleStayAtWork)
		{
			return "Stay at work";
		}
		return ((Occupation.Job)role).ToString();
	}

	// ---- actions

	private void ChangeQuota(Occupation.Job job, int delta)
	{
		int value = CallToArmsSettings.GetQuota(job) + delta;
		if (delta > 0)
		{
			value = Mathf.Min(value, CallToArms.CountSets()[job]);
		}
		CallToArmsSettings.SetQuota(job, value);
		Refresh();
	}

	private void AssignRole(int role)
	{
		HashSet<Labor> selected = SelectedWorkers();
		if (selected.Count == 0)
		{
			return;
		}
		int assigned = CallToArms.AssignRole(selected, role);
		if (assigned < selected.Count)
		{
			_feedbackText.text = "Only " + assigned + " of " + selected.Count + " got " + RoleName(role) + ": not enough sets.";
		}
		else
		{
			_feedbackText.text = selected.Count + " set to " + RoleName(role) + ".";
		}
		// The role arrives through the network, so refresh a little later.
		_nextRefresh = Time.unscaledTime + 0.1f;
	}

	private void StartPlacing(Placing placing)
	{
		_placing = placing;
		_placingFrame = Time.frameCount;
		_window.SetActive(false);
		_hintText.text = "Left click to place the " + ((placing == Placing.Ranged) ? "RANGED" : "MELEE") + " rally point. Right click or Esc cancels.";
		_hint.SetActive(true);
		if (Picking.Instance != null)
		{
			Picking.Instance.UILocked = true;
		}
	}

	private void StopPlacing()
	{
		_placing = Placing.None;
		_hint.SetActive(false);
		if (Picking.Instance != null)
		{
			Picking.Instance.UILocked = false;
		}
		SetVisible(true);
	}

	private void UpdatePlacing()
	{
		if (Time.frameCount == _placingFrame)
		{
			return;
		}
		if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || UnityEngine.Input.GetMouseButtonDown(1))
		{
			StopPlacing();
			return;
		}
		if (!UnityEngine.Input.GetMouseButtonDown(0))
		{
			return;
		}
		if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
		{
			return;
		}
		if (TryGroundPoint(UnityEngine.Input.mousePosition, out Vector3 position))
		{
			CallToArmsSettings.SetRally(_placing == Placing.Ranged, position);
		}
		StopPlacing();
	}

	private static bool TryGroundPoint(Vector2 mouse, out Vector3 position)
	{
		position = Vector3.zero;
		if (Camera.main == null || Picking.Instance == null)
		{
			return false;
		}
		VoxelRaycastHit hit = new VoxelRaycastHit();
		LayerBundle layers = new LayerBundle((int)UnityLayer.Terrain, (int)UnityLayer.FreeBlocks);
		if (!hit.PlaceAtRaycast(Camera.main.ScreenPointToRay(mouse), Picking.Instance.MaxDistance, layers))
		{
			return false;
		}
		XYZ voxel = hit.VoxelPosition;
		if (!Voxel.IsOpenAndWalkable(voxel))
		{
			voxel = voxel + XYZ.FromVector3(Vector3.up);
		}
		position = voxel.ToVector3();
		return true;
	}

	// ---- markers

	private GameObject CreateMarker(string text, Color color, IconKey icon)
	{
		GameObject root = new GameObject("RallyMarker " + text);
		root.transform.SetParent(transform, worldPositionStays: false);
		Material material = MarkerMaterial(color);
		CreatePart(PrimitiveType.Cylinder, root.transform, new Vector3(0f, 1.5f, 0f), new Vector3(0.08f, 1.5f, 0.08f), material);
		CreatePart(PrimitiveType.Cube, root.transform, new Vector3(0.45f, 2.7f, 0f), new Vector3(0.85f, 0.5f, 0.04f), material);
		GameObject label = new GameObject("Label", typeof(RectTransform));
		label.transform.SetParent(root.transform, worldPositionStays: false);
		label.transform.localPosition = new Vector3(0f, 3.5f, 0f);
		label.transform.localScale = Vector3.one * 0.01f;
		Canvas canvas = label.AddComponent<Canvas>();
		canvas.renderMode = RenderMode.WorldSpace;
		label.GetComponent<RectTransform>().sizeDelta = new Vector2(200f, 50f);
		Image image = CreateRect("Icon", label.transform).gameObject.AddComponent<Image>();
		image.sprite = icon.Get64();
		image.color = color;
		image.raycastTarget = false;
		SetRect(image.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(25f, 0f), new Vector2(44f, 44f));
		Text title = CreateText(label.transform, text, 30, color, TextAnchor.MiddleLeft);
		title.fontStyle = FontStyle.Bold;
		SetRect(title.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(26f, 0f), new Vector2(-52f, 0f));
		Outline outline = title.gameObject.AddComponent<Outline>();
		outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
		root.SetActive(false);
		return root;
	}

	private static Material MarkerMaterial(Color color)
	{
		Shader shader = Shader.Find("Sprites/Default");
		if (shader == null)
		{
			shader = Shader.Find("Unlit/Color");
		}
		if (shader == null)
		{
			return null;
		}
		Material material = new Material(shader);
		material.color = color;
		return material;
	}

	private static void CreatePart(PrimitiveType type, Transform parent, Vector3 position, Vector3 scale, Material material)
	{
		GameObject part = GameObject.CreatePrimitive(type);
		Collider collider = part.GetComponent<Collider>();
		if (collider != null)
		{
			Destroy(collider);
		}
		part.layer = 2;
		part.transform.SetParent(parent, worldPositionStays: false);
		part.transform.localPosition = position;
		part.transform.localScale = scale;
		Renderer renderer = part.GetComponent<Renderer>();
		renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
		renderer.receiveShadows = false;
		if (material != null)
		{
			renderer.sharedMaterial = material;
		}
	}

	private static void UpdateMarker(GameObject marker, bool visible, Vector3 position)
	{
		if (marker.activeSelf != visible)
		{
			marker.SetActive(visible);
		}
		if (!visible)
		{
			return;
		}
		marker.transform.position = position + new Vector3(0.5f, 0f, 0.5f);
		Camera camera = Camera.main;
		if (camera != null)
		{
			Transform label = marker.transform.Find("Label");
			label.rotation = camera.transform.rotation;
		}
	}

	// ---- window

	private void BuildCanvas()
	{
		GameObject canvasGo = new GameObject("CallToArmsCanvas", typeof(RectTransform));
		canvasGo.transform.SetParent(transform, worldPositionStays: false);
		Canvas canvas = canvasGo.AddComponent<Canvas>();
		canvas.renderMode = RenderMode.ScreenSpaceOverlay;
		canvas.sortingOrder = 900;
		CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
		scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
		scaler.referenceResolution = new Vector2(1920f, 1080f);
		scaler.matchWidthOrHeight = 1f;
		canvasGo.AddComponent<GraphicRaycaster>();

		_hint = CreatePanel("PlacingHint", canvasGo.transform);
		RectTransform hintRect = _hint.GetComponent<RectTransform>();
		SetRect(hintRect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -90f), new Vector2(620f, 40f));
		_hint.GetComponent<Image>().raycastTarget = false;
		_hintText = CreateText(_hint.transform, string.Empty, 16, Yellow, TextAnchor.MiddleCenter);
		SetRect(_hintText.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
		_hint.SetActive(false);

		_window = CreatePanel("Window", canvasGo.transform);
		RectTransform windowRect = _window.GetComponent<RectTransform>();
		windowRect.anchorMin = new Vector2(1f, 0.5f);
		windowRect.anchorMax = new Vector2(1f, 0.5f);
		windowRect.pivot = new Vector2(1f, 0.5f);
		windowRect.anchoredPosition = new Vector2(-64f, 0f);
		windowRect.sizeDelta = new Vector2(380f, 0f);
		VerticalLayoutGroup layout = _window.AddComponent<VerticalLayoutGroup>();
		layout.padding = new RectOffset(14, 14, 10, 14);
		layout.spacing = 4f;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = false;
		ContentSizeFitter fitter = _window.AddComponent<ContentSizeFitter>();
		fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

		Transform header = CreateRow(_window.transform, 30f);
		Text title = CreateText(header, "CALL TO ARMS", 18, Yellow, TextAnchor.MiddleLeft);
		title.fontStyle = FontStyle.Bold;
		Flexible(title.gameObject);
		CreateButton(header, "X", 28f, () => SetVisible(false));

		AddSection("SOLDIERS PER CLASS");
		AddNote("Workers called when the alarm sounds. Limited by the weapon/armour sets you own. All 0 = everyone takes any kit.");
		foreach (Occupation.Job job in CallToArms.SoldierJobs)
		{
			AddQuotaRow(job);
		}

		AddSection("RALLY POINTS");
		_rangedStatus = AddRallyRow(Placing.Ranged, RangedColor);
		_meleeStatus = AddRallyRow(Placing.Melee, MeleeColor);

		AddSection("ROLE OF SELECTED WORKERS");
		_selectionText = AddNote(string.Empty);
		int[] roles = new int[8]
		{
			CallToArms.RoleAuto,
			CallToArms.RoleStayAtWork,
			(int)Occupation.Job.Knight,
			(int)Occupation.Job.Halberdier,
			(int)Occupation.Job.Archer,
			(int)Occupation.Job.Arbalist,
			(int)Occupation.Job.Alchemist,
			(int)Occupation.Job.Artificer
		};
		Transform row = null;
		for (int i = 0; i < roles.Length; i++)
		{
			if (i % 2 == 0)
			{
				row = CreateRow(_window.transform, 28f);
			}
			int role = roles[i];
			Button button = CreateButton(row, RoleName(role), 0f, () => AssignRole(role));
			Flexible(button.gameObject);
			_roleButtons.Add(new RoleButton
			{
				Role = role,
				Button = button,
				Label = button.GetComponentInChildren<Text>()
			});
		}
		_feedbackText = AddNote(string.Empty);
		_feedbackText.color = Yellow;
	}

	private void AddSection(string text)
	{
		Transform row = CreateRow(_window.transform, 26f);
		Text label = CreateText(row, text, 12, Grey, TextAnchor.LowerLeft);
		Flexible(label.gameObject);
	}

	private Text AddNote(string text)
	{
		Text note = CreateText(_window.transform, text, 12, Grey, TextAnchor.UpperLeft);
		note.horizontalOverflow = HorizontalWrapMode.Wrap;
		return note;
	}

	private void AddQuotaRow(Occupation.Job job)
	{
		bool ranged = CallToArms.IsRanged(job);
		Transform row = CreateRow(_window.transform, 26f);
		Image icon = CreateRect("Icon", row).gameObject.AddComponent<Image>();
		icon.sprite = (ranged ? IconKeys._UI_Bow : IconKeys._UI_Sword).Get32();
		icon.color = ranged ? RangedColor : MeleeColor;
		icon.raycastTarget = false;
		Fixed(icon.gameObject, 20f, 20f);
		Text name = CreateText(row, job.ToString(), 14, TextColor, TextAnchor.MiddleLeft);
		Flexible(name.gameObject);
		Text sets = CreateText(row, string.Empty, 12, Grey, TextAnchor.MiddleRight);
		Fixed(sets.gameObject, 64f, 26f);
		CreateButton(row, "-", 26f, () => ChangeQuota(job, -1));
		Text count = CreateText(row, "0", 15, Yellow, TextAnchor.MiddleRight);
		Fixed(count.gameObject, 30f, 26f);
		Button plus = CreateButton(row, "+", 26f, () => ChangeQuota(job, 1));
		_quotaTexts[job] = count;
		_setTexts[job] = sets;
		_plusButtons[job] = plus;
	}

	private Text AddRallyRow(Placing placing, Color color)
	{
		Transform row = CreateRow(_window.transform, 28f);
		Text status = CreateText(row, string.Empty, 14, color, TextAnchor.MiddleLeft);
		Flexible(status.gameObject);
		CreateButton(row, "Set", 56f, () => StartPlacing(placing));
		CreateButton(row, "Clear", 56f, () =>
		{
			CallToArmsSettings.ClearRally(placing == Placing.Ranged);
			Refresh();
		});
		return status;
	}

	// ---- uGUI helpers

	private static RectTransform CreateRect(string name, Transform parent)
	{
		GameObject go = new GameObject(name, typeof(RectTransform));
		go.transform.SetParent(parent, worldPositionStays: false);
		return go.GetComponent<RectTransform>();
	}

	private static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 position, Vector2 size)
	{
		rect.anchorMin = anchorMin;
		rect.anchorMax = anchorMax;
		rect.anchoredPosition = position;
		rect.sizeDelta = size;
	}

	private static GameObject CreatePanel(string name, Transform parent)
	{
		RectTransform rect = CreateRect(name, parent);
		Image image = rect.gameObject.AddComponent<Image>();
		image.color = Background;
		return rect.gameObject;
	}

	private static Transform CreateRow(Transform parent, float height)
	{
		RectTransform rect = CreateRect("Row", parent);
		HorizontalLayoutGroup layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
		layout.spacing = 6f;
		layout.childAlignment = TextAnchor.MiddleLeft;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = false;
		layout.childForceExpandHeight = false;
		LayoutElement element = rect.gameObject.AddComponent<LayoutElement>();
		element.minHeight = height;
		element.preferredHeight = height;
		return rect;
	}

	private Text CreateText(Transform parent, string text, int size, Color color, TextAnchor alignment)
	{
		Text label = CreateRect("Text", parent).gameObject.AddComponent<Text>();
		label.font = _font;
		label.fontSize = size;
		label.color = color;
		label.alignment = alignment;
		label.text = text;
		label.raycastTarget = false;
		label.horizontalOverflow = HorizontalWrapMode.Overflow;
		return label;
	}

	private Button CreateButton(Transform parent, string text, float width, Action onClick)
	{
		RectTransform rect = CreateRect("Button", parent);
		Image image = rect.gameObject.AddComponent<Image>();
		image.color = Color.white;
		Button button = rect.gameObject.AddComponent<Button>();
		ColorBlock colors = button.colors;
		colors.normalColor = new Color(0.22f, 0.22f, 0.26f, 1f);
		colors.highlightedColor = new Color(0.34f, 0.34f, 0.4f, 1f);
		colors.pressedColor = new Color(0.5f, 0.42f, 0.18f, 1f);
		colors.disabledColor = new Color(0.14f, 0.14f, 0.16f, 0.7f);
		colors.colorMultiplier = 1f;
		button.colors = colors;
		button.targetGraphic = image;
		button.onClick.AddListener(() => onClick());
		Text label = CreateText(rect, text, 13, TextColor, TextAnchor.MiddleCenter);
		SetRect(label.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
		if (width > 0f)
		{
			Fixed(rect.gameObject, width, 24f);
		}
		else
		{
			LayoutElement element = rect.gameObject.AddComponent<LayoutElement>();
			element.minHeight = 24f;
			element.preferredHeight = 24f;
		}
		return button;
	}

	private static void Fixed(GameObject go, float width, float height)
	{
		LayoutElement element = go.GetComponent<LayoutElement>();
		if (element == null)
		{
			element = go.AddComponent<LayoutElement>();
		}
		element.minWidth = width;
		element.preferredWidth = width;
		element.minHeight = height;
		element.preferredHeight = height;
		element.flexibleWidth = 0f;
	}

	private static void Flexible(GameObject go)
	{
		LayoutElement element = go.GetComponent<LayoutElement>();
		if (element == null)
		{
			element = go.AddComponent<LayoutElement>();
		}
		element.flexibleWidth = 1f;
		element.minWidth = 0f;
	}
}
