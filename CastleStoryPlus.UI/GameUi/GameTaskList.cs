using System;
using System.Collections.Generic;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Network;
using Brix.UI;
using Brix.UI.GameSelector;
using Brix.UI.Icons;
using CastleStoryPlus.LuaUi;
using CastleStoryPlus.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CastleStoryPlus.GameUi;

// The task list along the bottom of the screen (Menu_TaskList.lua), read straight from the game: the idle group,
// the selected workshop, every task, then the melee and ranged soldiers at the right end. Each row shows the task's
// icon and name, its crew's faces (selected ones lit) and its priority. A click on the icon selects or deselects
// the task; on the crew it selects them; right click assigns the selected bricktrons (on the idle group: makes them
// free); Shift and Ctrl raise and lower the priority (on the idle group, Shift selects its idle workers).
internal class GameTaskList : MonoBehaviour
{
	internal const float Height = 72f;

	private const float RefreshSeconds = 0.5f;

	// A row's key: a task, the selected workshop's row or a soldiers' row.
	internal sealed class RowKey
	{
		public Project Project;

		public bool Operable;

		public int Army;
	}

	private const int Melee = 1;

	private const int Ranged = 2;

	private readonly Dictionary<Project, GameTaskRow> _rows = new Dictionary<Project, GameTaskRow>();

	private GameTaskRow _operable;

	private GameTaskRow _melee;

	private GameTaskRow _ranged;

	private RectTransform _spacer;

	private float _refreshAt;

	private bool _dirty = true;

	internal static GameTaskList Current { get; private set; }

	public static GameTaskList Create(Transform parent)
	{
		RectTransform rect = UiKit.CreateRect("CS task list", parent);
		rect.anchorMin = new Vector2(0f, 0f);
		rect.anchorMax = new Vector2(1f, 0f);
		rect.pivot = new Vector2(0.5f, 0f);
		rect.offsetMin = new Vector2(CsPanelView.BarWidth + 1f, 0f);
		rect.offsetMax = new Vector2(-CsPanelView.BarWidth - 1f, Height);
		HorizontalLayoutGroup layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
		layout.spacing = 1f;
		layout.childAlignment = TextAnchor.LowerLeft;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = false;
		layout.childForceExpandHeight = false;
		GameTaskList list = rect.gameObject.AddComponent<GameTaskList>();
		list._spacer = UiKit.CreateRect("Spacer", rect);
		LayoutElement spacer = list._spacer.gameObject.AddComponent<LayoutElement>();
		spacer.flexibleWidth = 1f;
		spacer.minHeight = 48f;
		list._operable = GameTaskRow.Create(list, new RowKey { Operable = true });
		list._melee = GameTaskRow.Create(list, new RowKey { Army = Melee });
		list._ranged = GameTaskRow.Create(list, new RowKey { Army = Ranged });
		GameUiState.Changed += list.MarkDirty;
		Current = list;
		return list;
	}

	private void MarkDirty()
	{
		_dirty = true;
	}

	private void OnDestroy()
	{
		GameUiState.Changed -= MarkDirty;
		if (Current == this)
		{
			Current = null;
		}
	}

	private void Update()
	{
		if (!_dirty && Time.unscaledTime < _refreshAt)
		{
			return;
		}
		_dirty = false;
		_refreshAt = Time.unscaledTime + RefreshSeconds;
		try
		{
			Refresh();
		}
		catch (Exception ex)
		{
			LuaBridge.LogOnce("GameUi task list", ex.ToString());
		}
	}

	private readonly List<Project> _order = new List<Project>();

	private readonly HashSet<Project> _seen = new HashSet<Project>();

