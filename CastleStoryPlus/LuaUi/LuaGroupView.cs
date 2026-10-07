using System;
using System.Collections.Generic;
using MoonSharp.Interpreter;
using UnityEngine;

namespace CastleStoryPlus.LuaUi;

// Draws a menu group of the game's (MenuGroup.lua: GameMenu's _m.mg.right, _m.mg.left...) as a row or column of
// handle buttons, in the group's order. The game and mods add handles to a group while GameMenu is built, so the
// buttons are made again whenever the group's handles change; open and close (onAnyOpen) mark them for reading.
internal class LuaGroupView : MonoBehaviour
{
	private Table _group;

	private float _size;

	private LuiIconButton.Side _labelSide;

	private readonly List<LuaHandleView> _views = new List<LuaHandleView>();

	private Action _unlisten;

	public static LuaGroupView Create(Transform parent, Table group, bool vertical, float size, LuiIconButton.Side labelSide)
	{
		RectTransform rect = vertical ? LuiWidgets.Vertical(parent, 0f, 0) : LuiWidgets.Horizontal(parent, 0f, 0);
		rect.name = "Group " + LuaBridge.String(LuaBridge.Get(group, "_str_name"), string.Empty);
		LuaGroupView view = rect.gameObject.AddComponent<LuaGroupView>();
		view._group = group;
		view._size = size;
		view._labelSide = labelSide;
		Table onAnyOpen = LuaBridge.GetTable(group, "onAnyOpen");
		if (onAnyOpen != null)
		{
			view._unlisten = LuaBridge.Listen(onAnyOpen, group.OwnerScript, view.MarkAllDirty);
		}
		view.Rebuild();
		return view;
	}

	private void Update()
	{
		if (_group != null && !SameHandles())
		{
			Rebuild();
		}
	}

	private bool SameHandles()
	{
		Table children = LuaBridge.GetTable(_group, "children");
		int count = (children != null) ? children.Length : 0;
		if (count != _views.Count)
		{
			return false;
		}
		for (int i = 0; i < count; i++)
		{
			DynValue child = children.Get(i + 1);
			if (child.Type != DataType.Table || child.Table != _views[i].Handle)
			{
				return false;
			}
		}
		return true;
	}

	private void Rebuild()
	{
		foreach (LuaHandleView view in _views)
		{
			if (view != null)
			{
				Destroy(view.gameObject);
			}
		}
		_views.Clear();
		foreach (Table handle in GameMenuLink.Children(_group))
		{
			_views.Add(LuaHandleView.Create(transform, handle, _size, _labelSide));
		}
	}

	private void MarkAllDirty()
	{
		foreach (LuaHandleView view in _views)
		{
			if (view != null)
			{
				view.MarkDirty();
			}
		}
	}

	private void OnDestroy()
	{
		if (_unlisten != null && GameMenuLink.Script != null)
		{
			_unlisten();
		}
		_unlisten = null;
		_group = null;
	}
}
