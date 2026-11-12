using System.Collections.Generic;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Semantique;
using Brix.Lifecycle.Pooling;
using Brix.NewUI.OverheadDisplay;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Performance;

// Less CPU time per frame for the same game; measured in a base with 17 bricktrons the frame was 14 ms, of which
// 2 ms was drawing and the rest the game's own work on one thread.
// - The icons over bricktrons, items and buildings (OverheadDisplay, over a hundred of them) are hidden by making
//   them see-through, so Unity still rebuilt and batched every hidden one's canvas each frame. A hidden one's canvas
//   is now switched off, and on again when it shows.
// - An icon sorted by its distance to the camera set its canvas's sorting order every frame, also to the same number
//   and also while hidden, and a canvas whose order is set is sorted and batched again; it is now set only when it
//   changed, and not while the icon's canvas is off.
// - A storage's shelf display (the blocks drawn on a stockpile, RecepteurTracksDisplay) worked out its count and set
//   every shelf object visible or not each frame; it now touches them only when a count changed (on a weapon
//   stand, kit bundles count for the rows of their pieces).
// - A worker's search for a resource to fetch (Knowledge.ClosestObjectToPickup, about half a millisecond each) was
//   made again on every decision, also when it found nothing a moment ago: every idle worker asked over and over for
//   what no one can get (a ward's crystal). A search that found nothing is not made again by that worker for that
//   resource for MissSeconds; one that finds something is never held back.
// - The bricktrons' and enemies' animators that animate always (also off the screen) cull their bones off the
//   screen (CullUpdateTransforms): their state machines and animation events still run, so what they do is the
//   same; only the pose of one nobody sees is not worked out. Looked for every AnimatorSeconds.
// - The animators of decoration (stockpile planks and logs, beacon handles, furnace fires) stop off the screen
//   (CullCompletely). Looked for every DecorAnimatorSeconds.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
internal static class FrameSavers
{
	internal const float MissSeconds = 2f;

	internal const float AnimatorSeconds = 2f;

	internal const float DecorAnimatorSeconds = 10f;

	// Per worker and resource, until when a search found nothing.
	internal static readonly Dictionary<Labor, Dictionary<Ressource, float>> Misses = new Dictionary<Labor, Dictionary<Ressource, float>>();

	// Per shelf display, the count each of its tracks shows.
	internal static readonly Dictionary<RecepteurTracksDisplay, int[]> Shown = new Dictionary<RecepteurTracksDisplay, int[]>();

	private static float _nextCleanup;

	private static void Enable()
	{
		GameSession.OnLeave(() =>
		{
			Misses.Clear();
			Shown.Clear();
			DecorSeen.Clear();
			IdleUnitMotion.StillUnits.Clear();
			KnowledgeRegionsSpread.Clear();
			FrameSaversOverheadPatch.RestoreAll();
		});
		Plugin.Root.AddComponent<FrameSaversDriver>();
	}

	// Decoration animators already set to cull completely (or looked at and left as they are).
	internal static readonly HashSet<Animator> DecorSeen = new HashSet<Animator>();

	// The animators of things that only show something (the planks and logs on a stockpile, a beacon's handle, a
	// furnace's fire, a ward's lantern; some 600 in a big base) animated every frame, also off the screen. They now
	// stop while none of their renderers is on the screen and go on where they were when one is. Units keep animating
	// (the game waits on their animation states); the overhead icons have no renderer to show them and are left too.
	// The stockpiles' idle overhead animators are switched off (see Clipless).
	internal static void CullDecorAnimators()
	{
		foreach (Animator animator in Object.FindObjectsOfType<Animator>())
		{
			if (animator == null || !DecorSeen.Add(animator) || animator.cullingMode != AnimatorCullingMode.AlwaysAnimate)
			{
				continue;
			}
			if (animator.GetComponentInParent<Labor>() != null)
			{
				continue;
			}
			if (Clipless(animator))
			{
				animator.enabled = false;
				continue;
			}
			if (animator.GetComponentInParent<Canvas>() != null || animator.GetComponentInChildren<Renderer>() == null)
			{
				continue;
			}
			animator.cullingMode = AnimatorCullingMode.CullCompletely;
		}
	}

