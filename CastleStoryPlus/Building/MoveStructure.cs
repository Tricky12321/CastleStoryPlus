using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using Brix.Game.Factories;
using Brix.Game.Network;
using Brix.Game.Semantique;
using Brix.Input;
using Brix.Lifecycle.Pooling;
using Brix.Transactions;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace CastleStoryPlus.Building;

// Moves a placed building (anything with a MovableVolume: workshops, stockpiles, racks, nests...).
// Client: hold the move key and left-click a building; the game's own blueprint placement then shows the
// building's blueprint at the cursor (rotate as usual) and the next placement click sends the move.
// Server: spawns the new blueprint and an anti-blueprint on the old building into the selected build
// project. Workers demolish the old one, which drops its contents plus the materials it cost, and build
// the new one from them, so a move costs work but no materials.
[Feature(Features.MoveStructure, Features.MoveStructureInfo)]
internal static class MoveStructure
{
	private const int MoveHash = 1129595220;

	private static ConfigEntry<KeyCode> _key;

	// Client: the building being moved while its blueprint is held.
	internal static GameObject Source;

	// Server: buildings waiting to be demolished by a move, with the materials to drop when they go.
	private static readonly Dictionary<GameObject, Description> Pending = new Dictionary<GameObject, Description>();

	private static void Enable()
	{
		_key = Plugin.Cfg.Bind("Building", "MoveStructureKey", KeyCode.M, "Hold this key and left-click a building to move it.");
		NetworkBehaviour.RegisterCommandDelegate(typeof(UNetBlueprint), MoveHash, InvokeMove);
		SceneManager.activeSceneChanged += (Scene from, Scene to) =>
		{
			Pending.Clear();
			Source = null;
		};
	}

	// Client, every frame from InputModeController.Update.
	internal static void Update()
	{
		if (Source != null && InputModeController._mode != InputMode.blueprint)
		{
			Source = null;
		}
		if (!Input.GetKey(_key.Value) || !Input.GetMouseButtonDown(0) || InputModeController.DefaultMode != InputMode.command)
		{
			return;
		}
		if (Picking.Instance == null || Picking.Instance.UILocked || InputModeController.IsPaused() || InputModeController.PickerLocked || InputModeController.BuildModeLocked)
		{
			return;
		}
		GameObject building = BuildingUnderCursor(out Factory.AssetKey blueprintKey);
		if (building == null)
		{
			return;
		}
		Picking.Instance.StartCoroutine(Pick(building, blueprintKey));
	}

	private static System.Collections.IEnumerator Pick(GameObject building, Factory.AssetKey blueprintKey)
	{
		System.Collections.IEnumerator select = Eyedropper.SelectForBuilding(blueprintKey);
		while (select.MoveNext())
		{
			yield return select.Current;
		}
		if (InputModeController._mode == InputMode.blueprint)
		{
			Source = building;
		}
	}

	private static GameObject BuildingUnderCursor(out Factory.AssetKey blueprintKey)
	{
		blueprintKey = null;
		VoxelRaycastHit hit = new VoxelRaycastHit();
		LayerBundle layers = new LayerBundle((int)UnityLayer.Terrain, (int)UnityLayer.Blueprints, (int)UnityLayer.FreeBlocks, (int)UnityLayer.DynamicObjects);
		if (!hit.PlaceAtRaycast(Camera.main.ScreenPointToRay(Input.mousePosition), Picking.Instance.MaxDistance, layers))
		{
			return null;
		}
		GameObject go = hit.HitGameObject;
		if (go == null || go.layer == (int)UnityLayer.Terrain || go.GetComponentInParent<Blueprint>() != null)
		{
			return null;
		}
		MovableVolume volume = go.GetComponentInParent<MovableVolume>();
		FactoryImprint imprint = volume != null ? volume.GetComponent<FactoryImprint>() : null;
		GameComponent component = volume != null ? volume.GetComponent<GameComponent>() : null;
		if (imprint == null || component == null || !CopyPaste.IsPlacedBuilding(volume) || component.GetComponent<RopeBridgeConfigurator>() != null)
		{
			return null;
		}
		Faction local = User.LocalUser != null ? User.LocalUser.faction : null;
		if (local == null || !local.IsSame(volume.gameObject))
		{
			return null;
		}
		blueprintKey = BlueprintAssetKeyResolver.Resolve(imprint.AssetKey);
		return blueprintKey.IsNullOrInvalid() ? null : volume.gameObject;
	}

	// Client: called instead of placing a normal blueprint while a building is being moved.
	internal static void Send(GameObject building, Blueprint held, GameObject buildProject)
	{
		UNetBlueprint cmd = User.LocalUser.GetComponent<UNetBlueprint>();
		Factory.AssetKey key = held.AssetKey;
		Vector3 position = held.transform.position;
		Quaternion rotation = held.transform.rotation;
		if (cmd.isServer)
		{
			Move(cmd, building, key, position, rotation, buildProject);
			return;
		}
		NetworkWriter writer = new NetworkWriter();
		writer.Write((short)0);
		writer.Write((short)5);
		writer.WritePackedUInt32((uint)MoveHash);
		writer.Write(cmd.GetComponent<NetworkIdentity>().netId);
		writer.Write(building);
		writer.Write(key.Factory);
		writer.Write(key.Name);
		writer.Write(position);
		writer.Write(rotation);
		writer.Write(buildProject);
		cmd.SendCommandInternal(writer, 0, "CmdMoveStructure");
	}

