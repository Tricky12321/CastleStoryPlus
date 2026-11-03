using System.Collections;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Factories;
using Brix.Game.Network;
using Brix.Input;
using Brix.UI.GameSelector;
using Brix.Util;
using UnityEngine;

namespace CastleStoryPlus.Building;

// Middle-mouse click (no drag) on a placed block, structure or blueprint selects that same blueprint
// for placement, so the player can build a copy. Holding/dragging the middle button still rotates the camera.
internal static class Eyedropper
{
	private const string Button = "cam_mouse_rotate";

	// A press of the mouse wheel is stiff: it takes longer than a left click and moves the mouse a little, more pixels
	// on a big screen. Longer or further is a drag that turns the camera.
	private const float MaxClickSeconds = 0.5f;

	private const float MaxClickPixels = 6f;

	private const float MaxClickScreenShare = 0.01f;

	private const float WaitForProjectSeconds = 3f;

	private static Vector2 _downPosition;

	private static float _downTime;

	private static bool _pressed;

	public static void Update()
	{
		if (InputModeController.DefaultMode != InputMode.command || PickingUtility.Player == null)
		{
			return;
		}
		Vector2 mouse = PickingUtility.Player.controllers.Mouse.screenPosition;
		if (PickingUtility.Player.GetButtonDown(Button))
		{
			_pressed = true;
			_downPosition = mouse;
			_downTime = Time.unscaledTime;
			return;
		}
		if (!_pressed || !PickingUtility.Player.GetButtonUp(Button))
		{
			return;
		}
		_pressed = false;
		float held = Time.unscaledTime - _downTime;
		float moved = (mouse - _downPosition).magnitude;
		if (held > MaxClickSeconds || moved > Mathf.Max(MaxClickPixels, Screen.height * MaxClickScreenShare))
		{
			// Only near misses: every turn of the camera would fill the log.
			if (held < 1.5f && moved < 60f)
			{
				Plugin.Log.LogInfo("Eyedropper: middle button held " + held.ToString("0.00") + " s and moved " + moved.ToString("0") + " px: turning the camera, nothing picked");
			}
			return;
		}
		if (Picking.Instance == null || Picking.Instance.UILocked || InputModeController.IsPaused() || InputModeController.PickerLocked || InputModeController.BuildModeLocked)
		{
			Plugin.Log.LogInfo("Eyedropper: nothing picked, picking is locked (UI " + (Picking.Instance != null && Picking.Instance.UILocked) + ", paused " + InputModeController.IsPaused() + ", picker " + InputModeController.PickerLocked + ", build mode " + InputModeController.BuildModeLocked + ")");
			return;
		}
		Factory.AssetKey key = KeyUnderCursor(mouse, out string what);
		if (key.IsNullOrInvalid())
		{
			Plugin.Log.LogInfo("Eyedropper: nothing to pick: " + what);
			return;
		}
		Plugin.Log.LogInfo("Eyedropper: picked " + key.Name + " (" + what + ")");
		Picking.Instance.StartCoroutine(SelectForBuilding(key));
	}