	// Each stockpile's overhead icon (some 300 in a base) has an animator with Show and Hide triggers that nothing in
	// the game sets: it sits in a state without a clip, moves nothing, and Unity still updated it every frame.
	private static bool Clipless(Animator animator)
	{
		RuntimeAnimatorController controller = animator.runtimeAnimatorController;
		return controller != null && controller.name == "OverheadAnimationController" && animator.isInitialized
			&& !animator.IsInTransition(0) && animator.GetCurrentAnimatorClipInfoCount(0) == 0;
	}

	// Units' animators that always animate cull their bones off the screen.
	internal static void CullAnimators()
	{
		foreach (Labor labor in Live<Labor>.Active())
		{
			if (labor == null)
			{
				continue;
			}
			Animator animator = labor.navigation != null && labor.navigation.Locomotion != null ? labor.navigation.Locomotion.animator : null;
			if (animator != null && animator.cullingMode == AnimatorCullingMode.AlwaysAnimate)
			{
				animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
			}
		}
	}

	// Drops entries of destroyed workers and displays now and then.
	internal static void CleanupNow()
	{
		if (Time.time < _nextCleanup)
		{
			return;
		}
		_nextCleanup = Time.time + 30f;
		List<Labor> labors = null;
		foreach (Labor labor in Misses.Keys)
		{
			if (labor == null)
			{
				(labors ?? (labors = new List<Labor>())).Add(labor);
			}
		}
		if (labors != null)
		{
			foreach (Labor labor in labors)
			{
				Misses.Remove(labor);
			}
		}
		List<RecepteurTracksDisplay> displays = null;
		foreach (RecepteurTracksDisplay display in Shown.Keys)
		{
			if (display == null)
			{
				(displays ?? (displays = new List<RecepteurTracksDisplay>())).Add(display);
			}
		}
		if (displays != null)
		{
			foreach (RecepteurTracksDisplay display in displays)
			{
				Shown.Remove(display);
			}
		}
	}
}

// A hidden overhead icon's canvases are switched off (they lie inside the part it hides), on again when it shows.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(OverheadDisplay), "Show")]
internal static class FrameSaversOverheadPatch
{
	// The canvases inside hidden icons this switched off. Kept per canvas, not per icon: the lines in an icon (name
	// tag, big icon, ...) and the icons themselves are pooled, and one given back to the pool while hidden kept its
	// canvases off. The next icon to take it from the pool showed it without them (a task's beacon pole without its
	// icon, mostly after loading a save, whose objects all came from the pool again).
	internal static readonly HashSet<Canvas> Off = new HashSet<Canvas>();

	// The icons that switched off canvases inside them since they last showed (the rest have nothing to switch on).
	private static readonly HashSet<OverheadDisplay> Dimmed = new HashSet<OverheadDisplay>();

	private static readonly List<Canvas> Found = new List<Canvas>();

	private static void Postfix(OverheadDisplay __instance, bool value)
	{
		Switch(__instance.MainCanvas, __instance, value);
		Switch(__instance.ArrowCanvas, __instance, value);
		SwitchNested(__instance, value);
		SwitchContent(__instance, value);
	}

	// Every canvas under root this switched off is switched on again.
	internal static void Restore(Transform root)
	{
		if (Off.Count == 0 || root == null)
		{
			return;
		}
		Found.Clear();
		root.GetComponentsInChildren(true, Found);
		foreach (Canvas canvas in Found)
		{
			if (Off.Remove(canvas))
			{
				canvas.enabled = true;
			}
		}
		Found.Clear();
	}

	internal static void Released(OverheadDisplay display)
	{
		if (display != null)
		{
			Dimmed.Remove(display);
		}
	}

