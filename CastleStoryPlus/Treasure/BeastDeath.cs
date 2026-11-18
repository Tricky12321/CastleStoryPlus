using System.Collections.Generic;
using Brix.External.Factories;
using Brix.Game;
using Brix.Lua;
using Brix.Game.Components;
using Brix.Game.AI.Damage;
using Brix.Game.AI;
using Brix.Game.Semantique;
using Brix.Transactions;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Treasure;

// A beast's death: its corpse (the game's Corpse, which the beast's model lies on as a ragdoll, BeastRagdoll) lies
// DebrisSeconds, then crumbles into what the beast was made of instead of the corruptron's debris, and its soul goes
// up as a white firefly (a bricktron's is blue, a corruptron's purple):
// - Ironhorn (iron rhino): raw iron;
// - Spirestalker (crystal giraffe): orange and blue crystal;
// - Boulderback (stone gorilla): stone;
// - Cindermane (fire lion): coal (with Metallurgy, else orange crystal) and orange crystal.
// The soul is marked by a face no named firefly has (the face is synced with the firefly; an unnamed firefly has no
// use for it), so every peer draws it white (BeastSouls).
internal static class BeastDeath
{
	// The face that marks a beast's soul.
	internal const Face.Expression SoulFace = (Face.Expression)900;

	internal const float DebrisSeconds = 5f;

	internal static bool IsBeastCorpse(Corpse corpse)
	{
		BeastBody body = (corpse != null) ? corpse.GetComponent<BeastBody>() : null;
		return body != null && body.Dead;
	}

	// What the beast leaves, at the spot (host).
	internal static void Drop(BeastInfo info, Vector3 at)
	{
		List<GameObject> drops = new List<GameObject>();
		switch (info.Kind)
		{
			case BeastKind.Ironhorn:
				Spawn(drops, RawResources.RawIron, Random.Range(4, 7), at);
				break;
			case BeastKind.Spirestalker:
				Spawn(drops, RawResources.RawOrangeCrystal, Random.Range(2, 4), at);
				Spawn(drops, RawResources.RawBlueCrystal, Random.Range(2, 4), at);
				break;
			case BeastKind.Boulderback:
				Spawn(drops, RawResources.RawStone, Random.Range(5, 8), at);
				break;
			case BeastKind.Cindermane:
				if (Plugin.IsEnabled(Features.Metallurgy))
				{
					Spawn(drops, Metallurgy.Metallurgy.CoalKey, Random.Range(3, 5), at);
					Spawn(drops, RawResources.RawOrangeCrystal, Random.Range(1, 3), at);
				}
				else
				{
					Spawn(drops, RawResources.RawOrangeCrystal, Random.Range(3, 5), at);
				}
				break;
		}
		Recepteur.ScatterObjects(drops, at);
	}

	private static void Spawn(List<GameObject> drops, Factory.AssetKey key, int count, Vector3 at)
	{
		for (int i = 0; i < count; i++)
		{
			GameObject go = Transactor.SpawnUNet(key, null, at + Vector3.up * 0.5f, Quaternion.identity);
			if (go != null)
			{
				drops.Add(go);
			}
		}
	}
}

// The beast's corpse lies a little longer before it crumbles, so its ragdoll falls and comes to rest first.
[Feature(Features.Treasure, Features.TreasureInfo)]
[HarmonyPatch(typeof(Corpse), nameof(Corpse.Spawn))]
internal static class BeastCorpseDelayPatch
{
	[HarmonyPriority(Priority.Low)]
	private static void Postfix(Corpse __result)
	{
		if (BeastDeath.IsBeastCorpse(__result))
		{
			__result.debrisDelay = BeastDeath.DebrisSeconds;
		}
	}
}

