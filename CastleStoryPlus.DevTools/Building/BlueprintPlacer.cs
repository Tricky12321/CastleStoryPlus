using CastleStoryPlus.Core;
using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using Brix.Game.Network;
using UnityEngine.Networking;
using Brix.Input;
using Brix.Lifecycle.Pooling;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Building;

// Places blueprints for /api/blueprints/place the way the player's blueprint picker does
// (BlueprintPlacementPicker): a held copy of the blueprint is fitted at the voxel by its own fitter (rotation
// turns, offsets, standing on the ground) and validated; then the host spawns the real one at that position
// (UNetBlueprint, the body of CmdSpawnPlacedBlueprint, which checks again for obstruction) into the build project.
internal static class BlueprintPlacer
{
	internal sealed class Fit
	{
		public Vector3 Position;

		public Quaternion Rotation;

		public ValidationResult Check;

		public bool CanPlaceInvalid;

		// The voxels the blueprint takes (its fantome cells), as a box.
		public XYZ Min;

		public XYZ Max;

		public bool HasCells;

		public XYZ Size => HasCells ? new XYZ(Max.x - Min.x + 1, Max.y - Min.y + 1, Max.z - Min.z + 1) : new XYZ(1, 1, 1);
	}

	internal sealed class Result
	{
		public XYZ At;

		public bool Placed;

		public Blueprint Blueprint;

		// placed: buildable or waiting for support; refused: obstructed, unsupported, invalid.
		public string Outcome;

		public string Detail;

		public int Id;
	}

	// Fits a held copy of the blueprint at the voxel with turns quarter turns, like hovering it there.
	internal static Fit Try(Factory.AssetKey key, XYZ at, int turns)
	{
		if (!ObjectPoolSingleton.Request(out GameObject ghost, key, at.ToVector3(), Quaternion.identity) || ghost == null)
		{
			return null;
		}
		Dictionary<GameObject, int> layers = new Dictionary<GameObject, int>();
		try
		{
			ghost.SetActive(true);
			// As a held object (Slave): no raycasts hit it, and it is the player's.
			foreach (Transform child in ghost.GetComponentsInChildren<Transform>(true))
			{
				layers[child.gameObject] = child.gameObject.layer;
				child.gameObject.layer = 2;
			}
			IVolume volume = ghost.GetInterface<IVolume>();
			if (volume != null)
			{
				volume.Faction = User.LocalUser.faction;
			}
			IFitter fitter = ghost.GetInterface<IFitter>();
			if (fitter == null)
			{
				return null;
			}
			// A pooled copy keeps the rotation of its last use.
			if (fitter is AbstractFitter abstractFitter)
			{
				abstractFitter._rotation = Quaternion.identity;
			}
			for (int i = 0; i < turns; i++)
			{
				fitter.ChangeFit();
			}
			fitter.FitAndOrient(new VoxelRaycastHit().PlaceAtVector(at.ToVector3()));
			Fit fit = new Fit { Position = ghost.transform.position, Rotation = ghost.transform.rotation, Check = ValidationResult.Valid, CanPlaceInvalid = true };
			ValidatorComponent validator = ghost.GetComponent<ValidatorComponent>();
			if (validator != null)
			{
				fit.Check = validator.Validate();
				fit.CanPlaceInvalid = validator.CanPlaceInvalid;
				IVolume cells = validator.VData.Volume;
				if (cells != null)
				{
					foreach (XYZ cell in cells.GetCases(VProperty.fantome))
					{
						Include(fit, cell);
					}
				}
			}
			return fit;
		}
		finally
		{
			foreach (KeyValuePair<GameObject, int> pair in layers)
			{
				if (pair.Key != null)
				{
					pair.Key.layer = pair.Value;
				}
			}
			ObjectPoolSingleton.Release(ghost);
		}
	}