	// Leaving a game: everything still there gets its canvases back (pooled icons and lines outlive the game).
	internal static void RestoreAll()
	{
		foreach (Canvas canvas in Off)
		{
			if (canvas != null)
			{
				canvas.enabled = true;
			}
		}
		Off.Clear();
		Dimmed.Clear();
	}

	// The hidden content object itself is switched off (Unity 6 port): see-through, it still cost every frame. Its
	// graphics stayed registered for the screen's raycast (300 overhead icons' worth gone over on every mouse move),
	// its lines' animators (name tag, thought bubble) and Updates (ItemCounts) ran, and its rect transforms were gone
	// over by the canvas. Switched on again when the icon shows; the game's own code tolerates inactive lines (a
	// thought bubble checks activeInHierarchy before its animator).
	private static void SwitchContent(OverheadDisplay display, bool value)
	{
#if UNITY6
		Transform content = display.Content;
		if (content == null || content == display.transform)
		{
			return;
		}
		GameObject go = content.gameObject;
		if (go.activeSelf != value)
		{
			go.SetActive(value);
		}
#endif
	}

	// The canvases nested in a hidden icon (its content's, each line's arrow; three a stockpile icon, some 600 in a big
	// base) are switched off too: what they hold then belongs to the icon's own canvas, which draws nothing while the
	// content is see-through. They are switched on again when it shows.
	private static void SwitchNested(OverheadDisplay display, bool value)
	{
		if (value)
		{
			if (Dimmed.Remove(display))
			{
				Restore(display.transform);
			}
			return;
		}
		Found.Clear();
		display.GetComponentsInChildren(true, Found);
		foreach (Canvas canvas in Found)
		{
			if (canvas == display.MainCanvas || !canvas.enabled)
			{
				continue;
			}
			canvas.enabled = false;
			Off.Add(canvas);
			Dimmed.Add(display);
		}
		Found.Clear();
	}

	// Only a canvas that holds nothing but the hidden part: the content itself or inside it, or the canvas the
	// content is the only thing in.
	private static void Switch(Canvas canvas, OverheadDisplay display, bool value)
	{
		Transform content = display.Content;
		if (canvas == null || content == null || canvas.enabled == value)
		{
			return;
		}
		Transform t = canvas.transform;
		bool inside = t == content || t.IsChildOf(content);
		bool holdsOnlyContent = content.IsChildOf(t) && content.parent == t && t.childCount == 1;
		if (inside || holdsOnlyContent)
		{
			canvas.enabled = value;
		}
	}
}

// An icon or one of its lines given back to the pool gets the canvases back that FrameSaversOverheadPatch switched
// off while it was hidden; the pool hands it out again as it is.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(ObjectPoolSingleton), nameof(ObjectPoolSingleton.Release), typeof(FactoryImprint))]
internal static class FrameSaversOverheadReleasePatch
{
	private static void Prefix(FactoryImprint imprint)
	{
		if (FrameSaversOverheadPatch.Off.Count == 0 || imprint == null)
		{
			return;
		}
		GameObject go = imprint.gameObject;
		if (go.GetComponent<Overhead>() != null || go.GetComponent<OverheadDisplay>() != null)
		{
			FrameSaversOverheadPatch.Restore(go.transform);
			FrameSaversOverheadPatch.Released(go.GetComponent<OverheadDisplay>());
		}
	}
}

// An icon's sorting order (by its distance to the camera) is set only when it changed and its canvas is on.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(OverheadDisplay), "BrixUpdate")]
internal static class FrameSaversOverheadSortPatch
{
	private static bool Prefix(OverheadDisplay __instance)
	{
		Canvas canvas = __instance.MainCanvas;
		if (!__instance.SortBasedOnCamera || canvas == null || !canvas.enabled)
		{
			return false;
		}
		// The camera's position is read once per frame (OverheadDistance).
		Transform pivot = __instance.pivot;
		if (pivot == null || !OverheadDistance.CameraPosition(out Vector3 camera))
		{
			return false;
		}
		int order = -(int)Mathf.Abs(Vector3.Distance(pivot.position, camera));
		if (canvas.sortingOrder != order)
		{
			canvas.sortingOrder = order;
		}
		return false;
	}
}

