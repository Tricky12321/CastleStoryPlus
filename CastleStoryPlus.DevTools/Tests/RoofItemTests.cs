using System.Collections;
using Brix.Engine;
using CastleStoryPlus.Building;
using CastleStoryPlus.Core;
using CastleStoryPlus.DevTools.Testing;
using UnityEngine;
using FactoryBlocks = Brix.External.Factories.Blocks;

namespace CastleStoryPlus.DevTools.Tests;

// A loose item that lies still on the roof of a mod building (#128) is put on open ground beside it.
internal static class RoofItemTests
{
	[GameTest("Buildings", "A log lying on the roof of a mod building is moved to open ground next to it, off the roof", TimeoutSeconds = 90f)]
	private static IEnumerator LogOnRoofMovedToGround(TestContext t)
	{
		if (!Plugin.IsEnabled(Features.RoofItems) || !Plugin.IsEnabled(Features.Warehouse))
		{
			t.Skip("RoofItems or Warehouse is switched off");
		}
		XYZ site = Scenario.Site(t, 14, 10);
		GameObject building = null;
		yield return Scenario.Building(t, Warehouse.Building.BlueprintKey, site, Scenario.BuildProject(t, site), go => building = go);
		t.NotNull(building, "The warehouse built for the test");
		BoxCollider box = building.GetComponent<BoxCollider>();
		t.NotNull(box, "The warehouse's collider");
		Bounds bounds = box.bounds;
		Vector3 roof = new Vector3(bounds.center.x, bounds.max.y + 0.4f, bounds.center.z);
		GameObject log = Scenario.Items(t, FactoryBlocks.Wood_DoubleLog_AA, 1, roof)[0];
		// Held where it is, as if it had come to rest there.
		Rigidbody body = log.GetComponent<Rigidbody>();
		t.NotNull(body, "The log's body");
		body.useGravity = false;
		body.velocity = Vector3.zero;
		body.angularVelocity = Vector3.zero;
		log.transform.position = roof;
		yield return t.Frames(3);
		t.True(RoofItems.ItemsOnRoof(building).Contains(log), "The log is not seen on the roof");
		float top = bounds.max.y;
		yield return t.Until(() => RoofItems.Moved > 0 && log.transform.position.y < top - 1f, 30f, "the log to be moved off the roof");
		body.useGravity = true;
		Vector3 p = log.transform.position;
		t.Record("movedTo", p.ToString());
		t.False(RoofItems.ItemsOnRoof(building).Contains(log), "The log is still on the roof");
		bool outside = p.x < bounds.min.x - 0.2f || p.x > bounds.max.x + 0.2f || p.z < bounds.min.z - 0.2f || p.z > bounds.max.z + 0.2f;
		t.True(outside, "The log was put " + p + ", not beside the building " + bounds);
	}
}