	private static void Include(Fit fit, XYZ cell)
	{
		if (!fit.HasCells)
		{
			fit.Min = cell;
			fit.Max = cell;
			fit.HasCells = true;
			return;
		}
		fit.Min = new XYZ(Mathf.Min(fit.Min.x, cell.x), Mathf.Min(fit.Min.y, cell.y), Mathf.Min(fit.Min.z, cell.z));
		fit.Max = new XYZ(Mathf.Max(fit.Max.x, cell.x), Mathf.Max(fit.Max.y, cell.y), Mathf.Max(fit.Max.z, cell.z));
	}

	// One piece: fit, refuse what the player could not place (obstructed; unsupported unless force, as the game
	// lets the player place an unsupported blueprint that waits for its support), else spawn it into the project.
	internal static Result Place(Factory.AssetKey key, XYZ at, int turns, Project project, bool force)
	{
		Result result = new Result { At = at };
		Fit fit = Try(key, at, turns);
		if (fit == null)
		{
			result.Outcome = "invalid";
			result.Detail = "the blueprint could not be made or fitted";
			return result;
		}
		if ((fit.Check & ValidationResult.Obstructed) != 0)
		{
			result.Outcome = "obstructed";
			result.Detail = "something is in the way (terrain, a block, a building or another blueprint)";
			return result;
		}
		if ((fit.Check & ValidationResult.Invalid) != 0 && (!force || !fit.CanPlaceInvalid))
		{
			result.Outcome = "unsupported";
			result.Detail = fit.CanPlaceInvalid ? "not enough support below or beside it (force=true places it anyway, waiting for support)" : "invalid here";
			return result;
		}
		UNetBlueprint net = User.LocalUser.GetComponent<UNetBlueprint>();
		if (!NetworkServer.active)
		{
			// A client: the host spawns it (and checks it again), as when the player places it.
			net.CallCmdSpawnPlacedBlueprint(key.Factory, key.Name, fit.Position, fit.Rotation, false, project.gameObject, false);
			result.Placed = true;
			result.Outcome = "sent to the host";
			return result;
		}
		Blueprint blueprint = net._PrepareSpawnBlueprint(key, fit.Position, fit.Rotation, false);
		if (blueprint == null)
		{
			result.Outcome = "obstructed";
			result.Detail = "the game refused it when spawning (obstructed)";
			return result;
		}
		net._FinishSpawnBlueprint(blueprint, false, project.gameObject);
		result.Placed = true;
		result.Blueprint = blueprint;
		result.Id = blueprint.gameObject.InstanceId();
		result.Outcome = ((fit.Check & ValidationResult.Buildable) != 0) ? "buildable" : (((fit.Check & ValidationResult.Valid) != 0) ? "waiting for support" : "unsupported (forced)");
		return result;
	}

	// Takes down placed blocks of this blueprint, as the remove tool does (UNetBlueprint.CmdPlaceAnti with the held
	// copy's position and rotation): a demolition blueprint for the build project's workers, or gone at once.
	// Refused when the cells the blueprint would take do not all hold that block.
	internal static Result Demolish(Factory.AssetKey key, XYZ at, int turns, Project project, bool instant)
	{
		Result result = new Result { At = at };
		Fit fit = Try(key, at, turns);
		if (fit == null || !fit.HasCells)
		{
			result.Outcome = "invalid";
			result.Detail = "the blueprint could not be made or fitted";
			return result;
		}
		for (int x = fit.Min.x; x <= fit.Max.x; x++)
		{
			for (int y = fit.Min.y; y <= fit.Max.y; y++)
			{
				for (int z = fit.Min.z; z <= fit.Max.z; z++)
				{
					IVoxel root = (Voxel.GetBlockData(new XYZ(x, y, z)) is BlockData block) ? block.RootOrSelf : null;
					if (root == null || root.IsNull || root.BlockInfo.BlueprintKey != key)
					{
						result.Outcome = "nothing to demolish";
						result.Detail = "no placed " + key.Name + " there (turned " + turns * 90 + "°)";
						return result;
					}
				}
			}
		}
		User.LocalUser.GetComponent<UNetBlueprint>().CmdPlaceAnti(key.Factory, key.Name, fit.Position, fit.Rotation, instant, (project != null) ? project.gameObject : null);
		result.Placed = true;
		result.Outcome = instant ? "demolished" : "to demolish";
		return result;
	}
}