	// The nearest thing under the cursor. Not VoxelRaycastHit.PlaceAtRaycast: it hits trigger colliders too, and
	// the invisible trigger zones around buildings and units in front of the block made the menu open with that
	// building instead. Triggers count only when they belong to a blueprint (a ghost has no solid collider).
	private static Factory.AssetKey KeyUnderCursor(Vector2 mouse, out string what)
	{
		what = "nothing under the mouse";
		LayerBundle layers = new LayerBundle((int)UnityLayer.Terrain, (int)UnityLayer.Blueprints, (int)UnityLayer.FreeBlocks, (int)UnityLayer.DynamicObjects);
		Ray ray = Camera.main.ScreenPointToRay(mouse);
		RaycastHit[] hits = Physics.RaycastAll(ray, Picking.Instance.MaxDistance, layers, QueryTriggerInteraction.Collide);
		RaycastHit nearest = default(RaycastHit);
		bool found = false;
		foreach (RaycastHit candidate in hits)
		{
			if (found && candidate.distance >= nearest.distance)
			{
				continue;
			}
			if (candidate.collider.isTrigger && candidate.collider.GetComponentInParent<Blueprint>() == null)
			{
				continue;
			}
			nearest = candidate;
			found = true;
		}
		if (!found)
		{
			return null;
		}
		GameObject go = nearest.collider.gameObject;
		XYZ voxel = XYZ.FromVector3(Calcule.RoundVecteur(nearest.point - nearest.normal * 0.0001f));
		what = go.name + " on layer " + LayerMask.LayerToName(go.layer) + " at " + voxel;
		if (go != null)
		{
			Blueprint blueprint = go.GetComponentInParent<Blueprint>();
			if (blueprint != null)
			{
				return Normalize(blueprint.GetComponent<FactoryImprint>()?.AssetKey);
			}
			if (go.layer != (int)UnityLayer.Terrain)
			{
				FactoryImprint imprint = go.GetComponentInParent<FactoryImprint>();
				if (imprint != null)
				{
					return Normalize(BlueprintAssetKeyResolver.Resolve(imprint.AssetKey));
				}
			}
		}
		// Placed voxel block. Natural terrain has no block data and is ignored.
		if (!(Voxel.GetBlockData(voxel) is BlockData blockData))
		{
			return null;
		}
		IVoxel root = blockData.RootOrSelf;
		if (root == null || root.IsNull || root is MultiBlockData)
		{
			return null;
		}
		Factory.AssetKey key = root.BlockInfo.BlueprintKey;
		if (key == Blueprints.RopeBridgePlank)
		{
			return null;
		}
		return Normalize(key);
	}

	// Map piece variants (column middle/top, ramp parts) to the item the build menu offers.
	private static Factory.AssetKey Normalize(Factory.AssetKey key)
	{
		if (key.IsNullOrInvalid() || key.Factory != "Blueprints")
		{
			return key.IsNullOrInvalid() ? null : key;
		}
		string name = key.Name;
		if (name.StartsWith("Stone_Column") && (name.EndsWith("_Middle") || name.EndsWith("_Top")))
		{
			return new Factory.AssetKey("Blueprints", name.Substring(0, name.LastIndexOf('_')) + "_Base");
		}
		if (name.StartsWith("Wood_Ramp_Junction"))
		{
			return Blueprints.Wood_Ramp_JunctionA;
		}
		if (name.StartsWith("Wood_Ramp_"))
		{
			return Blueprints.Wood_Ramp_TopA;
		}
		return key;
	}

	// Blueprints are attached to the selected build project, so make sure one is selected first,
	// creating one like the bricks hotkey does when none exists.
	internal static IEnumerator SelectForBuilding(Factory.AssetKey key)
	{
		IEnumerator select = SelectBuildProject();
		while (select.MoveNext())
		{
			yield return select.Current;
		}
		if (UIGameObserver.projects.CurrentSelected != null)
		{
			InputModeController.PlaceBlueprint(key);
		}
	}

	internal static IEnumerator SelectBuildProject()
	{
		Project current = UIGameObserver.projects.CurrentSelected;
		if (current == null || current != UIGameObserver.projects.LastSelectedBuild)
		{
			Project build = UIGameObserver.projects.LastSelectedBuild;
			if (build == null)
			{
				User.LocalUser.GetComponent<UNetGroupe>().CallCmdCreate();
				float until = Time.unscaledTime + WaitForProjectSeconds;
				while (UIGameObserver.projects.LastSelectedBuild == null && Time.unscaledTime < until)
				{
					yield return null;
				}
				build = UIGameObserver.projects.LastSelectedBuild;
				if (build == null)
				{
					yield break;
				}
			}
			UIGameSelector.Select(build.gameObject);
			yield return null;
		}
	}
}
