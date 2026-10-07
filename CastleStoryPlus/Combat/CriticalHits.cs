using System.Collections.Generic;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.Damage;
using Brix.Game.Locomotion;
using CastleStoryPlus.Core;
using CastleStoryPlus.Experience;
using CastleStoryPlus.UI;
using CastleStoryPlus.Upgrades;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace CastleStoryPlus.Combat;

// Critical hits: from combat level 3 a bricktron's sword strike or shot can do double damage, a 5% chance at level 3,
// 10% at 5, 15% at 8 and 20% at 10. A red "2x damage" rises over the bricktron that made it (when a strike lands, when
// a shot leaves), and the weapon glows red for that hit: the sword through its swing, the arrow, bolt or magic shot
// with its trail in flight. The host rolls the hit and doubles its damage (melee: the strike's own attack; shots:
// the projectile's attack when it hits, through ShotDamage); the looks are sent to the clients with the attacker,
// so they need the mod.
[Feature(Features.CriticalHits, Features.CriticalHitsInfo)]
internal class CriticalHits : MonoBehaviour
{
	internal const float Multiplier = 2f;

	// Debug menu: every hit and shot of a player's bricktron is a critical hit, whatever its level. Not saved.
	internal static bool Forced;

	private const int CritHash = 1129595240;

	// What the host tells the clients.
	private const byte Swing = 0;

	private const byte Hit = 1;

	private const byte Missed = 2;

	private const byte Shot = 3;

	// The sword glows from the swing's start until just after the hit; never longer than this.
	private const float SwingSeconds = 4f;

	private const float AfterHitSeconds = 0.35f;

	private const float TextSeconds = 1.2f;

	// How far the text rises (screen pixels at 1080p) and starts over the head.
	private const float TextRise = 40f;

	private const float AboveHead = 0.35f;

	private const float GlowRange = 2.5f;

	private const float GlowIntensity = 2.5f;

	private static readonly Color TextRed = new Color(1f, 0.2f, 0.15f, 1f);

	private class Glow
	{
		public readonly List<GameObject> Weapons = new List<GameObject>();

		public readonly List<GameObject> Lights = new List<GameObject>();

		public float Until;
	}

	private class Popup
	{
		public GameObject Attacker;

		public float Head;

		public float Started;

		public Text Label;
	}

	// Host: critical strikes not landed yet, with when they were rolled.
	internal static readonly Dictionary<MeleeAttack, float> Strikes = new Dictionary<MeleeAttack, float>();

	private static CriticalHits _instance;

	private readonly Dictionary<GameObject, Glow> _glows = new Dictionary<GameObject, Glow>();

	private readonly List<Popup> _popups = new List<Popup>();

	private readonly List<GameObject> _ended = new List<GameObject>();

	private readonly List<MeleeAttack> _stale = new List<MeleeAttack>();

	private Canvas _canvas;

	private static void Enable()
	{
		NetworkBehaviour.RegisterRpcDelegate(typeof(BricktronDamageReceiver), CritHash, InvokeCriticalHit);
		_instance = Plugin.Root.AddComponent<CriticalHits>();
		GameSession.OnLeave(() =>
		{
			Strikes.Clear();
			if (_instance != null)
			{
				_instance.Clear();
			}
		});
	}

	// The chance of a critical hit at a combat level.
	internal static float Chance(int level)
	{
		if (level >= 10)
		{
			return 0.2f;
		}
		if (level >= 8)
		{
			return 0.15f;
		}
		if (level >= 5)
		{
			return 0.1f;
		}
		return (level >= 3) ? 0.05f : 0f;
	}

	internal static bool Roll(GameObject attacker)
	{
		Labor labor = (attacker != null) ? attacker.GetComponent<Labor>() : null;
		if (labor == null)
		{
			return false;
		}
		if (Forced)
		{
			Faction faction = Faction.GetFaction(attacker);
			if (faction != null && !faction.isAI)
			{
				return true;
			}
		}
		float chance = Chance(WorkerExperience.CombatLevel(labor));
		return chance > 0f && Random.value < chance;
	}

	internal static void StrikeRolled(MeleeAttack attack)
	{
		Strikes[attack] = Time.time;
		Send(attack.Attacker, Swing, null);
	}

