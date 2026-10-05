using System;
using System.Collections.Generic;
using Brix.Game.AI;
using Brix.Lua;
using CastleStoryPlus.Core;
using MoonSharp.Interpreter;
using UnityEngine;

namespace CastleStoryPlus.Building;

// The menu of a selected build task lists what the task still needs: one row per resource with its icon, name and
// count, most needed first. The count is red when the stockpiles hold less than that.
[Feature(Features.BuildNeeds, Features.BuildNeedsInfo)]
internal static class BuildNeedsMenu
{
	private const int Slots = 12;

	private struct Missing
	{
		public LuaCrafting.LuaResource Resource;

		public int Count;
	}

	private static readonly ProjectNeeds Needs = new ProjectNeeds();

	private static readonly List<Missing> Rows = new List<Missing>();

	private static int _frame = -1;

	private static int _left;

	private static void Enable()
	{
		LuaInjection.AddFunction("BuildNeedsLeft", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			Measure();
			return DynValue.NewNumber(_left);
		});
		LuaInjection.AddFunction("BuildNeedsCount", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			Measure();
			int slot = (int)args.AsInt(0, "BuildNeedsCount") - 1;
			return DynValue.NewNumber((slot >= 0 && slot < Rows.Count) ? Rows[slot].Count : 0);
		});
		LuaInjection.AddFunction("BuildNeedsResource", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			Measure();
			int slot = (int)args.AsInt(0, "BuildNeedsResource") - 1;
			LuaCrafting.LuaResource resource = (slot >= 0 && slot < Rows.Count) ? Rows[slot].Resource : LuaCrafting.LuaResource.None;
			return DynValue.FromObject(context.OwnerScript, resource);
		});
		LuaInjection.AddPatch(Features.BuildNeeds, "LUI/Menus/GameMenu.lua", "_m.mg.projectContext:AddChild(h)\n_m.mh.blueprintClear = h\nend\n", LuaInjection.Mode.InsertAfter, @"

---build: still needed (Castle Story Plus: resources the selected build task still needs)
do
local h = DividerHandle.New()
h.Visible = ||Data.Project:IsSelectedOfTypeBuild() and CastleStoryPlus.BuildNeedsLeft() > 0
h.ev_refresh = Event.New()

_m.onSetSelectedProject:AddListener(||h.ev_refresh:Invoke())
Data.Storage.ev_onSetContent:AddListener(||h.ev_refresh:Invoke())

_m.dh.buildNeeds = h
end

do
local h = SectionHandle.New()
h.Visible = ||Data.Project:IsSelectedOfTypeBuild() and CastleStoryPlus.BuildNeedsLeft() > 0
h.Label = ||GetLocalized(""##gamemenu_taskbar_required"")
h.LabelColor = ||CastleYellow
h.hideHeader = true
h.ev_refresh = Event.New()
h.ev_onLoad = Event.New()

_m.onSetSelectedProject:AddListener(||h.ev_refresh:Invoke())
Data.Storage.ev_onSetContent:AddListener(||h.ev_refresh:Invoke())

_m.sh.buildNeeds = h
end

do
for i = 1, " + Slots + @" do
local GetResource = ||CastleStoryPlus.BuildNeedsResource(i)
local h = MenuHandle.New()
h.Visible = ||Data.Project:IsSelectedOfTypeBuild() and CastleStoryPlus.BuildNeedsCount(i) > 0
h.TooltipPath = ||Data.Resource:GetTooltipPath(GetResource())
h.Count = ||tostring(CastleStoryPlus.BuildNeedsCount(i))
h.CountColor = ||(Data.Storage:GetResourceCount(GetResource()) < CastleStoryPlus.BuildNeedsCount(i)) and CastleRed or CastleYellow
h.Label = ||Data.Resource:GetName(GetResource())
h.LabelColor = ||CastleYellow
h.Icon = ||Data.Resource:GetIcon(GetResource())
h.IconColor = ||White
h.IconSize = ||64
h.ev_refresh = Event.New()

_m.onSetSelectedProject:AddListener(||h.ev_refresh:Invoke())
Data.Storage.ev_onSetContent:AddListener(||h.ev_refresh:Invoke())

_m.sh.buildNeeds.ev_onLoad:AddListener(function(m, sh) m.AddMenuHandleToggle(h, sh) end)

_m.mg.projectContext:AddChild(h)
end
end
");
		LuaInjection.AddPatch(Features.BuildNeeds, "LUI/Menus/GameMenu.lua", "\t\t.AddDivider(_m.dh.build)\n\t\t.AddSection(_m.sh.build)\n", LuaInjection.Mode.InsertAfter, "\t\t.AddDivider(_m.dh.buildNeeds)\n\t\t.AddSection(_m.sh.buildNeeds)\n");
	}

	// Counts the selected build task once per frame (every row asks several times).
	private static void Measure()
	{
		if (_frame == Time.frameCount)
		{
			return;
		}
		_frame = Time.frameCount;
		Rows.Clear();
		_left = 0;
		BuildGoalProvider provider = SelectedProvider();
		if (provider == null)
		{
			return;
		}
		_left = Needs.Sum(provider);
		Dictionary<AdjectiveKey, LuaCrafting.LuaResource> toLua = LuaCrafting._adjectiveToLuaResource;
		foreach (KeyValuePair<Type, Adjectif> pair in Needs.BaseCapacity.DicoAdjectif)
		{
			Ressource resource = pair.Value as Ressource;
			if (resource == null || toLua == null || !toLua.TryGetValue(resource.GetKey(), out LuaCrafting.LuaResource luaResource))
			{
				continue;
			}
			Rows.Add(new Missing
			{
				Resource = luaResource,
				Count = Needs.BaseCapacity.Value(resource)
			});
		}
		Rows.Sort((Missing a, Missing b) => b.Count.CompareTo(a.Count));
	}

	// Same as the Lua Project.GetSelected.
	private static BuildGoalProvider SelectedProvider()
	{
		if (UIGameObserver.projects == null || UIGameObserver.projects.all == null)
		{
			return null;
		}
		foreach (Project project in UIGameObserver.projects.all)
		{
			if (project != null && project.selection)
			{
				return project.GetComponent<BuildGoalProvider>();
			}
		}
		return null;
	}
}
