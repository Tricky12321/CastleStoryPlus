using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using Brix.Assets;
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
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace CastleStoryPlus.Building;

// Moves a placed building (anything with a MovableVolume: workshops, stockpiles, racks, nests...).
// Client: hold the move key and left-click a building; the game's own blueprint placement then shows the
// building's blueprint at the cursor (rotate as usual) and the next placement click sends the move.
// Server: spawns the new blueprint and an anti-blueprint on the old building into the selected build
// project. Workers demolish the old one, which drops its contents; the materials it cost go straight into the
// new blueprint (workers do not fetch loose items for a blueprint), so a move costs work but no materials.
[Feature(Features.MoveStructure, Features.MoveStructureInfo)]
internal static class MoveStructure
{
	private const int MoveHash = 1129595220;

	private static ConfigEntry<KeyCode> _key;

	// Client: the building being moved while its blueprint is held.
	internal static GameObject Source;

	// Client: the click that picked a building to move is still going on (its blueprint is being put in the hand,
	// which can take a few frames, or the button is not yet released). The game places a held blueprint on that same
	// click otherwise: a new blueprint next to the building instead of a move.
	private static bool _picking;

	private static bool _waitRelease;

	internal static bool PickClickActive
	{
		get { return _picking || _waitRelease; }
	}

	// Server: a building waiting to be demolished by a move, the materials it cost and the blueprint they go to.
	private class PendingMove
	{
		public Description Cost;

		public Blueprint Target;
	}

	private static readonly Dictionary<GameObject, PendingMove> Pending = new Dictionary<GameObject, PendingMove>();

	private class Demolished
	{
		public Factory.AssetKey BlueprintKey;

		public Vector3 Position;

		public float CheckAt;
	}

	// Server: places where a moved building was just demolished, checked a moment later for a demolish ghost
	// (anti-blueprint) left behind.
	private static readonly List<Demolished> ToCheck = new List<Demolished>();

	private static void Enable()
	{
		_key = Plugin.Cfg.Bind("Building", "MoveStructureKey", KeyCode.M, "Hold this key and left-click a building to move it.");
		NetworkBehaviour.RegisterCommandDelegate(typeof(UNetBlueprint), MoveHash, InvokeMove);
		SceneManager.activeSceneChanged += (Scene from, Scene to) =>
		{
			Pending.Clear();
			ToCheck.Clear();
			Source = null;
		};
		GameSignals.Connect(GameSignals.LevelReady, RestorePending);
		LuaInjection.AddFunction("MovingStructure", (MoonSharp.Interpreter.ScriptExecutionContext context, MoonSharp.Interpreter.CallbackArguments args) => MoonSharp.Interpreter.DynValue.NewBoolean(PickClickActive || Source != null));
		// Putting a building's blueprint in the hand opens the blocks or structures catalogue (GameMenu's
		// OnReopenBuild); not while it is being moved.
		LuaInjection.AddPatch(Features.MoveStructure, "LUI/Menus/GameMenu.lua", "\t\tif _m.IsHeldBlueprintOfMetaType(Meta.StoneBlock) then\n", LuaInjection.Mode.InsertBefore, "\t\tif CastleStoryPlus.MovingStructure() then return end\n");
	}

	// Client, every frame from InputModeController.Update.
	internal static void Update()
	{
		CheckDemolished();
		if (_waitRelease && !Input.GetMouseButton(0) && !Input.GetMouseButtonUp(0))
		{
			_waitRelease = false;
		}
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
		_picking = true;
		_waitRelease = true;
		Picking.Instance.StartCoroutine(Pick(building, blueprintKey));
	}

	private static System.Collections.IEnumerator Pick(GameObject building, Factory.AssetKey blueprintKey)
	{
		try
		{
			// No build task is selected (that opens its menus) or made: the host puts the move into the team's build task.
			InputModeController.PlaceBlueprint(blueprintKey);
			yield return null;
			if (InputModeController._mode == InputMode.blueprint)
			{
				Source = building;
			}
		}
		finally
		{
			_picking = false;
		}
	}

