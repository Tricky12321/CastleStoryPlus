using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Locomotion;
using Brix.Input;
using Brix.Network;
using Brix.UI.GameSelector;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace CastleStoryPlus.Upgrades;

// Research > Sprint (a colony upgrade at the research station): a bricktron can sprint, walking and running
// [Sprint] SpeedBonus percent faster for a few seconds, then it has to cool down before it can sprint again. No stamina.
//   Tiers: higher tiers make the sprint longer and the cooldown shorter (SprintRules).
//   Triggers: by itself when a melee fighter is sent at an enemy from some way off (charge) and when a fighter runs to
//   safety (Retreat, flee); and by the [Sprint] Key for the selected bricktrons. The host decides: in multiplayer a
//   client's key press is sent to the host, which sprints only the sender's own team's bricktrons.
//   Speed: a plain factor on the walking and running speed, like Light Boots and Roads, so they multiply in any order.
//   Enemies never sprint. The host's settings count (the units walk on the host).
//   Visible: a puff of dust at the feet every few frames, from one pooled particle system; the host tells the other
//   players when a sprint starts, so they see it too.
[Feature(Features.Sprint, Features.SprintInfo)]
internal sealed class Sprint : MonoBehaviour
{
	private sealed class Run
	{
		public Transform Transform;

		public float Until;

		public float NextPuff;

		public Vector3 Last;
	}

	// The game's own messages end at 63; Castle Story Plus uses 200-213 (ModMessages).
	private const short CommandMessage = 212;

	private const short StartedMessage = 213;

	private const float PuffSeconds = 0.07f;

	private const float CleanSeconds = 10f;

	internal static ConfigEntry<int> SpeedBonus;

	internal static ConfigEntry<float> DurationSeconds;

	internal static ConfigEntry<float> CooldownSeconds;

	internal static ConfigEntry<KeyCode> Key;

	internal static ConfigEntry<bool> Auto;

	internal static ConfigEntry<float> MinChargeDistance;

	// Units that sprint now (and on a client: the ones the host said sprint, for the dust only).
	private static readonly Dictionary<GameObject, Run> Sprinting = new Dictionary<GameObject, Run>();

	// When a unit may sprint again (host).
	private static readonly Dictionary<GameObject, float> ReadyAt = new Dictionary<GameObject, float>();

	private static readonly List<GameObject> Ended = new List<GameObject>();

	private static List<GameObject> Selected = new List<GameObject>();

	private static ParticleSystem _dust;

	private static ParticleSystem.EmitParams _puff;

	private float _nextClean;

	private sealed class CommandNetMessage : MessageBase
	{
		public List<uint> Units = new List<uint>();

		public override void Serialize(NetworkWriter writer)
		{
			writer.Write((ushort)Units.Count);
			for (int i = 0; i < Units.Count; i++)
			{
				writer.Write(Units[i]);
			}
		}

		public override void Deserialize(NetworkReader reader)
		{
			int count = reader.ReadUInt16();
			Units = new List<uint>(System.Math.Min(count, SprintRules.MaxCommandUnits));
			for (int i = 0; i < count; i++)
			{
				uint id = reader.ReadUInt32();
				if (Units.Count < SprintRules.MaxCommandUnits)
				{
					Units.Add(id);
				}
			}
		}
	}

	private sealed class StartedNetMessage : MessageBase
	{
		public uint Unit;

		public float Seconds;

		public override void Serialize(NetworkWriter writer)
		{
			writer.Write(Unit);
			writer.Write(Seconds);
		}

		public override void Deserialize(NetworkReader reader)
		{
			Unit = reader.ReadUInt32();
			Seconds = reader.ReadSingle();
		}
	}

