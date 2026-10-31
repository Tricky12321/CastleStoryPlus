using System.Collections.Generic;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Semantique;
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
			FrameSaversOverheadPatch.Off.Clear();
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
	// Per hidden icon, the canvases inside it this switched off.
	internal static readonly Dictionary<OverheadDisplay, List<Canvas>> Off = new Dictionary<OverheadDisplay, List<Canvas>>();

	private static void Postfix(OverheadDisplay __instance, bool value)
	{
		Switch(__instance.MainCanvas, __instance, value);
		Switch(__instance.ArrowCanvas, __instance, value);
		SwitchNested(__instance, value);
	}

	// The canvases nested in a hidden icon (its content's, each line's arrow; three a stockpile icon, some 600 in a big
	// base) are switched off too: what they hold then belongs to the icon's own canvas, which draws nothing while the
	// content is see-through. They are switched on again, as they were, when it shows.
	private static void SwitchNested(OverheadDisplay display, bool value)
	{
		if (value)
		{
			if (Off.TryGetValue(display, out List<Canvas> off))
			{
				foreach (Canvas canvas in off)
				{
					if (canvas != null)
					{
						canvas.enabled = true;
					}
				}
				Off.Remove(display);
			}
			return;
		}
		foreach (Canvas canvas in display.GetComponentsInChildren<Canvas>(true))
		{
			if (canvas == display.MainCanvas || !canvas.enabled)
			{
				continue;
			}
			if (!Off.TryGetValue(display, out List<Canvas> off))
			{
				off = new List<Canvas>();
				Off[display] = off;
			}
			canvas.enabled = false;
			off.Add(canvas);
		}
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
