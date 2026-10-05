using System.Collections;
using CastleStoryPlus.DevTools.Api;
using CastleStoryPlus.DevTools.Testing;

namespace CastleStoryPlus.DevTools.Tests;

// The mod itself: every feature that is switched on started without errors.
internal static class ModTests
{
	[GameTest("Mod", "Every feature switched on was patched and started without errors", NeedsHostGame = false)]
	private static IEnumerator FeaturesStarted(TestContext t)
	{
		int failed = 0;
		foreach (LogBuffer.Line line in LogBuffer.Since(0, int.MaxValue, true))
		{
			if (line.Message.StartsWith("Feature '") && line.Message.Contains("' failed in "))
			{
				failed++;
				t.Log(line.Message.Split('\n')[0]);
			}
		}
		int on = 0;
		foreach (bool enabled in CastleStoryPlus.Plugin.LoadedFeatures.Values)
		{
			if (enabled)
			{
				on++;
			}
		}
		t.Record("featuresOn", on);
		t.Equal(0, failed, "Features that failed to start");
		yield break;
	}
}