	private static void Enable()
	{
		SpeedBonus = Plugin.Cfg.Bind("Sprint", "SpeedBonus", 50, new ConfigDescription("How much faster (percent) a sprinting bricktron walks and runs; it multiplies with Light Boots and roads. In multiplayer the host's value counts.", new AcceptableValueRange<int>(0, 300)));
		DurationSeconds = Plugin.Cfg.Bind("Sprint", "DurationSeconds", 5f, new ConfigDescription("How many seconds a sprint lasts at the first tier (the next tiers last 30% and 60% longer).", new AcceptableValueRange<float>(1f, 30f)));
		CooldownSeconds = Plugin.Cfg.Bind("Sprint", "CooldownSeconds", 60f, new ConfigDescription("How many seconds a bricktron has to wait after a sprint at the first tier (the next tiers wait 25% and 50% less).", new AcceptableValueRange<float>(5f, 300f)));
		Key = Plugin.Cfg.Bind("Sprint", "Key", KeyCode.Alpha7, "Key that makes the selected bricktrons sprint (None: no key). 7 is not bound in the game or by the mod (G and the other letters are the game's). Not while typing or in a menu.");
		Auto = Plugin.Cfg.Bind("Sprint", "Auto", true, "Soldiers sprint by themselves when sent at an enemy from some way off, and fighters sprint when they run to safety (Retreat).");
		MinChargeDistance = Plugin.Cfg.Bind("Sprint", "MinChargeDistance", 10f, new ConfigDescription("A melee fighter sprints on its own at an enemy or an attack order only when it is at least this many blocks away.", new AcceptableValueRange<float>(2f, 60f)));
		SpeedBonus.SettingChanged += (object sender, System.EventArgs args) => RefreshText();
		DurationSeconds.SettingChanged += (object sender, System.EventArgs args) => RefreshText();
		CooldownSeconds.SettingChanged += (object sender, System.EventArgs args) => RefreshText();
		GameSession.OnLeave(Clear);
		GameSession.OnLevelReady(LevelReady);
		Plugin.Root.AddComponent<Sprint>();
		RefreshText();
	}

	private static void Clear()
	{
		Sprinting.Clear();
		ReadyAt.Clear();
	}

	// Whether the Sprint feature is on (the line stays registered either way, for the ids and the byte count).
	internal static bool Active => Plugin.LoadedFeatures.TryGetValue(Features.Sprint, out bool on) && on;

	private static float Bonus => (SpeedBonus != null) ? SpeedBonus.Value : 50;

	private static float BaseDuration => (DurationSeconds != null) ? DurationSeconds.Value : 5f;

	private static float BaseCooldown => (CooldownSeconds != null) ? CooldownSeconds.Value : 60f;

	// The upgrade window's text for each tier, with the settings' numbers.
	internal static void RefreshText()
	{
		if (UpgradeLines.All.Count <= UpgradeLines.Sprint)
		{
			return;
		}
		UpgradeLine line = UpgradeLines.All[UpgradeLines.Sprint];
		for (int tier = 1; tier <= line.Tiers; tier++)
		{
			line.Effect[tier] = "+" + Mathf.RoundToInt(Bonus) + "% speed for " + SprintRules.Duration(tier, BaseDuration).ToString("0.#") + " s, " + SprintRules.Cooldown(tier, BaseCooldown).ToString("0.#") + " s cooldown";
		}
	}

	// The factor on a unit's walking and running speed: the sprint's while it sprints, else 1.
	internal static float SpeedFactor(Locomotion4 locomotion)
	{
		if (Sprinting.Count == 0 || locomotion == null || !NetworkServer.active)
		{
			return 1f;
		}
		return (Sprinting.TryGetValue(locomotion.gameObject, out Run run) && Time.time < run.Until) ? SprintRules.Factor(Bonus) : 1f;
	}

	internal static bool IsSprinting(GameObject unit)
	{
		return unit != null && Sprinting.TryGetValue(unit, out Run run) && Time.time < run.Until;
	}

	private static bool IsEnemy(Labor labor)
	{
		Faction faction = labor.faction;
		if (faction == null || faction.isAI)
		{
			return true;
		}
		Profession profession = labor.Profession;
		return profession != null && profession.IsCorruptron();
	}