// The crumbling corpse leaves the beast's materials instead of the debris (host).
[Feature(Features.Treasure, Features.TreasureInfo)]
[HarmonyPatch(typeof(Corpse), "SpawnDebris")]
internal static class BeastDebrisPatch
{
	private static bool Prefix(Corpse __instance)
	{
		if (!BeastDeath.IsBeastCorpse(__instance))
		{
			return true;
		}
		if (NetworkServer.active)
		{
			BeastBody body = __instance.GetComponent<BeastBody>();
			BeastDeath.Drop(body.Info, __instance.transform.position);
		}
		return false;
	}
}

// The beast's soul is marked, to be drawn white (host).
[Feature(Features.Treasure, Features.TreasureInfo)]
[HarmonyPatch(typeof(Corpse), "EmitFirefly")]
internal static class BeastSoulMarkPatch
{
	private static void Postfix(Corpse __instance)
	{
		if (__instance.firefly != null && BeastDeath.IsBeastCorpse(__instance))
		{
			__instance.firefly.NetworklaborFace = BeastDeath.SoulFace;
		}
	}
}

// Draws the beasts' souls white: the firefly's own blue materials, copied white (on every peer; the game sets the
// materials again now and then, so they are put back each time).
[Feature(Features.Treasure, Features.TreasureInfo)]
[HarmonyPatch(typeof(Firefly), nameof(Firefly.UpdateVisuals))]
internal class BeastSouls : MonoBehaviour
{
	private const float CheckSeconds = 0.25f;

	private static Material _body;

	private static Material _particle;

	private static Material _trail;

	private float _nextCheck;

	private static void Enable()
	{
		Plugin.Root.AddComponent<BeastSouls>();
	}

	private static void Postfix(Firefly __instance)
	{
		Whiten(__instance);
	}

	private void Update()
	{
		if (Time.time < _nextCheck)
		{
			return;
		}
		_nextCheck = Time.time + CheckSeconds;
		foreach (Firefly firefly in Firefly._allFireflies)
		{
			Whiten(firefly);
		}
	}

	private static void Whiten(Firefly firefly)
	{
		if (firefly == null || firefly.laborFace != BeastDeath.SoulFace || firefly.IsNamed)
		{
			return;
		}
		if (_body == null)
		{
			_body = White(firefly.mat_blue);
			_particle = White(firefly.mat_blueParticle);
			_trail = White(firefly.mat_blueTrail);
		}
		MeshRenderer mesh = firefly.GetComponent<MeshRenderer>();
		if (mesh != null && _body != null && mesh.sharedMaterial != _body)
		{
			mesh.sharedMaterial = _body;
		}
		ParticleSystemRenderer particles = firefly.GetComponent<ParticleSystemRenderer>();
		if (particles != null && _particle != null && particles.sharedMaterial != _particle)
		{
			particles.sharedMaterial = _particle;
		}
		TrailRenderer trail = firefly.GetComponent<TrailRenderer>();
		if (trail != null && _trail != null && trail.sharedMaterial != _trail)
		{
			trail.sharedMaterial = _trail;
		}
	}

	private static Material White(Material blue)
	{
		if (blue == null)
		{
			return null;
		}
		Material white = new Material(blue) { name = blue.name + " (beast soul)" };
		foreach (string property in new[] { "_Color", "_TintColor", "_EmissionColor" })
		{
			if (white.HasProperty(property))
			{
				Color color = white.GetColor(property);
				float strength = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
				white.SetColor(property, new Color(strength, strength, strength, color.a));
			}
		}
		return white;
	}
}

// The soul of an enemy or beast slain by a player's unit flies to that player's nearest home crystal and hovers round
// it, where the crystal catches it (the game's own capture: it wanders round it until it turns the crystal's own). The
// game sent it to a nest of its own faction or of the default allies, and with none there it only rose to the sky;
// a soul with no player's hand in its death still does. The killer is the faction of the last player's unit that hit
// the unit, carried on to its corpse and its soul. Host side (where the fireflies are steered).
[Feature]
[HarmonyPatch]
internal static class SoulsToCrystal
{
	private static readonly Dictionary<Labor, Faction> LastHitBy = new Dictionary<Labor, Faction>();