	internal static void StrikeLanded(MeleeAttack attack)
	{
		if (attack != null && Strikes.Remove(attack))
		{
			Send(attack.Attacker, Hit, null);
		}
	}

	internal static void StrikeMissed(MeleeAttack attack)
	{
		if (attack != null && Strikes.Remove(attack))
		{
			Send(attack.Attacker, Missed, null);
		}
	}

	internal static void ShotFired(GameObject attacker, KinematicProjectile projectile)
	{
		ShotDamage.Scale(projectile, Multiplier);
		Send(attacker, Shot, projectile.gameObject);
	}

	// Host: shown here, and sent to the clients with the attacker.
	private static void Send(GameObject attacker, byte kind, GameObject projectile)
	{
		if (attacker == null)
		{
			return;
		}
		Show(attacker, kind, projectile);
		BricktronDamageReceiver receiver = attacker.GetComponent<BricktronDamageReceiver>();
		NetworkIdentity identity = attacker.GetComponent<NetworkIdentity>();
		if (receiver == null || identity == null)
		{
			return;
		}
		NetworkWriter writer = new NetworkWriter();
		writer.Write((short)0);
		writer.Write((short)2);
		writer.WritePackedUInt32((uint)CritHash);
		writer.Write(identity.netId);
		writer.Write(kind);
		writer.Write(projectile);
		receiver.SendRPCInternal(writer, 0, "RpcCriticalHit");
	}

	// Clients (the host has shown it already).
	private static void InvokeCriticalHit(NetworkBehaviour obj, NetworkReader reader)
	{
		if (NetworkServer.active)
		{
			return;
		}
		byte kind = reader.ReadByte();
		GameObject projectile = reader.ReadGameObject();
		Show(obj.gameObject, kind, projectile);
	}

	private static void Show(GameObject attacker, byte kind, GameObject projectile)
	{
		if (_instance == null || attacker == null)
		{
			return;
		}
		switch (kind)
		{
		case Swing:
			_instance.GlowWeapons(attacker, Time.time + SwingSeconds);
			break;
		case Hit:
			_instance.AddPopup(attacker);
			_instance.EndGlow(attacker, Time.time + AfterHitSeconds);
			break;
		case Missed:
			_instance.EndGlow(attacker, Time.time);
			break;
		case Shot:
			_instance.AddPopup(attacker);
			GlowTint.ApplyBolt(projectile, GlowTint.Red);
			break;
		}
	}

	private void Update()
	{
		float now = Time.time;
		_ended.Clear();
		foreach (KeyValuePair<GameObject, Glow> entry in _glows)
		{
			if (entry.Key == null || now >= entry.Value.Until)
			{
				_ended.Add(entry.Key);
			}
		}
		foreach (GameObject attacker in _ended)
		{
			StopGlow(attacker);
		}
		// A strike that never ended (its bricktron died mid swing) is dropped.
		if (Strikes.Count > 0)
		{
			_stale.Clear();
			foreach (KeyValuePair<MeleeAttack, float> entry in Strikes)
			{
				if (now - entry.Value > SwingSeconds)
				{
					_stale.Add(entry.Key);
				}
			}
			foreach (MeleeAttack attack in _stale)
			{
				Strikes.Remove(attack);
			}
		}
	}

	private void LateUpdate()
	{
		if (_popups.Count == 0)
		{
			return;
		}
		Camera camera = Camera.main;
		float scale = (_canvas != null && _canvas.scaleFactor > 0f) ? _canvas.scaleFactor : 1f;
		for (int i = _popups.Count - 1; i >= 0; i--)
		{
			Popup popup = _popups[i];
			float age = Time.unscaledTime - popup.Started;
			if (popup.Attacker == null || popup.Label == null || age >= TextSeconds)
			{
				if (popup.Label != null)
				{
					Destroy(popup.Label.gameObject);
				}
				_popups.RemoveAt(i);
				continue;
			}
			Vector3 head = popup.Attacker.transform.position + Vector3.up * (popup.Head + AboveHead);
			Vector3 screen = (camera != null) ? camera.WorldToScreenPoint(head) : Vector3.back;
			bool visible = screen.z > 0f;
			popup.Label.gameObject.SetActive(visible);
			if (!visible)
			{
				continue;
			}
			float t = age / TextSeconds;
			popup.Label.rectTransform.position = new Vector3(screen.x, screen.y + TextRise * t * scale, 0f);
			Color color = TextRed;
			// Full for the first half, then fading out.
			color.a = Mathf.Clamp01(2f - 2f * t);
			popup.Label.color = color;
		}
	}

