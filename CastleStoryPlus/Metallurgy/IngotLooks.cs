using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CastleStoryPlus.Metallurgy;

// How steel and glass ingots look, on stockpiles and as loose items. Steel takes the game's iron ingot (its mesh, its
// place on a stockpile) with a copy of the iron material tinted a cool blue-grey and polished (#146): it used to lie
// on a stockpile as the game's terracotta, pots in a clay texture that looked like wood, and later as the game's
// glass ingots, which looked like glass. Glass gets a clear, shiny light-blue material in its place, which reads as
// glass. Done once on the factories' templates (stockpiles and the ingot items), so everything made from them looks so.
internal static class IngotLooks
{
	private static readonly HashSet<GameObject> Done = new HashSet<GameObject>();

	private static readonly Dictionary<Material, Material> Glassy = new Dictionary<Material, Material>();

	// The clear glass back to the game's glass material it replaced (a stockpile copied from one already done, the
	// large stockpile, has the clear glass on its glass track).
	private static readonly Dictionary<Material, Material> Originals = new Dictionary<Material, Material>();

	private static readonly Dictionary<Material, Material> Steely = new Dictionary<Material, Material>();

	private static readonly HashSet<Material> SteelMaterials = new HashSet<Material>();

	// Multiplies the iron material's colour: darker in red, lighter in blue, so steel is a cool blue-grey next to the
	// warmer iron and opaque next to the clear light-blue glass.
	internal static readonly Color SteelTint = new Color(0.7f, 0.85f, 1.25f);

	// Steel is polished: smoother than the iron it is made from.
	private const float SteelGlossiness = 0.75f;

	private static readonly string[] ColorProperties = { "_Color", "_BaseColor", "_TintColor" };

	private static readonly string[] GlossProperties = { "_Gloss", "_Glossiness" };

	internal static Material Original(Material material)
	{
		return (material != null && Originals.TryGetValue(material, out Material original)) ? original : material;
	}

	// The clear glass: Unity's standard shader in its transparent mode; the original when the game has no such shader.
	internal static Material GlassLook(Material original)
	{
		if (original == null || Originals.ContainsKey(original))
		{
			return original;
		}
		if (Glassy.TryGetValue(original, out Material glass) && glass != null)
		{
			return glass;
		}
		Shader standard = Shader.Find("Standard");
		if (standard == null)
		{
			Plugin.Log.LogWarning("Metallurgy: no standard shader for the glass look; glass keeps its own");
			Glassy[original] = original;
			return original;
		}
		glass = new Material(standard);
		glass.name = "CastleStoryPlus glass";
		glass.color = new Color(0.72f, 0.9f, 1f, 0.42f);
		glass.SetFloat("_Mode", 3f);
		glass.SetInt("_SrcBlend", (int)BlendMode.One);
		glass.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
		glass.SetInt("_ZWrite", 0);
		glass.DisableKeyword("_ALPHATEST_ON");
		glass.DisableKeyword("_ALPHABLEND_ON");
		glass.EnableKeyword("_ALPHAPREMULTIPLY_ON");
		glass.SetFloat("_Glossiness", 0.95f);
		glass.SetFloat("_Metallic", 0.05f);
		glass.renderQueue = 3000;
		Glassy[original] = glass;
		Originals[glass] = original;
		return glass;
	}

