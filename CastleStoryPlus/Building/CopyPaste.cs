using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Audio;
using Brix.Components;
using Brix.Engine;
using Brix.Engine.Blocks;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Components.Validators.Graphs;
using Brix.Game.Components.Volumes;
using Brix.Game.Compounders;
using Brix.Game.Factories;
using Brix.Input;
using Brix.Lifecycle.Pooling;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace CastleStoryPlus.Building;

// Copy and paste a group of blocks, buildings and blueprints.
// Ctrl+C: drag an area on the ground. Everything placed in those columns (from a little below the lowest
// corner and up) is copied: placed blocks, own buildings and own pending blueprints. Right-click or Esc cancels.
// Ctrl+V: the copy follows the cursor as the game's own compound blueprint (the one used for workshop
// blueprints): right-click rotates, left-click places it as blueprints in the selected build project.
// Obstructed pieces are skipped, like when placing a workshop blueprint.
[Feature(Features.CopyPaste, Features.CopyPasteInfo)]
internal static class CopyPaste
{
	private class Piece
	{
		public Factory.AssetKey Key;

		// Blocks: the root voxel. Objects: the transform position.
		public Vector3 Position;

		public Quaternion Rotation;

		// Blocks are stored by voxel; the blueprint pivot is derived from it after spawning (as the remove tool does).
		public bool AtVoxel;

		public XYZ Voxel;
	}

	private const int BelowCorners = 8;

	private static readonly Factory.AssetKey[] Excluded =
	{
		Blueprints.RopeBridgeFrame,
		Blueprints.RopeBridgePlank,
		Blueprints.RopeBridgePlacement
	};

	private static ConfigEntry<KeyCode> _copyKey;

	private static ConfigEntry<KeyCode> _pasteKey;

	// Pieces relative to the paste origin (lowest layer, near the middle).
	private static readonly List<Piece> Clipboard = new List<Piece>();

	private static bool _dragging;

	private static XYZ _start;

	private static XYZ _end;

	private static LineRenderer _outline;

	private static Material _outlineMaterial;

	// While true, the game's pickers are paused so the drag does not select units or place anything.
	internal static bool Selecting;

	private static void Enable()
	{
		_copyKey = Plugin.Cfg.Bind("Building", "CopyKey", KeyCode.C, "With Ctrl held: start copying an area (drag it with the left mouse button).");
		_pasteKey = Plugin.Cfg.Bind("Building", "PasteKey", KeyCode.V, "With Ctrl held: paste the copied area as blueprints.");
		SceneManager.activeSceneChanged += (Scene from, Scene to) =>
		{
			StopSelecting();
			Clipboard.Clear();
		};
	}