	// The sword (any melee weapon the bricktron holds) red, with a red light on it.
	private void GlowWeapons(GameObject attacker, float until)
	{
		if (!_glows.TryGetValue(attacker, out Glow glow))
		{
			glow = new Glow();
			foreach (MeleeWeapon weapon in attacker.GetComponentsInChildren<MeleeWeapon>())
			{
				glow.Weapons.Add(weapon.gameObject);
			}
			if (glow.Weapons.Count == 0)
			{
				Profession profession = attacker.GetComponent<Profession>();
				Tool tool = (profession != null) ? profession.GetMeleeWeapon() : null;
				if (tool != null)
				{
					glow.Weapons.Add(tool.gameObject);
				}
			}
			foreach (GameObject weapon in glow.Weapons)
			{
				// Its picture's metal red (the game's shaders have no colour to tint), over its upgrade tier.
				UpgradeVisuals.SetCritical(weapon, true);
				GameObject lightGo = new GameObject("CastleStoryPlusCritGlow");
				lightGo.transform.SetParent(weapon.transform, worldPositionStays: false);
				Light light = lightGo.AddComponent<Light>();
				light.type = LightType.Point;
				light.color = GlowTint.Red;
				light.range = GlowRange;
				light.intensity = GlowIntensity;
				light.shadows = LightShadows.None;
				glow.Lights.Add(lightGo);
			}
			_glows[attacker] = glow;
		}
		glow.Until = until;
	}

	private void EndGlow(GameObject attacker, float until)
	{
		if (_glows.TryGetValue(attacker, out Glow glow))
		{
			glow.Until = Mathf.Min(glow.Until, until);
		}
	}

	private void StopGlow(GameObject attacker)
	{
		if (!_glows.TryGetValue(attacker, out Glow glow))
		{
			return;
		}
		_glows.Remove(attacker);
		foreach (GameObject weapon in glow.Weapons)
		{
			UpgradeVisuals.SetCritical(weapon, false);
		}
		foreach (GameObject lightGo in glow.Lights)
		{
			if (lightGo != null)
			{
				Destroy(lightGo);
			}
		}
	}

	private void AddPopup(GameObject attacker)
	{
		if (_canvas == null)
		{
			_canvas = UiKit.CreateCanvas("CastleStoryPlusCriticalHits", transform, 0).GetComponent<Canvas>();
			_canvas.GetComponent<GraphicRaycaster>().enabled = false;
		}
		Text label = UiKit.CreateText(_canvas.transform, "2x damage", 24, TextRed, TextAnchor.MiddleCenter);
		label.fontStyle = FontStyle.Bold;
		label.rectTransform.sizeDelta = new Vector2(200f, 36f);
		Outline outline = label.gameObject.AddComponent<Outline>();
		outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
		outline.effectDistance = new Vector2(2f, -2f);
		label.gameObject.SetActive(false);
		_popups.Add(new Popup { Attacker = attacker, Head = HeadHeight(attacker), Started = Time.unscaledTime, Label = label });
	}

	// The top of the bricktron's model above its feet (a giant bricktron is taller).
	private static float HeadHeight(GameObject unit)
	{
		float height = 1f;
		bool any = false;
		foreach (Renderer renderer in unit.GetComponentsInChildren<Renderer>())
		{
			if (renderer is ParticleSystemRenderer || !renderer.enabled)
			{
				continue;
			}
			float top = renderer.bounds.max.y - unit.transform.position.y;
			height = any ? Mathf.Max(height, top) : top;
			any = true;
		}
		return Mathf.Clamp(height, 0.5f, 4f);
	}