	// A stockpile template: its steel track ("TerraCotta") gets the iron track's ingots ("Ingots": their places, their
	// meshes and the steel look of their material), then the glass track the clear glass. A stockpile without an iron
	// track lays its steel out as its glass ingots.
	internal static void Storage(GameObject storage)
	{
		if (storage == null || !Done.Add(storage))
		{
			return;
		}
		Transform steel = Find(storage.transform, "TerraCotta");
		Transform glass = Find(storage.transform, "Glass");
		Transform iron = Find(storage.transform, "Ingots");
		Transform model = (iron != null) ? iron : glass;
		if (steel == null || model == null)
		{
			return;
		}
		steel.localPosition = model.localPosition;
		steel.localRotation = model.localRotation;
		steel.localScale = model.localScale;
		List<Transform> from = Pieces(model);
		List<Transform> to = Pieces(steel);
		for (int i = 0; i < to.Count && i < from.Count; i++)
		{
			to[i].localPosition = from[i].localPosition;
			to[i].localRotation = from[i].localRotation;
			to[i].localScale = from[i].localScale;
			MeshFilter source = from[i].GetComponent<MeshFilter>();
			MeshFilter target = to[i].GetComponent<MeshFilter>();
			if (source != null && target != null)
			{
				target.sharedMesh = source.sharedMesh;
			}
			Renderer sourceRenderer = from[i].GetComponent<Renderer>();
			Renderer targetRenderer = to[i].GetComponent<Renderer>();
			if (sourceRenderer != null && targetRenderer != null)
			{
				Material[] materials = sourceRenderer.sharedMaterials;
				for (int m = 0; m < materials.Length; m++)
				{
					materials[m] = SteelLook(Original(materials[m]));
				}
				targetRenderer.sharedMaterials = materials;
			}
		}
		if (glass != null)
		{
			foreach (Transform piece in Pieces(glass))
			{
				Glassify(piece.gameObject);
			}
		}
		Plugin.Log.LogInfo("Metallurgy: steel (as " + model.name + ") and glass ingots on " + storage.name + " (" + to.Count + " pieces)");
	}

	// The loose steel ingot (a copy of the iron ingot): its materials take the steel look.
	internal static void SteelItem(GameObject steel)
	{
		if (steel == null)
		{
			return;
		}
		foreach (Renderer renderer in steel.GetComponentsInChildren<Renderer>(true))
		{
			Material[] materials = renderer.sharedMaterials;
			for (int i = 0; i < materials.Length; i++)
			{
				materials[i] = SteelLook(materials[i]);
			}
			renderer.sharedMaterials = materials;
		}
	}

	// The steel look of a material: a copy, its colour multiplied by SteelTint, smoother. One copy per material; a
	// steel material stays as it is (a stockpile copied from one already done).
	internal static Material SteelLook(Material original)
	{
		if (original == null || SteelMaterials.Contains(original))
		{
			return original;
		}
		if (Steely.TryGetValue(original, out Material steel) && steel != null)
		{
			return steel;
		}
		steel = new Material(original);
		steel.name = original.name + " (CastleStoryPlus steel)";
		bool tinted = false;
		foreach (string property in ColorProperties)
		{
			if (steel.HasProperty(property))
			{
				Color color = original.GetColor(property);
				steel.SetColor(property, new Color(color.r * SteelTint.r, color.g * SteelTint.g, color.b * SteelTint.b, color.a));
				tinted = true;
			}
		}
		if (!tinted)
		{
			Plugin.Log.LogWarning("Metallurgy: the material " + original.name + " (shader " + original.shader.name + ") has no colour to tint; steel keeps it");
		}
		// The iron ingot's shader (Shader Forge/Metal) calls it _Gloss; Unity's standard shader _Glossiness.
		foreach (string property in GlossProperties)
		{
			if (steel.HasProperty(property))
			{
				steel.SetFloat(property, Mathf.Max(steel.GetFloat(property), SteelGlossiness));
			}
		}
		Steely[original] = steel;
		SteelMaterials.Add(steel);
		return steel;
	}

	// Whether the material is a steel look (for tests).
	internal static bool IsSteelLook(Material material)
	{
		return material != null && SteelMaterials.Contains(material);
	}

	// Whether the material is the clear glass look (for tests).
	internal static bool IsGlassLook(Material material)
	{
		return material != null && Originals.ContainsKey(material);
	}

	// The loose glass ingot.
	internal static void GlassItem(GameObject glassItem)
	{
		if (glassItem != null && Done.Add(glassItem))
		{
			Glassify(glassItem);
		}
	}

	private static void Glassify(GameObject go)
	{
		foreach (Renderer renderer in go.GetComponentsInChildren<Renderer>(true))
		{
			Material[] materials = renderer.sharedMaterials;
			for (int i = 0; i < materials.Length; i++)
			{
				materials[i] = GlassLook(materials[i]);
			}
			renderer.sharedMaterials = materials;
		}
	}

	// A track's pieces in their order (the children with a mesh).
	private static List<Transform> Pieces(Transform track)
	{
		List<Transform> pieces = new List<Transform>();
		foreach (Transform child in track)
		{
			if (child.GetComponent<MeshFilter>() != null)
			{
				pieces.Add(child);
			}
		}
		return pieces;
	}

	private static Transform Find(Transform parent, string name)
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