	// Ctrl with the copy or paste key: the game's own key actions on those keys (C is call to arms) stand by.
	internal static bool IsShortcutHeld()
	{
		if (_copyKey == null || (!Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.RightControl)))
		{
			return false;
		}
		return Input.GetKey(_copyKey.Value) || Input.GetKey(_pasteKey.Value);
	}

	// Every frame from InputModeController.Update.
	internal static void Update()
	{
		if (Picking.Instance == null || User.LocalUser == null)
		{
			return;
		}
		if (Selecting)
		{
			UpdateSelection();
			return;
		}
		if (!Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.RightControl))
		{
			return;
		}
		if (InputModeController.IsPaused() || InputModeController.IsInMenu() || InputModeController.PickerLocked || InputModeController.BuildModeLocked)
		{
			return;
		}
		InputMode mode = InputModeController._mode;
		if (mode != InputMode.command && mode != InputMode.blueprint)
		{
			return;
		}
		if (Input.GetKeyDown(_copyKey.Value))
		{
			if (mode == InputMode.blueprint)
			{
				Picking.Instance.ClearPick();
			}
			Selecting = true;
			_dragging = false;
		}
		else if (Input.GetKeyDown(_pasteKey.Value) && Clipboard.Count > 0 && !BlueprintCompounder.IsInstantiatingCompound())
		{
			Picking.Instance.StartCoroutine(Paste());
		}
	}

	private static void UpdateSelection()
	{
		if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1) || InputModeController.IsPaused() || InputModeController.IsInMenu())
		{
			StopSelecting();
			return;
		}
		bool hit = VoxelUnderCursor(out XYZ voxel);
		if (!_dragging)
		{
			if (Input.GetMouseButtonDown(0) && hit && !OverUI())
			{
				_dragging = true;
				_start = voxel;
				_end = voxel;
			}
			else
			{
				if (hit)
				{
					DrawBox(voxel, voxel);
				}
				else
				{
					HideBox();
				}
				return;
			}
		}
		if (hit)
		{
			_end = voxel;
		}
		DrawBox(_start, _end);
		if (Input.GetMouseButtonUp(0) || !Input.GetMouseButton(0))
		{
			Copy(_start, _end);
			StopSelecting();
		}
	}

	private static void StopSelecting()
	{
		Selecting = false;
		_dragging = false;
		HideBox();
	}

	private static bool OverUI()
	{
		return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
	}

	private static bool VoxelUnderCursor(out XYZ voxel)
	{
		voxel = XYZ.zero;
		VoxelRaycastHit hit = new VoxelRaycastHit();
		LayerBundle layers = new LayerBundle((int)UnityLayer.Terrain, (int)UnityLayer.Blueprints, (int)UnityLayer.FreeBlocks, (int)UnityLayer.DynamicObjects);
		if (Camera.main == null || !hit.PlaceAtRaycast(Camera.main.ScreenPointToRay(Input.mousePosition), Picking.Instance.MaxDistance, layers))
		{
			return false;
		}
		voxel = hit.VoxelPosition;
		return true;
	}

	private static void Copy(XYZ a, XYZ b)
	{
		int minX = Mathf.Min(a.x, b.x);
		int maxX = Mathf.Max(a.x, b.x);
		int minZ = Mathf.Min(a.z, b.z);
		int maxZ = Mathf.Max(a.z, b.z);
		int minY = Mathf.Min(a.y, b.y) - BelowCorners;
		Faction faction = User.LocalUser.faction;
		List<Piece> pieces = new List<Piece>();
		HashSet<GameObject> taken = new HashSet<GameObject>();

		System.Func<XYZ, bool> inside = p => p.x >= minX && p.x <= maxX && p.z >= minZ && p.z <= maxZ && p.y >= minY;

		// Placed blocks.
		BlockEngine engine = BrixSingleton<BlockEngine>.Instance;
		if (engine != null)
		{
			HashSet<RootBlockData> roots = new HashSet<RootBlockData>();
			foreach (KeyValuePair<XYZ, IBlockData> entry in engine._blocks)
			{
				if (!inside(entry.Key) || entry.Value == null)
				{
					continue;
				}
				if (entry.Value is MultiBlockData multi)
				{
					foreach (IBlockData child in multi.ChildBlocks)
					{
						if (child is BlockData childBlock && childBlock.RootOrSelf != null)
						{
							roots.Add(childBlock.RootOrSelf);
						}
					}
				}
				else if (entry.Value is BlockData block && block.RootOrSelf != null)
				{
					roots.Add(block.RootOrSelf);
				}
			}
			foreach (RootBlockData root in roots)
			{
				Factory.AssetKey key = root.BlockInfo.BlueprintKey;
				if (key.IsNullOrInvalid() || IsExcluded(key))
				{
					continue;
				}
				if (root.PlacedBlock != null)
				{
					taken.Add(root.PlacedBlock);
				}
				pieces.Add(new Piece { Key = key, Position = root.Position.ToVector3(), Rotation = root.Rotation, AtVoxel = true, Voxel = root.Position });
			}
		}

		// Own buildings (workshops, stockpiles, doors, racks...).
		foreach (MovableVolume volume in Object.FindObjectsOfType<MovableVolume>())
		{
			GameObject go = volume.gameObject;
			if (!go.activeInHierarchy || taken.Contains(go) || !IsPlacedBuilding(volume) || go.GetComponent<Blueprint>() != null || go.GetComponent<RopeBridgeConfigurator>() != null || !faction.IsSame(go))
			{
				continue;
			}
			FactoryImprint imprint = go.GetComponent<FactoryImprint>();
			if (imprint == null || imprint.Released)
			{
				continue;
			}
			XYZ voxel = VoxelOf(go);
			if (!inside(voxel))
			{
				continue;
			}
			Factory.AssetKey key = BlueprintAssetKeyResolver.Resolve(imprint.AssetKey);
			if (key.IsNullOrInvalid() || IsExcluded(key))
			{
				continue;
			}
			taken.Add(go);
			pieces.Add(new Piece { Key = key, Position = go.transform.position, Rotation = go.transform.rotation, Voxel = voxel });
		}

		// Own pending blueprints (not removal markers, not the cursor's ghosts).
		foreach (Blueprint blueprint in Object.FindObjectsOfType<Blueprint>())
		{
			GameObject go = blueprint.gameObject;
			if (!go.activeInHierarchy || taken.Contains(go) || blueprint.Network_isAnti || !faction.IsSame(go))
			{
				continue;
			}
			NetworkIdentity identity = go.GetComponent<NetworkIdentity>();
			if (identity == null || identity.netId.IsEmpty())
			{
				continue;
			}
			Factory.AssetKey key = blueprint.AssetKey;
			XYZ voxel = VoxelOf(go);
			if (key.IsNullOrInvalid() || IsExcluded(key) || !inside(voxel))
			{
				continue;
			}
			taken.Add(go);
			pieces.Add(new Piece { Key = key, Position = go.transform.position, Rotation = go.transform.rotation, Voxel = voxel });
		}

		if (pieces.Count == 0)
		{
			SoundEngine.Play("Brick_Cancel");
			return;
		}
		XYZ origin = Origin(pieces);
		Vector3 offset = origin.ToVector3();
		Clipboard.Clear();
		foreach (Piece piece in pieces)
		{
			piece.Position -= offset;
			Clipboard.Add(piece);
		}
		SoundEngine.Play("Brick_Turn");
		Plugin.Log.LogInfo("Copied " + Clipboard.Count + " pieces (" + (maxX - minX + 1) + "x" + (maxZ - minZ + 1) + " area)");
	}

	private static bool IsExcluded(Factory.AssetKey key)
	{
		foreach (Factory.AssetKey excluded in Excluded)
		{
			if (key == excluded)
			{
				return true;
			}
		}
		return false;
	}

	// Loose blocks lying around, and anything carried or stored, also have a MovableVolume. The home crystal
	// (firefly nest) has a blueprint too, but must never be copied or moved: demolishing it for a move left the
	// faction without a crystal, and a save without one could not be loaded.
	internal static bool IsPlacedBuilding(MovableVolume volume)
	{
		return volume.gameObject.layer != (int)UnityLayer.FreeBlocks && !volume.IsParentTracked && volume.GetComponent<FireflyNest>() == null;
	}

	private static XYZ VoxelOf(GameObject go)
	{
		BaseVoxelTransform voxelTransform = go.GetComponent<BaseVoxelTransform>();
		return voxelTransform != null ? voxelTransform.position : XYZ.FromVector3(go.transform.position);
	}

	// The paste point (under the cursor): on the lowest layer, the piece closest to the middle. Same idea as
	// the game's BlueprintGraph.RelativizeNodePositions.
	private static XYZ Origin(List<Piece> pieces)
	{
		int minY = int.MaxValue;
		float sumX = 0f;
		float sumZ = 0f;
		foreach (Piece piece in pieces)
		{
			minY = Mathf.Min(minY, piece.Voxel.y);
			sumX += piece.Voxel.x;
			sumZ += piece.Voxel.z;
		}
		float midX = sumX / pieces.Count;
		float midZ = sumZ / pieces.Count;
		XYZ best = pieces[0].Voxel;
		float bestDistance = float.MaxValue;
		foreach (Piece piece in pieces)
		{
			if (piece.Voxel.y != minY)
			{
				continue;
			}
			float dx = piece.Voxel.x - midX;
			float dz = piece.Voxel.z - midZ;
			float distance = dx * dx + dz * dz;
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = piece.Voxel;
			}
		}
		return new XYZ(best.x, minY, best.z);
	}

	private static IEnumerator Paste()
	{
		IEnumerator select = Eyedropper.SelectBuildProject();
		while (select.MoveNext())
		{
			yield return select.Current;
		}
		Project project = UIGameObserver.projects.CurrentSelected;
		if (project == null)
		{
			yield break;
		}
		InputModeController.SelectBlueprints();

		BlueprintGraph graph = new BlueprintGraph();
		foreach (Piece piece in Clipboard)
		{
			BlueprintValidationNode node = new BlueprintValidationNode();
			node.Type = piece.Key;
			node.Pos = piece.Position;
			node.Rot = piece.Rotation;
			graph._nodes.Add(node);
		}
		GameObject compoundObject = ObjectPoolSingleton.Request_Unsafe(Brix.External.Factories.Compounders.BlueprintCompound, active: false);
		compoundObject.transform.position = Vector3.zero;
		compoundObject.transform.rotation = Quaternion.identity;
		BlueprintCompoundComponent compound = compoundObject.GetComponent<BlueprintCompoundComponent>();
		compound.Populate(graph, User.LocalUser.faction);
		// Blocks: move the blueprint so its voxel lands on the copied block's voxel.
		for (int i = 0; i < Clipboard.Count; i++)
		{
			GameObject target = graph.Nodes[i].Target;
			BaseVoxelTransform voxelTransform = Clipboard[i].AtVoxel && target != null ? target.GetComponent<BaseVoxelTransform>() : null;
			if (voxelTransform != null)
			{
				target.transform.localPosition = Clipboard[i].Position - target.transform.localRotation * voxelTransform.CenterOffset;
			}
		}
		compound.DestinationProject = project;
		// Rebuild the support graph from the spawned blueprints, like a workshop blueprint loaded from disk.
		compound.NeedsGraphRebuild = true;
		BlueprintCompounder.CompoundPlacementMode = BlueprintCompounder.PlacementMode.duplicate;
		Picking.Instance.SendPicker(Pickers.CompoundPlacement, null, new CompoundPlacementOptions(compoundObject));
	}

	private static void DrawBox(XYZ a, XYZ b)
	{
		if (_outline == null)
		{
			GameObject go = new GameObject("CastleStoryPlusCopyArea");
			_outline = go.AddComponent<LineRenderer>();
			// The outline is part of the game scene and made again every game; the material is kept for the session.
			if (_outlineMaterial == null)
			{
				Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
				_outlineMaterial = new Material(shader);
			}
			_outline.sharedMaterial = _outlineMaterial;
			_outline.useWorldSpace = true;
			_outline.startWidth = 0.1f;
			_outline.endWidth = 0.1f;
			Color color = new Color(1f, 0.82f, 0.1f, 1f);
			_outline.startColor = color;
			_outline.endColor = color;
			_outline.positionCount = 16;
		}
		float x0 = Mathf.Min(a.x, b.x) - 0.5f;
		float x1 = Mathf.Max(a.x, b.x) + 0.5f;
		float z0 = Mathf.Min(a.z, b.z) - 0.5f;
		float z1 = Mathf.Max(a.z, b.z) + 0.5f;
		float y0 = Mathf.Min(a.y, b.y) - 0.5f;
		float y1 = Mathf.Max(a.y, b.y) + 2.5f;
		Vector3 b0 = new Vector3(x0, y0, z0);
		Vector3 b1 = new Vector3(x1, y0, z0);
		Vector3 b2 = new Vector3(x1, y0, z1);
		Vector3 b3 = new Vector3(x0, y0, z1);
		Vector3 t0 = new Vector3(x0, y1, z0);
		Vector3 t1 = new Vector3(x1, y1, z0);
		Vector3 t2 = new Vector3(x1, y1, z1);
		Vector3 t3 = new Vector3(x0, y1, z1);
		_outline.SetPositions(new[] { b0, b1, b2, b3, b0, t0, t1, b1, t1, t2, b2, t2, t3, b3, t3, t0 });
		_outline.enabled = true;
	}

	private static void HideBox()
	{
		if (_outline != null)
		{
			_outline.enabled = false;
		}
	}
}

