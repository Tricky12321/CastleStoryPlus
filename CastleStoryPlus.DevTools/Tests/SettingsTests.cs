using System.Collections;
using System.Reflection;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.UI;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.DevTools.Tests;

// The All settings list built into a narrow page, as in Options > Castle Story Plus.
internal static class SettingsTests
{
	[GameTest("Settings", "All settings in a narrow page: every value's name keeps its room beside the editor, and the refresh rate shows in Hz (#95)", NeedsHostGame = false, TimeoutSeconds = 30f)]
	private static IEnumerator NarrowAllSettings(TestContext t)
	{
		GameObject canvasGo = new GameObject("CSP test settings canvas", typeof(RectTransform));
		t.Cleanup(() => Object.Destroy(canvasGo));
		Canvas canvas = canvasGo.AddComponent<Canvas>();
		canvas.renderMode = RenderMode.ScreenSpaceOverlay;
		RectTransform page = new GameObject("Page", typeof(RectTransform)).GetComponent<RectTransform>();
		page.SetParent(canvasGo.transform, false);
		page.anchorMin = page.anchorMax = new Vector2(0.5f, 0.5f);
		page.sizeDelta = new Vector2(560f, 600f);
		AllSettingsPanel panel = AllSettingsPanel.Embed(page, 560f);
		// Shown as the page is: the values are listed when it becomes active.
		panel.gameObject.SetActive(false);
		panel.gameObject.SetActive(true);
		InputField search = (InputField)typeof(AllSettingsPanel).GetField("_search", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
		search.text = "RefreshRate";
		yield return null;
		LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)panel.transform);
		yield return null;
		int rows = 0;
		float narrowest = float.MaxValue;
		foreach (RectTransform rect in panel.GetComponentsInChildren<RectTransform>(true))
		{
			if (rect.name != "Names")
			{
				continue;
			}
			rows++;
			narrowest = Mathf.Min(narrowest, rect.rect.width);
			Text title = rect.GetComponentInChildren<Text>();
			Text value = null;
			foreach (Text text in rect.parent.GetComponentsInChildren<Text>(true))
			{
				if (text.GetComponentInParent<Button>() != null)
				{
					value = text;
				}
			}
			t.Log(((title != null) ? title.text : "?") + ": name " + rect.rect.width + " wide, value " + ((value != null) ? value.text : "none"));
			if (title != null && title.text == "REFRESH RATE")
			{
				t.NotNull(value, "The refresh rate's value");
				t.True(!value.text.Contains("/"), "The refresh rate shows as a fraction: " + value.text);
				t.True(value.text == "Auto" || value.text.EndsWith(" Hz"), "The refresh rate is not shown in Hz: " + value.text);
			}
		}
		t.Record("rows", rows);
		t.Record("narrowestName", narrowest);
		if (rows == 0)
		{
			t.Skip("No value matches RefreshRate (the refresh rate feature is off)");
		}
		t.AtLeast(149f, narrowest, "Width of the narrowest value name");
		// Many values of every kind (switches, numbers, keys, choices, text).
		search.text = "e";
		yield return null;
		LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)panel.transform);
		yield return null;
		int all = 0;
		float narrowestAll = float.MaxValue;
		foreach (RectTransform rect in panel.GetComponentsInChildren<RectTransform>(true))
		{
			if (rect.name == "Names")
			{
				all++;
				narrowestAll = Mathf.Min(narrowestAll, rect.rect.width);
			}
		}
		t.Record("rowsAll", all);
		t.Record("narrowestNameAll", narrowestAll);
		t.AtLeast(149f, narrowestAll, "Width of the narrowest value name of every value with an e");
	}
}
