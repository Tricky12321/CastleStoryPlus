using System.Collections;
using System.Collections.Generic;
using Brix.Components;
using Brix.Engine;
using Brix.Engine.Blocks;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using Brix.Game.Network;
using Brix.Game.Semantique;
using Brix.Pathfinding.Test;
using CastleStoryPlus.DevTools.Building;
using CastleStoryPlus.DevTools.Pathing;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.Pathfinding;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// Stone roads (Roads): a road tile costs one stone block and becomes a block that does not fill its voxel; a bricktron
// walks a road faster than plain ground over the same distance; and a route takes a road that is longer than the
// straight line when the road is quicker overall; a road can be laid on every free cell right beside a building.
[ParallelTests]
internal static class RoadTests
{
	private static readonly Factory.AssetKey RoadBlueprint = new Factory.AssetKey("Blueprints", Roads.Name);

	// A workshop that claims the spot in front of it where its worker stands (WorkSpotReserve).
	private static readonly Factory.AssetKey ForgeBlueprint = new Factory.AssetKey("Blueprints", "Forge");

	private static BlockEngine Engine => BrixSingleton<BlockEngine>.Instance;

	// Road blocks at the voxels; the test's clean-up removes them again.
	private static void PlaceRoad(TestContext t, List<XYZ> voxels)
	{
		BlockInfo road = BlockInfo.GetFromID(Roads.Id);
		t.NotNull(road, "The road's block info (is the Roads feature on?)");
		foreach (XYZ voxel in voxels)
		{
			Engine.CreateBlock(voxel, Quaternion.identity, road);
		}
		List<XYZ> placed = new List<XYZ>(voxels);
		t.Cleanup(() => Remove(placed));
		Placed[t] = placed;
	}

	private static readonly Dictionary<TestContext, List<XYZ>> Placed = new Dictionary<TestContext, List<XYZ>>();

	private static void Remove(List<XYZ> voxels)
	{
		foreach (XYZ voxel in voxels)
		{
			IBlockData block = Engine.GetBlockData(voxel);
			if (block != null && Engine.IsBlock(voxel))
			{
				block.RootOrSelf.DeBlock();
			}
		}
		voxels.Clear();
	}

	// Takes the test's roads away and waits until the route graph (and the navigation mesh) has taken that in, so the
	// next test on the same ground starts from plain ground. The clean-up does the same, but cannot wait.
	private static IEnumerator RemoveRoads(TestContext t)
	{
		if (Placed.TryGetValue(t, out List<XYZ> voxels))
		{
			Remove(voxels);
			Placed.Remove(t);
		}
		yield return TestWorld.WaitForGraph(t);
#if UNITY6_PLAYER
		yield return t.Until(() => !NavMeshExperiment.Busy, 10f, "the navigation mesh to take in the removed roads");
#endif
		yield return t.Frames(2);
	}

	private static List<XYZ> Row(XYZ from, int length)
	{
		List<XYZ> row = new List<XYZ>();
		for (int i = 0; i < length; i++)
		{
			row.Add(new XYZ(from.x + i, from.y, from.z));
		}
		return row;
	}

	// Orders the builder to a voxel and waits until it stands there; returns the game time it took.
	private static IEnumerator Walk(TestContext t, Labor labor, XYZ goal, List<Vector3> track, float[] took, List<float> stamps = null)
	{
		labor.AssignToIdle();
		Order.Package.OfLocation(Order.Move.RunToward, Location.Of(goal), Labor.DecisionSource.User).Send(labor);
		float start = Time.time;
		float end = Time.realtimeSinceStartup + 60f;
		int still = 0;
		while (still < 5)
		{
			if (labor == null)
			{
				t.Fail("The builder is gone");
			}
			if (Time.realtimeSinceStartup > end)
			{
				t.Fail("Waited 60 s for " + labor.name + " to reach " + goal + "; it stands at " + labor.transform.position);
			}
			track?.Add(labor.transform.position);
			stamps?.Add(Time.time);
			still = (Units.VoxelOf(labor) == goal && labor.navigation.Locomotion.InPlace) ? still + 1 : 0;
			yield return null;
		}
		if (took != null)
		{
			took[0] = Time.time - start;
		}
	}

