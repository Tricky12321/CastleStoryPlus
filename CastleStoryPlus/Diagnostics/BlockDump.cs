using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game.AI.Damage;
using Brix.Game.Components;
using Brix.Game.Factories;
using Brix.Game.Semantique;
using CastleStoryPlus.Building;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Diagnostics;

// Modding aid: once the game has made its block infos, writes every block to BepInEx/blockdump.log: ID, keys,
// groups, resource, size, HP, the blueprint's cost, components (fitter, validator) and cells, and the block's mesh
// and materials. The values live in the game's prefabs, not in code; new blocks are cloned from these.
[Feature(Features.DebugMenu, Features.DebugMenuInfo)]
[HarmonyPatch(typeof(BlockInfoGenerator), nameof(BlockInfoGenerator.CreateMetaBlockInfos))]
internal static class BlockDump
{
	private static bool _done;

	private static void Postfix()
	{
		if (_done)
		{
			return;
		}
		_done = true;
		try
		{
			StringBuilder text = new StringBuilder();
			text.AppendLine("Block dump " + DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss"));
			List<int> ids = new List<int>(BlockInfoGenerator._idToBlockInfos.Keys);
			ids.Sort();
			foreach (int id in ids)
			{
				Dump(BlockInfoGenerator._idToBlockInfos[id], text);
			}
			string path = Path.Combine(Paths.BepInExRootPath, "blockdump.log");
			File.WriteAllText(path, text.ToString());
			Plugin.Log.LogInfo("BlockDump: " + ids.Count + " blocks written to " + path);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("BlockDump: " + ex);
		}
	}

	private static void Dump(BlockInfo info, StringBuilder text)
	{
		text.AppendLine();
		text.AppendLine("#" + info.ID + " " + info.Key + " blueprint " + info.BlueprintKey + " groups " + info.Groups + " resource " + info.Ressource + " type " + info.Type
			+ " partial " + info.IsPartialBloc + " mergeable " + info.MergeableBloc + " centre " + info.CenterOffset + " maxMissingSupport " + info.MaxMissingSupport);
		try
		{
			GameObject block = Factory.Peek(info.Key);
			if (block != null)
			{
				IDamageReceiver damage = block.GetComponent<IDamageReceiver>();
				text.AppendLine("  block HP " + ((damage != null) ? damage.MaxHP.ToString() : "-") + " components " + Components(block));
				MeshFilter mesh = block.GetComponentInChildren<MeshFilter>(true);
				if (mesh != null && mesh.sharedMesh != null)
				{
					text.AppendLine("  mesh " + mesh.sharedMesh.name + " bounds " + mesh.sharedMesh.bounds + " submeshes " + mesh.sharedMesh.subMeshCount + " readable " + mesh.sharedMesh.isReadable);
				}
				MeshRenderer renderer = block.GetComponentInChildren<MeshRenderer>(true);
				if (renderer != null)
				{
					foreach (Material material in renderer.sharedMaterials)
					{
						if (material != null)
						{
							text.AppendLine("  material " + material.name + " shader " + material.shader.name + " texture " + ((material.mainTexture != null) ? material.mainTexture.name : "-"));
						}
					}
				}
			}
		}
		catch (Exception ex)
		{
			text.AppendLine("  block: " + ex.Message);
		}
		if (info.BlueprintKey == null || string.IsNullOrEmpty(info.BlueprintKey.Name))
		{
			return;
		}
		try
		{
			GameObject blueprint = Factory.Peek(info.BlueprintKey);
			if (blueprint != null)
			{
				Recepteur recepteur = blueprint.GetComponent<Recepteur>();
				text.AppendLine("  blueprint cost " + ((recepteur != null) ? CustomBuilding.Describe(recepteur._baseCapacity) : "-") + " components " + Components(blueprint));
			}
			Dictionary<XYZ, Brix.Game.Components.Volumes.VPropertyMask> cells = VolumePreprocessor.GetIndexedVSettings(info.BlueprintKey, 0);
			if (cells != null)
			{
				List<string> parts = new List<string>();
				foreach (KeyValuePair<XYZ, Brix.Game.Components.Volumes.VPropertyMask> cell in cells)
				{
					parts.Add(cell.Key.x + "," + cell.Key.y + "," + cell.Key.z + "=" + cell.Value);
				}
				text.AppendLine("  blueprint cells " + string.Join(" ", parts.ToArray()));
			}
		}
		catch (Exception ex)
		{
			text.AppendLine("  blueprint: " + ex.Message);
		}
	}

	private static string Components(GameObject go)
	{
		List<string> names = new List<string>();
		foreach (Component component in go.GetComponents<Component>())
		{
			if (component != null)
			{
				names.Add(component.GetType().Name);
			}
		}
		return string.Join(", ", names.ToArray());
	}
}
