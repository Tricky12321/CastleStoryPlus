using System;
using System.Collections;
using System.Collections.Generic;
using Brix.Components;
using Brix.Engine;
using Brix.Engine.Blocks;
using Brix.UI;
using CastleStoryPlus.Core;
using CastleStoryPlus.Loading;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.UI;

// The minimap only showed units and crystals on a plain background: its shader draws the terrain only with
// _ShowTerrain, which the game never turns on. The minimap now shows a top-down map of the island: terrain type
// colours (grass, earth, snow, pavement...), lighter the higher it is, shaded slopes, and placed blocks (walls,
// buildings) in stone grey. Units and crystals are drawn on top as before. Refreshed every 15 s, built a slice
// per frame so the game does not stutter.
[Feature(Features.MinimapTerrain, Features.MinimapTerrainInfo)]
[HarmonyPatch(typeof(MiniMap), nameof(MiniMap.InitHeightmap))]
internal class MinimapTerrain : MonoBehaviour
{
	private const float RefreshSeconds = 15f;

	private const int MaxSize = 1024;

	private const byte BlockType = 255;

	private static readonly Color32 Background = new Color32(18, 24, 34, 255);

	private static readonly Color BlockColor = new Color(0.62f, 0.6f, 0.56f, 1f);

	private MiniMap _minimap;

	private Texture2D _texture;

	private int _minX;

	private int _minZ;

	private int _size;

	private static void Postfix(MiniMap __instance)
	{
		if (__instance.mapImage == null || __instance.GetComponent<MinimapTerrain>() != null)
		{
			return;
		}
		__instance.gameObject.AddComponent<MinimapTerrain>()._minimap = __instance;
	}

	private IEnumerator Start()
	{
		Bounds bounds = _minimap.mapBounds;
		_minX = Mathf.FloorToInt(bounds.min.x);
		_minZ = Mathf.FloorToInt(bounds.min.z);
		_size = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(bounds.size.x, bounds.size.z)), 1, MaxSize);
		_texture = new Texture2D(_size, _size, TextureFormat.ARGB32, mipmap: false);
		_texture.filterMode = FilterMode.Bilinear;
		_texture.wrapMode = TextureWrapMode.Clamp;
		while (true)
		{
			yield return Build();
			yield return new WaitForSecondsRealtime(RefreshSeconds);
		}
	}

	private void OnDestroy()
	{
		if (_texture != null)
		{
			Destroy(_texture);
		}
	}

	private IEnumerator Build()
	{
		int count = _size * _size;
		int[] heights = new int[count];
		byte[] types = new byte[count];
		for (int i = 0; i < count; i++)
		{
			heights[i] = -1;
		}
		FrameBudget budget = new FrameBudget(4);
		// The terrain can change between frames; if the enumeration breaks, this refresh is skipped.
		IEnumerator<XYZ> tops = Voxel.Engine.GetTopVoxelPositionsFromLayers().GetEnumerator();
		while (true)
		{
			XYZ top;
			try
			{
				if (!tops.MoveNext())
				{
					break;
				}
				top = tops.Current;
			}
			catch (InvalidOperationException)
			{
				yield break;
			}
			int index = IndexOf(top);
			if (index >= 0 && top.y > heights[index])
			{
				heights[index] = top.y;
				types[index] = (byte)Voxel.GetTerrainBlockData(top).BlockInfo.ID;
			}
			if (budget.Exceeded)
			{
				yield return null;
				budget.Reset();
			}
		}
		BlockEngine blocks = BrixSingleton<BlockEngine>.Instance;
		if (blocks != null && blocks._blocks != null)
		{
			List<XYZ> placed = new List<XYZ>(blocks._blocks.Keys);
			foreach (XYZ block in placed)
			{
				int index = IndexOf(block);
				if (index >= 0 && block.y >= heights[index])
				{
					heights[index] = block.y;
					types[index] = BlockType;
				}
				if (budget.Exceeded)
				{
					yield return null;
					budget.Reset();
				}
			}
		}
		yield return null;
		_texture.SetPixels32(Colorize(heights, types));
		_texture.Apply();
		Show();
	}

	private int IndexOf(XYZ position)
	{
		int x = position.x - _minX;
		int z = position.z - _minZ;
		if (x < 0 || z < 0 || x >= _size || z >= _size)
		{
			return -1;
		}
		return x + z * _size;
	}

	private Color32[] Colorize(int[] heights, byte[] types)
	{
		int minY = int.MaxValue;
		int maxY = int.MinValue;
		foreach (int height in heights)
		{
			if (height >= 0)
			{
				minY = Mathf.Min(minY, height);
				maxY = Mathf.Max(maxY, height);
			}
		}
		float range = Mathf.Max(1, maxY - minY);
		Color32[] pixels = new Color32[heights.Length];
		for (int z = 0; z < _size; z++)
		{
			for (int x = 0; x < _size; x++)
			{
				int index = x + z * _size;
				int height = heights[index];
				if (height < 0)
				{
					pixels[index] = Background;
					continue;
				}
				Color color = (types[index] == BlockType) ? BlockColor : Minimap.typeColors[types[index]];
				float shade = Mathf.Lerp(0.7f, 1.15f, (height - minY) / range);
				// Light from the north-west: slopes facing it are lighter, the others darker; cliffs get an edge.
				int west = (x > 0) ? heights[index - 1] : height;
				int north = (z < _size - 1) ? heights[index + _size] : height;
				float slope = (west < 0 ? 0 : height - west) + (north < 0 ? 0 : height - north);
				shade += Mathf.Clamp(slope * 0.05f, -0.3f, 0.3f);
				pixels[index] = (Color32)new Color(color.r * shade, color.g * shade, color.b * shade, 1f);
			}
		}
		return pixels;
	}

	// The game's map material only draws the heightmap data; show the coloured map with the plain UI material.
	private void Show()
	{
		RawImage image = _minimap.mapImage;
		if (image == null)
		{
			return;
		}
		if (image.texture != _texture)
		{
			image.material = null;
			image.texture = _texture;
			image.uvRect = new Rect(0f, 0f, 1f, 1f);
			image.color = Color.white;
		}
	}
}
