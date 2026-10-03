using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Brix.Assets;
using Brix.Components;
using Brix.Engine;
using Brix.Engine.Blocks;
using Brix.Engine.FlatWorld;
using Brix.Engine.FlatWorld.Layers;
using Brix.Engine.Nature;
using Brix.Engine.Trackers;
using Brix.Game;
using Brix.Legacy;
using Brix.IO.Serialization;
using Brix.Utils.Logging;
using CastleStoryPlus.Core;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Loading;

// The "wait for X" loops in GameLoader polled with WaitForSeconds(1f). Ready flags such as TerrainLoaded
// and LevelReady are set via UNET commands one frame later, even for the local host, so each loop cost a
// full second in single player. They now poll once per frame.
[Feature(Features.FasterLoading, Features.FasterLoadingInfo)]
[HarmonyPatch]
internal static class PollEveryFramePatch
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		foreach (Type nested in typeof(GameLoader).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
		{
			if (!typeof(IEnumerator).IsAssignableFrom(nested))
			{
				continue;
			}
			MethodInfo moveNext = nested.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (moveNext != null)
			{
				yield return moveNext;
			}
		}
	}

	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		ConstructorInfo waitForSeconds = AccessTools.Constructor(typeof(WaitForSeconds), new[] { typeof(float) });
		List<CodeInstruction> code = new List<CodeInstruction>(instructions);
		for (int i = 0; i < code.Count - 1; i++)
		{
			if (code[i].opcode == OpCodes.Ldc_R4 && (float)code[i].operand == 1f && code[i + 1].opcode == OpCodes.Newobj && Equals(code[i + 1].operand, waitForSeconds))
			{
				code[i].opcode = OpCodes.Nop;
				code[i].operand = null;
				code[i + 1].opcode = OpCodes.Ldnull;
				code[i + 1].operand = null;
			}
		}
		return code;
	}
}

// The loading progress bar was fed at most 100 steps per frame, which only burned frames catching up.
[Feature(Features.FasterLoading, Features.FasterLoadingInfo)]
[HarmonyPatch]
internal static class ProgressCatchUpPatch
{
	private static MethodBase TargetMethod()
	{
		return AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(GameLoader), nameof(GameLoader.LoadingStepsCheckpoint)));
	}

	private static int Second(int a, int b)
	{
		return b;
	}

	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		MethodInfo min = AccessTools.Method(typeof(Mathf), nameof(Mathf.Min), new[] { typeof(int), typeof(int) });
		foreach (CodeInstruction instruction in instructions)
		{
			if (instruction.Calls(min))
			{
				instruction.operand = AccessTools.Method(typeof(ProgressCatchUpPatch), nameof(Second));
			}
			yield return instruction;
		}
	}
}

// Saved blocks and game objects are read and parsed on a worker thread; the objects are then created a few
// at a time across frames (game time frozen meanwhile, so AI and physics do not run on a half-loaded world).
[Feature(Features.FasterLoading, Features.FasterLoadingInfo)]
[HarmonyPatch(typeof(GameLoader), nameof(GameLoader.LoadGameObjects))]
internal static class LoadGameObjectsPatch
{
	private static readonly BrixLogger Logger = new BrixLogger(typeof(LoadGameObjectsPatch).FullName);

	private static bool Prefix(GameLoader __instance, XYZ offset, ref IEnumerator __result)
	{
		__result = LoadGameObjects(__instance._mapToLoad, offset);
		return false;
	}

	private static IEnumerator LoadGameObjects(Asset_Map map, XYZ offset)
	{
		if (NetworkServer.active)
		{
			BackgroundTask<BlockDataStruct[]> readBlocks = BackgroundTask<BlockDataStruct[]>.Start(() => map.Disk_ReadPlacedBlocks);
			BackgroundTask<JToken> readObjects = BackgroundTask<JToken>.Start(() => ParseState(map.Disk_ReadGameObjects()));
			yield return readBlocks.Wait();
			yield return BrixSingleton<BlockEngine>.Instance.LoadBlockData(readBlocks.Result);
			yield return readObjects.Wait();
			JToken savedObjects = readObjects.Result;
			if (savedObjects == null)
			{
				yield break;
			}
			List<GameObject> objs = new List<GameObject>();
			float timeScale = Time.timeScale;
			Time.timeScale = 0f;
			try
			{
				yield return LoadState("save", savedObjects, objs);
			}
			finally
			{
				Time.timeScale = timeScale;
			}
			Vector3 translation = offset.ToVector3();
			foreach (GameObject item in objs)
			{
				if (item != null)
				{
					item.transform.position += translation;
					GameSignals.Invoke(GameSignals.GameObjectLoaded, item);
				}
			}
		}
		yield return GameClock.WaitForEndOfFrame;
	}