	// Host: starts a sprint for the unit if it has the research, is not an enemy and has cooled down. The auto
	// triggers pass the distance to where the unit is going (charge) or 0.
	internal static bool TryStart(Labor labor, SprintTrigger trigger, float distance = 0f)
	{
		if (SpeedBonus == null || !NetworkServer.active || labor == null || !labor.gameObject.activeInHierarchy)
		{
			return false;
		}
		if (trigger != SprintTrigger.Key && (Auto == null || !Auto.Value))
		{
			return false;
		}
		GameObject unit = labor.gameObject;
		int tier = TeamUpgrades.Tier(unit, UpgradeLines.Sprint);
		float now = Time.time;
		ReadyAt.TryGetValue(unit, out float ready);
		if (!SprintRules.CanStart(tier, IsEnemy(labor), IsSprinting(unit), now, ready))
		{
			return false;
		}
		Profession profession = labor.Profession;
		bool melee = profession != null && profession.IsStrongInMelee();
		if (!SprintRules.ShouldAutoStart(trigger, melee, distance, (MinChargeDistance != null) ? MinChargeDistance.Value : 10f))
		{
			return false;
		}
		float seconds = SprintRules.Duration(tier, BaseDuration);
		ReadyAt[unit] = SprintRules.ReadyAt(now, tier, BaseDuration, BaseCooldown);
		Begin(unit, seconds);
		if (Neo.multiplayer)
		{
			NetworkIdentity identity = unit.GetComponent<NetworkIdentity>();
			if (identity != null)
			{
				NetworkServer.SendToAll(StartedMessage, new StartedNetMessage { Unit = identity.netId.Value, Seconds = seconds });
			}
		}
		return true;
	}

	private static void Begin(GameObject unit, float seconds)
	{
		Vector3 position = unit.transform.position;
		Sprinting[unit] = new Run { Transform = unit.transform, Until = Time.time + seconds, NextPuff = 0f, Last = position };
	}

	// ---- the key ----

