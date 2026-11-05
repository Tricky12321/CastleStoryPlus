#if UNITY6
#if !UNITY6_PLAYER
using Brix.External.Factories.Templates;
#endif
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CastleStoryPlus.Core;

#if !UNITY6_PLAYER

// The Unity 6 reconstruction (editor): Unity 6 sizes a root screen-space canvas to the screen as soon as it is
// enabled; Unity 5.6 did that later, when it drew. The game clones its UI templates as root objects and moves them
// under their canvas right after, so their nested canvases kept the screen's size: the small arrows and backgrounds
// of the bricktrons' name tags covered the screen as dark plates. Each canvas of a clone gets the template's layout
// back once it is nested. The port does the same through its installer (Compat.NestedCanvasRect).
[Feature]
[HarmonyPatch(typeof(CloneTemplate), nameof(CloneTemplate.Instantiate))]
internal static class NestedCanvasFix
{
	private static void Postfix(CloneTemplate __instance, GameObject __result)
	{
		if (__result == null)
		{
			return;
		}
		Canvas[] templates = __instance.gameObject.GetComponentsInChildren<Canvas>(true);
		Canvas[] canvases = __result.GetComponentsInChildren<Canvas>(true);
		for (int i = 0; i < canvases.Length && i < templates.Length; i++)
		{
			if (canvases[i].GetComponent<NestedCanvasLayout>() == null)
			{
				canvases[i].gameObject.AddComponent<NestedCanvasLayout>().Capture((RectTransform)templates[i].transform);
			}
		}
	}
}

// Puts a nested canvas's layout back to its template's once it is under another canvas.
// The fields are serialized so a clone of the clone (a dropdown's list) carries them; one that captured nothing
// leaves its layout alone (else the list got scale 0 and opened unseen).
internal sealed class NestedCanvasLayout : MonoBehaviour
{
	[SerializeField]
	private bool captured;

	[SerializeField]
	private Vector2 anchorMin;

	[SerializeField]
	private Vector2 anchorMax;

	[SerializeField]
	private Vector2 anchoredPosition;

	[SerializeField]
	private Vector2 sizeDelta;

	[SerializeField]
	private Vector2 pivot;

	[SerializeField]
	private Vector3 localScale;

	internal void Capture(RectTransform template)
	{
		captured = true;
		anchorMin = template.anchorMin;
		anchorMax = template.anchorMax;
		anchoredPosition = template.anchoredPosition;
		sizeDelta = template.sizeDelta;
		pivot = template.pivot;
		localScale = template.localScale;
	}

	private void OnEnable()
	{
		Restore();
	}

	private void OnTransformParentChanged()
	{
		Restore();
	}

	private void OnCanvasHierarchyChanged()
	{
		Restore();
	}

	private void Restore()
	{
		Transform parent = transform.parent;
		if (!captured || parent == null || parent.GetComponentInParent<Canvas>() == null)
		{
			return;
		}
		RectTransform rect = (RectTransform)transform;
		rect.anchorMin = anchorMin;
		rect.anchorMax = anchorMax;
		rect.pivot = pivot;
		rect.sizeDelta = sizeDelta;
		rect.anchoredPosition = anchoredPosition;
		rect.localScale = localScale;
	}
}

#endif

// The Unity 6 reconstruction (editor) and the port's player: the game's main scene has two EventSystems (one under
// each root canvas). Unity 6's editor warns about it every frame with a stack trace (log and console grow, a few
// tenths of a ms a frame); in both, the second one still gets its Update every frame (it only returns early because
// it is not the current one). The one enabled last steps aside; the first stays the current one, as before.
[Feature]
[HarmonyPatch(typeof(EventSystem), "OnEnable")]
internal static class DuplicateEventSystemFix
{
	// Those already enabled (the plugin reloaded in a game).
	private static void Enable()
	{
		foreach (EventSystem system in Object.FindObjectsOfType<EventSystem>())
		{
			if (system.isActiveAndEnabled && EventSystem.current != system)
			{
				Postfix(system);
			}
		}
	}

	private static void Postfix(EventSystem __instance)
	{
		foreach (EventSystem other in Object.FindObjectsOfType<EventSystem>())
		{
			if (other != __instance && other.isActiveAndEnabled && other.gameObject.scene == __instance.gameObject.scene)
			{
				Plugin.Log.LogInfo("EventSystem " + __instance.name + " under " + (__instance.transform.parent != null ? __instance.transform.parent.name : "-") + " disabled: " + other.transform.parent?.name + " has one");
				__instance.enabled = false;
				return;
			}
		}
	}
}
#endif