	// Thread-safe: only parses text, creates no Unity objects.
	private static JToken ParseState(string json)
	{
		return string.IsNullOrEmpty(json) ? null : JToken.Parse(json);
	}

	// Same result as GameObjectSerializer.LoadState, but creates the saved GameObjects one at a time and
	// yields when the frame budget is spent. Saves only reference objects written earlier in the array
	// (later occurrences become "$ref"), so one shared serializer resolves references exactly as
	// deserializing the whole list at once does.
	private static IEnumerator LoadState(string target, JToken state, List<GameObject> loaded)
	{
		JArray items = state as JArray;
		if (items == null && state is JObject wrapper)
		{
			items = wrapper["$values"] as JArray;
		}
		if (items == null)
		{
			Logger.Error("Saved game objects are not a JSON array.");
			yield break;
		}
		JsonSerializer serializer = JsonSerializer.Create(Io.GetLoadSerializerSettings(target));
		FrameBudget budget = new FrameBudget();
		foreach (JToken item in items)
		{
			try
			{
				GameObject gameObject = serializer.Deserialize<GameObject>(item.CreateReader());
				if (gameObject != null)
				{
					loaded.Add(gameObject);
				}
			}
			catch (Exception ex)
			{
				Logger.Error(ex);
			}
			if (budget.ExceededNow)
			{
				yield return null;
				budget.Reset();
			}
		}
	}
}

// Nature files (trees, plants, boulders, iron, pyrite) are read and parsed on a worker thread; only the
// registering runs on the main thread, and the tree loop yields on a time budget.
[Feature(Features.FasterLoading, Features.FasterLoadingInfo)]
[HarmonyPatch(typeof(GameLoader), nameof(GameLoader.LoadNature))]
internal static class LoadNaturePatch
{
	private class NatureFiles
	{
		public Asset_Map.TreeInfo[] Trees;

		public PlantSerializedData[] Plants;

		public BoulderSerializedData[] Boulders;

		public MineralSerializedData[] Irons;

		public MineralSerializedData[] Pyrites;
	}

	private static bool Prefix(GameLoader __instance, ref IEnumerator __result)
	{
		__result = LoadNature(__instance._mapToLoad);
		return false;
	}

	private static IEnumerator LoadNature(Asset_Map map)
	{
		BackgroundTask<NatureFiles> read = BackgroundTask<NatureFiles>.Start(() =>
		{
			NatureFiles files = new NatureFiles
			{
				Trees = map.Disk_ReadTrees(),
				Plants = map.Disk_ReadPlants(),
				Boulders = map.Disk_ReadBoulders(),
				Irons = map.Disk_ReadIrons(),
				Pyrites = map.Disk_ReadPyrites()
			};
			SortTreesByMegaVoxel(files.Trees);
			return files;
		});
		yield return read.Wait();
		NatureFiles nature = read.Result;
		yield return LoadTrees(nature.Trees);
		BrixSingleton<PlantRegistry>.Instance.SerializedData = nature.Plants;
		BoulderRegistry.BuildBouldersFromSerialized(nature.Boulders);
		IronRegistry.BuildMineralsFromSerialized(nature.Irons);
		PyriteRegistry.BuildMineralsFromSerialized(nature.Pyrites);
	}

	private static IEnumerator LoadTrees(Asset_Map.TreeInfo[] trees)
	{
		int steps = 0;
		FrameBudget budget = new FrameBudget();
		foreach (Asset_Map.TreeInfo treeInfo in trees)
		{
			steps++;
			if (steps == 10000)
			{
				GameSignals.Invoke(GameSignals.LoadingProgress);
				steps = 0;
			}
			if (budget.Exceeded)
			{
				yield return null;
				budget.Reset();
			}
			XYZ position = new XYZ(treeInfo.x, treeInfo.y, treeInfo.z);
			int tree = TreeRegistry.GetPseudoRandomArbreInfo(position.ToScalaire());
			tree = (tree & 0xF) | (Mathf.Clamp(treeInfo.s, 0, 15) << 4);
			TreeRegistry.Instance.Add(position, tree);
		}
	}