	// Blocks per second between the first quarter and the last quarter of the walk's distance.
	private static float MiddleSpeed(List<Vector3> track, List<float> stamps)
	{
		if (track.Count < 8)
		{
			return 0f;
		}
		Vector3 first = track[0];
		Vector3 last = track[track.Count - 1];
		float total = Vector3.Distance(first, last);
		int from = 0;
		int to = track.Count - 1;
		for (int i = 0; i < track.Count; i++)
		{
			float done = Vector3.Distance(first, track[i]);
			if (done < total * 0.25f)
			{
				from = i;
			}
			if (done <= total * 0.75f)
			{
				to = i;
			}
		}
		float seconds = stamps[to] - stamps[from];
		return (seconds > 0.01f) ? Vector3.Distance(track[from], track[to]) / seconds : 0f;
	}

	[GameTest("Roads", "A road tile costs one stone block")]
	private static IEnumerator RoadCostsOneStone(TestContext t)
	{
		GameObject template = Factory.Peek(RoadBlueprint);
		t.NotNull(template, "The road blueprint (is Roads, with CustomBlocks, on?)");
		Recepteur recepteur = template.GetComponent<Recepteur>();
		t.NotNull(recepteur, "The road blueprint's receiver");
		int stone = 0;
		int kinds = 0;
		foreach (KeyValuePair<System.Type, Adjectif> entry in recepteur._baseCapacity.DicoAdjectif)
		{
			if (entry.Value.quantifiable == null)
			{
				continue;
			}
			kinds++;
			if (entry.Key == Adjectif.stoneBlock.GetType())
			{
				stone = (int)entry.Value.quantifiable.valeur;
			}
		}
		t.Record("kinds", kinds);
		t.Equal(1, stone, "Stone blocks one road tile costs");
		t.Equal(1, kinds, "Kinds of resource one road tile costs");
		yield break;
	}

	[GameTest("Roads", "A road blueprint finished becomes a road block that is walkable ground, not a wall")]
	private static IEnumerator FinishedRoadIsWalkableBlock(TestContext t)
	{
		XYZ site = Scenario.Site(t, 3, 3, 4);
		BuildProject project = Scenario.BuildProject(t, site);
		BlueprintPlacer.Result placed = BlueprintPlacer.Place(RoadBlueprint, site, 0, project, force: false);
		t.True(placed.Placed, "The blueprint: " + placed.Outcome + " " + placed.Detail);
		t.Cleanup(() =>
		{
			IBlockData block = Engine.GetBlockData(site);
			if (block != null && Engine.IsBlock(site))
			{
				block.RootOrSelf.DeBlock();
			}
		});
		InstantBuild.Complete(new[] { placed.Blueprint });
		yield return t.Until(() => Engine.GetBlockID(site) == Roads.Id, 5f, "the road to be built");
		yield return t.Frames(3);
		t.True(Roads.IsRoad(site), "The road is not in the road set");
		t.False(Voxel.IsFull(site), "The road fills its voxel");
		t.True(Voxel.IsOpenAndWalkable(site), "A unit cannot stand in the road's voxel");
		t.Equal(0, Voxel.GetNiveaux(site), "The road's floor (niveaux)");
		Placed[t] = new List<XYZ> { site };
		yield return RemoveRoads(t);
	}

