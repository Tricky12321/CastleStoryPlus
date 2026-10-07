using System.Collections.Generic;
using UnityEngine;

namespace CastleStoryPlus.UI;

// Recoloured copies of the game's pictures. The game's own shaders mostly have no colour property (_Color), so a
// tint has to be painted into the picture itself: each pixel takes the colour at its own brightness. Metal only
// recolours the grey, unsaturated pixels (blades, rims, helmets) and leaves coloured ones (wood, leather, cloth, team
// colours); otherwise every pixel is recoloured. Pictures cannot be read directly, so they are read back through the
// graphics card. One copy per picture, colour and kind, shared by everything that uses it; null when the picture
// cannot be read.
internal static class TextureRecolour
{
	private static readonly string[] TextureProperties = new string[] { "_Diffuse", "_MainTex" };

	private static readonly Dictionary<Texture, Dictionary<string, Texture2D>> Copies = new Dictionary<Texture, Dictionary<string, Texture2D>>();

	// The material's picture property (with a picture), or null.
	internal static string TextureProperty(Material material)
	{
		if (material == null)
		{
			return null;
		}
		foreach (string name in TextureProperties)
		{
			if (material.HasProperty(name) && material.GetTexture(name) != null)
			{
				return name;
			}
		}
		return null;
	}

	internal static Texture2D Recoloured(Texture source, Color colour, bool metalOnly, string label)
	{
		if (source == null)
		{
			return null;
		}
		string key = colour.r.ToString("0.000") + "," + colour.g.ToString("0.000") + "," + colour.b.ToString("0.000") + (metalOnly ? " metal" : " all");
		if (!Copies.TryGetValue(source, out Dictionary<string, Texture2D> copies))
		{
			copies = new Dictionary<string, Texture2D>();
			Copies[source] = copies;
		}
		if (copies.TryGetValue(key, out Texture2D made))
		{
			return made;
		}
		Texture2D texture = null;
		try
		{
			Texture2D copy = CastleStoryPlus.Building.LongStoneTexture.ReadBack(source, linear: false);
			if (copy != null)
			{
				Color32[] pixels = copy.GetPixels32();
				for (int i = 0; i < pixels.Length; i++)
				{
					Color pixel = pixels[i];
					float weight = 1f;
					if (metalOnly)
					{
						float max = Mathf.Max(pixel.r, Mathf.Max(pixel.g, pixel.b));
						float min = Mathf.Min(pixel.r, Mathf.Min(pixel.g, pixel.b));
						float saturation = (max > 0.001f) ? (max - min) / max : 0f;
						weight = Mathf.Clamp01(1f - saturation * 4f);
						if (weight <= 0f)
						{
							continue;
						}
					}
					float brightness = pixel.r * 0.3f + pixel.g * 0.59f + pixel.b * 0.11f;
					Color target = new Color(Mathf.Clamp01(brightness * colour.r), Mathf.Clamp01(brightness * colour.g), Mathf.Clamp01(brightness * colour.b), pixel.a);
					pixels[i] = Color.Lerp(pixel, target, weight);
				}
				texture = new Texture2D(copy.width, copy.height, TextureFormat.RGBA32, true, false)
				{
					name = source.name + " (" + label + ")",
					wrapMode = source.wrapMode,
					filterMode = source.filterMode,
					anisoLevel = source.anisoLevel
				};
				texture.SetPixels32(pixels);
				texture.Apply(true);
				Object.Destroy(copy);
			}
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogWarning("TextureRecolour: could not recolour " + source.name + ": " + ex.Message);
			texture = null;
		}
		// Kept also when it failed, so a picture that cannot be read is not read again.
		copies[key] = texture;
		return texture;
	}
}