	private static readonly Dictionary<Corpse, Faction> CorpseKiller = new Dictionary<Corpse, Faction>();

	private static readonly Dictionary<Firefly, Faction> SoulKiller = new Dictionary<Firefly, Faction>();

	private static void Enable()
	{
		GameSession.OnLeave(() =>
		{
			LastHitBy.Clear();
			CorpseKiller.Clear();
			SoulKiller.Clear();
		});
	}

	[HarmonyPatch(typeof(BricktronDamageReceiver), nameof(BricktronDamageReceiver.ApplyDamage), new[] { typeof(float), typeof(DamageType), typeof(DamageReaction), typeof(Vector3), typeof(Vector3), typeof(float), typeof(GameObject) })]
	[HarmonyPrefix]
	private static void Hit(BricktronDamageReceiver __instance, float value, GameObject attacker)
	{
		if (value <= 0f || attacker == null || !NetworkServer.active)
		{
			return;
		}
		Labor victim = __instance.Labor;
		Faction faction = Faction.GetFaction(attacker);
		if (victim != null && faction != null && !faction.isAI)
		{
			if (LastHitBy.Count > 500)
			{
				Prune(LastHitBy);
			}
			LastHitBy[victim] = faction;
		}
	}

	[HarmonyPatch(typeof(Corpse), nameof(Corpse.Spawn))]
	[HarmonyPostfix]
	private static void Died(GameObject original, Corpse __result)
	{
		Labor labor = (original != null) ? original.GetComponent<Labor>() : null;
		if (labor == null || __result == null || !LastHitBy.TryGetValue(labor, out Faction killer))
		{
			return;
		}
		LastHitBy.Remove(labor);
		Achievements.AchievementHooks.Killed(labor, killer);
		if (CorpseKiller.Count > 200)
		{
			Prune(CorpseKiller);
		}
		CorpseKiller[__result] = killer;
	}

	[HarmonyPatch(typeof(Corpse), "EmitFirefly")]
	[HarmonyPostfix]
	private static void SoulOut(Corpse __instance)
	{
		if (__instance.firefly == null)
		{
			return;
		}
		// Fireflies come from a pool: a soul nobody killed must not keep the killer of the one before it.
		SoulKiller.Remove(__instance.firefly);
		if (!CorpseKiller.TryGetValue(__instance, out Faction killer))
		{
			return;
		}
		CorpseKiller.Remove(__instance);
		if (SoulKiller.Count > 200)
		{
			Prune(SoulKiller);
		}
		SoulKiller[__instance.firefly] = killer;
	}

	[HarmonyPatch(typeof(Firefly), nameof(Firefly.GetNearestBestNest))]
	[HarmonyPostfix]
	private static void ToKillersCrystal(Firefly __instance, ref GameObject __result)
	{
		if (!NetworkServer.active || __instance == null || __instance.IsNamed || !SoulKiller.TryGetValue(__instance, out Faction killer))
		{
			return;
		}
		Faction own = __instance.GetComponent<AffiliationComponent>()?.faction;
		if (own == null || !own.isAI)
		{
			// Caught and turned the crystal's own: no longer an enemy's soul.
			SoulKiller.Remove(__instance);
			return;
		}
		GameObject nearest = null;
		float best = float.MaxValue;
		Vector3 at = __instance.transform.position;
		foreach (LuaDriver crystal in LuaDriverHQ.GetStaticDriver("fy_Crystal")._allInstances)
		{
			if (crystal == null || crystal.faction != killer)
			{
				continue;
			}
			FireflyNest nest = crystal.GetComponentInChildren<FireflyNest>();
			if (nest == null || !nest.isHome)
			{
				continue;
			}
			float distance = Vector3.Distance(nest.transform.position, at);
			if (distance < best)
			{
				best = distance;
				nearest = crystal.gameObject;
			}
		}
		if (nearest != null)
		{
			__result = nearest;
		}
	}

