using System;
using System.IO;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CastleStoryPlus.DevTools.Endpoints;

// A smaller copy of a screenshot (POST /api/screenshot with maxWidth), for tools that send the picture on: an MCP
// client closes a connection that sends more than 10 MB in one message, and a full-size PNG of the game, a third larger
// as base64, often is. Scaled on the graphics card, halving at a time so the picture stays sharp, on every platform.
// The full-size file stays where it is.
// After a request by Pabloks-png (pull request #4), who scaled the picture in the MCP server instead.
internal sealed class ScreenshotCopy : MonoBehaviour
{
	// How long to wait for a capture to be written (at the end of its frame).
	private const float WaitSeconds = 5f;

	private string _source;

	private string _target;

	private int _maxWidth;

	private float _until;

	private int _frame;

	// The copy of a capture that is written at the end of this frame.
	internal static void After(string source, string target, int maxWidth)
	{
		var go = new GameObject("CSP_ScreenshotCopy");
		Object.DontDestroyOnLoad(go);
		ScreenshotCopy copy = go.AddComponent<ScreenshotCopy>();
		copy._source = source;
		copy._target = target;
		copy._maxWidth = maxWidth;
		copy._until = Time.unscaledTime + WaitSeconds;
		copy._frame = Time.frameCount;
	}

	private void Update()
	{
		if (Time.unscaledTime > _until)
		{
			DevToolsPlugin.Log.LogWarning("DevTools: no screenshot to copy within " + WaitSeconds + " s: " + _source);
			Destroy(gameObject);
			return;
		}
		// The frame after the capture's: the file is written whole by then.
		if (Time.frameCount <= _frame + 1 || !File.Exists(_source) || new FileInfo(_source).Length == 0)
		{
			return;
		}
		try
		{
			File.WriteAllBytes(_target, Scaled(File.ReadAllBytes(_source), _maxWidth));
		}
		catch (Exception ex)
		{
			DevToolsPlugin.Log.LogWarning("DevTools: could not make the small screenshot: " + ex.Message);
		}
		Destroy(gameObject);
	}

	// The PNG at most maxWidth pixels wide (the same PNG when it is not wider).
	internal static byte[] Scaled(byte[] png, int maxWidth)
	{
		var source = new Texture2D(2, 2);
		try
		{
			if (!source.LoadImage(png) || source.width <= maxWidth)
			{
				return png;
			}
			int width = maxWidth;
			int height = Mathf.Max(1, Mathf.RoundToInt(source.height * (float)maxWidth / source.width));
			// Halving with bilinear filtering until one step from the size; one big step would skip pixels.
			Texture current = source;
			RenderTexture step = null;
			int w = source.width;
			int h = source.height;
			while (w / 2 >= width)
			{
				w /= 2;
				h = Mathf.Max(1, h / 2);
				RenderTexture next = RenderTexture.GetTemporary(w, h, 0);
				Graphics.Blit(current, next);
				if (step != null)
				{
					RenderTexture.ReleaseTemporary(step);
				}
				step = next;
				current = next;
			}
			RenderTexture final = RenderTexture.GetTemporary(width, height, 0);
			Graphics.Blit(current, final);
			if (step != null)
			{
				RenderTexture.ReleaseTemporary(step);
			}
			RenderTexture before = RenderTexture.active;
			RenderTexture.active = final;
			var result = new Texture2D(width, height, TextureFormat.RGB24, false);
			result.ReadPixels(new Rect(0, 0, width, height), 0, 0);
			result.Apply();
			RenderTexture.active = before;
			RenderTexture.ReleaseTemporary(final);
			byte[] scaled = result.EncodeToPNG();
			Object.Destroy(result);
			return scaled;
		}
		finally
		{
			Object.Destroy(source);
		}
	}
}
