using System;
using System.Collections.Generic;
using CastleStoryPlus.Core;
using CastleStoryPlus.UI;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CastleStoryPlus.LuaUi;

// [LuaUi] CsTaskList: the task list along the bottom of the screen (Menu_TaskList.lua with a
// TaskbarProjectCrewPanel per task) drawn in C#. The game built a pool of 20+ task panels, each with up to 8 to 30
// crew faces and their hover panels, when a map loads. GameMenu's task list block is cut out and its menu field gets
// a small Lua table instead (LuaSide), which keeps what the Lua menu kept: the tasks in their slots (a new task
// takes the first free one, as the panel pool did), the same hooks, and the same calls when a task or its crew is
// clicked (select, assign the selection, raise or lower the priority...), so it behaves as before, in multiplayer
// too. The C# view asks it for the rows when a hook said something changed (and twice a second besides) and draws
// them. The task popup and the task tooltip, still the game's Lua menus, are pointed at the C# rows through
// LuaAnchors.
[Feature(Features.CsMenus, Features.CsMenusInfo)]
internal static class CsTaskList
{
	// GameMenu's menu_projectList: the task list's data and actions, kept in GameMenu's own script. The ids are the
	// Lua menu's: the idle group 1, the selected workshop 2 (Data.Project.operableId), the melee and ranged units
	// 111 and 112 (Data.Project.knightId / archerId), and the tasks in the slots between.
	private const string LuaSide = @"(function()
_m.projectMenuHeight = 72
local P = Data.Project
local tl = { slots = {}, slotOf = {}, anchors = {}, pending = {}, hasPending = false }
local _operable = {}

local function Dirty() CastleStoryPlus.TaskListDirty() end

local function Fire(menu, name, arg)
	if type(menu) ~= ""table"" then return end
	local e = rawget(menu, name)
	if type(e) ~= ""table"" then return end
	if arg ~= nil then e:Invoke(arg) else e:Invoke() end
end

local function IsVisible(menu)
	if type(menu) ~= ""table"" then return false end
	local v = rawget(menu, ""Visible"")
	if type(v) == ""function"" then return v() and true or false end
	return false
end

local function Anchor(id, part)
	if id == nil then return nil end
	local key = tostring(id) .. part
	local a = tl.anchors[key]
	if a == nil then
		a = CastleStoryPlus.TaskListAnchor(id, part)
		tl.anchors[key] = a
	end
	return a
end

local function Register(o)
	if o == nil or tl.slotOf[o] ~= nil then return end
	local id = 1
	while tl.slots[id] ~= nil or id == P.knightId or id == P.archerId do id = id + 1 end
	tl.slots[id] = o
	tl.slotOf[o] = id
end

local function Unregister(o)
	local id = o and tl.slotOf[o]
	if id == nil then return end
	tl.slotOf[o] = nil
	tl.slots[id] = nil
end

local function Exists(id)
	if id == P.idleId then return true end
	if id == P.operableId then return Data.Operable:HasSelected() and true or false end
	if id == P.knightId or id == P.archerId then return true end
	local o = tl.slots[id]
	if o == nil or IsNilOrReleased(o) then return false end
	return true
end

local function GetProject(id)
	if not Exists(id) then return nil end
	if id == P.operableId then return Data.Operable:GetSelected() end
	return tl.slots[id]
end

local function GetSelected()
	local project = P:GetSelected()
	if project and not IsNilOrReleased(project) then return project end
	return P:GetIdle()
end

local function IsProjectSelected(id)
	if id == P.knightId or id == P.archerId then return false end
	if id == P.operableId then return Data.Operable:HasSelected() and true or false end
	if Data.Operable:HasSelected() then return false end
	if not Exists(id) then return false end
	return GetProject(id) == GetSelected()
end

local function CrewCount(id)
	if not Exists(id) then return -1 end
	return P:GetAllBuildersCount(GetProject(id))
end

local function Name(id)
	if id == P.knightId then return GetLocalized(""##gamemenu_taskbar_meleeunits"") end
	if id == P.archerId then return GetLocalized(""##gamemenu_taskbar_rangedunits"") end
	if id == P.operableId then
		local operableType = Data.Operable:GetSelectedType()
		if not operableType then return ""typeless operable"" end
		return Data.Operable:GetName(operableType)
	end
	if not Exists(id) then return """" end
	local project = GetProject(id)
	if not project then return """" end
	return P:GetName(project.type)
end

local function PrioritySprite(id)
	if not Exists(id) or id == P.operableId then return nil end
	local priority = Project.GetPriority(GetProject(id))
	if priority == Priority.Low then return IconKeys._UI_PriorityLow_alt:Get64() end
	if priority == Priority.Medium then return IconKeys._UI_PriorityMedium_alt:Get64() end
	if priority == Priority.High then return IconKeys._UI_PriorityHigh_alt:Get64() end
	if priority == Priority.Locked then return IconKeys._UI_PriorityLocked_alt:Get64() end
	return nil
end

local function IconSprite(id)
	if id == P.knightId then return IconKeys.UI_Sword:Get64() end
	if id == P.archerId then return IconKeys.UI_Bow:Get64() end
	local project = GetProject(id)
	if not project then return IconKeys.UI_Help:Get64() end
	if id == P.operableId then return Data.Operable:GetIcon(project.type) end
	if project.type == fy_HarvestProject then return P:GetHarvestIcon(project) end
	return P:GetIcon(project.type)
end

local function IconSize(id)
	if id == P.knightId or id == P.archerId or id == P.idleId then return 32 end
	return 48
end

local function IconColor(id)
	if id == P.operableId then return White end
	if id == P.knightId or id == P.archerId then return CastleYellow end
	local project = GetProject(id)
	if not project then return White end
	if project.type == fy_HarvestProject then return P:GetHarvestIconColor(P:GetHarvestIcon(project)) end
	return P:GetIconColor(project.type)
end

local function MaxCrew(id)
	if id == P.idleId then return 15 end
	if id == P.knightId or id == P.archerId then return 30 end
	return 8
end

local function HasCrew(id, n)
	if id == P.idleId then return n <= P:GetAllIdlersCount() end
	if id == P.knightId then return n <= UIGame.CountMelee() end
	if id == P.archerId then return n <= UIGame.CountRanged() end
	return n <= CrewCount(id)
end

local function Faces(n, jobs)
	local i = 0
	for k = 1, #jobs, 3 do
		i = i + P:GetSelectedFightersCountAtJob(jobs[k])
		if n <= i then return jobs[k + 2] end
		i = i + P:GetUnselectedFightersCountAtJob(jobs[k])
		if n <= i then return jobs[k + 1] end
	end
	return nil
end

local function CrewSprite(id, n)
	if id == P.operableId then return IconKeys._Face_Worker:Get64() end
	if id == P.knightId then
		return Faces(n, { Jobs.Halberdier, IconKeys._Face_Halberdier:Get64(), IconKeys._Face_Halberdier_Selected:Get64(),
			Jobs.Knight, IconKeys._Face_Knight:Get64(), IconKeys._Face_Knight_Selected:Get64() })
	end
	if id == P.archerId then
		return Faces(n, { Jobs.Archer, IconKeys._Face_Archer:Get64(), IconKeys._Face_Archer_Selected:Get64(),
			Jobs.Arbalist, IconKeys._Face_Arbalist:Get64(), IconKeys._Face_Arbalist_Selected:Get64(),
			Jobs.Artificer, IconKeys._Face_Artificer:Get64(), IconKeys._Face_Artificer_Selected:Get64(),
			Jobs.Alchemist, IconKeys._Face_Alchemist:Get64(), IconKeys._Face_Alchemist_Selected:Get64() })
	end
	local project = GetProject(id)
	if not project then return IconKeys._Face_Worker:Get64() end
	if n <= P:GetAllBuildersCount(project) then
		if n <= P:GetSelectedBuildersCount(project) then return IconKeys._Face_Worker_Selected:Get64() end
		return IconKeys._Face_Worker:Get64()
	end
	return IconKeys._Face_Empty:Get64()
end

local function Fill(r, id, popupVisible)
	r.name = Name(id)
	r.icon = IconSprite(id)
	r.iconColor = IconColor(id)
	r.iconSize = IconSize(id)
	r.selected = IsProjectSelected(id)
	local army = id == P.knightId or id == P.archerId
	local project = GetProject(id)
	r.down = (not army and project ~= nil and P:IsSelected(project)) and true or false
	r.popupArrow = popupVisible and r.selected
	if not army and id ~= P.idleId and id ~= P.operableId then r.priority = PrioritySprite(id) end
	local crew = {}
	for n = 1, MaxCrew(id) do
		if not HasCrew(id, n) then break end
		crew[n] = CrewSprite(id, n) or false
	end
	r.crew = crew
end

function tl.Rows()
	local popupVisible = IsVisible(tl.popup)
	local ids = {}
	for id, _ in pairs(tl.slots) do table.insert(ids, id) end
	table.sort(ids)
	table.insert(ids, P.knightId)
	table.insert(ids, P.archerId)
	local rows = {}
	for i = 1, #ids do
		local id = ids[i]
		local r = { id = id, exists = Exists(id) }
		if r.exists then
			local ok, err = pcall(Fill, r, id, popupVisible)
			if not ok then
				r.exists = false
				r.err = tostring(err)
			end
		end
		rows[i] = r
	end
	return rows
end

function tl.Sprites()
	return { arrow = IconKeys._UI_Big_Arrow_Down:Get64(), up = IconKeys.UI_Up:Get64(), down = IconKeys.UI_Down:Get64(),
		select = IconKeys.UI_Select:Get64(), plus = IconKeys.UI_Plus:Get64(), minus = IconKeys.UI_Minus:Get64(),
		knight = P.knightId, archer = P.archerId, idle = P.idleId, operable = P.operableId }
end

function tl.Toggle(id)
	if id == P.operableId then Project.Deselect() end
	if id == P.knightId or id == P.archerId then return end
	if not Exists(id) then return end
	local dst = GetProject(id)
	local src = P:GetSelected()
	P:ToggleSelection(dst)
	if id == P.idleId and Data.Operable:HasSelected() then Data.Operable:DeselectAll() end
	if src ~= dst then Fire(tl.tooltipTask, ""ev_onReset"") end
	Dirty()
end

-- 0: no hover, 1: hovered, 2: hovered and the task tooltip shown.
function tl.TaskHover(id, on)
	if not on then
		Fire(tl.tooltipTask, ""ev_onReset"")
		return 0
	end
	if P:IsSelectedOfTypeIdle() and id == P.idleId then return 0 end
	if id == P.knightId or id == P.archerId then return 1 end
	local path = IsProjectSelected(id) and { ""Task"", ""All"", ""Deselect"" } or { ""Task"", ""All"", ""Select"" }
	Fire(tl.tooltipTask, ""ev_onSet"", { targetPanel = Anchor(id, ""task""), tooltip = path })
	return IsVisible(tl.tooltipTask) and 2 or 1
end

-- mode: """" (left), ""select"", ""shift"" or ""ctrl""; whether the task tooltip is shown.
function tl.CrewHover(id, mode)
	local path = nil
	if mode == ""ctrl"" then path = { ""Task"", ""All"", ""LowerPriority"" }
	elseif mode == ""shift"" then path = { ""Task"", ""All"", ""RaisePriority"" }
	elseif mode == ""select"" then path = { ""Task"", ""All"", ""SelectCrew"" } end
	if path then
		Fire(tl.tooltipTask, ""ev_onSet"", { targetPanel = Anchor(id, ""crew""), tooltip = path })
	else
		Fire(tl.tooltipTask, ""ev_onReset"")
	end
	return IsVisible(tl.tooltipTask)
end

-- kind: ""action"" (left click), ""alt"" (right click), ""shift"" or ""ctrl"" (left click with it).
function tl.CrewAction(id, kind)
	local regular = id ~= P.idleId and id ~= P.knightId and id ~= P.archerId
	local own = Exists(id) and id ~= P.operableId
	if kind == ""action"" then
		if id == P.knightId then Project.SelectMeleeUnits(Data.Faction:GetAllied())
		elseif id == P.archerId then Project.SelectRangedUnits(Data.Faction:GetAllied())
		elseif own then Project.SelectWorkers(GetProject(id), true, true) end
	elseif kind == ""alt"" then
		if id == P.idleId then
			if own then Project.UnassignSelection(GetProject(id)) end
		elseif regular and own then
			Project.AssignSelection(GetProject(id))
		end
	elseif kind == ""shift"" then
		if id == P.idleId then
			if own then Project.SelectWorkers(GetProject(id), true, false) end
		elseif regular and own then
			Project.RaisePriority(GetProject(id))
		end
	elseif kind == ""ctrl"" then
		if regular and own then Project.LowerPriority(GetProject(id)) end
	end
	Dirty()
end

-- What changed since the last frame is collapsed, as the Lua menu did on hk_Update.
function tl.Flush()
	if not tl.hasPending then return end
	local projects = tl.pending
	tl.pending = {}
	tl.hasPending = false
	P:OnSetFightersInfo()
	P:OnSetIdlersInfo()
	P:OnSetBuildersInfoMany(projects)
end

local function Pend(project)
	if project ~= nil then tl.pending[project] = true end
	tl.hasPending = true
	Dirty()
end

local function OnSelectProject()
	local isFirstRun = not tl.selectedProject
	local src = tl.selectedProject or P:GetIdle()
	local dst = P:GetSelected()
	tl.selectedProject = dst
	if isFirstRun or src ~= dst then
		local id = dst and tl.slotOf[dst]
		tl.popupId = id
		if id then Fire(tl.popup, ""ev_onSet"", { targetPanel = Anchor(id, ""row"") }) else Fire(tl.popup, ""ev_onReset"") end
	end
	Dirty()
end

local function OnSetSelectedOperable()
	local selectedId = tl.slotOf[P:GetSelected()]
	local id = Data.Operable:HasSelected() and P.operableId or selectedId
	tl.popupId = id
	Fire(tl.popup, ""ev_onSet"", { targetPanel = Anchor(id, ""row"") })
	Dirty()
end

tl.SetPopupMenu = function(m) tl.popup = m return tl end
tl.SetTaskTooltipMenu = function(m) tl.tooltipTask = m return tl end
tl.SetTooltipMenu = function(m) tl.tooltip = m return tl end
tl.AddMenuHandleToggle = function(h) return tl end

tl.Begin = function()
	Register(P:GetIdle())
	Register(_operable)
	local projects = P:GetAll()
	for i = 1, #projects do Register(projects[i]) end
	P:OnSetFightersInfo()
	P:OnSetIdlersInfo()
	P:OnSetBuildersInfoMany(ToMap(projects))

	local k = hk_OnScriptKilled
	Hooks.Connect(Picking.hk_OnSetPickerOptions, function() Dirty() end, k)
	Hooks.Connect(Project.hk_OnProjectStarted, function(project)
		P:OnSetBuildersInfo(project)
		if not IsNilOrReleased(project) and not Project.IsBeingPlaced(project) and P:TypeExists(project.type) then Register(project) end
		Pend(project)
	end, k)
	Hooks.Connect(Project.hk_OnProjectDeleted, function(project)
		P:OnResetBuildersInfo(project)
		Unregister(project)
		Dirty()
	end, k)
	Hooks.Connect(Project.hk_OnProjectSelected, function(project) OnSelectProject() end, k)
	Hooks.Connect(Project.hk_OnAddLabor2, function(project) Pend(project) end, k)
	Hooks.Connect(Project.hk_OnRemoveLabor2, function(project) Pend(project) end, k)
	Hooks.Connect(Project.hk_OnSetCrewInfo, function(projects)
		if type(projects) == ""table"" then
			for p, _ in pairs(ToMap(projects)) do tl.pending[p] = true end
		end
		Pend(nil)
	end, k)
	Hooks.Connect(UIGame.hk_OnOccupationChanged, function(project) Pend(project) end, k)
	Data.Operable.ev_onSetSelected:AddListener(OnSetSelectedOperable)

	CastleStoryPlus.TaskListBegin(tl)
	if P:HasSelected() then OnSelectProject() end
	return tl
end

return tl
end)()";

	private static void Enable()
	{
		if (!LuaUiConfig.CsTaskList)
		{
			return;
		}
		LuaInjection.AddFunction("TaskListBegin", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			if (args.Count > 0 && args[0].Type == DataType.Table)
			{
				CsTaskListView.Show(args[0].Table, context.OwnerScript);
			}
			return DynValue.Void;
		});
		LuaInjection.AddFunction("TaskListDirty", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			CsTaskListView.MarkDirty();
			return DynValue.Void;
		});
		LuaInjection.AddFunction("TaskListAnchor", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			int id = (args.Count > 0 && args[0].Type == DataType.Number) ? (int)args[0].Number : 0;
			string part = (args.Count > 1 && args[1].Type == DataType.String) ? args[1].String : "row";
			Transform place = CsTaskListView.Place(id, part);
			return (place != null) ? DynValue.NewTable(LuaAnchors.Create(context.OwnerScript, place)) : DynValue.Nil;
		});
		GameMenuLink.ReplaceMenuBlock(Features.CsMenus, "LUI/Menus/GameMenu.lua", "---lyt: taskList menu\ndo\n", "menu_projectList", LuaSide);
		GameSession.OnLeave(CsTaskListView.Drop);
	}
}

// The task list: a row per task along the bottom of the screen between the side bars, the melee and ranged units
// at the right end. Rows are drawn again when the Lua side said something changed, and twice a second besides
// (a priority or a selection the hooks do not tell about).
internal class CsTaskListView : MonoBehaviour
{
	private const float Height = 72f;

	private const float RefreshSeconds = 0.5f;

	private static CsTaskListView _current;

	private static bool _dirty;

	private Table _side;

	private Script _script;

	private readonly Dictionary<int, CsTaskRow> _rows = new Dictionary<int, CsTaskRow>();

	private RectTransform _spacer;

	private float _refreshAt;

	internal int KnightId = 111;

	internal int ArcherId = 112;

	internal int IdleId = 1;

	internal int OperableId = 2;

	internal Sprite Arrow;

	internal Sprite HoverUp;

	internal Sprite HoverDown;

	internal Sprite CrewSelect;

	internal Sprite CrewPlus;

	internal Sprite CrewMinus;

	public static void MarkDirty()
	{
		_dirty = true;
	}

	public static void Show(Table side, Script script)
	{
		Drop();
		RectTransform rect = UiKit.CreateRect("CS task list", LuiCanvas.Instance.Menus);
		rect.anchorMin = new Vector2(0f, 0f);
		rect.anchorMax = new Vector2(1f, 0f);
		rect.pivot = new Vector2(0.5f, 0f);
		rect.offsetMin = new Vector2(49f, 0f);
		rect.offsetMax = new Vector2(-49f, Height);
		HorizontalLayoutGroup layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
		layout.spacing = 1f;
		layout.childAlignment = TextAnchor.LowerLeft;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = false;
		layout.childForceExpandHeight = false;
		_current = rect.gameObject.AddComponent<CsTaskListView>();
		_current._side = side;
		_current._script = script;
		_current._spacer = UiKit.CreateRect("Spacer", rect);
		LayoutElement spacer = _current._spacer.gameObject.AddComponent<LayoutElement>();
		spacer.flexibleWidth = 1f;
		spacer.minHeight = 48f;
		_dirty = true;
		if (script != null)
		{
			script.OnScriptKilled += OnScriptKilled;
		}
		Plugin.Log.LogInfo("CsMenus: the task list is drawn in C#");
	}

	private static void OnScriptKilled(Script script)
	{
		if (_current != null && _current._script == script)
		{
			Drop();
		}
	}

	public static void Drop()
	{
		if (_current == null)
		{
			return;
		}
		if (_current._script != null)
		{
			_current._script.OnScriptKilled -= OnScriptKilled;
			LuaAnchors.Forget(_current._script);
		}
		Destroy(_current.gameObject);
		_current = null;
	}

	// Where a part of a task's row is ("row", "task" or "crew"), for the Lua menus that follow it; the row is made
	// at once when the Lua side asks before the view drew it.
	public static Transform Place(int id, string part)
	{
		if (_current == null || id == 0)
		{
			return null;
		}
		CsTaskRow row = _current.Row(id);
		switch (part)
		{
		case "task":
			return row.Task;
		case "crew":
			return row.Crew;
		default:
			return row.transform;
		}
	}

	internal Table Side
	{
		get { return _side; }
	}

	private CsTaskRow Row(int id)
	{
		if (!_rows.TryGetValue(id, out CsTaskRow row) || row == null)
		{
			row = CsTaskRow.Create(this, id);
			row.gameObject.SetActive(false);
			_rows[id] = row;
		}
		return row;
	}

	private void Update()
	{
		if (_side == null || !_side.isAlive)
		{
			return;
		}
		if (Arrow == null)
		{
			ReadSprites();
		}
		if (_dirty)
		{
			_dirty = false;
			CallSide("Flush");
			Refresh();
		}
		else if (Time.unscaledTime >= _refreshAt)
		{
			Refresh();
		}
	}

	private void ReadSprites()
	{
		DynValue value = CallSide("Sprites");
		Table sprites = (value.Type == DataType.Table) ? value.Table : null;
		if (sprites == null)
		{
			return;
		}
		Arrow = LuaBridge.Sprite(sprites.Get("arrow"));
		HoverUp = LuaBridge.Sprite(sprites.Get("up"));
		HoverDown = LuaBridge.Sprite(sprites.Get("down"));
		CrewSelect = LuaBridge.Sprite(sprites.Get("select"));
		CrewPlus = LuaBridge.Sprite(sprites.Get("plus"));
		CrewMinus = LuaBridge.Sprite(sprites.Get("minus"));
		KnightId = (int)LuaBridge.Number(sprites.Get("knight"), KnightId);
		ArcherId = (int)LuaBridge.Number(sprites.Get("archer"), ArcherId);
		IdleId = (int)LuaBridge.Number(sprites.Get("idle"), IdleId);
		OperableId = (int)LuaBridge.Number(sprites.Get("operable"), OperableId);
	}

	internal DynValue CallSide(string name, params DynValue[] args)
	{
		return LuaBridge.Call(LuaBridge.Get(_side, name), "task list " + name, args);
	}

	private void Refresh()
	{
		_refreshAt = Time.unscaledTime + RefreshSeconds;
		DynValue value = CallSide("Rows");
		if (value.Type != DataType.Table)
		{
			return;
		}
		Table rows = value.Table;
		HashSet<int> seen = new HashSet<int>();
		int sibling = 0;
		for (int i = 1; i <= rows.Length; i++)
		{
			DynValue entry = rows.Get(i);
			if (entry.Type != DataType.Table)
			{
				continue;
			}
			Table data = entry.Table;
			int id = (int)LuaBridge.Number(data.Get("id"), 0);
			if (id == 0)
			{
				continue;
			}
			seen.Add(id);
			DynValue error = data.Get("err");
			if (!error.IsNil())
			{
				LuaBridge.LogOnce("task list row", LuaBridge.String(error, string.Empty));
			}
			bool exists = LuaBridge.Bool(data.Get("exists"), false);
			if (!exists && !_rows.ContainsKey(id))
			{
				continue;
			}
			CsTaskRow row = Row(id);
			if (id == KnightId)
			{
				_spacer.SetSiblingIndex(sibling++);
			}
			row.transform.SetSiblingIndex(sibling++);
			if (row.gameObject.activeSelf != exists)
			{
				row.gameObject.SetActive(exists);
			}
			if (exists)
			{
				row.Show(data);
			}
		}
		// Rows of tasks that are gone (deleted, or their slot is free).
		foreach (KeyValuePair<int, CsTaskRow> pair in _rows)
		{
			if (!seen.Contains(pair.Key) && pair.Value != null && pair.Value.gameObject.activeSelf)
			{
				pair.Value.gameObject.SetActive(false);
			}
		}
	}

	private void OnDestroy()
	{
		if (_current == this)
		{
			_current = null;
		}
	}
}

// One task's row: its icon (a click selects or deselects the task, as the whole row's button did), its crew's
// faces (a click selects the crew; right click assigns the selected bricktrons; Shift and Ctrl raise and lower the
// priority), its name and its priority.
internal class CsTaskRow : MonoBehaviour
{
	private const float FaceSize = 32f;

	private const float FaceWidth = 10f;

	private CsTaskListView _list;

	private int _id;

	private GameObject _highlight;

	private Image _icon;

	private Image _taskHoverBg;

	private Image _taskHoverIcon;

	private Image _crewHoverBg;

	private Image _crewHoverIcon;

	private Image _popupArrow;

	private Image _taskArrow;

	private Image _crewArrow;

	private Image _priority;

	private Text _title;

	private LayoutElement _crewLayout;

	private readonly List<Image> _faces = new List<Image>();

	private bool _taskHovered;

	private bool _crewHovered;

	private string _crewMode = string.Empty;

	private bool _down;

	public RectTransform Task { get; private set; }

	public RectTransform Crew { get; private set; }

	private bool Army
	{
		get { return _id == _list.KnightId || _id == _list.ArcherId; }
	}

	// Raise and lower priority (and their icons) are for tasks, not the idle group or the units.
	private bool Regular
	{
		get { return !Army && _id != _list.IdleId; }
	}

	public static CsTaskRow Create(CsTaskListView list, int id)
	{
		RectTransform rect = UiKit.CreateRect("Task " + id, list.transform);
		CsTaskRow row = rect.gameObject.AddComponent<CsTaskRow>();
		row._list = list;
		row._id = id;
		row.Build(rect);
		return row;
	}

	private void Build(RectTransform rect)
	{
		Image background = rect.gameObject.AddComponent<Image>();
		background.color = LuiStyle.BgPanel;
		background.raycastTarget = false;
		HorizontalLayoutGroup layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
		layout.padding = new RectOffset(2, 2, 2, 2);
		layout.spacing = 0f;
		layout.childAlignment = TextAnchor.LowerLeft;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = false;
		layout.childForceExpandHeight = false;
		LayoutElement element = rect.gameObject.AddComponent<LayoutElement>();
		element.minHeight = 72f;
		element.preferredHeight = 72f;

		// The task's icon, with its hover.
		Task = UiKit.CreateRect("Task", rect);
		LayoutElement task = Task.gameObject.AddComponent<LayoutElement>();
		task.minWidth = 12f;
		task.preferredWidth = 48f;
		task.minHeight = 48f;
		task.preferredHeight = 48f;
		_icon = Pin("Icon", Task, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(48f, 48f));
		_icon.preserveAspect = true;
		_taskHoverBg = Stretch("HoverBg", Task, LuiStyle.FgHover);
		_taskHoverBg.enabled = false;
		_taskHoverIcon = Pin("HoverIcon", Task, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(32f, 32f));
		_taskHoverIcon.color = LuiStyle.CastleYellow;
		_taskHoverIcon.enabled = false;

		// The whole row is the task's button; the crew's button lies over it.
		Image button = Stretch("Button", rect, Color.clear);
		button.raycastTarget = true;
		CsTaskListPointer taskPointer = button.gameObject.AddComponent<CsTaskListPointer>();
		taskPointer.Enter = (bool on) => SetTaskHover(on);
		taskPointer.Click = (PointerEventData.InputButton which, bool shift, bool ctrl) =>
		{
			if (which == PointerEventData.InputButton.Left)
			{
				_list.CallSide("Toggle", DynValue.NewNumber(_id));
			}
		};

		// The crew's faces, two rows deep, each 10 pixels on.
		Crew = UiKit.CreateRect("Crew", rect);
		HorizontalLayoutGroup crewLayout = Crew.gameObject.AddComponent<HorizontalLayoutGroup>();
		crewLayout.padding = new RectOffset(0, 0, 12, 0);
		crewLayout.childAlignment = TextAnchor.MiddleLeft;
		crewLayout.childControlWidth = true;
		crewLayout.childControlHeight = true;
		crewLayout.childForceExpandWidth = false;
		crewLayout.childForceExpandHeight = false;
		_crewLayout = Crew.gameObject.AddComponent<LayoutElement>();
		_crewLayout.minWidth = 48f;
		_crewLayout.minHeight = 48f;
		_crewLayout.preferredHeight = 48f;
		_crewHoverBg = Stretch("HoverBg", Crew, LuiStyle.FgHover);
		_crewHoverBg.enabled = false;
		_crewHoverIcon = Pin("HoverIcon", Crew, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(32f, 32f));
		_crewHoverIcon.color = LuiStyle.CastleYellow;
		_crewHoverIcon.enabled = false;
		_crewArrow = Pin("TooltipArrow", Crew, new Vector2(0.5f, 0f), new Vector2(0f, 50f), new Vector2(64f, 64f));
		_crewArrow.rectTransform.pivot = new Vector2(0.5f, 0f);
		_crewArrow.color = new Color(22f / 255f, 22f / 255f, 22f / 255f, 1f);
		_crewArrow.enabled = false;
		Image crewButton = Stretch("Button", Crew, Color.clear);
		crewButton.raycastTarget = true;
		CsTaskListPointer crewPointer = crewButton.gameObject.AddComponent<CsTaskListPointer>();
		crewPointer.Enter = (bool on) => SetCrewHover(on);
		crewPointer.Click = (PointerEventData.InputButton which, bool shift, bool ctrl) => CrewClick(which, shift, ctrl);

		// Name, priority and the arrows over the row.
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
		_popupArrow = Pin("PopupArrow", rect, new Vector2(0.5f, 0f), new Vector2(0f, 52f), new Vector2(64f, 64f));
		_popupArrow.rectTransform.pivot = new Vector2(0.5f, 0f);
		_popupArrow.color = new Color(22f / 255f, 22f / 255f, 22f / 255f, 1f);
		_popupArrow.enabled = false;
		_taskArrow = Pin("TooltipArrow", rect, Vector2.zero, new Vector2(28f, 52f), new Vector2(64f, 64f));
		_taskArrow.rectTransform.pivot = new Vector2(0.5f, 0f);
		_taskArrow.color = new Color(22f / 255f, 22f / 255f, 22f / 255f, 1f);
		_taskArrow.enabled = false;
	}

	// An image outside the layout, anchored at one point.
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

	// An image outside the layout over its parent's whole area.
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

	public void Show(Table data)
	{
		_title.text = LuaBridge.String(data.Get("name"), string.Empty);
		Sprite icon = LuaBridge.Sprite(data.Get("icon"));
		_icon.sprite = icon;
		_icon.enabled = icon != null;
		_icon.color = LuaBridge.Color(data.Get("iconColor"), LuiStyle.White);
		float iconSize = LuaBridge.Number(data.Get("iconSize"), 48f);
		_icon.rectTransform.sizeDelta = new Vector2(iconSize, iconSize);
		bool selected = LuaBridge.Bool(data.Get("selected"), false);
		if (_highlight == null)
		{
			_highlight = LuiWidgets.Frame(transform, LuiStyle.Highlight, 2f);
		}
		if (_highlight.activeSelf != selected)
		{
			_highlight.SetActive(selected);
			_highlight.transform.SetAsLastSibling();
		}
		_down = LuaBridge.Bool(data.Get("down"), false);
		Sprite arrow = _list.Arrow;
		_popupArrow.sprite = arrow;
		_taskArrow.sprite = arrow;
		_crewArrow.sprite = arrow;
		_popupArrow.enabled = arrow != null && LuaBridge.Bool(data.Get("popupArrow"), false);
		Sprite priority = LuaBridge.Sprite(data.Get("priority"));
		_priority.sprite = priority;
		_priority.enabled = priority != null;
		_crewLayout.minWidth = Army ? 20f : 48f;
		ShowCrew(data.Get("crew"));
		if (_taskHovered)
		{
			_taskHoverIcon.sprite = _down ? _list.HoverDown : _list.HoverUp;
		}
	}

	private void ShowCrew(DynValue crew)
	{
		Table faces = (crew.Type == DataType.Table) ? crew.Table : null;
		int count = (faces != null) ? faces.Length : 0;
		while (_faces.Count < count)
		{
			int n = _faces.Count + 1;
			RectTransform slot = UiKit.CreateRect("Face " + n, Crew);
			LayoutElement element = slot.gameObject.AddComponent<LayoutElement>();
			element.preferredWidth = FaceWidth;
			element.preferredHeight = FaceWidth;
			RectTransform face = UiKit.CreateRect("Image", slot);
			face.anchorMin = face.anchorMax = new Vector2(0.5f, 0.5f);
			face.sizeDelta = new Vector2(FaceSize, FaceSize);
			// Alternately higher and lower, as the game's crew panel staggers them.
			face.anchoredPosition = new Vector2(4f, (n % 2 == 0) ? -10f : 10f);
			Image image = face.gameObject.AddComponent<Image>();
			image.raycastTarget = false;
			_faces.Add(image);
			// Behind the hover and the crew's button.
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
				Sprite sprite = LuaBridge.Sprite(faces.Get(i + 1));
				image.sprite = sprite;
				image.enabled = sprite != null;
			}
		}
	}

	private void SetTaskHover(bool on)
	{
		DynValue result = _list.CallSide("TaskHover", DynValue.NewNumber(_id), DynValue.NewBoolean(on));
		int state = on ? (int)LuaBridge.Number(result, 0f) : 0;
		_taskHovered = state > 0;
		bool visual = _taskHovered && !Army;
		_taskHoverBg.enabled = visual;
		_taskHoverIcon.sprite = _down ? _list.HoverDown : _list.HoverUp;
		_taskHoverIcon.enabled = visual && _taskHoverIcon.sprite != null;
		_taskArrow.enabled = state == 2 && _taskArrow.sprite != null;
	}

	private void SetCrewHover(bool on)
	{
		_crewHovered = on;
		_crewMode = string.Empty;
		UpdateCrewHover();
	}

	private void Update()
	{
		if (_crewHovered && Mode() != _crewMode)
		{
			UpdateCrewHover();
		}
	}

	private static string Mode()
	{
		if (LuiInput.Ctrl)
		{
			return "ctrl";
		}
		return LuiInput.Shift ? "shift" : "select";
	}

	private void UpdateCrewHover()
	{
		string mode = _crewHovered ? Mode() : string.Empty;
		_crewMode = mode;
		DynValue shown = _list.CallSide("CrewHover", DynValue.NewNumber(_id), DynValue.NewString(mode));
		_crewHoverBg.enabled = _crewHovered;
		Sprite sprite = _list.CrewSelect;
		if (Regular && mode == "ctrl")
		{
			sprite = _list.CrewMinus;
		}
		else if (Regular && mode == "shift")
		{
			sprite = _list.CrewPlus;
		}
		_crewHoverIcon.sprite = sprite;
		_crewHoverIcon.enabled = _crewHovered && sprite != null;
		_crewArrow.enabled = _crewHovered && LuaBridge.Bool(shown, false) && _crewArrow.sprite != null;
	}

	private void CrewClick(PointerEventData.InputButton which, bool shift, bool ctrl)
	{
		string kind;
		if (which == PointerEventData.InputButton.Right)
		{
			kind = "alt";
		}
		else if (which != PointerEventData.InputButton.Left)
		{
			return;
		}
		else if (shift)
		{
			kind = "shift";
		}
		else if (ctrl)
		{
			kind = "ctrl";
		}
		else
		{
			kind = "action";
		}
		_list.CallSide("CrewAction", DynValue.NewNumber(_id), DynValue.NewString(kind));
		// The tooltip says what the next click does; a priority change shows at once.
		_crewMode = string.Empty;
	}

	private void OnDisable()
	{
		if (_taskHovered || _crewHovered)
		{
			_list.CallSide("TaskHover", DynValue.NewNumber(_id), DynValue.False);
		}
		_taskHovered = false;
		_crewHovered = false;
		if (_taskHoverBg != null)
		{
			_taskHoverBg.enabled = false;
			_taskHoverIcon.enabled = false;
			_taskArrow.enabled = false;
			_crewHoverBg.enabled = false;
			_crewHoverIcon.enabled = false;
			_crewArrow.enabled = false;
		}
	}
}

// Hover and clicks (left and right) of a button area in the task list.
internal class CsTaskListPointer : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
{
	public Action<bool> Enter;

	public Action<PointerEventData.InputButton, bool, bool> Click;

	public void OnPointerEnter(PointerEventData eventData)
	{
		Enter?.Invoke(true);
	}

	public void OnPointerExit(PointerEventData eventData)
	{
		Enter?.Invoke(false);
	}

	public void OnPointerClick(PointerEventData eventData)
	{
		Click?.Invoke(eventData.button, LuiInput.Shift, LuiInput.Ctrl);
	}
}
