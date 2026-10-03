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
using UnityEngine;

namespace CastleStoryPlus.Building;

// Middle-mouse click (no drag) on a placed block, structure or blueprint selects that same blueprint
// for placement, so the player can build a copy. Holding/dragging the middle button still rotates the camera.
internal static class Eyedropper
{
	private const string Button = "cam_mouse_rotate";

	private const float MaxClickSeconds = 0.3f;

	private const float MaxClickPixels = 6f;

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
		if (Time.unscaledTime - _downTime > MaxClickSeconds || (mouse - _downPosition).magnitude > MaxClickPixels)
		{
			return;
		}
		if (Picking.Instance == null || Picking.Instance.UILocked || InputModeController.IsPaused() || InputModeController.PickerLocked || InputModeController.BuildModeLocked)
		{
			return;
		}
		Factory.AssetKey key = KeyUnderCursor(mouse);
		if (key.IsNullOrInvalid())
		{
			return;
		}
		Picking.Instance.StartCoroutine(SelectForBuilding(key));
	}

	private static Factory.AssetKey KeyUnderCursor(Vector2 mouse)
	{
		VoxelRaycastHit hit = new VoxelRaycastHit();
		LayerBundle layers = new LayerBundle((int)UnityLayer.Terrain, (int)UnityLayer.Blueprints, (int)UnityLayer.FreeBlocks, (int)UnityLayer.DynamicObjects);
		if (!hit.PlaceAtRaycast(Camera.main.ScreenPointToRay(mouse), Picking.Instance.MaxDistance, layers))
		{
			return null;
		}
		GameObject go = hit.HitGameObject;
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
		if (!(Voxel.GetBlockData(hit.VoxelPosition) is BlockData blockData))
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
