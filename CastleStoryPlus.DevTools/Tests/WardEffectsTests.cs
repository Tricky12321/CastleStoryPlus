#if UNITY6
using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Input;
using CastleStoryPlus.Core;
using CastleStoryPlus.DevTools.Building;
using CastleStoryPlus.DevTools.Endpoints;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.Performance;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// Dozens of sentinel and healing wards made the frame time jump (#78, WardEffects): far from the camera a ward's aura,
// particles and light are switched off, and only the nearest few lights are lit. Not a parallel test: it moves the
// camera.
internal static class WardEffectsTests
{
	private const int PerKind = 8;

	private static void Look(XYZ site, float distance)
	{
		Vector3 at = site.ToVector3();
		Quaternion rotation = Quaternion.Euler(55f, 0f, 0f);
		Vector3 position = at - rotation * Vector3.forward * distance;
		CameraScript.Instance.Positionner(position, rotation, distance);
		CameraHold.Set(true, position, rotation, distance);
	}

	[GameTest("Performance", "16 sentinel and healing wards: their effects and all but the nearest lights go out when the camera is far away, and come back near them (#78)", TimeoutSeconds = 120f)]
	private static IEnumerator FarWardsSwitchEffectsOff(TestContext t)
	{
		if (!Plugin.IsEnabled(Features.FrameSavers) || !WardEffects.Enabled.Value)
		{
			t.Skip("WardEffects is off");
		}
		XYZ site = Scenario.Site(t, 36, 10, 4);
		BuildProject project = Scenario.BuildProject(t, site);
		HashSet<WardController> before = new HashSet<WardController>(WardEffects.Wards);
		List<Blueprint> blueprints = new List<Blueprint>();
		for (int i = 0; i < PerKind * 2; i++)
		{
			string key = i % 2 == 0 ? "SentinelWard" : "HealingWard";
			XYZ at = new XYZ(site.x - 16 + (i / 2) * 4, site.y, site.z - 2 + (i % 2) * 4);
			BlueprintPlacer.Result placed = BlueprintPlacer.Place(new Factory.AssetKey("Blueprints", key), at, 0, project, force: false);
			t.True(placed.Placed, key + " at " + at + ": " + placed.Outcome + " " + placed.Detail);
			blueprints.Add(placed.Blueprint);
		}
		InstantBuild.Complete(blueprints);
		List<WardController> mine = new List<WardController>();
		t.Cleanup(() =>
		{
			CameraHold.Set(false, Vector3.zero, Quaternion.identity, 0f);
			foreach (WardController ward in mine)
			{
				if (ward != null)
				{
					Scenario.Release(ward.gameObject);
				}
			}
		});
		yield return t.Until(() =>
		{
			mine.Clear();
			foreach (WardController ward in WardEffects.Wards)
			{
				if (!before.Contains(ward))
				{
					mine.Add(ward);
				}
			}
			return mine.Count == PerKind * 2;
		}, 10f, "the wards to be built");
		Look(site, 25f);
		int maxLights = WardEffects.MaxLights.Value;
		yield return t.Until(() => mine.TrueForAll(w => WardEffects.EffectsShown(w)) && (maxLights == 0 || mine.FindAll(w => WardEffects.LightShown(w)).Count <= maxLights), 10f, "the wards' effects to be shown and no more than MaxLights lit with the camera near");
		t.Record("litNear", mine.FindAll(w => WardEffects.LightShown(w)).Count);
		float far = WardEffects.EffectDistance.Value;
		if (far <= 0f)
		{
			t.Skip("EffectDistance is 0 (no distance limit)");
		}
		Look(site, far + 150f);
		t.Record("cameraFar", Vector3.Distance(Camera.main.transform.position, site.ToVector3()));
		yield return t.Until(() => mine.TrueForAll(w => !WardEffects.EffectsShown(w)), 10f, "the wards' effects to go out with the camera far away");
		t.Record("litFar", mine.FindAll(w => WardEffects.LightShown(w)).Count);
		yield return t.Until(() => mine.TrueForAll(w => !WardEffects.LightShown(w)), 10f, "the wards' lights to go out with the camera far away");
		Look(site, 25f);
		yield return t.Until(() => mine.TrueForAll(w => WardEffects.EffectsShown(w)), 10f, "the wards' effects to come back with the camera near");
	}
}
#endif