	private static void InvokeMove(NetworkBehaviour obj, NetworkReader reader)
	{
		if (!NetworkServer.active)
		{
			return;
		}
		GameObject building = reader.ReadGameObject();
		Factory.AssetKey key = new Factory.AssetKey(reader.ReadString(), reader.ReadString());
		Move((UNetBlueprint)obj, building, key, reader.ReadVector3(), reader.ReadQuaternion(), reader.ReadGameObject());
	}

	// Server.
	private static void Move(UNetBlueprint cmd, GameObject building, Factory.AssetKey key, Vector3 position, Quaternion rotation, GameObject buildProject)
	{
		User user = cmd.GetComponent<User>();
		if (building.IsNullOrReleased() || buildProject == null || Pending.ContainsKey(building) || !user.faction.IsSame(building))
		{
			return;
		}
		FactoryImprint imprint = building.GetComponent<FactoryImprint>();
		if (imprint == null || BlueprintAssetKeyResolver.Resolve(imprint.AssetKey) != key)
		{
			return;
		}
		BuildGoalProvider provider = buildProject.GetComponent<BuildGoalProvider>();
		Blueprint blueprint = cmd._PrepareSpawnBlueprint(key, position, rotation, spawnSelected: false);
		if (provider == null || blueprint == null)
		{
			return;
		}
		Description cost = blueprint.recepteur != null ? blueprint.recepteur.BaseCapacity.Clone() : null;
		cmd._FinishSpawnBlueprint(blueprint, instant: false, buildProject);
		// Same as the remove tool's CmdPlaceAnti.
		GameObject anti = Transactor.SpawnUNet(key, user.faction, building.transform.position, building.transform.rotation);
		if (anti == null)
		{
			return;
		}
		anti.GetComponent<Blueprint>().Network_isAnti = true;
		provider.AddObject(anti);
		Pending[building] = cost;
	}

	// Server: a building demolished by a move drops its contents and the materials it cost.
	internal static void OnRelease(GameObject building)
	{
		if (!NetworkServer.active || !Pending.TryGetValue(building, out Description cost))
		{
			return;
		}
		Pending.Remove(building);
		GameComponent component = building.GetComponent<GameComponent>();
		if (component != null && component.recepteur != null && component.recepteur.CarriesSomething())
		{
			component.recepteur.DropAll();
		}
		if (cost == null)
		{
			return;
		}
		Vector3 center = building.transform.position;
		List<GameObject> dropped = new List<GameObject>();
		foreach (Ressource ressource in cost.GetRessources().ToList())
		{
			Factory.AssetKey item = Description.RepresentativeOf(ressource);
			if (item == null)
			{
				continue;
			}
			for (int i = 0; i < ressource.quantifiable.valeur; i++)
			{
				GameObject go = Transactor.SpawnUNet(item, component != null ? component.faction : null, center + Vector3.up * 0.5f, Quaternion.identity);
				if (go != null)
				{
					dropped.Add(go);
				}
			}
		}
		Recepteur.ScatterObjects(dropped, center);
		Plugin.Log.LogInfo("Moved " + building.name + ": dropped " + dropped.Count + " material items");
	}
}

[Feature(Features.MoveStructure, Features.MoveStructureInfo)]
[HarmonyPatch(typeof(InputModeController), nameof(InputModeController.Update))]
internal static class MoveStructureInputPatch
{
	private static void Prefix()
	{
		MoveStructure.Update();
	}
}

// While a building is being moved, the placement click sends the move instead of a new blueprint.
[Feature(Features.MoveStructure, Features.MoveStructureInfo)]
[HarmonyPatch(typeof(BlueprintPlacementPicker), nameof(BlueprintPlacementPicker.PlaceBlueprint))]
internal static class MoveStructurePlacePatch
{
	// Drag placement (stockpiles) places extra slaves in the same click: those are dropped too.
	private static int _sentFrame = -1;

	private static bool Prefix(Slave s)
	{
		if (_sentFrame == Time.frameCount)
		{
			return false;
		}
		GameObject source = MoveStructure.Source;
		if (source == null)
		{
			return true;
		}
		Blueprint held = s.GetHeldComponent<Blueprint>();
		ValidatorComponent validator = s.GetHeldComponent<ValidatorComponent>();
		if (source.IsNullOrReleased())
		{
			MoveStructure.Source = null;
			return false;
		}
		// Same rule as the game's own placement.
		ValidationResult state = validator != null ? validator.GetCurrentState() : ValidationResult.Valid;
		if (held == null || state == ValidationResult.Obstructed || (!validator.CanPlaceInvalid && state == ValidationResult.Invalid))
		{
			return false;
		}
		MoveStructure.Source = null;
		_sentFrame = Time.frameCount;
		Project project = UIGameObserver.projects.CurrentSelected;
		MoveStructure.Send(source, held, project != null ? project.gameObject : null);
		Brix.Audio.SoundEngine.Play("Brick_Place");
		InputModeController.SetCurrentMode(InputModeController.DefaultMode);
		return false;
	}
}

[Feature(Features.MoveStructure, Features.MoveStructureInfo)]
[HarmonyPatch(typeof(ObjectPoolSingleton), nameof(ObjectPoolSingleton.Release), typeof(FactoryImprint))]
internal static class MoveStructureReleasePatch
{
	private static void Prefix(FactoryImprint imprint)
	{
		if (imprint != null && !imprint.Released)
		{
			MoveStructure.OnRelease(imprint.gameObject);
		}
	}
}