[Feature(Features.CopyPaste, Features.CopyPasteInfo)]
[HarmonyPatch(typeof(InputModeController), nameof(InputModeController.Update))]
internal static class CopyPasteInputPatch
{
	private static void Prefix()
	{
		CopyPaste.Update();
	}
}

// Key actions (call to arms on C, ...) do not fire for Ctrl+C and Ctrl+V.
[Feature(Features.CopyPaste, Features.CopyPasteInfo)]
[HarmonyPatch(typeof(Brix.Utils.UI.KeyBindingsUtility), nameof(Brix.Utils.UI.KeyBindingsUtility.RegisterAction))]
internal static class CopyPasteKeyActionPatch
{
	private static void Prefix(ref System.Action<Rewired.InputActionEventData> action)
	{
		System.Action<Rewired.InputActionEventData> inner = action;
		action = (Rewired.InputActionEventData data) =>
		{
			if (!CopyPaste.IsShortcutHeld())
			{
				inner(data);
			}
		};
	}
}

// While an area is being dragged for copying, the game's pickers stand by (no unit selection, no placing).
[Feature(Features.CopyPaste, Features.CopyPasteInfo)]
[HarmonyPatch(typeof(Picking), "OnUpdate")]
internal static class CopyPastePickingPatch
{
	private static void Prefix()
	{
		if (CopyPaste.Selecting)
		{
			Picking.actif = false;
		}
	}
}
