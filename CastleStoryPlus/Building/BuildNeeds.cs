using System.Collections.Generic;
using Brix.External.Signals;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Input;
using Brix.NewUI.Tooltips;
using Brix.UI.GameSelector;
using Brix.Utils;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Building;

// The game has a cursor tooltip listing, with icon and count, what a blueprint still needs, but it rarely shows:
// only for structures (more than one resource or more than one of it), and in the normal command mode only once
// something has been delivered. Here it shows for every blueprint you hover, also empty ones, and a block of a
// build task shows what the whole task still needs, per resource.
[Feature(Features.BuildNeeds, Features.BuildNeedsInfo)]
internal static class BuildNeeds
{
	private const float RefreshSeconds = 0.5f;

	private static readonly ProjectNeeds Needs = new ProjectNeeds();

	private static Project _shown;

	private static float _nextRefresh;

	private static void Enable()
	{
		Plugin.Root.AddComponent<BuildNeedsRefresher>();
		GameSession.OnLeave(() => _shown = null);
	}

	// What to show for a hovered blueprint: a structure its own needs, a block those of its whole task.
	internal static void Show(Blueprint blueprint)
	{
		CursorTooltip tooltip = CursorTooltip.instance;
		if (tooltip == null || blueprint.IsNullOrReleased() || blueprint.IsAnti || blueprint.recepteur == null || SelectionBox.active)
		{
			return;
		}
		Project project = blueprint.Project;
		BuildGoalProvider provider = (!blueprint.recepteur.HasComplexRecipe() && project != null) ? project.GetComponent<BuildGoalProvider>() : null;
		if (provider == null)
		{
			_shown = null;
			tooltip.ShowBlueprintRequirement(blueprint);
			return;
		}
		int blueprints = Needs.Sum(provider);
		_shown = project;
		_nextRefresh = Time.unscaledTime + RefreshSeconds;
		// ShowBlueprintRequirement sets the title and opens the tooltip; the content is then replaced with the task's needs.
		tooltip.ShowBlueprintRequirement(blueprint);
		tooltip.Title.text = I2Helper.TryGet("##gamemenu_taskbar_required") + " (" + blueprints + " left to build)";
		tooltip.StorageUi.SetBlueprintRecepteur(Needs);
	}

	internal static void Hide()
	{
		_shown = null;
		if (CursorTooltip.instance != null)
		{
			CursorTooltip.instance.Hide();
		}
	}

	// While a task's needs are shown, they are counted again every half second as workers deliver.
	internal static void Refresh()
	{
		if (_shown == null || Time.unscaledTime < _nextRefresh)
		{
			return;
		}
		_nextRefresh = Time.unscaledTime + RefreshSeconds;
		CursorTooltip tooltip = CursorTooltip.instance;
		BuildGoalProvider provider = _shown.IsNullOrReleased() ? null : _shown.GetComponent<BuildGoalProvider>();
		if (tooltip == null || !tooltip.gameObject.activeSelf || provider == null)
		{
			_shown = null;
			return;
		}
		Needs.Sum(provider);
		Needs.RaiseChanged();
	}
}

internal class BuildNeedsRefresher : MonoBehaviour
{
	private void Update()
	{
		BuildNeeds.Refresh();
	}
}

// A stand-in recepteur for the tooltip: its capacity is what all blueprints of a task still need, its content empty.
internal class ProjectNeeds : IRecepteur
{
	private readonly Signal _changed = new Signal();

	private readonly Description _missing = new Description();

	private readonly Description _empty = new Description();

	private readonly HashSet<IDescriptor> _stored = new HashSet<IDescriptor>();

	public IReadOnlySignal Changed => _changed;

	public HashSet<IDescriptor> StoredItems => _stored;

	public Description SelfDescription => _empty;

	public Description ContentDescription => _empty;

	public Description BaseCapacity => _missing;

	public Description BaseCapacityWithExclusions => _missing;

	public Description CurrentCapacity => _missing;

	public bool AcceptMultipleResourceType => true;

	// Returns the number of blueprints still to build (blocks and structures).
	public int Sum(BuildGoalProvider provider)
	{
		_missing.Clear();
		int count = 0;
		foreach (Blueprint blueprint in provider.TrackedBlueprints)
		{
			if (blueprint.IsNullOrReleased() || blueprint.IsAnti || blueprint.recepteur == null)
			{
				continue;
			}
			count++;
			Description needed = blueprint.recepteur.BaseCapacity;
			Description delivered = blueprint.recepteur.ContentDescription;
			foreach (KeyValuePair<System.Type, Adjectif> pair in needed.DicoAdjectif)
			{
				if (!(pair.Value is Ressource))
				{
					continue;
				}
				int missing = needed.Value(pair.Value) - delivered.Value(pair.Value);
				if (missing > 0)
				{
					_missing.Add(Adjectif.New(pair.Key, missing));
				}
			}
		}
		return count;
	}

	public void RaiseChanged()
	{
		_changed.Invoke();
	}
}

// Hovered blueprints the game already reports (it skipped the tooltip for single blocks).
[Feature(Features.BuildNeeds, Features.BuildNeedsInfo)]
[HarmonyPatch(typeof(BlueprintsHighlightDriver), "OnObjectHovered")]
internal static class BuildNeedsHoverPatch
{
	private static void Postfix(Blueprint blueprint)
	{
		if (!blueprint.IsNullOrReleased() && !UIGameSelector.IsSelected(blueprint.gameObject))
		{
			BuildNeeds.Show(blueprint);
		}
	}
}

// In the normal command mode the game only hovers blueprints that already hold something; empty ones are left to
// the click-through behaviour, so only the tooltip is shown for them, without hovering.
[Feature(Features.BuildNeeds, Features.BuildNeedsInfo)]
[HarmonyPatch(typeof(CommandSelectionPicker), nameof(CommandSelectionPicker.OnEnterBlueprint))]
internal static class BuildNeedsEnterPatch
{
	private static void Postfix(Blueprint hoveredBlueprint)
	{
		if (!hoveredBlueprint.IsNullOrReleased() && hoveredBlueprint.recepteur != null && hoveredBlueprint.recepteur.IsEmpty() && !UIGameSelector.IsSelected(hoveredBlueprint.gameObject))
		{
			BuildNeeds.Show(hoveredBlueprint);
		}
	}
}

[Feature(Features.BuildNeeds, Features.BuildNeedsInfo)]
[HarmonyPatch(typeof(CommandSelectionPicker), nameof(CommandSelectionPicker.OnLeaveBlueprint))]
internal static class BuildNeedsLeavePatch
{
	private static void Postfix()
	{
		BuildNeeds.Hide();
	}
}