// A shelf display touches its shelf objects only when a count changed.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(RecepteurTracksDisplay), "Update")]
internal static class FrameSaversShelfPatch
{
	private static bool Prefix(RecepteurTracksDisplay __instance)
	{
		Recepteur target = __instance.targetRecepteur;
		List<RecepteurTracksDisplay.DisplayTrack> tracks = __instance.DisplayTracks;
		if (target == null || tracks == null)
		{
			return false;
		}
		if (!FrameSavers.Shown.TryGetValue(__instance, out int[] shown) || shown.Length != tracks.Count)
		{
			shown = new int[tracks.Count];
			for (int i = 0; i < shown.Length; i++)
			{
				shown[i] = -1;
			}
			FrameSavers.Shown[__instance] = shown;
		}
		for (int i = 0; i < tracks.Count; i++)
		{
			RecepteurTracksDisplay.DisplayTrack track = tracks[i];
			if (track == null || !track.Valid)
			{
				continue;
			}
			int count = 0;
			foreach (Adjectif adjectif in track.adjs)
			{
				count += target.ContentDescription.Value(adjectif);
			}
			// A weapon stand's kit bundles light the rows of their pieces too (KitBundleRackDisplayPatch).
			count += Army.KitBundleRackDisplayPatch.BundlesFor(track.adjs, target);
			if (count == shown[i])
			{
				continue;
			}
			shown[i] = count;
			track.UpdateDisplay(target);
		}
		FrameSavers.CleanupNow();
		return false;
	}
}

// A reused (pooled) shelf display starts over.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(RecepteurTracksDisplay), nameof(RecepteurTracksDisplay.OnRequested))]
internal static class FrameSaversShelfResetPatch
{
	private static void Postfix(RecepteurTracksDisplay __instance)
	{
		FrameSavers.Shown.Remove(__instance);
	}
}

// A worker's resource search that found nothing is not made again by it for that resource for a moment.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(Labor), "FindOneResource")]
internal static class FrameSaversSearchPatch
{
	private static bool Prefix(Labor __instance, Ressource resource, ref GameObject __result)
	{
		if (resource == null || !FrameSavers.Misses.TryGetValue(__instance, out Dictionary<Ressource, float> misses) || !misses.TryGetValue(resource, out float until))
		{
			return true;
		}
		if (Time.time >= until)
		{
			misses.Remove(resource);
			return true;
		}
		__result = null;
		return false;
	}

	private static void Postfix(Labor __instance, Ressource resource, GameObject __result, bool __runOriginal)
	{
		if (!__runOriginal || resource == null)
		{
			return;
		}
		if (!FrameSavers.Misses.TryGetValue(__instance, out Dictionary<Ressource, float> misses))
		{
			if (__result != null)
			{
				return;
			}
			misses = new Dictionary<Ressource, float>();
			FrameSavers.Misses[__instance] = misses;
		}
		if (__result == null)
		{
			misses[resource] = Time.time + FrameSavers.MissSeconds;
		}
		else
		{
			misses.Remove(resource);
		}
	}
}

internal class FrameSaversDriver : MonoBehaviour
{
	private float _next;

	private float _nextDecor;

	private void FixedUpdate()
	{
		KnowledgeRegionsSpread.RunPending();
	}

	private void Update()
	{
		if (Time.unscaledTime < _next)
		{
			return;
		}
		_next = Time.unscaledTime + FrameSavers.AnimatorSeconds;
		FrameSavers.CullAnimators();
		if (Time.unscaledTime >= _nextDecor)
		{
			_nextDecor = Time.unscaledTime + FrameSavers.DecorAnimatorSeconds;
			FrameSavers.CullDecorAnimators();
		}
	}
}
