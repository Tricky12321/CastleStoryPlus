using UnityEngine;

namespace CastleStoryPlus.UI;

// The radius of a ring on the ground round a thing (a blueprint, a tree, a loose item): its renderers' extent across,
// without the rings already on it. Used by BuildPriority's and UnreachableTasks' rings.
internal static class RingRadius
{
	internal static float Of(GameObject go)
	{
		bool any = false;
		Bounds bounds = new Bounds(go.transform.position, Vector3.zero);
		foreach (Renderer renderer in go.GetComponentsInChildren<Renderer>())
		{
			if (renderer.GetComponentInParent<CircleHighlight>() != null)
			{
				continue;
			}
			if (any)
			{
				bounds.Encapsulate(renderer.bounds);
			}
			else
			{
				bounds = renderer.bounds;
				any = true;
			}
		}
		return any ? Mathf.Max(0.6f, Mathf.Max(bounds.extents.x, bounds.extents.z) + 0.3f) : 1f;
	}
}