	// The move key is held over a building that can be moved: the game's own key action on the same key (the
	// machine shop on M) must not fire, or it puts that building in the hand and the click places it.
	internal static bool KeyClaimed()
	{
		if (_key == null || !Input.GetKey(_key.Value) || InputModeController.DefaultMode != InputMode.command)
		{
			return false;
		}
		return BuildingUnderCursor(out Factory.AssetKey _) != null;
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

	// Host, for the developer tools: the same move as the player's, of a building of the local player to a new
	// position into a build project. Null when ordered, else why not.
	internal static string MoveTo(GameObject building, Vector3 position, Quaternion rotation, GameObject buildProject)
	{
		if (!NetworkServer.active || User.LocalUser == null)
		{
			return "Only the host can move buildings";
		}
		MovableVolume volume = (building != null) ? building.GetComponent<MovableVolume>() : null;
		FactoryImprint imprint = (building != null) ? building.GetComponent<FactoryImprint>() : null;
		if (volume == null || imprint == null || !CopyPaste.IsPlacedBuilding(volume) || building.GetComponent<Blueprint>() != null)
		{
			return "Not a placed building that can be moved (no MovableVolume)";
		}
		Factory.AssetKey key = BlueprintAssetKeyResolver.Resolve(imprint.AssetKey);
		if (key.IsNullOrInvalid())
		{
			return "The building has no blueprint to move it with";
		}
		if (Pending.ContainsKey(building))
		{
			return "The building is already being moved";
		}
		Move(User.LocalUser.GetComponent<UNetBlueprint>(), building, key, position, rotation, buildProject);
		return Pending.ContainsKey(building) ? null : "The game refused the move (not the player's building, or no room for the blueprint)";
	}

	// Server.
	private static void Move(UNetBlueprint cmd, GameObject building, Factory.AssetKey key, Vector3 position, Quaternion rotation, GameObject buildProject)
	{
		User user = cmd.GetComponent<User>();
		if (building.IsNullOrReleased() || Pending.ContainsKey(building) || !user.faction.IsSame(building))
		{
			Plugin.Log.LogInfo("MoveStructure: move refused (" + (building.IsNullOrReleased() ? "the building is gone" : Pending.ContainsKey(building) ? "already being moved" : "not the player's building") + ")");
			return;
		}
		FactoryImprint imprint = building.GetComponent<FactoryImprint>();
		if (imprint == null || BlueprintAssetKeyResolver.Resolve(imprint.AssetKey) != key)
		{
			Plugin.Log.LogInfo("MoveStructure: move of " + building.name + " refused (its blueprint is not " + key + ")");
			return;
		}
		// The team's build task with the most workers (GlobalBuildJob), not the project the client sent: that was
		// the repair task, or a new empty build task made after loading, which nobody works on, so the old building
		// was never demolished and the move never happened.
		BuildProject team = GlobalBuildJob.TeamProject(user);
		if (team == null)
		{
			Plugin.Log.LogInfo("MoveStructure: move of " + building.name + " refused (no build task)");
			return;
		}
		buildProject = team.gameObject;
		BuildGoalProvider provider = buildProject.GetComponent<BuildGoalProvider>();
		Blueprint blueprint = cmd._PrepareSpawnBlueprint(key, position, rotation, spawnSelected: false);
		if (provider == null || blueprint == null)
		{
			Plugin.Log.LogInfo("MoveStructure: move of " + building.name + " refused (no room for its blueprint there)");
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
		// Committed at once (as when the build task is deselected), whether or not that task is selected: both are
		// worked on right away.
		GlobalBuildJob.Commit(blueprint);
		GlobalBuildJob.Commit(anti.GetComponent<Blueprint>());
		Pending[building] = new PendingMove { Cost = cost, Target = blueprint };
		Plugin.Log.LogInfo("MoveStructure: " + building.name + " moves to " + position + " in " + buildProject.name + " (" + team.CrewCount() + " workers)");
	}

	// The pending moves are saved next to the save's gameobjects.json, so a building whose move was ordered
	// before a save still gives its materials back when it is demolished after loading it. A building is found
	// again by its key and position.
	private const string SaveFile = "castlestoryplus_moves.json";

	// Server: called when the game has written a save into its folder.
	internal static void WritePending(string folder)
	{
		string file = System.IO.Path.Combine(folder, SaveFile);
		JArray moves = new JArray();
		foreach (KeyValuePair<GameObject, PendingMove> pending in Pending)
		{
			FactoryImprint imprint = pending.Key != null ? pending.Key.GetComponent<FactoryImprint>() : null;
			if (imprint == null || pending.Key.IsNullOrReleased())
			{
				continue;
			}
			Vector3 position = pending.Key.transform.position;
			JArray cost = new JArray();
			if (pending.Value.Cost != null)
			{
				foreach (KeyValuePair<Type, Adjectif> entry in pending.Value.Cost.DicoAdjectif)
				{
					if (entry.Value is Ressource && entry.Value.quantifiable != null)
					{
						cost.Add(new JObject { ["type"] = entry.Key.FullName, ["count"] = entry.Value.quantifiable.valeur });
					}
				}
			}
			JObject move = new JObject
			{
				["factory"] = imprint.AssetKey.Factory,
				["name"] = imprint.AssetKey.Name,
				["x"] = position.x,
				["y"] = position.y,
				["z"] = position.z,
				["cost"] = cost
			};
			Blueprint target = pending.Value.Target;
			if (IsLiveTarget(target))
			{
				Vector3 at = target.transform.position;
				move["tx"] = at.x;
				move["ty"] = at.y;
				move["tz"] = at.z;
			}
			moves.Add(move);
		}
		try
		{
			if (moves.Count == 0)
			{
				if (File.Exists(file))
				{
					File.Delete(file);
				}
				return;
			}
			File.WriteAllText(file, moves.ToString());
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("MoveStructure: could not save the pending moves: " + ex.Message);
		}
	}

	// Server, when a level is ready: pending moves of the loaded save.
	private static void RestorePending()
	{
		Asset_Map map = GameParam.Map;
		if (!NetworkServer.active || map == null || string.IsNullOrEmpty(map.path))
		{
			return;
		}
		string file = System.IO.Path.Combine(map.path, SaveFile);
		if (!File.Exists(file))
		{
			return;
		}
		try
		{
			FactoryImprint[] buildings = UnityEngine.Object.FindObjectsOfType<FactoryImprint>();
			int restored = 0;
			foreach (JToken move in JArray.Parse(File.ReadAllText(file)))
			{
				Factory.AssetKey key = new Factory.AssetKey((string)move["factory"], (string)move["name"]);
				Vector3 position = new Vector3((float)move["x"], (float)move["y"], (float)move["z"]);
				GameObject building = Find(buildings, key, position);
				if (building == null || Pending.ContainsKey(building))
				{
					continue;
				}
				Description cost = new Description();
				foreach (JToken entry in move["cost"])
				{
					Type type = typeof(Adjectif).Assembly.GetType((string)entry["type"]);
					if (type != null)
					{
						cost.Add(Adjectif.New(type, (int)entry["count"]));
					}
				}
				Blueprint target = null;
				if (move["tx"] != null)
				{
					target = FindBlueprint(BlueprintAssetKeyResolver.Resolve(key), new Vector3((float)move["tx"], (float)move["ty"], (float)move["tz"]));
				}
				Pending[building] = new PendingMove { Cost = cost, Target = target };
				restored++;
			}
			Plugin.Log.LogInfo("MoveStructure: restored " + restored + " pending moves");
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("MoveStructure: could not read the pending moves: " + ex.Message);
		}
	}

	private static GameObject Find(FactoryImprint[] buildings, Factory.AssetKey key, Vector3 position)
	{
		foreach (FactoryImprint imprint in buildings)
		{
			if (imprint != null && !imprint.Released && imprint.AssetKey == key && (imprint.transform.position - position).sqrMagnitude < 0.01f)
			{
				return imprint.gameObject;
			}
		}
		return null;
	}

	// The new blueprint of a move, found again by its key and position after loading a save.
	private static Blueprint FindBlueprint(Factory.AssetKey key, Vector3 position)
	{
		foreach (Blueprint blueprint in UnityEngine.Object.FindObjectsOfType<Blueprint>())
		{
			if (blueprint != null && !blueprint.Released && !blueprint.IsAnti && blueprint.AssetKey == key && (blueprint.transform.position - position).sqrMagnitude < 0.01f)
			{
				return blueprint;
			}
		}
		return null;
	}

	private static bool IsLiveTarget(Blueprint target)
	{
		return target != null && !target.Released && !target.IsAnti && target.recepteur != null;
	}

	// Server: the anti-blueprint removes itself once the building is demolished. If one is still there a moment
	// later it has nothing left to demolish, so it is removed instead of staying as a ghost.
	private static void CheckDemolished()
	{
		if (ToCheck.Count == 0 || !NetworkServer.active)
		{
			return;
		}
		float now = Time.realtimeSinceStartup;
		for (int i = ToCheck.Count - 1; i >= 0; i--)
		{
			Demolished demolished = ToCheck[i];
			if (now < demolished.CheckAt)
			{
				continue;
			}
			ToCheck.RemoveAt(i);
			foreach (Blueprint blueprint in UnityEngine.Object.FindObjectsOfType<Blueprint>())
			{
				if (blueprint == null || blueprint.Released || !blueprint.IsAnti || blueprint.AssetKey != demolished.BlueprintKey || (blueprint.transform.position - demolished.Position).sqrMagnitude > 0.01f)
				{
					continue;
				}
				Plugin.Log.LogInfo("MoveStructure: removed the demolish ghost left at " + demolished.Position + " (" + blueprint.name + ", state " + blueprint.State + ")");
				blueprint.gameObject.SafeNetworkDestroy();
			}
		}
	}

	// Server: a building demolished by a move drops its contents; the materials it cost go into the new blueprint,
	// or on the ground when that blueprint is gone.
	internal static void OnRelease(GameObject building)
	{
		if (!NetworkServer.active || !Pending.TryGetValue(building, out PendingMove move))
		{
			return;
		}
		Description cost = move.Cost;
		Blueprint target = IsLiveTarget(move.Target) ? move.Target : null;
		Pending.Remove(building);
		FactoryImprint imprint = building.GetComponent<FactoryImprint>();
		if (imprint != null)
		{
			ToCheck.Add(new Demolished { BlueprintKey = BlueprintAssetKeyResolver.Resolve(imprint.AssetKey), Position = building.transform.position, CheckAt = Time.realtimeSinceStartup + 1f });
		}
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
		int delivered = 0;
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
				if (go == null)
				{
					continue;
				}
				// As a worker's delivery does it (Transaction.Store).
				if (target != null && target.recepteur.HasRoomFor(go))
				{
					target.recepteur.AddObject(go);
					go.GetComponent<GameComponent>().Soulever();
					delivered++;
				}
				else
				{
					dropped.Add(go);
				}
			}
		}
		if (dropped.Count > 0)
		{
			Recepteur.ScatterObjects(dropped, center);
		}
		Plugin.Log.LogInfo("Moved " + building.name + ": " + delivered + " material items into the new blueprint, " + dropped.Count + " dropped");
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
// Key actions on the move key (the machine shop on M) do not fire while it is held to move a building.
[Feature(Features.MoveStructure, Features.MoveStructureInfo)]
[HarmonyPatch(typeof(Brix.Utils.UI.KeyBindingsUtility), nameof(Brix.Utils.UI.KeyBindingsUtility.RegisterAction))]
internal static class MoveStructureKeyActionPatch
{
	private static void Prefix(ref Action<Rewired.InputActionEventData> action)
	{
		Action<Rewired.InputActionEventData> inner = action;
		action = (Rewired.InputActionEventData data) =>
		{
			if (!MoveStructure.KeyClaimed())
			{
				inner(data);
			}
		};
	}
}

[Feature(Features.MoveStructure, Features.MoveStructureInfo)]
[HarmonyPatch(typeof(BlueprintPlacementPicker), nameof(BlueprintPlacementPicker.PlaceBlueprint))]
internal static class MoveStructurePlacePatch
{
	// Drag placement (stockpiles) places extra slaves in the same click: those are dropped too.
	private static int _sentFrame = -1;

	private static bool Prefix(Slave s)
	{
		if (_sentFrame == Time.frameCount || MoveStructure.PickClickActive)
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
		Project project = UIGameObserver.projects.LastSelectedBuild;
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

// Saves the pending moves with the game.
[Feature(Features.MoveStructure, Features.MoveStructureInfo)]
[HarmonyPatch(typeof(Asset_Map), "Disk_Save")]
internal static class MoveStructureSavePatch
{
	private static void Postfix(Asset_Map __instance)
	{
		if (NetworkServer.active && !string.IsNullOrEmpty(__instance.path))
		{
			MoveStructure.WritePending(__instance.path);
		}
	}
}
