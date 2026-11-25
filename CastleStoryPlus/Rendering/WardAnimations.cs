using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Game.Components;
using Brix.Game.Components.Wards;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Rendering;

// Three switches for the wards' animations (#200, point 3), all off by default so the game looks as it always did,
// each applied at once when switched (Settings > Castle Story Plus settings > Wards). Only this player's view
// changes: what the game sends between players (a sentinel's aim, glow and shots, the links) is untouched.
// - NoDefenseAnimation: a sentinel ward (WardAttack) drives its animator from its aim: the gems spin faster while it
//   targets, glow while it charges and kick back on every shot. Its animator is held at the standby spin without
//   glow and the shot's trigger is taken back the moment it is set. The shots themselves, their sounds, the muzzle
//   and overheat particles stay.
// - FewerHealParticles: a healing ward (the ward with a RegenAura) gives off a quarter of the particles of its
//   systems (rate and cap). What each system had is kept and given back when switched off or when the ward goes
//   back to the game's pool.
// - StillLinks: the line between a ward and its pylon (WardLink, the "Castle Story/Energy Beam" shader) is bent by a
//   sine wave in its vertex shader (_Amplitude); with the switch the wave is 0 and the line is straight. The line's
//   material is shared by all links, so it is set once per material.
[Feature(Features.WardAnimations, Features.WardAnimationsInfo)]
internal static class WardAnimations
{
	internal static ConfigEntry<bool> NoDefenseAnimation;

	internal static ConfigEntry<bool> FewerHealParticles;

	internal static ConfigEntry<bool> StillLinks;

	private static readonly int Amplitude = Shader.PropertyToID("_Amplitude");

	private sealed class HealSystem
	{
		internal ParticleSystem System;

		internal float RateOverTime;

		internal float RateOverDistance;

		internal int MaxParticles;
	}

	// Each healing ward's particle systems, with what the game gave them.
	private static readonly Dictionary<WardController, List<HealSystem>> Heals = new Dictionary<WardController, List<HealSystem>>();

	// Each link material's own sway. Materials are assets that outlive a game, so these are not dropped when a game
	// is left: the next game's links use the same material, already changed.
	private static readonly Dictionary<Material, float> LinkMaterials = new Dictionary<Material, float>();

	private static void Enable()
	{
		NoDefenseAnimation = Plugin.Cfg.Bind("WardAnimations", "NoDefenseAnimation", false, "Sentinel wards do not spin up, glow or kick back when they aim and shoot (their shots, sounds and particles stay). Off: the game's animation.");
		FewerHealParticles = Plugin.Cfg.Bind("WardAnimations", "FewerHealParticles", false, "Healing wards give off a quarter of their particles. Off: all of them.");
		StillLinks = Plugin.Cfg.Bind("WardAnimations", "StillLinks", false, "The lines between wards and pylons are straight instead of swaying. Off: they sway as in the game.");
		NoDefenseAnimation.SettingChanged += (object sender, EventArgs args) => ApplyDefenseAll();
		FewerHealParticles.SettingChanged += (object sender, EventArgs args) => ApplyHealAll();
		StillLinks.SettingChanged += (object sender, EventArgs args) => ApplyLinksAll();
		GameSession.OnLeave(Heals.Clear);
	}

	private static bool On(ConfigEntry<bool> entry)
	{
		return entry != null && entry.Value && Plugin.IsEnabled(Features.WardAnimations);
	}

	// ---- Defense animation

	internal static bool DefenseStill
	{
		get { return On(NoDefenseAnimation); }
	}

	// The animator as the switch wants it: held at standby, or as the ward's own state says (switched off again).
	internal static void ApplyDefense(WardAttack attack)
	{
		if (attack == null || attack.animator == null)
		{
			return;
		}
		Animator animator = attack.animator;
		bool still = DefenseStill;
		bool glow = attack.anim_glow && !still;
		animator.SetBool("Glow", glow);
		animator.SetFloat("Glow Amount", glow ? attack.GlowIntensity : 0f);
		animator.SetFloat("Speed", WardAnimationRules.DefenseSpeed(attack.anim_speed, attack.StandbyRotationSpeed, still));
		if (still)
		{
			animator.ResetTrigger("Fire");
		}
	}

	private static void ApplyDefenseAll()
	{
		foreach (WardAttack attack in UnityEngine.Object.FindObjectsOfType<WardAttack>())
		{
			ApplyDefense(attack);
		}
	}

	// ---- Heal particles

	internal static void RegisterHeal(WardController ward)
	{
		if (ward == null || Heals.ContainsKey(ward) || ward.GetComponentInChildren<RegenAura>(true) == null)
		{
			return;
		}
		List<HealSystem> systems = new List<HealSystem>();
		foreach (ParticleSystem system in ward.GetComponentsInChildren<ParticleSystem>(true))
		{
			systems.Add(new HealSystem
			{
				System = system,
				RateOverTime = system.emission.rateOverTimeMultiplier,
				RateOverDistance = system.emission.rateOverDistanceMultiplier,
				MaxParticles = system.main.maxParticles
			});
		}
		Heals[ward] = systems;
		if (On(FewerHealParticles))
		{
			ApplyHeal(systems, true);
		}
	}

	internal static void UnregisterHeal(WardController ward)
	{
		if (ward != null && Heals.TryGetValue(ward, out List<HealSystem> systems))
		{
			ApplyHeal(systems, false);
			Heals.Remove(ward);
		}
	}