	private void Refresh()
	{
		_order.Clear();
		Project idle = IdleProject.GetAlliedProject();
		if (idle != null)
		{
			_order.Add(idle);
		}
		if (UIGameObserver.projects != null && UIGameObserver.projects.all != null)
		{
			foreach (Project project in UIGameObserver.projects.all)
			{
				if (project != idle && GameUiState.Exists(project) && GameMenuTexts.Known(GameUiState.TypeOf(project)))
				{
					_order.Add(project);
				}
			}
		}
		_seen.Clear();
		int sibling = 0;
		bool added = false;
		for (int i = 0; i < _order.Count; i++)
		{
			Project project = _order[i];
			_seen.Add(project);
			if (!_rows.TryGetValue(project, out GameTaskRow row) || row == null)
			{
				row = GameTaskRow.Create(this, new RowKey { Project = project });
				_rows[project] = row;
				added = true;
			}
			row.transform.SetSiblingIndex(sibling++);
			if (!row.gameObject.activeSelf)
			{
				row.gameObject.SetActive(true);
			}
			row.Show();
			// The selected workshop's row comes right after the idle group.
			if (i == 0)
			{
				bool operable = GameUiState.Station != null;
				_operable.transform.SetSiblingIndex(sibling++);
				if (_operable.gameObject.activeSelf != operable)
				{
					_operable.gameObject.SetActive(operable);
				}
				if (operable)
				{
					_operable.Show();
				}
			}
		}
		List<Project> gone = null;
		foreach (KeyValuePair<Project, GameTaskRow> pair in _rows)
		{
			if (!_seen.Contains(pair.Key))
			{
				(gone ?? (gone = new List<Project>())).Add(pair.Key);
			}
		}
		if (gone != null)
		{
			foreach (Project project in gone)
			{
				if (_rows[project] != null)
				{
					Destroy(_rows[project].gameObject);
				}
				_rows.Remove(project);
			}
		}
		_spacer.SetSiblingIndex(sibling++);
		_melee.transform.SetSiblingIndex(sibling++);
		_ranged.transform.SetSiblingIndex(sibling++);
		_melee.Show();
		_ranged.Show();
		// New rows are laid out now, not at the next canvas update, so the popup can stand over its row at once (it
		// read a row at the list's middle while the list was built, after a UI reload).
		if (added)
		{
			LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)transform);
		}
	}

	// Where the selected task's row is, for the popup (null when none shows).
	internal RectTransform SelectedRow()
	{
		if (GameUiState.Station != null && _operable != null && _operable.gameObject.activeSelf)
		{
			return (RectTransform)_operable.transform;
		}
		Project selected = GameUiState.Selected;
		return (selected != null && _rows.TryGetValue(selected, out GameTaskRow row) && row != null) ? (RectTransform)row.transform : null;
	}

	// ---- What a row shows and does ----

	internal static string Name(RowKey key)
	{
		if (key.Army == Melee)
		{
			return GameUiState.Text("##gamemenu_taskbar_meleeunits");
		}
		if (key.Army == Ranged)
		{
			return GameUiState.Text("##gamemenu_taskbar_rangedunits");
		}
		if (key.Operable)
		{
			return WorkshopName(GameUiState.Station);
		}
		return GameMenuTexts.TaskName(key.Project);
	}

	internal static string WorkshopName(CraftingStation station)
	{
		string type = GameUiState.TypeOf(station);
		if (type == null)
		{
			return string.Empty;
		}
		string key = type.StartsWith("fy_", StringComparison.Ordinal) ? type.Substring(3).ToLowerInvariant() : type.ToLowerInvariant();
		switch (key)
		{
			case "primitiveworkbench":
				key = "choppingblock";
				break;
			case "anvil":
				key = "forge";
				break;
		}
		return GameUiState.Text("##operable_" + key);
	}

	internal static Sprite Icon(RowKey key)
	{
		if (key.Army == Melee)
		{
			return GameUiState.Icon(IconKeys.UI_Sword);
		}
		if (key.Army == Ranged)
		{
			return GameUiState.Icon(IconKeys.UI_Bow);
		}
		if (key.Operable)
		{
			CraftingStation station = GameUiState.Station;
			return (station != null) ? GameUiState.Icon(station.Icon) : null;
		}
		return GameMenuTexts.TaskIcon(key.Project);
	}

	internal static Color IconColor(RowKey key)
	{
		if (key.Army != 0)
		{
			return LuiStyle.CastleYellow;
		}
		return key.Operable ? LuiStyle.White : GameMenuTexts.TaskIconColor(key.Project);
	}

	internal static bool IsIdle(RowKey key)
	{
		return key.Project != null && key.Project is IdleProject;
	}

	internal static bool Selected(RowKey key)
	{
		if (key.Army != 0)
		{
			return false;
		}
		if (key.Operable)
		{
			return GameUiState.Station != null;
		}
		return GameUiState.Station == null && key.Project == GameUiState.Selected;
	}

	internal static Sprite Priority(RowKey key)
	{
		if (key.Army != 0 || key.Operable || IsIdle(key) || key.Project == null)
		{
			return null;
		}
		// Six levels (PriorityLevels) on the game's four icons: 1-2 low, 3 medium, 4-5 high.
		switch (CastleStoryPlus.WorkerAI.PriorityLevels.Level(key.Project.GetPriority()))
		{
			case 0:
				return GameUiState.Icon(IconKeys._UI_PriorityLocked_alt);
			case 1:
			case 2:
				return GameUiState.Icon(IconKeys._UI_PriorityLow_alt);
			case 3:
				return GameUiState.Icon(IconKeys._UI_PriorityMedium_alt);
			default:
				return GameUiState.Icon(IconKeys._UI_PriorityHigh_alt);
		}
	}

	// The task's priority level (0 to 5), or -1 where no priority shows.
	internal static int PriorityLevel(RowKey key)
	{
		if (Priority(key) == null)
		{
			return -1;
		}
		return CastleStoryPlus.WorkerAI.PriorityLevels.Level(key.Project.GetPriority());
	}

	private static readonly Occupation.Job[] MeleeJobs = new Occupation.Job[2] { Occupation.Job.Halberdier, Occupation.Job.Knight };

	private static readonly Occupation.Job[] RangedJobs = new Occupation.Job[4] { Occupation.Job.Archer, Occupation.Job.Arbalist, Occupation.Job.Artificer, Occupation.Job.Alchemist };

	// The crew's faces: per bricktron its face, lit when selected (at most MaxCrew).
	internal static void Crew(RowKey key, List<Sprite> faces)
	{
		faces.Clear();
		if (key.Army != 0)
		{
			ArmyCrew(key.Army == Melee ? MeleeJobs : RangedJobs, faces, 30);
			return;
		}
		Project project = key.Operable ? ((GameUiState.Station != null) ? GameUiState.Station.GetComponent<Project>() : null) : key.Project;
		int max = IsIdle(key) ? 15 : 8;
		if (project == null)
		{
			return;
		}
		Sprite worker = GameUiState.Icon(IconKeys._Face_Worker);
		Sprite selected = GameUiState.Icon(IconKeys._Face_Worker_Selected);
		int count = 0;
		int lit = 0;
		foreach (Labor labor in project.Crew())
		{
			if (labor != null && labor.Occupation.CurrentJob == Occupation.Job.Builder)
			{
				count++;
				if (labor.selection)
				{
					lit++;
				}
			}
		}
		// The idle group's row has a place for every bricktron, empty for those busy elsewhere.
		int places = IsIdle(key) ? GameActions.CountBricktrons() : count;
		Sprite empty = GameUiState.Icon(IconKeys._Face_Empty);
		for (int n = 0; n < places && n < max; n++)
		{
			faces.Add((n < lit) ? selected : (n < count) ? worker : empty);
		}
	}

	private static void ArmyCrew(Occupation.Job[] jobs, List<Sprite> faces, int max)
	{
		Dictionary<Occupation.Job, int> all = new Dictionary<Occupation.Job, int>();
		Dictionary<Occupation.Job, int> lit = new Dictionary<Occupation.Job, int>();
		if (UIGameObserver.bricktrons == null)
		{
			return;
		}
		foreach (Labor labor in UIGameObserver.bricktrons.CheckCreation())
		{
			if (labor == null)
			{
				continue;
			}
			Occupation.Job job = labor.Occupation.CurrentJob;
			all.TryGetValue(job, out int n);
			all[job] = n + 1;
			if (labor.selection)
			{
				lit.TryGetValue(job, out int s);
				lit[job] = s + 1;
			}
		}
		foreach (Occupation.Job job in jobs)
		{
			all.TryGetValue(job, out int count);
			lit.TryGetValue(job, out int selected);
			Sprite face = JobFace(job, selected: false);
			Sprite litFace = JobFace(job, selected: true);
			for (int i = 0; i < selected && faces.Count < max; i++)
			{
				faces.Add(litFace);
			}
			for (int i = selected; i < count && faces.Count < max; i++)
			{
				faces.Add(face);
			}
		}
	}

	private static Sprite JobFace(Occupation.Job job, bool selected)
	{
		IconKey key;
		switch (job)
		{
			case Occupation.Job.Halberdier:
				key = selected ? IconKeys._Face_Halberdier_Selected : IconKeys._Face_Halberdier;
				break;
			case Occupation.Job.Knight:
				key = selected ? IconKeys._Face_Knight_Selected : IconKeys._Face_Knight;
				break;
			case Occupation.Job.Archer:
				key = selected ? IconKeys._Face_Archer_Selected : IconKeys._Face_Archer;
				break;
			case Occupation.Job.Arbalist:
				key = selected ? IconKeys._Face_Arbalist_Selected : IconKeys._Face_Arbalist;
				break;
			case Occupation.Job.Artificer:
				key = selected ? IconKeys._Face_Artificer_Selected : IconKeys._Face_Artificer;
				break;
			default:
				key = selected ? IconKeys._Face_Alchemist_Selected : IconKeys._Face_Alchemist;
				break;
		}
		return GameUiState.Icon(key);
	}

	// A click on the row's icon: select or deselect the task.
	internal static void Toggle(RowKey key)
	{
		if (key.Army != 0)
		{
			return;
		}
		if (key.Operable)
		{
			UIGameActor.SelectIdleProject();
			UIGameSelector.UnselectAll(SelectionType.Crafting);
			return;
		}
		if (!GameUiState.Exists(key.Project) && !IsIdle(key))
		{
			return;
		}
		bool selected = GameUiState.Selected == key.Project && GameUiState.Station == null;
		if (selected && !IsIdle(key))
		{
			UIGameActor.SelectIdleProject();
		}
		else
		{
			UIGameSelector.Select(key.Project.gameObject);
		}
		if (IsIdle(key) && GameUiState.Station != null)
		{
			UIGameSelector.UnselectAll(SelectionType.Crafting);
		}
	}

	// A click on the row's crew: kind "action" (left), "alt" (right), "shift" or "ctrl".
	internal static void CrewAction(RowKey key, string kind)
	{
		Faction faction = (User.LocalUser != null) ? User.LocalUser.faction : null;
		bool idle = IsIdle(key);
		bool regular = key.Army == 0 && !idle && !key.Operable;
		Project project = key.Project;
		bool own = project != null && (idle || GameUiState.Exists(project));
		switch (kind)
		{
			case "action":
				if (key.Army == Melee)
				{
					UIGameActor.SelectIdleProject();
					UIGameActor.SelectByOccupation(Occupation.Type.Knight, faction);
				}
				else if (key.Army == Ranged)
				{
					UIGameActor.SelectIdleProject();
					UIGameActor.SelectByOccupation(Occupation.Type.Archer, faction);
				}
				else if (own)
				{
					UIGameActor.SelectIdleProject();
					UIGameActor.SelectProjectMember(project, true, true);
				}
				break;
			case "alt":
				if (idle)
				{
					UIGameActor.MakeSelectionFreeAgents();
				}
				else if (regular && own)
				{
					UIGameActor.AssignSelectionToProject(project);
				}
				break;
			case "shift":
				if (idle)
				{
					UIGameActor.SelectIdleProject();
					UIGameActor.SelectProjectMember(project, true, false);
				}
				else if (regular && own)
				{
					User.LocalUser.GetComponent<UNetGroupe>().CallCmdRaisePriority(project.gameObject);
				}
				break;
			case "ctrl":
				if (regular && own)
				{
					User.LocalUser.GetComponent<UNetGroupe>().CallCmdLowerPriority(project.gameObject);
				}
				break;
		}
		GameUiState.Announce();
	}
}

