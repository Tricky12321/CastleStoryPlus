using System;
using System.Collections;
using System.Collections.Generic;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Network;
using Brix.Input;
using Brix.IO.Serialization.Contracts;
using Brix.Legacy;
using CastleStoryPlus.Core;
using HarmonyLib;
using Motus.Behavior;
using Newtonsoft.Json.Serialization;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Building;

// One blueprint built before everything else: the context wheel over a blueprint, for example a warehouse being
// built, has a "Top priority" button (a star) that gives it top priority; the same button ("Normal priority") takes
// it away. A prioritized blueprint gets a yellow ring. Builders look at prioritized blueprints first and take one of
// them whenever they can work on it (fetch its materials or build it), however far it is; the rest goes on as before
// when they cannot. Several blueprints can have priority at once. Server side, saved with the blueprint ("plusPriority"); a client sends the
// change to the host and rings what it prioritized itself. The button is added to the game's wheel files (Info/Livre)
// as they are read: in the task wheel (nothing selected) and the wheel of selected bricktrons over a task.
[Feature(Features.BuildPriority, Features.BuildPriorityInfo)]
internal static class BuildPriority
{
	private const int ToggleHash = 1129595233;

	// Added to a prioritized blueprint's priority: more than distance, height and crowding ever take off.
	internal const int Boost = 100000;

	private static readonly Color RingColor = new Color(1f, 0.85f, 0.2f, 1f);

	private static readonly HashSet<BuildGoal> Prioritized = new HashSet<BuildGoal>();

	private static int _ringId = -1;

	private static CircleHighlighter _ringOwner;

	private static readonly HashSet<BuildGoal> Ringed = new HashSet<BuildGoal>();

	private static void Enable()
	{
		NetworkBehaviour.RegisterCommandDelegate(typeof(UNetBlueprint), ToggleHash, InvokeToggle);
		GameSession.OnLeave(() =>
		{
			Prioritized.Clear();
			Ringed.Clear();
			_ringOwner = null;
		});
	}

	internal static bool Has(BuildGoal goal)
	{
		return goal != null && Prioritized.Contains(goal);
	}

	internal static void Set(BuildGoal goal, bool on)
	{
		if (goal == null)
		{
			return;
		}
		if (on)
		{
			Prioritized.Add(goal);
		}
		else
		{
			Prioritized.Remove(goal);
		}
	}

	// The prioritized blueprints of a build project that are still to build, oldest first.
	internal static List<BuildGoal> Of(BuildProject project)
	{
		List<BuildGoal> list = null;
		Prioritized.RemoveWhere((BuildGoal g) => g.IsNullOrReleased() || g.Blueprint == null || !g.Blueprint.State.HasFlag(PlacementFlags.placed));
		foreach (BuildGoal goal in Prioritized)
		{
			if (goal.Provider != null && goal.Provider.project == project && goal.Blueprint.State.HasFlag(PlacementFlags.buildable))
			{
				list = list ?? new List<BuildGoal>();
				list.Add(goal);
			}
		}
		return list;
	}

	// Client, every frame from InputModeController.Update.
	internal static void Update()
	{
		RefreshRings();
	}

	// Context wheel: the blueprint the wheel was opened over, when it is the local player's.
	private static BuildGoal WheelTarget()
	{
		object target = Instructions.objet;
		GameObject go = (target is GameObject gameObject) ? gameObject : ((target is Component component) ? component.gameObject : null);
		Blueprint blueprint = (go != null) ? go.GetComponentInParent<Blueprint>() : null;
		BuildGoal goal = (blueprint != null) ? blueprint.GetComponent<BuildGoal>() : null;
		Faction local = (User.LocalUser != null) ? User.LocalUser.faction : null;
		if (goal == null || local == null || !local.IsSame(blueprint.gameObject))
		{
			return null;
		}
		return goal;
	}

	internal static void RegisterWheel()
	{
		Instructions.conditions[WheelCondition] = () => WheelTarget() != null;
		Instructions.conditions[WheelLabel] = () => Has(WheelTarget()) ? "Normal priority" : "Top priority";
		Instructions.commendes[WheelEvent] = () =>
		{
			BuildGoal goal = WheelTarget();
			if (goal != null)
			{
				Toggle(goal);
			}
		};
	}

	internal const string WheelButton = "PlusBuildPriority";

	private const string WheelCondition = "Condition_PlusBuildPriority";

	private const string WheelLabel = "Condition_PlusBuildPriorityLabel";

	private const string WheelEvent = "Event_PlusBuildPriority";

