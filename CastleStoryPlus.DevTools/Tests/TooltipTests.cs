using System.Collections;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.LuaUi;
using CastleStoryPlus.UI;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.DevTools.Tests;

// The C# menus' tooltips (LuiTooltip, LuiHoverTip): drawn above every panel, never clipped and on screen (#160).
internal static class TooltipTests
{
	[GameTest("UI", "A tooltip is drawn above every panel (the scoreboard's kill counters too), is not clipped and stays on screen (#160)", NeedsGraphics = true)]
	private static IEnumerator TooltipOnTopAndOnScreen(TestContext t)
	{
		LuiCanvas lui = LuiCanvas.Instance;
		yield return null;
		Canvas tipCanvas = lui.TooltipCanvas;
		t.NotNull(tipCanvas, "The tooltip layer's canvas");
		t.True(tipCanvas.overrideSorting, "The tooltip layer's canvas does not sort on its own");
		t.Equal(LuiCanvas.TooltipOrder, tipCanvas.sortingOrder, "The tooltip layer's sorting order");
		foreach (Canvas canvas in Object.FindObjectsOfType<Canvas>())
		{
			if (canvas == tipCanvas || !canvas.isActiveAndEnabled || !(canvas.isRootCanvas || canvas.overrideSorting))
			{
				continue;
			}
			if (canvas.rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay)
			{
				continue;
			}
			t.True(canvas.sortingOrder < tipCanvas.sortingOrder, "The canvas " + canvas.name + " sorts at " + canvas.sortingOrder + ", above the tooltips");
		}

		// A kill counter's tooltip on the scoreboard, where the game has one with tooltips (invasion), else one of our own.
		ScoreboardView scoreboard = Object.FindObjectOfType<ScoreboardView>();
		LuiHoverTip hover = (scoreboard != null) ? scoreboard.GetComponentInChildren<LuiHoverTip>() : null;
		t.Record("scoreboardTip", hover != null);
		if (hover != null)
		{
			LuiTooltip.Show(hover.Text, (RectTransform)hover.transform);
			t.Cleanup(() => LuiTooltip.Hide(null));
			yield return null;
			CheckPanel(t, tipCanvas, "the scoreboard's kill counter");
			Canvas boardCanvas = scoreboard.GetComponentInParent<Canvas>();
			t.True(boardCanvas == null || boardCanvas.rootCanvas.sortingOrder < tipCanvas.sortingOrder, "The scoreboard's canvas sorts above the tooltip");
			LuiTooltip.Hide((RectTransform)hover.transform);
		}

		// Targets in the screen's corners: the tooltip is moved back onto the screen.
		Vector2[] corners = { new Vector2(1f, 1f), new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f) };
		foreach (Vector2 corner in corners)
		{
			RectTransform target = UiKit.CreateRect("CSP_TooltipTestTarget", lui.Menus);
			t.Cleanup(() =>
			{
				LuiTooltip.Hide(null);
				if (target != null)
				{
					Object.Destroy(target.gameObject);
				}
			});
			target.anchorMin = target.anchorMax = target.pivot = corner;
			target.sizeDelta = new Vector2(40f, 40f);
			target.anchoredPosition = Vector2.zero;
			LuiTooltip.Show("A tooltip in the corner " + corner + " of the screen, long enough to need more than one line of text in it.", target);
			yield return null;
			CheckPanel(t, tipCanvas, "a target in the corner " + corner);
			LuiTooltip.Hide(target);
			Object.Destroy(target.gameObject);
		}
	}

	private static void CheckPanel(TestContext t, Canvas tipCanvas, string what)
	{
		RectTransform panel = LuiTooltip.Panel;
		t.NotNull(panel, "The tooltip for " + what);
		t.True(panel.gameObject.activeInHierarchy, "The tooltip for " + what + " is not shown");
		t.True(panel.GetComponentInParent<Canvas>() == tipCanvas, "The tooltip for " + what + " is not on the tooltip layer's canvas");
		t.True(panel.GetComponentInParent<Mask>() == null && panel.GetComponentInParent<RectMask2D>() == null, "The tooltip for " + what + " is under a mask");
		Vector3[] world = new Vector3[4];
		panel.GetWorldCorners(world);
		// A screen space overlay canvas: world positions are screen pixels.
		const float slack = 1f;
		t.True(world[0].x >= -slack && world[0].y >= -slack, "The tooltip for " + what + " goes off the screen's bottom left: " + world[0]);
		t.True(world[2].x <= Screen.width + slack && world[2].y <= Screen.height + slack, "The tooltip for " + what + " goes off the screen's top right: " + world[2] + " (screen " + Screen.width + " x " + Screen.height + ")");
	}
}