// One row of the task list.
internal class GameTaskRow : MonoBehaviour
{
	private const float FaceSize = 32f;

	private const float FaceWidth = 10f;

	private GameTaskList _list;

	private GameTaskList.RowKey _key;

	private Image _icon;

	private Image _priority;

	// Priority 1 to 5 as five pips, as many lit as the level (PriorityLevels); 0 (locked) keeps the game's icon.
	private readonly Image[] _pips = new Image[5];

	private int _shownLevel = -2;

	private static readonly Color PipOff = new Color(0.25f, 0.25f, 0.25f, 0.9f);

	private Text _title;

	private GameObject _highlight;

	private LayoutElement _crewLayout;

	private RectTransform _crew;

	private Image _taskHover;

	private Image _crewHover;

	private readonly List<Image> _faces = new List<Image>();

	private readonly List<Sprite> _shown = new List<Sprite>();

	public static GameTaskRow Create(GameTaskList list, GameTaskList.RowKey key)
	{
		RectTransform rect = UiKit.CreateRect("Task", list.transform);
		GameTaskRow row = rect.gameObject.AddComponent<GameTaskRow>();
		row._list = list;
		row._key = key;
		row.Build(rect);
		return row;
	}

	private void Build(RectTransform rect)
	{
		Image background = rect.gameObject.AddComponent<Image>();
		UiTheme.Round(background, LuiStyle.BgPanel, UiTheme.ButtonRadius);
		background.raycastTarget = false;
		HorizontalLayoutGroup layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
		layout.padding = new RectOffset(2, 2, 2, 2);
		layout.childAlignment = TextAnchor.LowerLeft;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = false;
		layout.childForceExpandHeight = false;
		LayoutElement element = rect.gameObject.AddComponent<LayoutElement>();
		element.minHeight = GameTaskList.Height;
		element.preferredHeight = GameTaskList.Height;

		RectTransform task = UiKit.CreateRect("Task", rect);
		LayoutElement taskElement = task.gameObject.AddComponent<LayoutElement>();
		taskElement.minWidth = 12f;
		taskElement.preferredWidth = 48f;
		taskElement.minHeight = 48f;
		taskElement.preferredHeight = 48f;
		_icon = Pin("Icon", task, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(48f, 48f));
		_icon.preserveAspect = true;
		_taskHover = Stretch("Hover", task, LuiStyle.FgHover);
		_taskHover.enabled = false;

		Image button = Stretch("Button", rect, Color.clear);
		button.raycastTarget = true;
		CsTaskListPointer taskPointer = button.gameObject.AddComponent<CsTaskListPointer>();
		taskPointer.Enter = (bool on) => _taskHover.enabled = on && _key.Army == 0;
		taskPointer.Click = (PointerEventData.InputButton which, bool shift, bool ctrl) =>
		{
			if (which == PointerEventData.InputButton.Left)
			{
				GameTaskList.Toggle(_key);
				GameUiState.Announce();
			}
			else if (which == PointerEventData.InputButton.Right)
			{
				// As a right-click on the crew: the selected bricktrons are put on the task (AssignNow starts them).
				GameTaskList.CrewAction(_key, "alt");
			}
		};

		_crew = UiKit.CreateRect("Crew", rect);
		HorizontalLayoutGroup crewLayout = _crew.gameObject.AddComponent<HorizontalLayoutGroup>();
		crewLayout.padding = new RectOffset(0, 0, 12, 0);
		crewLayout.childAlignment = TextAnchor.MiddleLeft;
		crewLayout.childControlWidth = true;
		crewLayout.childControlHeight = true;
		crewLayout.childForceExpandWidth = false;
		crewLayout.childForceExpandHeight = false;
		_crewLayout = _crew.gameObject.AddComponent<LayoutElement>();
		_crewLayout.minWidth = 48f;
		_crewLayout.minHeight = 48f;
		_crewLayout.preferredHeight = 48f;
		_crewHover = Stretch("Hover", _crew, LuiStyle.FgHover);
		_crewHover.enabled = false;
		Image crewButton = Stretch("Button", _crew, Color.clear);
		crewButton.raycastTarget = true;
		CsTaskListPointer crewPointer = crewButton.gameObject.AddComponent<CsTaskListPointer>();
		crewPointer.Enter = (bool on) => _crewHover.enabled = on;
		crewPointer.Click = (PointerEventData.InputButton which, bool shift, bool ctrl) =>
		{
			string kind = (which == PointerEventData.InputButton.Right) ? "alt" : shift ? "shift" : ctrl ? "ctrl" : "action";
			if (which == PointerEventData.InputButton.Left || which == PointerEventData.InputButton.Right)
			{
				GameTaskList.CrewAction(_key, kind);
			}
		};

		_title = LuiWidgets.Label(rect, string.Empty, 12, LuiStyle.CastleYellow, TextAnchor.UpperLeft, null);
		_title.raycastTarget = false;
		_title.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
		_title.horizontalOverflow = HorizontalWrapMode.Overflow;
		UiKit.SetRect(_title.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
		_title.rectTransform.offsetMin = new Vector2(8f, 6f);
		_title.rectTransform.offsetMax = new Vector2(-8f, -6f);
		_priority = Pin("Priority", rect, Vector2.one, new Vector2(0f, 6f), new Vector2(32f, 32f));
		_priority.rectTransform.pivot = Vector2.one;
		_priority.enabled = false;
		for (int i = 0; i < _pips.Length; i++)
		{
			// Right to left: the rightmost pip is the fifth.
			Image pip = Pin("Pip " + (_pips.Length - i), rect, Vector2.one, new Vector2(-5f - i * 6f, -4f), new Vector2(4f, 4f));
			pip.rectTransform.pivot = Vector2.one;
			pip.enabled = false;
			_pips[_pips.Length - 1 - i] = pip;
		}
		_highlight = LuiWidgets.Frame(rect, LuiStyle.Highlight, 2f);
	}

	private static Image Pin(string name, Transform parent, Vector2 anchor, Vector2 position, Vector2 size)
	{
		RectTransform rect = UiKit.CreateRect(name, parent);
		rect.anchorMin = rect.anchorMax = anchor;
		rect.anchoredPosition = position;
		rect.sizeDelta = size;
		rect.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
		Image image = rect.gameObject.AddComponent<Image>();
		image.raycastTarget = false;
		return image;
	}

	private static Image Stretch(string name, Transform parent, Color color)
	{
		RectTransform rect = UiKit.CreateRect(name, parent);
		UiKit.SetRect(rect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
		rect.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
		Image image = rect.gameObject.AddComponent<Image>();
		image.color = color;
		image.raycastTarget = false;
		return image;
	}

	internal void Show()
	{
		string name = GameTaskList.Name(_key);
		if (_title.text != name)
		{
			_title.text = name;
		}
		Sprite icon = GameTaskList.Icon(_key);
		_icon.sprite = icon;
		_icon.enabled = icon != null;
		_icon.color = GameTaskList.IconColor(_key);
		float iconSize = (_key.Army != 0 || GameTaskList.IsIdle(_key)) ? 32f : 48f;
		_icon.rectTransform.sizeDelta = new Vector2(iconSize, iconSize);
		bool selected = GameTaskList.Selected(_key);
		if (_highlight.activeSelf != selected)
		{
			_highlight.SetActive(selected);
			_highlight.transform.SetAsLastSibling();
		}
		Sprite priority = GameTaskList.Priority(_key);
		int level = GameTaskList.PriorityLevel(_key);
		_priority.sprite = priority;
		_priority.enabled = priority != null && level <= 0;
		if (level != _shownLevel)
		{
			_shownLevel = level;
			for (int i = 0; i < _pips.Length; i++)
			{
				_pips[i].enabled = level > 0;
				_pips[i].color = (i < level) ? LuiStyle.CastleYellow : PipOff;
			}
		}
		_crewLayout.minWidth = (_key.Army != 0) ? 20f : 48f;
		GameTaskList.Crew(_key, _shown);
		ShowCrew();
	}

	private void ShowCrew()
	{
		int count = _shown.Count;
		while (_faces.Count < count)
		{
			int n = _faces.Count + 1;
			RectTransform slot = UiKit.CreateRect("Face " + n, _crew);
			LayoutElement element = slot.gameObject.AddComponent<LayoutElement>();
			element.preferredWidth = FaceWidth;
			element.preferredHeight = FaceWidth;
			RectTransform face = UiKit.CreateRect("Image", slot);
			face.anchorMin = face.anchorMax = new Vector2(0.5f, 0.5f);
			face.sizeDelta = new Vector2(FaceSize, FaceSize);
			face.anchoredPosition = new Vector2(4f, (n % 2 == 0) ? -10f : 10f);
			Image image = face.gameObject.AddComponent<Image>();
			image.raycastTarget = false;
			_faces.Add(image);
			slot.SetSiblingIndex(n - 1);
		}
		for (int i = 0; i < _faces.Count; i++)
		{
			Image image = _faces[i];
			bool shown = i < count;
			GameObject slot = image.transform.parent.gameObject;
			if (slot.activeSelf != shown)
			{
				slot.SetActive(shown);
			}
			if (shown)
			{
				image.sprite = _shown[i];
				image.enabled = _shown[i] != null;
			}
		}
	}

	private void OnDisable()
	{
		if (_taskHover != null)
		{
			_taskHover.enabled = false;
			_crewHover.enabled = false;
		}
	}
}

// The selected task's popup: its sections stacked in a panel over the task list, above the selected row. Each
// section has a small grey caption in capitals and a thin line above it; a section laid out as rows draws its
// buttons as wide rows (CsListRowView).
internal class CsPopupView : MonoBehaviour
{
	private const float Width = 320f;

	private UiPanel _panel;

	private RectTransform _rect;

	private sealed class SectionView
	{
		public UiSection Section;

		public RectTransform Holder;

		public GameObject Line;
	}

	private readonly List<SectionView> _sections = new List<SectionView>();

	public static CsPopupView Create(Transform parent, UiPanel panel)
	{
		RectTransform rect = UiKit.CreateRect("CS task popup", parent);
		rect.anchorMin = rect.anchorMax = new Vector2(0f, 0f);
		rect.pivot = new Vector2(0f, 0f);
		rect.anchoredPosition = new Vector2(CsPanelView.BarWidth, GameTaskList.Height + 4f);
		Image background = rect.gameObject.AddComponent<Image>();
		UiTheme.Round(background, UiTheme.BgPopup, UiTheme.PanelRadius);
		VerticalLayoutGroup layout = rect.gameObject.AddComponent<VerticalLayoutGroup>();
		layout.padding = new RectOffset(10, 10, 8, 8);
		layout.spacing = 7f;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = false;
		ContentSizeFitter fitter = rect.gameObject.AddComponent<ContentSizeFitter>();
		fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
		rect.sizeDelta = new Vector2(Width, 0f);
		CsPopupView view = rect.gameObject.AddComponent<CsPopupView>();
		view._panel = panel;
		view._rect = rect;
		view._drag = CsWindowDrag.Add(rect, "task popup");
		foreach (UiSection section in panel.Sections)
		{
			GameObject line = LuiWidgets.Divider(rect, vertical: false).gameObject;
			line.GetComponent<Image>().color = UiTheme.Line;
			RectTransform holder = LuiWidgets.Vertical(rect, 4f, 0);
			FillWidth(holder);
			if (section.Title != null)
			{
				Text caption = LuiWidgets.Label(holder, string.Empty, 10, UiTheme.TextMuted, TextAnchor.MiddleLeft, LuiStyle.Bold);
				CsSectionCaption reader = caption.gameObject.AddComponent<CsSectionCaption>();
				reader.Upper = true;
				reader.Section = section;
			}
			bool buttons = section.Items.TrueForAll((UiItem i) => i is UiButton);
			bool rows = section.Layout == UiLayout.Rows;
			float cell = Mathf.Min(48f, section.CellSize);
			Transform body = (buttons && !rows) ? (Transform)LuiWidgets.Grid(holder, new Vector2(cell, cell), 0f, Mathf.FloorToInt((Width - 20f) / cell)) : LuiWidgets.Vertical(holder, 2f, 0);
			if (rows || !buttons)
			{
				FillWidth((RectTransform)body);
			}
			// In a section of mixed items (a bar and buttons: the crystal's energy and spawn), buttons in a run share
			// a grid row instead of standing in a column.
			Transform run = null;
			foreach (UiItem item in section.Items)
			{
				if (rows && item is UiButton button)
				{
					CsListRowView.Create(body, button);
					continue;
				}
				if (!buttons && item is UiButton)
				{
					if (run == null)
					{
						run = LuiWidgets.Grid(body, new Vector2(cell, cell), 0f, Mathf.FloorToInt((Width - 20f) / cell));
					}
					CsItemFactory.Create(run, item, cell, LuiIconButton.Side.Below, catalogue: true);
					continue;
				}
				run = null;
				CsItemFactory.Create(body, item, cell, LuiIconButton.Side.Below, catalogue: true);
			}
			view._sections.Add(new SectionView { Section = section, Holder = holder, Line = line });
		}
		GameUiState.Changed += view.Read;
		view.Read();
		return view;
	}

	// A part laid out by the popup across its whole width, not shrunk to what it holds (its fitter would fight the
	// popup's layout and leave it centred), its children stretched across it.
	private static void FillWidth(RectTransform rect)
	{
		ContentSizeFitter fitter = rect.GetComponent<ContentSizeFitter>();
		if (fitter != null)
		{
			DestroyImmediate(fitter);
		}
		VerticalLayoutGroup layout = rect.GetComponent<VerticalLayoutGroup>();
		if (layout != null)
		{
			layout.childForceExpandWidth = true;
		}
	}

	private float _readAt;

	private bool _covered;

	private CsWindowDrag _drag;

	private void Update()
	{
		// At once when a building's window opens or closes in its place, not up to a quarter second later.
		if (Time.unscaledTime - _readAt > 0.25f || _covered != CsStationWindow.Covering)
		{
			Read();
		}
		Place();
	}

	private void Read()
	{
		_readAt = Time.unscaledTime;
		_covered = CsStationWindow.Covering;
		bool any = false;
		foreach (SectionView view in _sections)
		{
			// A building's own window (research station, market) stands in the popup's place.
			bool visible = !CsStationWindow.Covering && view.Section.IsVisible();
			if (view.Holder.gameObject.activeSelf != visible)
			{
				view.Holder.gameObject.SetActive(visible);
			}
			// A line between sections: above every shown section but the first.
			bool line = visible && any;
			if (view.Line.activeSelf != line)
			{
				view.Line.SetActive(line);
			}
			any |= visible;
		}
		Image background = GetComponent<Image>();
		background.enabled = any;
		if (any)
		{
			CsItemViews.MarkAll();
		}
	}

	// Above the selected row of the task list, kept on screen; where the player moved it, if moved (CsWindowDrag).
	private void Place()
	{
		if (_drag.TryPlace(out Vector2 moved))
		{
			_rect.anchoredPosition = moved;
			return;
		}
		GameTaskList list = GameTaskList.Current;
		RectTransform row = (list != null) ? list.SelectedRow() : null;
		// Not laid out yet (the frame the menu is built, e.g. after a UI reload): no place to stand over.
		if (row == null || row.rect.width <= 0f)
		{
			return;
		}
		RectTransform parent = (RectTransform)_rect.parent;
		Vector3[] corners = new Vector3[4];
		row.GetWorldCorners(corners);
		Vector2 left = parent.InverseTransformPoint(corners[1]);
		float x = left.x - parent.rect.xMin;
		// Beside an open sidebar, not over it: on a small screen the popup covered the build menu's right-hand
		// structures. When the screen is too narrow for both, the left sidebar is kept clear.
		float min = CsPanelView.BarWidth + ((CsPanelView.LeftOpen > 0) ? CsPanelView.Width + 4f : 0f);
		float max = parent.rect.width - CsPanelView.BarWidth - ((CsPanelView.RightOpen > 0) ? CsPanelView.Width + 4f : 0f) - Width;
		x = Mathf.Max(Mathf.Min(x, max), min);
		_rect.anchoredPosition = new Vector2(x, GameTaskList.Height + 4f);
	}

	private void OnDestroy()
	{
		GameUiState.Changed -= Read;
	}
}

// A section caption that reads its section's title (some count what the section holds): at once, then four times a
// second by the clock (the game's pause does not hold it back).
internal class CsSectionCaption : MonoBehaviour
{
	private UiSection _section;

	// Shown in capitals (the popup's captions).
	public bool Upper;

	private Text _text;

	private float _readAt;

	public UiSection Section
	{
		get { return _section; }
		set
		{
			_section = value;
			Read();
		}
	}

	private void OnEnable()
	{
		Read();
	}

	private void Update()
	{
		if (Time.unscaledTime - _readAt >= 0.25f)
		{
			Read();
		}
	}

	private void Read()
	{
		_readAt = Time.unscaledTime;
		if (_text == null)
		{
			_text = GetComponent<Text>();
		}
		if (_text == null)
		{
			return;
		}
		string title = string.Empty;
		try
		{
			title = (_section != null && _section.Title != null) ? _section.Title() : string.Empty;
		}
		catch (System.Exception)
		{
			// Its section is not shown (no workshop selected...): nothing to read.
		}
		if (Upper)
		{
			title = title.ToUpperInvariant();
		}
		if (_text.text != title)
		{
			_text.text = title;
		}
		_text.enabled = !string.IsNullOrEmpty(title);
	}
}
