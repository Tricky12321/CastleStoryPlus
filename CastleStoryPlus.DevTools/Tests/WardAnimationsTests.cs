using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Components.Wards;
using CastleStoryPlus.Core;
using CastleStoryPlus.DevTools.Building;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.Rendering;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// The ward animation switches (#200, point 3): a sentinel ward without its defense animation, a healing ward with a
// quarter of its particles and link lines without sway, each applied at once and given back as the game had it when
// switched off.
internal static class WardAnimationsTests
{
	[GameTest("UI", "Ward animation switches: no sentinel spin-up or glow, a quarter of the healing ward's particles and straight link lines, all back as the game had them when switched off (#200)", TimeoutSeconds = 90f)]
	private static IEnumerator SwitchesApplyAndRestore(TestContext t)
	{
		if (!Plugin.IsEnabled(Features.WardAnimations) || WardAnimations.NoDefenseAnimation == null)
		{
			t.Skip("WardAnimations is off");
		}
		bool defenseBefore = WardAnimations.NoDefenseAnimation.Value;
		bool healBefore = WardAnimations.FewerHealParticles.Value;
		bool linksBefore = WardAnimations.StillLinks.Value;
		List<WardController> mine = new List<WardController>();
		t.Cleanup(() =>
		{
			WardAnimations.NoDefenseAnimation.Value = defenseBefore;
			WardAnimations.FewerHealParticles.Value = healBefore;
			WardAnimations.StillLinks.Value = linksBefore;
			foreach (WardController ward in mine)
			{
				if (ward != null)
				{
					Scenario.Release(ward.gameObject);
				}
			}
		});
		WardAnimations.NoDefenseAnimation.Value = false;
		WardAnimations.FewerHealParticles.Value = false;
		WardAnimations.StillLinks.Value = false;

		XYZ site = Scenario.Site(t, 12, 6, 4);
		BuildProject project = Scenario.BuildProject(t, site);
		HashSet<WardController> before = new HashSet<WardController>(Object.FindObjectsOfType<WardController>());
		List<Blueprint> blueprints = new List<Blueprint>();
		string[] keys = { "SentinelWard", "HealingWard" };
		for (int i = 0; i < keys.Length; i++)
		{
			XYZ at = new XYZ(site.x - 3 + i * 6, site.y, site.z);
			BlueprintPlacer.Result placed = BlueprintPlacer.Place(new Factory.AssetKey("Blueprints", keys[i]), at, 0, project, force: false);
			t.True(placed.Placed, keys[i] + " at " + at + ": " + placed.Outcome + " " + placed.Detail);
			blueprints.Add(placed.Blueprint);
		}
		InstantBuild.Complete(blueprints);
		WardAttack sentinel = null;
		WardController healing = null;
		yield return t.Until(() =>
		{
			mine.Clear();
			sentinel = null;
			healing = null;
			foreach (WardController ward in Object.FindObjectsOfType<WardController>())
			{
				if (before.Contains(ward))
				{
					continue;
				}
				mine.Add(ward);
				if (ward.GetComponent<WardAttack>() != null)
				{
					sentinel = ward.GetComponent<WardAttack>();
				}
				else if (WardAnimations.HealSystems(ward) > 0)
				{
					healing = ward;
				}
			}
			return sentinel != null && healing != null;
		}, 10f, "the sentinel and healing wards to be built");

		// Healing ward particles: a quarter, then all again.
		ParticleSystem system = healing.GetComponentInChildren<ParticleSystem>(true);
		t.NotNull(system, "A particle system on the healing ward");
		float rate = system.emission.rateOverTimeMultiplier;
		int max = system.main.maxParticles;
		t.Record("healRate", rate);
		t.Record("healMax", max);
		WardAnimations.FewerHealParticles.Value = true;
		t.True(Mathf.Approximately(system.emission.rateOverTimeMultiplier, WardAnimationRules.HealRate(rate, true)), "The healing ward's emission rate with FewerHealParticles: " + system.emission.rateOverTimeMultiplier + " (game " + rate + ")");
		t.True(system.main.maxParticles == WardAnimationRules.HealMax(max, true), "The healing ward's particle cap with FewerHealParticles: " + system.main.maxParticles + " (game " + max + ")");
		WardAnimations.FewerHealParticles.Value = false;
		t.True(Mathf.Approximately(system.emission.rateOverTimeMultiplier, rate), "The healing ward's emission rate is not the game's again: " + system.emission.rateOverTimeMultiplier + " (game " + rate + ")");
		t.True(system.main.maxParticles == max, "The healing ward's particle cap is not the game's again: " + system.main.maxParticles + " (game " + max + ")");

		// Link lines: straight, then swaying again.
		WardLink link = healing.GetComponent<WardLink>() ?? sentinel.GetComponent<WardLink>();
		t.NotNull(link, "A ward link on the wards");
		float sway = WardAnimations.LinkAmplitude(link);
		t.Record("linkAmplitude", sway);
		t.True(sway >= 0f, "The ward's link material is not looked after (no _Amplitude?)");
		WardAnimations.StillLinks.Value = true;
		t.True(WardAnimations.LinkAmplitude(link) == 0f, "The link line still sways with StillLinks: " + WardAnimations.LinkAmplitude(link));
		WardAnimations.StillLinks.Value = false;
		t.True(Mathf.Approximately(WardAnimations.LinkAmplitude(link), sway), "The link line's sway is not the game's again: " + WardAnimations.LinkAmplitude(link) + " (game " + sway + ")");

		// Sentinel: no glow and no faster spin while it aims, then the game's animation again.
		Animator animator = sentinel.animator;
		t.NotNull(animator, "The sentinel ward's animator");
		WardAnimations.NoDefenseAnimation.Value = true;
		t.Cleanup(() =>
		{
			if (sentinel != null)
			{
				sentinel.OnGlowChanged(false);
				sentinel.OnSpeedChanged(sentinel.StandbyRotationSpeed);
			}
		});
		sentinel.OnSpeedChanged(sentinel.TargetingRotationSpeed);
		sentinel.OnGlowChanged(true);
		t.False(animator.GetBool("Glow"), "The sentinel glows with NoDefenseAnimation");
		t.True(animator.GetFloat("Speed") <= sentinel.StandbyRotationSpeed + 0.001f, "The sentinel spins up with NoDefenseAnimation: " + animator.GetFloat("Speed"));
		WardAnimations.NoDefenseAnimation.Value = false;
		t.True(animator.GetBool("Glow"), "The sentinel's glow is not back when NoDefenseAnimation is switched off");
		t.True(Mathf.Approximately(animator.GetFloat("Speed"), sentinel.TargetingRotationSpeed), "The sentinel's spin is not back when NoDefenseAnimation is switched off: " + animator.GetFloat("Speed"));
	}
}