	// The wheel files as they are read: the button's definition after the game's buttons, and the button in place of
	// the last free slot of the task wheels.
	internal static string AddToWheel(string text)
	{
		RegisterWheel();
		if (text.Contains("BUTTON DeleteProject") && !text.Contains("BUTTON " + WheelButton))
		{
			return text + "\n\nBUTTON " + WheelButton + "\nName : Top priority\nLabel : " + WheelLabel + "\nCondition : " + WheelCondition + "\nEffect : " + WheelEvent + "\nIcon : UI_Star\nSize : Native\nTooltip : Build this blueprint before everything else (or again to build it as usual).\n";
		}
		if (!text.Contains("MENU Project") || text.Contains("Button: " + WheelButton))
		{
			return text;
		}
		List<string> lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
		foreach (string menu in new[] { "MENU Project", "MENU BricktronProject" })
		{
			int start = lines.FindIndex((string l) => l.Trim() == menu);
			if (start < 0)
			{
				continue;
			}
			int lastHole = -1;
			for (int i = start + 1; i < lines.Count && !lines[i].Trim().StartsWith("MENU "); i++)
			{
				if (lines[i].Trim() == "Hole")
				{
					lastHole = i;
				}
			}
			if (lastHole >= 0)
			{
				lines[lastHole] = "\tButton: " + WheelButton;
			}
			else
			{
				Plugin.Log.LogWarning("BuildPriority: no free slot in the wheel " + menu);
			}
		}
		return string.Join("\n", lines.ToArray());
	}

	// Shown at once; the host does the same.
	private static void Toggle(BuildGoal goal)
	{
		bool on = !Has(goal);
		Set(goal, on);
		Plugin.Log.LogInfo("BuildPriority: " + goal.Blueprint.name + (on ? " has top priority" : " has normal priority"));
		UNetBlueprint cmd = (User.LocalUser != null) ? User.LocalUser.GetComponent<UNetBlueprint>() : null;
		if (cmd == null || cmd.isServer)
		{
			return;
		}
		NetworkWriter writer = new NetworkWriter();
		writer.Write((short)0);
		writer.Write((short)5);
		writer.WritePackedUInt32((uint)ToggleHash);
		writer.Write(cmd.GetComponent<NetworkIdentity>().netId);
		writer.Write(goal.gameObject);
		writer.Write(on);
		cmd.SendCommandInternal(writer, 0, "CmdSetBuildPriority");
	}

	private static void InvokeToggle(NetworkBehaviour obj, NetworkReader reader)
	{
		if (!NetworkServer.active)
		{
			return;
		}
		GameObject go = reader.ReadGameObject();
		bool on = reader.ReadBoolean();
		if (go == null || !Affiliation.IsOwnedBy(go, obj.gameObject))
		{
			return;
		}
		Set(go.GetComponent<BuildGoal>(), on);
	}

	// A yellow ring under every prioritized blueprint.
	private static void RefreshRings()
	{
		CircleHighlighter owner = CircleHighlighter._circleHighlighter;
		if (owner == null)
		{
			return;
		}
		if (owner != _ringOwner)
		{
			Ringed.Clear();
			_ringOwner = owner;
			_ringId = CircleHighlighter.GetId();
		}
		if (Time.frameCount % 15 != 0)
		{
			return;
		}
		List<BuildGoal> gone = null;
		foreach (BuildGoal goal in Ringed)
		{
			if (!Prioritized.Contains(goal))
			{
				gone = gone ?? new List<BuildGoal>();
				gone.Add(goal);
			}
		}
		if (gone != null)
		{
			foreach (BuildGoal goal in gone)
			{
				Ringed.Remove(goal);
				try
				{
					CircleHighlighter.Remove(_ringId, goal.gameObject);
				}
				catch (Exception)
				{
					// The blueprint (and its ring with it) was already destroyed.
				}
			}
		}
		foreach (BuildGoal goal in Prioritized)
		{
			if (goal.IsNullOrReleased() || Ringed.Contains(goal))
			{
				continue;
			}
			try
			{
				CircleHighlight ring = CircleHighlighter.HighlightGameObject(_ringId, goal.gameObject, RingColor, Radius(goal.gameObject));
				ring.OccludedOpacity = 1f;
				Ringed.Add(goal);
			}
			catch (Exception ex)
			{
				Plugin.Log.LogWarning("BuildPriority: ring on " + goal.name + ": " + ex.Message);
			}
		}
	}

	private static float Radius(GameObject go)
	{
		bool any = false;
		Bounds bounds = new Bounds(go.transform.position, Vector3.zero);
		foreach (Renderer renderer in go.GetComponentsInChildren<Renderer>())
		{
			if (renderer.GetComponentInParent<CircleHighlight>() != null)
			{
				continue;
			}
			if (any)
			{
				bounds.Encapsulate(renderer.bounds);
			}
			else
			{
				bounds = renderer.bounds;
				any = true;
			}
		}
		return any ? Mathf.Max(0.6f, Mathf.Max(bounds.extents.x, bounds.extents.z) + 0.3f) : 1f;
	}
}