	[GameTest("Roads", "A unit's walking and running speed is raised by the road bonus on a road and normal beside it")]
	private static IEnumerator RoadSpeedIsRaised(TestContext t)
	{
		XYZ center = Scenario.Site(t, 9, 5, 3);
		XYZ onRoad = new XYZ(center.x, center.y, center.z);
		XYZ plain = new XYZ(center.x + 2, center.y, center.z);
		PlaceRoad(t, new List<XYZ> { onRoad });
		Labor labor = Scenario.Bricktron(t, Occupation.Type.Builder, onRoad);
		yield return t.Frames(5);
		yield return TestWorld.WaitForGraph(t);
		var run = typeof(Brix.Game.Locomotion.Running).GetProperty("SpeedMperS", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
		var walk = typeof(Brix.Game.Locomotion.Walking).GetProperty("SpeedMperS", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
		Brix.Game.Locomotion.Movement movement = labor.navigation.Locomotion.Movement;
		t.True(Roads.IsOn(labor.transform), "The builder does not stand on the road: " + labor.transform.position);
		float roadRun = (float)run.GetValue(movement.Running, null);
		float roadWalk = (float)walk.GetValue(movement.Walking, null);
		yield return Walk(t, labor, plain, null, null);
		t.False(Roads.IsOn(labor.transform), "The builder still counts as on the road: " + labor.transform.position);
		float plainRun = (float)run.GetValue(movement.Running, null);
		float plainWalk = (float)walk.GetValue(movement.Walking, null);
		t.Record("run", roadRun + " / " + plainRun);
		t.Record("walk", roadWalk + " / " + plainWalk);
		t.AtLeast(0.01f, plainRun, "Running speed");
		t.AtMost(Roads.Factor + 0.001f, roadRun / plainRun, "Running speed on the road over plain");
		t.AtLeast(Roads.Factor - 0.001f, roadRun / plainRun, "Running speed on the road over plain");
		t.AtLeast(Roads.Factor - 0.001f, roadWalk / plainWalk, "Walking speed on the road over plain");
		t.AtMost(Roads.Factor + 0.001f, roadWalk / plainWalk, "Walking speed on the road over plain");
		yield return RemoveRoads(t);
	}

	[GameTest("Roads", "A bricktron walks a road faster than plain ground over the same distance", TimeoutSeconds = 150f)]
	private static IEnumerator WalksFasterOnRoad(TestContext t)
	{
		XYZ center = Scenario.Site(t, 21, 11, 3);
		XYZ west = new XYZ(center.x - 8, center.y, center.z - 3);
		XYZ roadWest = new XYZ(center.x - 8, center.y, center.z + 3);
		const int length = 17;
		PlaceRoad(t, Row(roadWest, length));
		Labor labor = Scenario.Bricktron(t, Occupation.Type.Builder, west);
		yield return t.Frames(5);
		yield return TestWorld.WaitForGraph(t);
		float[] plain = new float[1];
		float[] road = new float[1];
		List<Vector3> plainTrack = new List<Vector3>();
		List<float> plainStamps = new List<float>();
		List<Vector3> roadTrack = new List<Vector3>();
		List<float> roadStamps = new List<float>();
		yield return Walk(t, labor, west, null, null);
		yield return Walk(t, labor, new XYZ(west.x + length - 1, west.y, west.z), plainTrack, plain, plainStamps);
		yield return Walk(t, labor, roadWest, null, null);
		yield return Walk(t, labor, new XYZ(roadWest.x + length - 1, roadWest.y, roadWest.z), roadTrack, road, roadStamps);
		// The speed in the middle of the walk, where neither the start nor the stop counts.
		float plainSpeed = MiddleSpeed(plainTrack, plainStamps);
		float roadSpeed = MiddleSpeed(roadTrack, roadStamps);
		float ratio = roadSpeed / Mathf.Max(0.01f, plainSpeed);
		int roadFrames = 0;
		foreach (Vector3 position in roadTrack)
		{
			roadFrames += Roads.IsRoad(Roads.VoxelAt(position)) ? 1 : 0;
		}
		t.Record("roadShare", System.Math.Round(roadFrames / (float)Mathf.Max(1, roadTrack.Count), 2));
		t.Record("roadZ", roadTrack.Count > 0 ? roadTrack[roadTrack.Count / 2].ToString() : "-");
		t.Record("plainSeconds", System.Math.Round(plain[0], 2));
		t.Record("roadSeconds", System.Math.Round(road[0], 2));
		t.Record("plainSpeed", System.Math.Round(plainSpeed, 2));
		t.Record("roadSpeed", System.Math.Round(roadSpeed, 2));
		t.Record("ratio", System.Math.Round(ratio, 2));
		t.Record("factor", System.Math.Round(Roads.Factor, 2));
		if (Roads.Factor <= 1.01f)
		{
			t.Skip("Roads are no faster (SpeedBonus 0)");
		}
		// A frame moves the game by at most 0.25 s, which at 10x caps how far a unit gets towards its next point in a
		// frame: the full bonus shows in RoadSpeedIsRaised, here only that the road is clearly quicker.
		t.AtLeast(1f + (Roads.Factor - 1f) * 0.4f, ratio, "Speed on the road over the speed on plain ground");
		yield return RemoveRoads(t);
	}

#if UNITY6_PLAYER
	// What the navigation mesh makes of the route: its corners and length.
	private static void Diagnose(TestContext t, XYZ a, XYZ b)
	{
		TestPathfinding graph = NavMeshExperiment.Graph;
		UnityEngine.AI.NavMeshQueryFilter filter = NavMeshExperiment.Filter(true, false, Scenario.Player);
		if (graph == null || !graph.TerrainNodes.TryGetValue(a, out TerrainPathNode from) || !graph.TerrainNodes.TryGetValue(b, out TerrainPathNode to))
		{
			t.Log("No graph nodes at the ends");
			return;
		}
		bool onA = UnityEngine.AI.NavMesh.SamplePosition(NavMeshExperiment.TopCentre(from), out UnityEngine.AI.NavMeshHit start, 1f, filter);
		bool onB = UnityEngine.AI.NavMesh.SamplePosition(NavMeshExperiment.TopCentre(to), out UnityEngine.AI.NavMeshHit end, 1f, filter);
		UnityEngine.AI.NavMeshPath path = new UnityEngine.AI.NavMeshPath();
		bool found = onA && onB && UnityEngine.AI.NavMesh.CalculatePath(start.position, end.position, filter, path);
		float length = 0f;
		string corners = string.Empty;
		if (found)
		{
			for (int i = 0; i < path.corners.Length; i++)
			{
				length += (i > 0) ? Vector3.Distance(path.corners[i - 1], path.corners[i]) : 0f;
				corners += path.corners[i].x.ToString("0.0") + "," + path.corners[i].z.ToString("0.0") + " ";
			}
		}
		t.Record("meshRoute", found ? (length.ToString("0.0") + " long, corners " + corners) : "none");
	}
#endif

#if UNITY6_PLAYER
	[GameTest("Roads", "Routes beside and across roads stay cheap to make (time in the route mender)", TimeoutSeconds = 200f, Exclusive = true)]
	private static IEnumerator RoadRoutesStayCheap(TestContext t)
	{
		XYZ center = Scenario.Site(t, 31, 15, 3);
		XYZ a = new XYZ(center.x - 12, center.y, center.z);
		XYZ b = new XYZ(center.x + 12, center.y, center.z);
		// A road beside the line, as long as it, and one across it.
		List<XYZ> road = Row(new XYZ(a.x, a.y, a.z + 1), 25);
		for (int z = -5; z <= 5; z++)
		{
			XYZ cross = new XYZ(center.x, center.y, center.z + z);
			if (!road.Contains(cross))
			{
				road.Add(cross);
			}
		}
		PlaceRoad(t, road);
		Labor labor = Scenario.Bricktron(t, Occupation.Type.Builder, a);
		yield return t.Frames(5);
		yield return TestWorld.WaitForGraph(t);
		yield return t.Until(() => !NavMeshExperiment.Busy, 10f, "the navigation mesh to take in the roads");
		yield return Walk(t, labor, a, null, null);
		NavMeshRoutes.ResetStats();
		for (int i = 0; i < 6; i++)
		{
			yield return Walk(t, labor, b, null, null);
			yield return Walk(t, labor, a, null, null);
		}
		double mean = NavMeshRoutes.EaseMilliseconds / System.Math.Max(1, NavMeshRoutes.EaseRoutes);
		t.Record("routes", NavMeshRoutes.EaseRoutes);
		t.Record("easeMsTotal", System.Math.Round(NavMeshRoutes.EaseMilliseconds, 2));
		t.Record("easeMsPerRoute", System.Math.Round(mean, 3));
		t.Record("roadLooks", NavMeshRoutes.RoadEases);
		t.AtMost(3f, (float)mean, "Milliseconds the route mender takes per route");
		yield return RemoveRoads(t);
	}
#endif

	[GameTest("Roads", "The cell bits the road's placement rule (RoadRules.MayLieOn) looks at are the game's")]
	private static IEnumerator RoadRuleBitsAreTheGames(TestContext t)
	{
		t.Equal((long)VPropertyMask.access, RoadRules.AccessBit, "access");
		t.Equal((long)VPropertyMask.obstacle, RoadRules.ObstacleBit, "obstacle");
		t.Equal((long)VPropertyMask.occupy, RoadRules.OccupyBit, "occupy");
		t.Equal((long)VPropertyMask.walkable, RoadRules.WalkableBit, "walkable");
		t.Equal((long)VPropertyMask.operation, RoadRules.OperationBit, "operation");
		t.Equal((long)VPropertyMask.fantome, RoadRules.FantomeBit, "fantome");
		t.Equal((long)VPropertyMask.debloc, RoadRules.DeblocBit, "debloc");
		yield break;
	}

	// The cells right beside a volume's body (its bottom layer, cells of bodyMask), on all four sides.
	private static List<XYZ> Beside(IVolume volume, VPropertyMask bodyMask, out List<XYZ> body)
	{
		body = new List<XYZ>();
		int bottom = int.MaxValue;
		foreach (XYZ cell in volume.GetCases(bodyMask))
		{
			body.Add(cell);
			bottom = Mathf.Min(bottom, cell.y);
		}
		body.RemoveAll(cell => cell.y != bottom);
		HashSet<XYZ> inside = new HashSet<XYZ>(body);
		List<XYZ> ring = new List<XYZ>();
		foreach (XYZ cell in body)
		{
			foreach (XYZ side in new XYZ[4] { new XYZ(cell.x + 1, cell.y, cell.z), new XYZ(cell.x - 1, cell.y, cell.z), new XYZ(cell.x, cell.y, cell.z + 1), new XYZ(cell.x, cell.y, cell.z - 1) })
			{
				if (!inside.Contains(side) && !ring.Contains(side))
				{
					ring.Add(side);
				}
			}
		}
		return ring;
	}

	// A road fitted on every cell beside the building is in the way of nothing, the building's work spot (which it
	// claims) included; one on the building's body is in the way.
	private static void RoadsFitBeside(TestContext t, IVolume building, VPropertyMask bodyMask, string what, bool mustClaim)
	{
		List<XYZ> ring = Beside(building, bodyMask, out List<XYZ> body);
		t.True(body.Count > 0, "The " + what + " has no body cells");
		int claimed = 0;
		foreach (XYZ cell in ring)
		{
			if (building.HasAnyPropertiesAt(VPropertyMask.occupy, cell))
			{
				claimed++;
			}
			BlueprintPlacer.Fit fit = BlueprintPlacer.Try(RoadBlueprint, cell, 0);
			t.NotNull(fit, "The road fitted at " + cell);
			t.True(fit.HasCells && fit.Min.Equals(cell) && fit.Max.Equals(cell), "The road fitted at " + cell + " lies at " + fit.Min + " to " + fit.Max);
			t.True((fit.Check & ValidationResult.Obstructed) == 0, "A road right beside the " + what + " at " + cell + " is in the way of something: " + fit.Check);
		}
		t.Record(what + " beside", ring.Count);
		t.Record(what + " claimed", claimed);
		// Without a claimed work spot beside it the test would not look at what used to be refused. The built forge always
		// claims its worker's spot; its blueprint only with WorkSpotReserve.
		if (mustClaim)
		{
			t.True(claimed > 0, "The " + what + " claims no cell beside it");
		}
		BlueprintPlacer.Fit inside = BlueprintPlacer.Try(RoadBlueprint, body[0], 0);
		t.NotNull(inside, "The road fitted on the " + what);
		t.True((inside.Check & ValidationResult.Obstructed) != 0, "A road on the " + what + "'s body at " + body[0] + " is not in the way: " + inside.Check);
	}

	// The built forge whose body takes the cell.
	private static IVolume BuiltForgeAt(XYZ cell)
	{
		foreach (IVolume volume in VolumeRegistry.VolumesWithPropertyAt(cell, VPropertyMask.obstacle))
		{
			if (!(volume is BlueprintVolume) && volume.AssetKey != null && volume.AssetKey.Name == "Forge")
			{
				return volume;
			}
		}
		return null;
	}

	private static void DestroyAfter(TestContext t, Blueprint blueprint)
	{
		t.Cleanup(() =>
		{
			if (blueprint != null && !blueprint.Released)
			{
				User.LocalUser.GetComponent<UNetBlueprint>().CmdDestroyBluePrint(blueprint.gameObject);
			}
		});
	}

	[GameTest("Roads", "A road can be laid on every free cell right beside a building (a forge, planned and built), its worker's spot in front included, but not on the building")]
	private static IEnumerator RoadBesideBuilding(TestContext t)
	{
		XYZ site = Scenario.Site(t, 7, 7, 4);
		BuildProject project = Scenario.BuildProject(t, site);
		BlueprintPlacer.Result forge = BlueprintPlacer.Place(ForgeBlueprint, site, 0, project, force: false);
		t.True(forge.Placed, "The forge blueprint: " + forge.Outcome + " " + forge.Detail);
		Blueprint planned = forge.Blueprint;
		DestroyAfter(t, planned);
		yield return t.Frames(3);
		IVolume plannedVolume = planned.GetComponent<IVolume>();
		t.NotNull(plannedVolume, "The forge blueprint's volume");
		RoadsFitBeside(t, plannedVolume, VPropertyMask.fantome, "forge blueprint", false);
		Beside(plannedVolume, VPropertyMask.fantome, out List<XYZ> plannedBody);
		XYZ bodyCell = plannedBody[0];
		InstantBuild.Complete(new[] { planned });
		IVolume built = null;
		yield return t.Until(() => (built = BuiltForgeAt(bodyCell)) != null, 10f, "the forge to be built");
		GameObject building = built.GameObject;
		t.Cleanup(() => Scenario.Release(building));
		yield return t.Frames(3);
		RoadsFitBeside(t, built, VPropertyMask.obstacle, "forge", true);
		// And placed for real: the host checks again when it spawns them.
		foreach (XYZ cell in Beside(built, VPropertyMask.obstacle, out List<XYZ> _))
		{
			BlueprintPlacer.Result road = BlueprintPlacer.Place(RoadBlueprint, cell, 0, project, force: false);
			t.True(road.Placed, "The road beside the forge at " + cell + ": " + road.Outcome + " " + road.Detail);
			DestroyAfter(t, road.Blueprint);
		}
	}

	[GameTest("Roads", "A route takes a road that is longer than the straight line when the road is quicker overall", TimeoutSeconds = 150f)]
	private static IEnumerator RoutePrefersLongerRoad(TestContext t)
	{
		XYZ center = Scenario.Site(t, 21, 11, 3);
		XYZ a = new XYZ(center.x - 8, center.y, center.z);
		XYZ b = new XYZ(center.x + 8, center.y, center.z);
		// A road one row beside the straight line, as long as the line: 16 plain steps cost 16, the road 1 step onto it,
		// 16 along it and 1 off, which is quicker from a bonus of about 20%.
		PlaceRoad(t, Row(new XYZ(a.x, a.y, a.z + 1), 17));
		Labor labor = Scenario.Bricktron(t, Occupation.Type.Builder, a);
		yield return t.Frames(5);
		yield return TestWorld.WaitForGraph(t);
		yield return Walk(t, labor, a, null, null);
#if UNITY6_PLAYER
		yield return t.Until(() => !NavMeshExperiment.Busy, 10f, "the navigation mesh to take in the road");
		yield return t.Frames(2);
		Diagnose(t, a, b);
#endif
		if (Roads.Factor < 1.3f)
		{
			t.Skip("The road bonus is too small to prefer the longer way");
		}
		List<Vector3> track = new List<Vector3>();
		yield return Walk(t, labor, b, track, null);
		int onRoad = 0;
		foreach (Vector3 position in track)
		{
			if (Roads.IsRoad(Roads.VoxelAt(position)))
			{
				onRoad++;
			}
		}
		float share = onRoad / (float)Mathf.Max(1, track.Count);
		t.Record("samples", track.Count);
		t.Record("onRoad", System.Math.Round(share, 2));
		t.AtLeast(0.4f, share, "Share of the walk spent on the road");
		yield return RemoveRoads(t);
	}
}
