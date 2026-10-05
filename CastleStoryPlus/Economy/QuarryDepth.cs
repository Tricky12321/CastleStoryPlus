using System;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Network;
using Brix.Input;
using Brix.Lua;
using CastleStoryPlus.Core;
using HarmonyLib;
using MoonSharp.Interpreter;
using UnityEngine;

namespace CastleStoryPlus.Economy;

// A quarry's depth can be changed at any time, also after its workers have started digging: a depth button in the
// quarry menu shows the depth in blocks, with small "+" and "-" over it (1 block at a time up to 10, then 5). The
// change goes through the game's own quarry command, so the stairs are extended to the new bottom, it is synced to
// multiplayer clients and saved with the quarry. A quarry can go 3 times as deep as the game's drag plane allows (the
// plane allows it too); never below the bottom of the island.
[Feature(Features.QuarryDepth, Features.QuarryDepthInfo)]
internal static class QuarryDepth
{
	internal const int DepthFactor = 3;

	// The game's limit when its plane has none to read (not expected).
	private const int FallbackGameMax = 16;

	// The game's own deepest drag below a quarry's zone, read from the quarry plane.
	private static int _gameMax;

	// The depth asked for, shown until the host's answer arrives (or for a second, if the host cut it).
	private static readonly Dictionary<QuarryData, KeyValuePair<int, float>> Asked = new Dictionary<QuarryData, KeyValuePair<int, float>>();

	internal static int MaxDepth => DepthFactor * ((_gameMax > 0) ? _gameMax : FallbackGameMax);

	private static void Enable()
	{
		GameSession.OnLeave(Asked.Clear);
		UI.StepButtons.Register(Features.QuarryDepth);
		LuaInjection.AddFunction("QuarryDepth", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			if (args.Count == 0 || args[0].IsNil())
			{
				return DynValue.NewString(string.Empty);
			}
			QuarryData data = QuarryLimit.DataOf(args.AsComponent<MineProject>(0, "QuarryDepth", allowNil: true));
			return DynValue.NewString((data != null) ? Shown(data).ToString() : string.Empty);
		});
		LuaInjection.AddFunction("QuarryDepthStep", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			if (args.Count < 2 || args[0].IsNil())
			{
				return DynValue.False;
			}
			return DynValue.NewBoolean(Step(QuarryLimit.DataOf(args.AsComponent<MineProject>(0, "QuarryDepthStep", allowNil: true)), (int)args[1].Number));
		});
		// After the stairs button, before the resource buttons.
		LuaInjection.AddPatch(Features.QuarryDepth, "LUI/Menus/GameMenu.lua", "--quarry.options.keepStone\ndo\n", LuaInjection.Mode.InsertBefore,
			"--quarry.options.depth (Castle Story Plus)\n"
			+ "do\n"
			+ "local h = MenuHandle.New()\n"
			+ "h.Visible = ||Data.Project:IsSelectedOfTypeQuarry()\n"
			+ "h.Icon = ||IconKeys._UI_Big_Arrow_Down:Get64()\n"
			+ "h.IconSize = ||32\n"
			+ "h.Label = ||\"Depth\"\n"
			+ "h.OnAction = function() end\n"
			+ "h.Count = function() if Data.Project:IsSelectedOfTypeQuarry() then return CastleStoryPlus.QuarryDepth(Data.Project:GetSelected()) end return \"\" end\n"
			+ "h.CountColor = ||CastleYellow\n"
			+ "h.CountFontSize = ||14\n"
			+ "h.ev_refresh = Event.New()\n"
			+ "h.Step = function(direction) if not Data.Project:IsSelectedOfTypeQuarry() then return false end return CastleStoryPlus.QuarryDepthStep(Data.Project:GetSelected(), direction) end\n"
			+ "h.StepChanged = h.ev_refresh\n"
			+ "_m.onSetSelectedProject:AddListener(||h.ev_refresh:Invoke())\n"
			+ "_m.sh.quarry.ev_onLoad:AddListener(function(m, sh) m.AddMenuHandleToggle(h, sh) end)\n"
			+ "_m.mg.projectContext:AddChild(h)\n"
			+ "_m.mh.quarryDepth = h\n"
			+ "end\n"
			+ "-----\n\n");
	}

	// Blocks from the top of the quarry's zone down to its bottom.
	private static int Depth(QuarryData data)
	{
		VoxelBounds bounds = data.Bounds;
		return bounds.max.y - bounds.min.y + 1;
	}

	private static int Shown(QuarryData data)
	{
		int depth = Depth(data);
		if (Asked.TryGetValue(data, out KeyValuePair<int, float> asked))
		{
			if (asked.Key != depth && Time.realtimeSinceStartup < asked.Value)
			{
				return asked.Key;
			}
			Asked.Remove(data);
		}
		return depth;
	}

	// Client or host: asks the host for the next depth, as the game's quarry handles do.
	private static bool Step(QuarryData data, int direction)
	{
		if (data == null || User.LocalUser == null)
		{
			return false;
		}
		int depth = Shown(data);
		int next = QuarryDepthSteps.Next(depth, direction, MaxDepth);
		if (next == depth)
		{
			return false;
		}
		VoxelBounds bounds = data.Bounds;
		Vector3 min = bounds.min.ToVector3();
		min.y = bounds.max.y - next + 1;
		Asked[data] = new KeyValuePair<int, float>(next, Time.realtimeSinceStartup + 1f);
		User.LocalUser.GetComponent<UNetGroupe>().CallCmdSetMine(data.gameObject, min, bounds.max.ToVector3(), data.NetworkstairCorner, data.NetworkstairDirection, data.Commited);
		return true;
	}

	internal static void ReadGameMax(QuarryProjectUI ui)
	{
		DraggableVolumePlane plane = ui.plane;
		if (plane == null)
		{
			return;
		}
		if (_gameMax == 0)
		{
			_gameMax = plane.relativeMinY;
			Plugin.Log.LogInfo("QuarryDepth: the game's quarry plane goes " + _gameMax + " below the zone, max depth now " + MaxDepth);
		}
		if (_gameMax > 0)
		{
			plane.SetRelativeConstraints(DepthFactor * _gameMax, plane.relativeMaxY);
		}
	}
}

// The depth buttons' steps: 1 block at a time up to 10, then 5; never under 1 or over the deepest allowed.
internal static class QuarryDepthSteps
{
	internal static int Next(int depth, int direction, int maxDepth)
	{
		int size = (direction > 0) ? ((depth < 10) ? 1 : 5) : ((depth <= 10) ? 1 : 5);
		return Math.Max(1, Math.Min(maxDepth, depth + direction * size));
	}
}

// The quarry's drag plane may also go 3 times as deep.
[Feature(Features.QuarryDepth, Features.QuarryDepthInfo)]
[HarmonyPatch(typeof(QuarryProjectUI), "Awake")]
internal static class QuarryDepthPlanePatch
{
	private static void Postfix(QuarryProjectUI __instance)
	{
		QuarryDepth.ReadGameMax(__instance);
	}
}