	private void Clear()
	{
		_ended.Clear();
		_ended.AddRange(_glows.Keys);
		foreach (GameObject attacker in _ended)
		{
			StopGlow(attacker);
		}
		_glows.Clear();
		foreach (Popup popup in _popups)
		{
			if (popup.Label != null)
			{
				Destroy(popup.Label.gameObject);
			}
		}
		_popups.Clear();
	}
}

// Host: a sword strike rolls for a critical hit when it starts; a critical one does double damage.
[Feature(Features.CriticalHits, Features.CriticalHitsInfo)]
[HarmonyPatch(typeof(MeleeWeapon), nameof(MeleeWeapon.CreateAttack))]
internal static class CriticalStrikePatch
{
	private static void Postfix(MeleeAttack __result, GameObject attacker)
	{
		if (!NetworkServer.active || __result == null || __result.Attack == null || !CriticalHits.Roll(attacker))
		{
			return;
		}
		// The strike's own attack (made for this strike), so no other strike is changed.
		__result.Attack.damage *= CriticalHits.Multiplier;
		CriticalHits.StrikeRolled(__result);
	}
}

[Feature(Features.CriticalHits, Features.CriticalHitsInfo)]
[HarmonyPatch(typeof(MeleeAttack), nameof(MeleeAttack.Apply))]
internal static class CriticalStrikeLandedPatch
{
	private static void Postfix(MeleeAttack __instance)
	{
		if (NetworkServer.active)
		{
			CriticalHits.StrikeLanded(__instance);
		}
	}
}

// A strike that ends without landing (its target gone) or is aborted stops the sword's glow.
[Feature(Features.CriticalHits, Features.CriticalHitsInfo)]
[HarmonyPatch(typeof(Striking), nameof(Striking.StartReacting))]
internal static class CriticalStrikeEndPatch
{
	private static void Prefix(Striking __instance, out MeleeAttack __state)
	{
		__state = __instance.SwordStrike;
	}

	private static void Postfix(MeleeAttack __state)
	{
		if (NetworkServer.active)
		{
			CriticalHits.StrikeMissed(__state);
		}
	}
}

[Feature(Features.CriticalHits, Features.CriticalHitsInfo)]
[HarmonyPatch(typeof(Striking), nameof(Striking.StartAbortingStrike))]
internal static class CriticalStrikeAbortPatch
{
	private static void Prefix(Striking __instance)
	{
		if (NetworkServer.active)
		{
			CriticalHits.StrikeMissed(__instance.SwordStrike);
		}
	}
}

// Host: a shot (arrow, bolt, magic shot, thrown rock or bomb) rolls for a critical hit when it leaves; healing bolts
// do not.
[Feature(Features.CriticalHits, Features.CriticalHitsInfo)]
[HarmonyPatch(typeof(ProjectileWeapon), nameof(ProjectileWeapon.Fire))]
internal static class CriticalShotPatch
{
	private static void Postfix(ProjectileAttack __result, GameObject source)
	{
		if (!NetworkServer.active || __result == null)
		{
			return;
		}
		KinematicProjectile projectile = __result.projectile as KinematicProjectile;
		if (projectile == null || ArtificerHealBoltFirePatch.Bolts.ContainsKey(projectile) || !CriticalHits.Roll(source))
		{
			return;
		}
		CriticalHits.ShotFired(source, projectile);
	}
}

// Projectiles and their trails are pooled: a reused one is an ordinary shot again, in its own colours.
[Feature(Features.CriticalHits, Features.CriticalHitsInfo)]
[HarmonyPatch(typeof(KinematicProjectile), nameof(KinematicProjectile.ResetComponent))]
internal static class CriticalShotResetPatch
{
	private static void Postfix(KinematicProjectile __instance)
	{
		GlowTint.Apply(__instance.gameObject, null);
	}
}

[Feature(Features.CriticalHits, Features.CriticalHitsInfo)]
[HarmonyPatch(typeof(ArrowTrailer), nameof(ArrowTrailer.OnRequested))]
internal static class CriticalShotTrailPatch
{
	private static void Postfix(ArrowTrailer __instance)
	{
		if (__instance.trail != null)
		{
			GlowTint.Apply(__instance.trail.gameObject, null);
		}
	}
}
