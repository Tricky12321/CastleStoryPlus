using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Brix.Engine;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Loading;

// Writes how long each map loading step took to the BepInEx log ("Load timing: terrain 2.41 s, 87 frames"),
// so slow loads can be measured and reported.
[Feature(Features.FasterLoading, Features.FasterLoadingInfo)]
[HarmonyPatch]
internal static class LoadTiming
{
	private static readonly string[] Steps = new string[11]
	{
		"Load", "BuildStep", "AwakeStep", "StartStep", "LoadBasicTerrain", "InitTerrainGravity", "LoadNature",
		"BuildTerrainMeshes", "LoadGameObjects", "InitPathfinding", "LoadGridGuide"
	};

	private static IEnumerable<MethodBase> TargetMethods()
	{
		foreach (string step in Steps)
		{
			MethodInfo method = AccessTools.Method(typeof(GameLoader), step);
			if (method != null)
			{
				yield return method;
			}
		}
	}

	private static void Postfix(MethodBase __originalMethod, ref IEnumerator __result)
	{
		if (__result != null)
		{
			__result = Timed(__originalMethod.Name, __result);
		}
	}

	private static IEnumerator Timed(string name, IEnumerator step)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		int startFrame = Time.frameCount;
		yield return step;
		Plugin.Log.LogInfo("Load timing: " + name + " " + (stopwatch.ElapsedMilliseconds / 1000f).ToString("0.00") + " s, " + (Time.frameCount - startFrame) + " frames");
	}
}