	private static bool KeyPressed()
	{
		if (Key == null || Key.Value == KeyCode.None || !UnityEngine.Input.GetKeyDown(Key.Value))
		{
			return false;
		}
		if (Brix.Engine.Voxel.Engine == null || InputModeController._mode == InputMode.blueprint || InputModeController.IsInMenu())
		{
			return false;
		}
		if (UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl) || UnityEngine.Input.GetKey(KeyCode.LeftAlt) || UnityEngine.Input.GetKey(KeyCode.RightAlt))
		{
			return false;
		}
		GameObject focused = (UnityEngine.EventSystems.EventSystem.current != null) ? UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject : null;
		InputField field = (focused != null) ? focused.GetComponent<InputField>() : null;
		return field == null || !field.isFocused;
	}

	// The key: the selected bricktrons of the local player's team sprint (the host decides).
	private static void KeyDown()
	{
		User user = User.LocalUser;
		Faction team = (user != null) ? user.faction : null;
		if (team == null)
		{
			return;
		}
		Selected.Clear();
		UIGameSelector.GetSelectedOfType(ref Selected, SelectionType.Unit);
		CommandNetMessage command = NetworkServer.active ? null : new CommandNetMessage();
		for (int i = 0; i < Selected.Count; i++)
		{
			GameObject unit = Selected[i];
			Labor labor = (unit != null) ? unit.GetComponent<Labor>() : null;
			if (labor == null || labor.faction != team)
			{
				continue;
			}
			if (command == null)
			{
				TryStart(labor, SprintTrigger.Key);
			}
			else
			{
				NetworkIdentity identity = unit.GetComponent<NetworkIdentity>();
				if (identity != null && command.Units.Count < SprintRules.MaxCommandUnits)
				{
					command.Units.Add(identity.netId.Value);
				}
			}
		}
		Selected.Clear();
		if (command != null && command.Units.Count > 0)
		{
			NetworkClient client = (UNetManager.instance != null) ? UNetManager.instance.LocalNetworkClient : null;
			if (client != null)
			{
				client.Send(CommandMessage, command);
			}
		}
	}

	// ---- network ----

	private static void LevelReady()
	{
		Clear();
		if (NetworkServer.active)
		{
			NetworkServer.UnregisterHandler(CommandMessage);
			NetworkServer.RegisterHandler(CommandMessage, CommandReceived);
		}
		NetworkClient client = (UNetManager.instance != null) ? UNetManager.instance.LocalNetworkClient : null;
		if (client == null)
		{
			return;
		}
		if (client.handlers.ContainsKey(StartedMessage))
		{
			client.UnregisterHandler(StartedMessage);
		}
		client.RegisterHandler(StartedMessage, StartedReceived);
	}

	// Host: a client's key press. Only bricktrons of the sender's own team sprint.
	private static void CommandReceived(NetworkMessage netMsg)
	{
		CommandNetMessage message = netMsg.ReadMessage<CommandNetMessage>();
		if (!NetworkServer.active)
		{
			return;
		}
		Faction team = TeamOf(netMsg.conn);
		if (!SprintRules.AcceptCommand(team != null, message.Units.Count))
		{
			return;
		}
		foreach (uint id in message.Units)
		{
			GameObject unit = NetworkServer.FindLocalObject(new NetworkInstanceId(id));
			Labor labor = (unit != null) ? unit.GetComponent<Labor>() : null;
			if (SprintRules.AcceptUnit(labor != null, labor != null && labor.faction == team))
			{
				TryStart(labor, SprintTrigger.Key);
			}
		}
	}

	private static Faction TeamOf(NetworkConnection connection)
	{
		if (connection == null || connection.playerControllers == null)
		{
			return null;
		}
		foreach (PlayerController controller in connection.playerControllers)
		{
			User user = (controller != null && controller.gameObject != null) ? controller.gameObject.GetComponent<User>() : null;
			if (user != null && user.faction != null)
			{
				return user.faction;
			}
		}
		return null;
	}

	// A client: the host started a sprint; shows the dust for as long as it lasts.
	private static void StartedReceived(NetworkMessage netMsg)
	{
		StartedNetMessage message = netMsg.ReadMessage<StartedNetMessage>();
		if (NetworkServer.active)
		{
			return;
		}
		GameObject unit = ClientScene.FindLocalObject(new NetworkInstanceId(message.Unit));
		if (unit != null)
		{
			Begin(unit, Mathf.Clamp(message.Seconds, 0f, 60f));
		}
	}

	// ---- dust and upkeep ----

	private static bool _dustFailed;

	private static ParticleSystem MakeDust(Transform parent)
	{
		GameObject go = new GameObject("SprintDust");
		try
		{
			return BuildDust(go, parent);
		}
		catch (System.Exception ex)
		{
			Object.Destroy(go);
			_dustFailed = true;
			Plugin.Log.LogWarning("Sprint: no dust trail: " + ex.Message);
			return null;
		}
	}

	private static ParticleSystem BuildDust(GameObject go, Transform parent)
	{
		go.transform.SetParent(parent, false);
		ParticleSystem system = go.AddComponent<ParticleSystem>();
		system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
		ParticleSystem.MainModule main = system.main;
		main.loop = false;
		main.playOnAwake = false;
		main.simulationSpace = ParticleSystemSimulationSpace.World;
		main.startLifetime = 0.6f;
		main.startSpeed = 0f;
		main.startSize = 0.3f;
		main.startColor = new Color(0.78f, 0.72f, 0.6f, 0.55f);
		main.maxParticles = 600;
		ParticleSystem.EmissionModule emission = system.emission;
		emission.enabled = false;
		ParticleSystem.SizeOverLifetimeModule size = system.sizeOverLifetime;
		size.enabled = true;
		size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.5f), new Keyframe(1f, 1.6f)));
		ParticleSystem.ColorOverLifetimeModule fade = system.colorOverLifetime;
		fade.enabled = true;
		Gradient gradient = new Gradient();
		gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) }, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
		fade.color = gradient;
		ParticleSystemRenderer renderer = go.GetComponent<ParticleSystemRenderer>();
		renderer.renderMode = ParticleSystemRenderMode.Billboard;
		renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
		renderer.receiveShadows = false;
		Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
		renderer.material = new Material(shader);
		system.Play();
		return system;
	}

	private void Update()
	{
		try
		{
			Step();
		}
		catch (System.Exception ex)
		{
			if (Time.unscaledTime >= _nextWarn)
			{
				_nextWarn = Time.unscaledTime + 30f;
				Plugin.Log.LogWarning("Sprint: " + ex);
			}
		}
	}

	private float _nextWarn;

	private void Step()
	{
		if (Key != null && KeyPressed())
		{
			KeyDown();
		}
		if (Sprinting.Count == 0)
		{
			return;
		}
		if (_dust == null && !_dustFailed)
		{
			_dust = MakeDust(transform);
		}
		float now = Time.time;
		foreach (KeyValuePair<GameObject, Run> entry in Sprinting)
		{
			Run run = entry.Value;
			if (entry.Key == null || now >= run.Until)
			{
				Ended.Add(entry.Key);
				continue;
			}
			if (_dust == null || now < run.NextPuff)
			{
				continue;
			}
			run.NextPuff = now + PuffSeconds;
			Vector3 position = run.Transform.position;
			// Only while it moves.
			if ((position - run.Last).sqrMagnitude < 0.0004f)
			{
				continue;
			}
			run.Last = position;
			_puff.position = position + new Vector3(0f, 0.1f, 0f) + Random.insideUnitSphere * 0.15f;
			_puff.velocity = new Vector3(0f, 0.5f, 0f) + Random.insideUnitSphere * 0.3f;
			_dust.Emit(_puff, 1);
		}
		for (int i = 0; i < Ended.Count; i++)
		{
			Sprinting.Remove(Ended[i]);
		}
		Ended.Clear();
		if (Time.unscaledTime >= _nextClean)
		{
			_nextClean = Time.unscaledTime + CleanSeconds;
			Clean(now);
		}
	}

	// Forgets cooldowns that are over and units that are gone.
	private static void Clean(float now)
	{
		foreach (KeyValuePair<GameObject, float> entry in ReadyAt)
		{
			if (entry.Key == null || now >= entry.Value)
			{
				Ended.Add(entry.Key);
			}
		}
		for (int i = 0; i < Ended.Count; i++)
		{
			ReadyAt.Remove(Ended[i]);
		}
		Ended.Clear();
	}
}

