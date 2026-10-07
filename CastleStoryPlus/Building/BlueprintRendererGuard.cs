using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Brix.Engine;
using Brix.Engine.Geometrie;
using Brix.External.Factories;
using Brix.Game.Components;
using Brix.Util;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Building;

// A blueprint's ghost mesh is built from its block's mesh descriptor, one piece per block the descriptor has, each
// looked up among the blueprint's own cells. When a descriptor block is not among those cells the game threw
// (KeyNotFoundException in BlueprintRenderer.RebuildMesh) and the blueprint kept no ghost mesh. Now it keeps its plain
// mesh instead, and the log says which blueprint it was and which block was missing (the cause found so far, the
// rounding of the long bricks, is fixed by BlueprintCellRounding below).
[Feature]
[HarmonyPatch(typeof(BlueprintRenderer), "RebuildMesh")]
internal static class BlueprintRendererGuard
{
	private static Exception Finalizer(BlueprintRenderer __instance, Exception __exception)
	{
		if (!(__exception is KeyNotFoundException) || __instance == null)
		{
			return __exception;
		}
		try
		{
			MeshFilter filter = __instance.GetComponent<MeshFilter>();
			if (filter != null && __instance.initialMesh != null)
			{
				filter.sharedMesh = __instance.initialMesh;
			}
			Plugin.Log.LogWarning("BlueprintRenderer: no ghost mesh for " + Describe(__instance));
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("BlueprintRenderer: no ghost mesh for " + __instance.name + " (" + ex.Message + ")");
		}
		return null;
	}

	// The blueprint, where it stands, its cells and the first descriptor block that is not among them.
	private static string Describe(BlueprintRenderer renderer)
	{
		Transform transform = renderer.transform;
		string text = renderer.name + " at " + transform.position + " turned " + transform.rotation.eulerAngles;
		XYZ[] cases = renderer.cases ?? new XYZ[0];
		text += ", cells " + string.Join(" ", Array.ConvertAll(cases, (XYZ c) => c.ToString()));
		AssetKeyHolder holder = renderer.GetComponent<AssetKeyHolder>();
		VoxelTransform voxelTransform = renderer.GetComponent<VoxelTransform>();
		if (holder == null || voxelTransform == null)
		{
			return text;
		}
		GameObject template = Factory.Peek(holder.AssetKey);
		Factory.AssetKey key = new Factory.AssetKey("Blocks_MeshDescriptors", template.name);
		text += ", block " + template.name + ", centre offset " + voxelTransform.CenterOffset;
		HashSet<XYZ> cells = new HashSet<XYZ>(cases);
		Vector3 origin = transform.position + transform.rotation * voxelTransform.CenterOffset;
		foreach (VoxelizedMesh.VoxelData voxel in Factory.Peek(key).GetComponent<VoxelizedMeshDescriptor>().voxelMesh.voxels)
		{
			XYZ cell = XYZ.FromVector3(BlueprintCellRounding.Snap(origin + transform.rotation * voxel.position.ToVector3(), renderer));
			if (!cells.Contains(cell))
			{
				text += ", descriptor block " + voxel.position + " lands on " + cell + ", not one of its cells";
				break;
			}
		}
		return text;
	}
}

// Why the ghost meshes of the 1 x 4 and 2 x 4 bricks were missing: the blueprint's cells are its root cell (its
// middle plus its centre offset, rounded once) plus whole blocks, but its ghost mesh rounded every block of the mesh
// descriptor on its own. Where the middle plus the centre offset falls between two cells (a new blueprint still at the
// world's origin, before it is put in place: a 4 long brick's offset of -1.5 lands on -1.5), the game's rounding (to
// the even number) sent -1.5 to -2 but 1.5 to 2, past the brick's last cell (1). The 1 x 2 brick's -0.5 and 0.5 both
// land on 0, one of its cells, so it never showed. Now every descriptor block is rounded as the cells are: the root
// cell rounded once, then whole blocks from it. Where the middle is on a cell, as for every placed blueprint, nothing
// changes.
[Feature]
[HarmonyPatch]
internal static class BlueprintCellRounding
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(BlueprintRenderer), "RebuildMesh");
		yield return AccessTools.Method(typeof(BlueprintRenderer), "RefreshSelfConfigs");
	}

	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
	{
		MethodInfo round = AccessTools.Method(typeof(Calcule), nameof(Calcule.RoundVecteur), new Type[] { typeof(Vector3) });
		List<CodeInstruction> code = new List<CodeInstruction>(instructions);
		int replaced = 0;
		for (int i = 0; i < code.Count; i++)
		{
			if (!code[i].Calls(round))
			{
				continue;
			}
			// The block's position is on the stack; the renderer goes after it.
			code[i] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BlueprintCellRounding), nameof(Snap))).MoveLabelsFrom(code[i]);
			code.Insert(i, new CodeInstruction(OpCodes.Ldarg_0));
			i++;
			replaced++;
		}
		if (replaced == 0)
		{
			Plugin.Log.LogWarning("BlueprintRenderer: no block rounding found in BlueprintRenderer." + __originalMethod.Name);
		}
		return code;
	}

	// A descriptor block's position rounded to its cell: the root cell, rounded as the blueprint's cells are, plus the
	// whole blocks from the root to it.
	internal static Vector3 Snap(Vector3 position, BlueprintRenderer renderer)
	{
		VoxelTransform voxelTransform = (renderer != null) ? renderer.GetComponent<VoxelTransform>() : null;
		if (voxelTransform == null)
		{
			return Calcule.RoundVecteur(position);
		}
		Transform transform = renderer.transform;
		Vector3 origin = transform.position + transform.rotation * voxelTransform.CenterOffset;
		return XYZ.FromVector3(origin).ToVector3() + Calcule.RoundVecteur(position - origin);
	}
}
