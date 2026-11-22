using System.Collections;
using System.Collections.Generic;
using Brix.External.Factories;
using Brix.UI.Icons;
using CastleStoryPlus.Core;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.LuaUi;
using CastleStoryPlus.Metallurgy;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// How things look: steel ingots (#146) and the scoreboard's enemy icons (#140).
internal static class LookTests
{
#if !ESSENTIALS
	[GameTest("Metallurgy", "Steel on a stockpile and as a loose ingot has the iron ingot's mesh with the steel look, not the glass or the iron material (#146)")]
	private static IEnumerator SteelLooksLikeTintedIron(TestContext t)
	{
		if (!Plugin.IsEnabled(Features.Metallurgy))
		{
			t.Skip("Metallurgy is off");
		}
		yield return null;
		Factory build = Metallurgy.Metallurgy.Ready("BuildResources", Metallurgy.Metallurgy.SteelName);
		t.NotNull(build, "The build resources factory with the steel ingot");
		GameObject steelItem = build.GetTemplate(Metallurgy.Metallurgy.SteelName).gameObject;
		GameObject ironItem = build.GetTemplate("IronIngot").gameObject;
		Renderer steelRenderer = steelItem.GetComponentInChildren<Renderer>(true);
		Renderer ironRenderer = ironItem.GetComponentInChildren<Renderer>(true);
		t.True(steelRenderer != null && ironRenderer != null, "The steel and iron ingots have a renderer");
		foreach (Material material in steelRenderer.sharedMaterials)
		{
			t.True(IngotLooks.IsSteelLook(material), "The loose steel ingot's material " + Name(material) + " is no steel look");
			t.False(IngotLooks.IsGlassLook(material), "The loose steel ingot has the glass look");
		}
		MeshFilter steelMesh = steelItem.GetComponentInChildren<MeshFilter>(true);
		MeshFilter ironMesh = ironItem.GetComponentInChildren<MeshFilter>(true);
		if (steelMesh != null && ironMesh != null)
		{
			t.True(steelMesh.sharedMesh == ironMesh.sharedMesh, "The loose steel ingot has another mesh than the iron ingot");
		}

		Factory storages = Metallurgy.Metallurgy.Ready("ObjetsDynamiques", "Palette");
		t.NotNull(storages, "The stockpile factory");
		GameObject pallet = storages.GetTemplate("Palette").gameObject;
		Transform steel = FindChild(pallet.transform, "TerraCotta");
		Transform iron = FindChild(pallet.transform, "Ingots");
		t.True(steel != null && iron != null, "The stockpile has a steel (TerraCotta) and an iron (Ingots) track");
		List<Mesh> ironMeshes = new List<Mesh>();
		foreach (MeshFilter filter in iron.GetComponentsInChildren<MeshFilter>(true))
		{
			ironMeshes.Add(filter.sharedMesh);
		}
		int pieces = 0;
		foreach (MeshFilter filter in steel.GetComponentsInChildren<MeshFilter>(true))
		{
			Renderer renderer = filter.GetComponent<Renderer>();
			if (renderer == null)
			{
				continue;
			}
			pieces++;
			t.True(ironMeshes.Contains(filter.sharedMesh), "The stockpile's steel piece " + filter.name + " has no iron ingot mesh");
			foreach (Material material in renderer.sharedMaterials)
			{
				t.True(IngotLooks.IsSteelLook(material), "The stockpile's steel piece " + filter.name + " has the material " + Name(material) + ", no steel look");
			}
		}
		t.Record("steelPieces", pieces);
		t.AtLeast(1f, pieces, "steel pieces on the stockpile");
		foreach (Renderer renderer in iron.GetComponentsInChildren<Renderer>(true))
		{
			foreach (Material material in renderer.sharedMaterials)
			{
				t.False(IngotLooks.IsSteelLook(material), "The stockpile's iron ingots took the steel look");
			}
		}
	}
#endif

	[GameTest("UI", "Every enemy kind on the scoreboard has an icon, the Healtron's badge too (#140)", NeedsHostGame = false)]
	private static IEnumerator ScoreboardEnemyIcons(TestContext t)
	{
		yield return null;
		string[] names = { "minitron", "corruptron", "healtron", "biftron", "warlock" };
		for (int k = 0; k < names.Length; k++)
		{
			Sprite sprite = ScoreboardView.IconSprite(ScoreboardView.KindIcon(k));
			t.True(sprite != null, "The " + names[k] + " has no icon");
		}
		t.True(ScoreboardView.IconSprite(IconKeys._Corruptrons_Group) != null, "The corrupted group has no icon");
		t.True(ScoreboardView.IconSprite(IconKeys.UI_Plus) != null, "The Healtron's plus has no icon");
	}

	private static string Name(Material material)
	{
		return (material != null) ? material.name : "(none)";
	}

	private static Transform FindChild(Transform parent, string name)
	{
		foreach (Transform child in parent.GetComponentsInChildren<Transform>(true))
		{
			if (child.name == name)
			{
				return child;
			}
		}
		return null;
	}
}