	// A crystal catches an enemy's soul (and gets its energy) only when that crystal's faction killed it: the game let
	// any enemy soul that drifted into a player's crystal be caught, souls of enemies the player never fought too. Any
	// other enemy soul that reaches a player's crystal rises to the sky (refused by the nest, the game left it
	// undecided, hanging still in the air).
	[HarmonyPatch(typeof(Firefly), "OnTriggerEnter")]
	[HarmonyPrefix]
	private static bool OnlyKilled(Firefly __instance, Collider collider)
	{
		if (!NetworkServer.active || __instance.IsNamed)
		{
			return true;
		}
		FireflyNest nest = collider.GetComponentInParent<FireflyNest>();
		Faction own = __instance.GetComponent<AffiliationComponent>()?.faction;
		if (nest == null || nest.faction == null || nest.faction.isAI || !nest.isHome || own == null || !own.isAI)
		{
			return true;
		}
		if (SoulKiller.TryGetValue(__instance, out Faction killer) && killer == nest.faction)
		{
			return true;
		}
		if (__instance.movementMode != FireflyMovementMode.GoingToHeaven)
		{
			__instance.GoToHeaven();
		}
		return false;
	}

	// A soul left undecided (refused by a nest, untracked, nowhere to land) hung still in the air for good: the game's
	// ReevaluatePriorities only sends a soul on its way when it is not undecided. Now it flies to its best nest (a slain
	// enemy's: its killer's crystal; a player's: its own crystal) and, with none, rises to the sky. Named souls (dead
	// bricktrons waiting to respawn) are left to the game.
	[HarmonyPatch(typeof(Firefly), "ReevaluatePriorities")]
	[HarmonyPostfix]
	private static void NowhereToGo(Firefly __instance)
	{
		if (!NetworkServer.active || __instance.IsNamed || __instance.movementMode != FireflyMovementMode.Undecided || __instance.spawningStep != 0)
		{
			return;
		}
		Faction own = __instance.GetComponent<AffiliationComponent>()?.faction;
		GameObject nest = (own != null) ? __instance.GetNearestBestNest(own) : null;
		// An enemy's soul nobody here killed is not let into a player's crystal: it rises.
		if (nest != null && (own == null || !own.isAI || SoulKiller.ContainsKey(__instance) || Faction.GetFaction(nest) == own))
		{
			__instance.TravelTo(nest);
			return;
		}
		__instance.GoToHeaven();
	}

	// Drops the entries whose unit, corpse or firefly is gone.
	private static void Prune<T>(Dictionary<T, Faction> entries) where T : Component
	{
		List<T> gone = new List<T>();
		foreach (KeyValuePair<T, Faction> entry in entries)
		{
			if (entry.Key == null || !entry.Key.gameObject.activeInHierarchy)
			{
				gone.Add(entry.Key);
			}
		}
		foreach (T key in gone)
		{
			entries.Remove(key);
		}
	}
}

// A caught soul hovers round the crystal for the game's capture time (Firefly.timeToAbsorption, 25 s) before it is
// the crystal's: the game sent a firefly wandering round another faction's nest (an enemy's soul at the player's
// crystal) up to the sky at once.
[Feature]
[HarmonyPatch(typeof(Firefly), "ReevaluatePriorities")]
internal static class SoulsHover
{
	private static bool Prefix(Firefly __instance)
	{
		if (__instance.IsNamed || __instance.movementMode != FireflyMovementMode.WanderingAround || !__instance.IsAlien())
		{
			return true;
		}
		return !(__instance.captureTimer.running && !__instance.captureTimer.Check());
	}
}

// A soul told to hover round nothing (no nest found for it) rises to the sky instead of hanging still in the air.
[Feature]
[HarmonyPatch(typeof(Firefly), nameof(Firefly.GoWanderAround), typeof(GameObject))]
internal static class SoulsNotStuck
{
	private static bool Prefix(Firefly __instance, GameObject target)
	{
		if (target != null || !NetworkServer.active)
		{
			return true;
		}
		__instance.GoToHeaven();
		return false;
	}
}