	// Plain loop instead of Slinq: Slinq uses shared pools and this runs on a worker thread.
	private static void SortTreesByMegaVoxel(Asset_Map.TreeInfo[] trees)
	{
		int[] keys = new int[trees.Length];
		for (int i = 0; i < trees.Length; i++)
		{
			keys[i] = Brix.Engine.Abstract.Voxel.GetGScalaire(trees[i].x, trees[i].y, trees[i].z);
		}
		Array.Sort(keys, trees);
	}
}

// Terrain gravity setup yields on a time budget instead of after a fixed number of voxels.
[Feature(Features.FasterLoading, Features.FasterLoadingInfo)]
[HarmonyPatch(typeof(GameLoader), nameof(GameLoader.InitTerrainGravity))]
internal static class TerrainGravityPatch
{
	private static bool Prefix(ref IEnumerator __result)
	{
		__result = InitTerrainGravity();
		return false;
	}

	private static IEnumerator InitTerrainGravity()
	{
		IEnumerable<XYZ> visibleVoxels = Voxel.Engine.GetNaturalBottomVoxelPositionsFromLayers();
		int steps = 0;
		FrameBudget budget = new FrameBudget();
		foreach (XYZ p in visibleVoxels)
		{
			steps++;
			if (steps == 10000)
			{
				GameSignals.Invoke(GameSignals.LoadingProgress);
				steps = 0;
			}
			if (budget.Exceeded)
			{
				yield return null;
				budget.Reset();
			}
			GeostaticityTracker.SetGeostatique(p);
		}
	}
}

// More placed blocks spawn per yield (was 350).
[Feature(Features.FasterLoading, Features.FasterLoadingInfo)]
[HarmonyPatch(typeof(BlockEngine), nameof(BlockEngine.LoadBlockData))]
internal static class BlockSpawnPatch
{
	private static void Prefix(BlockEngine __instance)
	{
		__instance.maxSpawnPerYield = 1500;
	}
}

// Terrain chunks: the loop is frame bound, not CPU bound. Waiting a frame between a chunk's render and its
// collider upload made every chunk cost ~3 frames, and only 8 uploads were allowed per frame.
[Feature(Features.FasterLoading, Features.FasterLoadingInfo)]
[HarmonyPatch(typeof(TestWorldConfig3), nameof(TestWorldConfig3.InitFullFromLayers_Loop))]
internal static class TerrainChunkSyncPatch
{
	private static void Prefix(TestWorldConfig3 __instance)
	{
		__instance.threadWaitForCollider = false;
		__instance.maxThreadSync = Mathf.Max(__instance.maxThreadSync, __instance._threadedWorkers.Count);
	}
}

// Without the collider wait, a worker could be woken after draining its queue before it had reserved a
// new megavoxel, and render without a reservation. Only wake it once it has one.
[Feature(Features.FasterLoading, Features.FasterLoadingInfo)]
[HarmonyPatch(typeof(ThreadedHeavyWorker), nameof(ThreadedHeavyWorker.MainThreadWork))]
internal static class HeavyWorkerWakePatch
{
	private static bool Prefix(ThreadedHeavyWorker __instance, ref bool __result)
	{
		__result = MainThreadWork(__instance);
		return false;
	}

	private static bool MainThreadWork(ThreadedHeavyWorker worker)
	{
		lock (worker._requests)
		{
			if (worker._requests.Count == 0)
			{
				return false;
			}
			bool reserved = false;
			do
			{
				bool flag = false;
				switch (worker._requests.Peek())
				{
				case ThreadedHeavyWorker.Request.GetDirtyMegaVoxel:
					if (!worker.GetDirtyMegaVoxel())
					{
						return false;
					}
					reserved = true;
					break;
				case ThreadedHeavyWorker.Request.RenderMegaVoxel:
					worker.RenderMegaVoxel();
					flag = worker.Test.threadWaitForCollider;
					break;
				case ThreadedHeavyWorker.Request.RefreshCollider:
					worker.RefreshCollider();
					flag = true;
					break;
				case ThreadedHeavyWorker.Request.CleanupPostRender:
					worker.CleanupPostRender();
					break;
				}
				worker._requests.Dequeue();
				if (flag)
				{
					return true;
				}
			}
			while (worker._requests.Count > 0);
			if (!reserved)
			{
				return false;
			}
		}
		worker.Thread.Interrupt();
		return false;
	}
}