// The prioritized blueprints first, then the project's own search.
internal class PriorityFirstEnumerator : IEnumerator<Goal>
{
	private readonly List<BuildGoal> _first;

	private BuildGoalProvider.FulfillableEnumerator _rest;

	private int _index = -1;

	private bool _inRest;

	public PriorityFirstEnumerator(List<BuildGoal> first, BuildGoalProvider.FulfillableEnumerator rest)
	{
		_first = first;
		_rest = rest;
	}

	public Goal Current => _inRest ? _rest.Current : _first[_index];

	object IEnumerator.Current => Current;

	public bool MoveNext()
	{
		if (!_inRest)
		{
			_index++;
			if (_index < _first.Count)
			{
				return true;
			}
			_inRest = true;
		}
		return _rest.MoveNext();
	}

	public void Reset()
	{
		_index = -1;
		_inRest = false;
		_rest.Reset();
	}

	public void Dispose()
	{
		_rest.Dispose();
	}
}

[Feature(Features.BuildPriority, Features.BuildPriorityInfo)]
[HarmonyPatch(typeof(BuildProject), "SelectBestGoal", new Type[] { typeof(Labor), typeof(Var<Goal>) })]
internal static class BuildPrioritySelectPatch
{
	private static bool Prefix(BuildProject __instance, Labor labor, Var<Goal> result, ref Node __result)
	{
		List<BuildGoal> first = BuildPriority.Of(__instance);
		if (first == null)
		{
			return true;
		}
		__result = __instance.goalSelector.SelectBestGoal(labor, new PriorityFirstEnumerator(first, __instance.goals.FulfillableBy(labor)), __instance, result);
		return false;
	}
}

[Feature(Features.BuildPriority, Features.BuildPriorityInfo)]
[HarmonyPatch(typeof(BuildGoal), nameof(BuildGoal.PriorityFor), new Type[] { typeof(Labor), typeof(float) }, new ArgumentType[] { ArgumentType.Normal, ArgumentType.Ref })]
internal static class BuildPriorityBoostPatch
{
	private static void Postfix(BuildGoal __instance, ref int __result)
	{
		if (BuildPriority.Has(__instance))
		{
			__result += BuildPriority.Boost;
		}
	}
}

[Feature(Features.BuildPriority, Features.BuildPriorityInfo)]
[HarmonyPatch(typeof(InputModeController), nameof(InputModeController.Update))]
internal static class BuildPriorityInputPatch
{
	private static void Prefix()
	{
		BuildPriority.Update();
	}
}

// The wheel's commands and conditions, with the game's own.
[Feature(Features.BuildPriority, Features.BuildPriorityInfo)]
[HarmonyPatch(typeof(Instructions), nameof(Instructions.Batire))]
internal static class BuildPriorityWheelCommandsPatch
{
	private static void Postfix()
	{
		BuildPriority.RegisterWheel();
	}
}

// The wheel's button, added to the wheel files as they are read.
[Feature(Features.BuildPriority, Features.BuildPriorityInfo)]
[HarmonyPatch(typeof(Livre), nameof(Livre.Split))]
internal static class BuildPriorityWheelFilesPatch
{
	private static void Prefix(ref string contenu)
	{
		if (contenu != null)
		{
			contenu = BuildPriority.AddToWheel(contenu);
		}
	}
}

// Saves: an extra "plusPriority" property on the blueprint's BuildGoal.
[Feature(Features.BuildPriority, Features.BuildPriorityInfo)]
[HarmonyPatch(typeof(DefaultGameContractResolver), nameof(DefaultGameContractResolver.GenerateDefaultMonoBehaviourContract))]
internal static class BuildPrioritySave
{
	private const string Property = "plusPriority";

	private class PriorityProvider : IValueProvider
	{
		public object GetValue(object target)
		{
			return BuildPriority.Has(target as BuildGoal);
		}

		public void SetValue(object target, object value)
		{
			BuildPriority.Set(target as BuildGoal, Convert.ToBoolean(value));
		}
	}

	private static void Postfix(Type objectType, JsonObjectContract __result)
	{
		if (objectType != typeof(BuildGoal) || __result == null || __result.Properties.Contains(Property))
		{
			return;
		}
		__result.Properties.AddProperty(new JsonProperty
		{
			PropertyName = Property,
			UnderlyingName = Property,
			PropertyType = typeof(bool),
			DeclaringType = typeof(BuildGoal),
			ValueProvider = new PriorityProvider(),
			Readable = true,
			Writable = true
		});
	}
}
