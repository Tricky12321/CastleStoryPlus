using System.Collections;
using System.IO;
using System.Reflection;
using Brix.Engine;
using Brix.Engine.Nature;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.Nature;
using CastleStoryPlus.Weather;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// Rain: the host starts and ends it by the game's clock, it speeds up the plants, it is saved with the world, and it is
// shown (drops, veil, darker light) and taken away again.
internal static class WeatherTests
{
	[GameTest("Weather", "Rain starts, runs for its game hours and stops by the game's clock; the boosts follow it", TimeoutSeconds = 60f)]
	private static IEnumerator RainRunsByTheClock(TestContext t)
	{
		t.True(Rain.Active, "The weather is not running (Rain feature off?)");
		t.Cleanup(() => Rain.Begin(WeatherKind.None, 0f));
		Rain.Begin(WeatherKind.None, 0f);
		t.Equal(1f, Rain.GrassBoost, "The grass boost without rain");
		Rain.Begin(WeatherKind.Rain, 2f);
		t.Equal(WeatherKind.Rain, Rain.Current, "The weather after Begin");
		t.True(Rain.GrassBoost > 1f && Rain.PlantBoost > 1f, "Rain gives no boost: grass " + Rain.GrassBoost + ", plants " + Rain.PlantBoost);
		yield return t.Frames(3);
		float before = Journee.instance.Heure;
		// An hour of a 2 hour rain goes by on the clock: it still rains, about an hour left.
		Journee.instance.SetTime(Mathf.Repeat(before + 1f, 24f));
		yield return t.Frames(3);
		t.Equal(WeatherKind.Rain, Rain.Current, "The weather an hour into a 2 hour rain");
		t.True(Rain.HoursLeft > 0.8f && Rain.HoursLeft < 1.2f, "Hours left an hour into a 2 hour rain: " + Rain.HoursLeft);
		Journee.instance.SetTime(Mathf.Repeat(before + 2.5f, 24f));
		yield return t.Frames(3);
		t.Equal(WeatherKind.None, Rain.Current, "The weather after its hours");
		t.Equal(1f, Rain.PlantBoost, "The plant boost after the rain");
		Journee.instance.SetTime(before);
	}

	[GameTest("Weather", "Plants grow faster in the rain", TimeoutSeconds = 60f)]
	private static IEnumerator PlantsGrowFaster(TestContext t)
	{
		t.Cleanup(() => Rain.Begin(WeatherKind.None, 0f));
		Rain.Begin(WeatherKind.None, 0f);
		MethodInfo grow = typeof(WildPlants).GetMethod("Grow", BindingFlags.Static | BindingFlags.NonPublic);
		t.NotNull(grow, "WildPlants.Grow");
		XYZ at = new XYZ(Scenario.Home().x + 3, 0, Scenario.Home().z + 3);
		t.Cleanup(() => PlantRegistry.Remove(at));
		PlantRegistry.Add(at, 0, 0.1f);
		t.NotNull(PlantRegistry.PlantAt(at), "The test plant");
		// The same step of growth, once dry and once in the rain.
		grow.Invoke(null, null);
		float dryStep = PlantRegistry.PlantAt(at).Growth - 0.1f;
		PlantRegistry.Remove(at);
		PlantRegistry.Add(at, 0, 0.1f);
		Rain.Begin(WeatherKind.Rain, 1f);
		grow.Invoke(null, null);
		float wetStep = PlantRegistry.PlantAt(at).Growth - 0.1f;
		t.Record("dryStep", dryStep);
		t.Record("wetStep", wetStep);
		t.True(dryStep > 0f, "The dry plant did not grow");
		t.True(wetStep > dryStep * 1.5f, "The plant in the rain grew " + wetStep + " against " + dryStep + " dry");
		yield return null;
	}

	[GameTest("Weather", "The weather is written beside the save and read back", TimeoutSeconds = 30f)]
	private static IEnumerator WeatherIsSaved(TestContext t)
	{
		t.Cleanup(() => Rain.Begin(WeatherKind.None, 0f));
		string folder = Path.Combine(Path.GetTempPath(), "csp-weather-" + System.Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		t.Cleanup(() => Directory.Delete(folder, true));
		Rain.Begin(WeatherKind.Snow, 2.5f);
		Rain.Write(folder);
		Rain.Begin(WeatherKind.None, 0f);
		t.Equal(WeatherKind.None, Rain.Current, "The weather after stopping");
		Rain.ReadFrom(folder);
		t.Equal(WeatherKind.Snow, Rain.Current, "The weather read back");
		t.True(Mathf.Abs(Rain.HoursLeft - 2.5f) < 0.01f, "Hours left read back: " + Rain.HoursLeft);
		yield return null;
	}

	[GameTest("Weather", "Rain is shown (drops, veil, darker light) and taken away again", TimeoutSeconds = 90f)]
	private static IEnumerator RainIsShown(TestContext t)
	{
		t.Cleanup(() => Rain.Begin(WeatherKind.None, 0f));
		Rain.Begin(WeatherKind.None, 0f);
		RainVisuals visuals = RainVisuals.Instance;
		t.NotNull(visuals, "The rain's visuals");
		yield return t.Until(() => visuals.Strength <= 0f, 15f, "the weather to be gone");
		yield return t.Frames(5);
		Color ambient = RenderSettings.ambientLight;
		Rain.Begin(WeatherKind.Rain, 3f);
		yield return t.Until(() => visuals.Strength >= 1f, 15f, "the rain to reach full strength");
		// The light is turned down in LateUpdate: read at the end of the frame, not between the game's Update and it.
		yield return new WaitForEndOfFrame();
		t.Log(visuals.Describe());
		t.True(RenderSettings.ambientLight.grayscale < ambient.grayscale * 0.9f || ambient.grayscale < 0.01f, "The ambient light did not get darker: " + ambient + " -> " + RenderSettings.ambientLight);
		Rain.Begin(WeatherKind.None, 0f);
		yield return t.Until(() => visuals.Strength <= 0f, 15f, "the rain to fade out");
		yield return new WaitForEndOfFrame();
		yield return new WaitForEndOfFrame();
		t.True(Mathf.Abs(RenderSettings.ambientLight.grayscale - ambient.grayscale) < 0.02f, "The ambient light is not back: " + ambient + " -> " + RenderSettings.ambientLight);
	}

	[GameTest("Weather", "Switching the Rain feature off in the rain ends it, so no boost is left", TimeoutSeconds = 60f)]
	private static IEnumerator SwitchingOffEndsTheRain(TestContext t)
	{
		t.Cleanup(() =>
		{
			Core.FeatureToggle.Set(Core.Features.Rain, true);
			Rain.Begin(WeatherKind.None, 0f);
		});
		Rain.Begin(WeatherKind.Rain, 2f);
		t.True(Rain.GrassBoost > 1f, "No boost in the rain");
		Core.FeatureToggle.Set(Core.Features.Rain, false);
		yield return t.Frames(3);
		t.Equal(WeatherKind.None, Rain.Current, "The weather after the feature was switched off");
		t.Equal(1f, Rain.GrassBoost, "The grass boost after the feature was switched off");
	}
}