	private static void ApplyHeal(List<HealSystem> systems, bool fewer)
	{
		foreach (HealSystem heal in systems)
		{
			if (heal.System == null)
			{
				continue;
			}
			ParticleSystem.EmissionModule emission = heal.System.emission;
			emission.rateOverTimeMultiplier = WardAnimationRules.HealRate(heal.RateOverTime, fewer);
			emission.rateOverDistanceMultiplier = WardAnimationRules.HealRate(heal.RateOverDistance, fewer);
			ParticleSystem.MainModule main = heal.System.main;
			main.maxParticles = WardAnimationRules.HealMax(heal.MaxParticles, fewer);
		}
	}

	private static void ApplyHealAll()
	{
		bool fewer = On(FewerHealParticles);
		if (fewer)
		{
			// Healing wards built before the feature was looked at (switched on in a game) are found now.
			foreach (WardController ward in UnityEngine.Object.FindObjectsOfType<WardController>())
			{
				RegisterHeal(ward);
			}
		}
		foreach (KeyValuePair<WardController, List<HealSystem>> pair in Heals)
		{
			if (pair.Key != null)
			{
				ApplyHeal(pair.Value, fewer);
			}
		}
	}

	// How many particle systems of healing wards are looked after, for the tests.
	internal static int HealSystems(WardController ward)
	{
		return Heals.TryGetValue(ward, out List<HealSystem> systems) ? systems.Count : 0;
	}

	// ---- Link lines

	internal static void RegisterLink(WardLink link)
	{
		if (link == null)
		{
			return;
		}
		AddLinkMaterial(link.LineMaterial);
		if (link.LinkLineRenderer != null)
		{
			AddLinkMaterial(link.LinkLineRenderer.sharedMaterial);
		}
	}

	private static void AddLinkMaterial(Material material)
	{
		// Not asked whether the shader has _Amplitude: without graphics (a headless player) no shader is loaded and the
		// material says it has none; setting it there does no harm.
		if (material == null || LinkMaterials.ContainsKey(material))
		{
			return;
		}
		LinkMaterials[material] = material.GetFloat(Amplitude);
		ApplyLink(material, LinkMaterials[material]);
	}

	private static void ApplyLink(Material material, float original)
	{
		material.SetFloat(Amplitude, WardAnimationRules.LinkAmplitude(original, On(StillLinks)));
	}

	private static void ApplyLinksAll()
	{
		foreach (WardLink link in UnityEngine.Object.FindObjectsOfType<WardLink>())
		{
			RegisterLink(link);
		}
		List<Material> gone = null;
		foreach (KeyValuePair<Material, float> pair in LinkMaterials)
		{
			if (pair.Key == null)
			{
				gone = gone ?? new List<Material>();
				gone.Add(pair.Key);
				continue;
			}
			ApplyLink(pair.Key, pair.Value);
		}
		if (gone != null)
		{
			foreach (Material material in gone)
			{
				LinkMaterials.Remove(material);
			}
		}
	}

	// The sway a link material has now, for the tests (-1: not a link material looked after).
	internal static float LinkAmplitude(WardLink link)
	{
		Material material = (link != null) ? link.LineMaterial : null;
		return (material != null && LinkMaterials.ContainsKey(material)) ? material.GetFloat(Amplitude) : -1f;
	}
}

[Feature(Features.WardAnimations, Features.WardAnimationsInfo)]
[HarmonyPatch(typeof(WardAttack), nameof(WardAttack.OnSpeedChanged))]
internal static class WardAnimationsSpeedPatch
{
	private static void Postfix(WardAttack __instance)
	{
		if (WardAnimations.DefenseStill)
		{
			WardAnimations.ApplyDefense(__instance);
		}
	}
}

[Feature(Features.WardAnimations, Features.WardAnimationsInfo)]
[HarmonyPatch(typeof(WardAttack), nameof(WardAttack.OnGlowChanged))]
internal static class WardAnimationsGlowPatch
{
	private static void Postfix(WardAttack __instance)
	{
		if (WardAnimations.DefenseStill)
		{
			WardAnimations.ApplyDefense(__instance);
		}
	}
}

[Feature(Features.WardAnimations, Features.WardAnimationsInfo)]
[HarmonyPatch(typeof(WardAttack), nameof(WardAttack.OnFireChanged))]
internal static class WardAnimationsFirePatch
{
	private static void Postfix(WardAttack __instance)
	{
		if (WardAnimations.DefenseStill)
		{
			WardAnimations.ApplyDefense(__instance);
		}
	}
}

[Feature(Features.WardAnimations, Features.WardAnimationsInfo)]
[HarmonyPatch(typeof(WardController), nameof(WardController.OnRequested))]
internal static class WardAnimationsRegisterPatch
{
	private static void Postfix(WardController __instance)
	{
		WardAnimations.RegisterHeal(__instance);
	}
}

[Feature(Features.WardAnimations, Features.WardAnimationsInfo)]
[HarmonyPatch(typeof(WardController), nameof(WardController.ResetComponent))]
internal static class WardAnimationsUnregisterPatch
{
	private static void Prefix(WardController __instance)
	{
		WardAnimations.UnregisterHeal(__instance);
	}
}

[Feature(Features.WardAnimations, Features.WardAnimationsInfo)]
[HarmonyPatch(typeof(WardLink), nameof(WardLink.OnRequested))]
internal static class WardAnimationsLinkPatch
{
	private static void Postfix(WardLink __instance)
	{
		WardAnimations.RegisterLink(__instance);
	}
}
