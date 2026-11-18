using System.Collections;
using System.Reflection;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Locomotion;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.Upgrades;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// Research > Sprint (Sprint): a bricktron of a team that has researched it walks and runs faster for a while, then
// cannot sprint again until its cooldown is over; enemies never sprint; the sprint multiplies with the other speed
// bonuses (Light Boots, roads) instead of replacing them.
internal static class SprintTests
{
	private static float WalkSpeed(Labor labor)
	{
		PropertyInfo walk = typeof(Walking).GetProperty("SpeedMperS", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
		return (float)walk.GetValue(labor.navigation.Locomotion.Movement.Walking, null);
	}

	private static float RunSpeed(Labor labor)
	{
		PropertyInfo run = typeof(Running).GetProperty("SpeedMperS", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
		return (float)run.GetValue(labor.navigation.Locomotion.Movement.Running, null);
	}

	// A short sprint and cooldown for the test; the settings go back afterwards.
	private static void ShortSettings(TestContext t, float duration, float cooldown)
	{
		float oldDuration = Sprint.DurationSeconds.Value;
		float oldCooldown = Sprint.CooldownSeconds.Value;
		int oldBonus = Sprint.SpeedBonus.Value;
		Sprint.DurationSeconds.Value = duration;
		Sprint.CooldownSeconds.Value = cooldown;
		Sprint.SpeedBonus.Value = 50;
		t.Cleanup(() =>
		{
			Sprint.DurationSeconds.Value = oldDuration;
			Sprint.CooldownSeconds.Value = oldCooldown;
			Sprint.SpeedBonus.Value = oldBonus;
		});
	}

	// Researches Sprint tier 1 for the player's team; the tier and the auto setting go back when the test ends so later
	// tests start as before.
	private static void ResearchSprint(TestContext t)
	{
		int before = TeamUpgrades.Tier(Scenario.Player, UpgradeLines.Sprint);
		bool auto = Sprint.Auto.Value;
		t.Cleanup(() =>
		{
			TeamUpgrades.SetTier(Scenario.Player, UpgradeLines.Sprint, before);
			Sprint.Auto.Value = auto;
		});
		TeamUpgrades.Unlock(Scenario.Player, UpgradeLines.Sprint, 1);
	}

	[GameTest("Sprint", "A sprinting bricktron walks and runs 50% faster for its duration, then cannot sprint until the cooldown is over", TimeoutSeconds = 90f, Exclusive = true)]
	private static IEnumerator SprintsThenCoolsDown(TestContext t)
	{
		ShortSettings(t, 2f, 8f);
		XYZ site = Scenario.Site(t, 6, 6);
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, site);
		yield return t.Frames(5);
		ResearchSprint(t);
		float walk = WalkSpeed(builder);
		float run = RunSpeed(builder);
		t.False(Sprint.IsSprinting(builder.gameObject), "The builder sprints before it was told to");
		t.True(Sprint.TryStart(builder, SprintTrigger.Key), "A bricktron of a team that has Sprint cannot start one");
		yield return t.Frames(2);
		t.True(Sprint.IsSprinting(builder.gameObject), "The builder does not sprint after the sprint started");
		t.AtLeast(1.499f, WalkSpeed(builder) / walk, "Walking speed while sprinting over before");
		t.AtMost(1.501f, WalkSpeed(builder) / walk, "Walking speed while sprinting over before");
		t.AtLeast(1.499f, RunSpeed(builder) / run, "Running speed while sprinting over before");
		t.AtMost(1.501f, RunSpeed(builder) / run, "Running speed while sprinting over before");
		t.False(Sprint.TryStart(builder, SprintTrigger.Key), "A second sprint started inside the first");
		yield return t.UntilGame(() => !Sprint.IsSprinting(builder.gameObject), 5f, "the sprint to end");
		t.AtLeast(0.999f, WalkSpeed(builder) / walk, "Walking speed after the sprint over before");
		t.AtMost(1.001f, WalkSpeed(builder) / walk, "Walking speed after the sprint over before");
		t.False(Sprint.TryStart(builder, SprintTrigger.Key), "A sprint started during the cooldown");
		yield return t.UntilGame(() => Sprint.TryStart(builder, SprintTrigger.Key), 12f, "the cooldown to end");
		t.True(Sprint.IsSprinting(builder.gameObject), "No sprint after the cooldown");
	}

	[GameTest("Sprint", "The sprint multiplies with Light Boots instead of replacing them", TimeoutSeconds = 60f, Exclusive = true)]
	private static IEnumerator MultipliesWithLightBoots(TestContext t)
	{
		ShortSettings(t, 3f, 8f);
		XYZ site = Scenario.Site(t, 6, 6);
		Labor builder = Scenario.Bricktron(t, Occupation.Type.Builder, site);
		yield return t.Frames(5);
		ResearchSprint(t);
		int boots = TeamUpgrades.Tier(Scenario.Player, UpgradeLines.LightBoots);
		t.Cleanup(() => TeamUpgrades.SetTier(Scenario.Player, UpgradeLines.LightBoots, boots));
		TeamUpgrades.SetTier(Scenario.Player, UpgradeLines.LightBoots, 3);
		float bootsOnly = WalkSpeed(builder);
		t.True(Sprint.TryStart(builder, SprintTrigger.Key), "The sprint did not start");
		yield return t.Frames(2);
		float both = WalkSpeed(builder);
		t.AtLeast(1.499f, both / bootsOnly, "Speed with boots and sprint over boots alone");
		t.AtMost(1.501f, both / bootsOnly, "Speed with boots and sprint over boots alone");
		TeamUpgrades.SetTier(Scenario.Player, UpgradeLines.LightBoots, 0);
		float sprintOnly = WalkSpeed(builder);
		t.AtLeast(1.1499f, both / sprintOnly, "Speed with boots over without, both sprinting");
		t.AtMost(1.1501f, both / sprintOnly, "Speed with boots over without, both sprinting");
	}

	[GameTest("Sprint", "Sprint's recipes come after the kit bundles' and the other late research recipes, so their ids are as before", TimeoutSeconds = 30f)]
	private static IEnumerator RecipesComeLast(TestContext t)
	{
		UpgradeLine sprint = UpgradeLines.All[UpgradeLines.Sprint];
		t.NotNull(sprint.Recipe[1], "Sprint has no recipe");
		int first = sprint.Recipe[1].id;
		t.True(sprint.Recipe[2].id == first + 1 && sprint.Recipe[3].id == first + 2, "Sprint's three recipes are not in a row");
		foreach (UpgradeLine line in UpgradeLines.All)
		{
			if (line != sprint && line.Recipe[1] != null)
			{
				t.True(line.Recipe[line.Tiers].id < first, line.Name + "'s recipes come after Sprint's");
			}
		}
		foreach (CastleStoryPlus.Army.KitBundles.Bundle bundle in CastleStoryPlus.Army.KitBundles.All)
		{
			if (bundle.Recipe != null)
			{
				t.True(bundle.Recipe.id < first, "The kit bundle " + bundle.Recipe.id + " comes after Sprint's recipes (" + first + ")");
			}
		}
		yield return null;
	}

	[GameTest("Sprint", "An enemy never sprints", TimeoutSeconds = 60f, Exclusive = true)]
	private static IEnumerator EnemiesDoNotSprint(TestContext t)
	{
		ShortSettings(t, 2f, 8f);
		XYZ site = Scenario.Site(t, 6, 6);
		Labor corruptron = Scenario.Corruptron(t, site);
		yield return t.Frames(5);
		float walk = WalkSpeed(corruptron);
		t.False(Sprint.TryStart(corruptron, SprintTrigger.Key), "A corruptron started a sprint");
		yield return t.Frames(2);
		t.False(Sprint.IsSprinting(corruptron.gameObject), "A corruptron sprints");
		t.AtLeast(0.999f, WalkSpeed(corruptron) / walk, "Corruptron walking speed");
		t.AtMost(1.001f, WalkSpeed(corruptron) / walk, "Corruptron walking speed");
	}

	[GameTest("Sprint", "A melee fighter sent at an enemy from afar sprints by itself; one sent a short way does not", TimeoutSeconds = 90f, Exclusive = true)]
	private static IEnumerator KnightChargesFromAfar(TestContext t)
	{
		ShortSettings(t, 3f, 30f);
		XYZ site = Scenario.Site(t, 36, 6, 3);
		Labor near = Scenario.Bricktron(t, Occupation.Type.Knight, new XYZ(site.x - 2, site.y, site.z));
		Labor far = Scenario.Bricktron(t, Occupation.Type.Knight, new XYZ(site.x - 16, site.y, site.z));
		Labor corruptron = Scenario.Corruptron(t, new XYZ(site.x + 2, site.y, site.z));
		yield return t.Frames(5);
		ResearchSprint(t);
		Sprint.Auto.Value = true;
		float minimum = Sprint.MinChargeDistance.Value;
		t.True(Vector3.Distance(far.transform.position, corruptron.transform.position) >= minimum, "The far knight stands closer than the charge distance");
		t.True(Vector3.Distance(near.transform.position, corruptron.transform.position) < minimum, "The near knight stands farther than the charge distance");
		Order.Package.OfGameObject(Order.Combat.GoAttack, corruptron.gameObject, Labor.DecisionSource.User).Send(near);
		Order.Package.OfGameObject(Order.Combat.GoAttack, corruptron.gameObject, Labor.DecisionSource.User).Send(far);
		yield return t.Frames(3);
		t.True(Sprint.IsSprinting(far.gameObject), "The knight sent from afar does not sprint");
		t.False(Sprint.IsSprinting(near.gameObject), "The knight sent a short way sprints");
	}
}