// The sprint's speed: a factor on the walking and running speed, as Light Boots (LightBootsPatch) and Roads
// (RoadsSpeedPatch) are; the product does not depend on which postfix runs first.
[Feature(Features.Sprint, Features.SprintInfo)]
[HarmonyPatch]
internal static class SprintSpeedPatch
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.PropertyGetter(typeof(Walking), "SpeedMperS");
		yield return AccessTools.PropertyGetter(typeof(Running), "SpeedMperS");
	}

	private static void Postfix(LocomotionWalkableCycleState __instance, ref float __result)
	{
		__result *= Sprint.SpeedFactor(__instance.Locomotion);
	}
}

// A melee fighter sent at an enemy (the instinct's or the player's attack order) sprints if it has some way to go.
[Feature(Features.Sprint, Features.SprintInfo)]
[HarmonyPatch]
internal static class SprintChargeTargetPatch
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(Labor), nameof(Labor.GoAttack));
		yield return AccessTools.Method(typeof(Labor), nameof(Labor.GoAttackWithPriority));
	}

	private static void Prefix(Labor __instance, GameObject target)
	{
		// Only at an enemy unit: GoAttackDirectly (path blockers, structures) is left out, and so is any target that is
		// not a hostile bricktron or corruptron.
		if (target != null && NetworkServer.active && target.GetComponent<Labor>() != null && Affiliation.IsEnemy(__instance.gameObject, target))
		{
			Sprint.TryStart(__instance, SprintTrigger.Charge, Vector3.Distance(__instance.transform.position, target.transform.position));
		}
	}
}

// The same for an attack-move order to a place.
[Feature(Features.Sprint, Features.SprintInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.MoveAttack))]
internal static class SprintChargePlacePatch
{
	private static void Prefix(Labor __instance, Location location)
	{
		if (location != null && !location.IsEmpty && location.HasSpecificPosition && NetworkServer.active)
		{
			Sprint.TryStart(__instance, SprintTrigger.Charge, Vector3.Distance(__instance.transform.position, location.Position));
		}
	}
}
