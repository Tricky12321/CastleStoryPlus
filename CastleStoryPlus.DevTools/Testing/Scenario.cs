using System;
using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Engine.Nature;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.Damage;
using Brix.Game.Components;
using Brix.Game.Network;
using Brix.Game.Semantique;
using Brix.Lifecycle.Pooling;
using Brix.Utils;
using UnityEngine.Networking;
using Brix.Transactions;
using CastleStoryPlus.Diagnostics;
using CastleStoryPlus.DevTools.Building;
using CastleStoryPlus.DevTools.Endpoints;
using CastleStoryPlus.DevTools.Pathing;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CastleStoryPlus.DevTools.Testing;

// Setting up a game scene for a test in the running game, the way the player or the game itself would: the test's
// own bricktrons, enemies, items, projects and buildings, all taken away again by the test's clean-up, so tests do
// not depend on what the world already holds and leave it as they found it.
internal static class Scenario
{
	internal static Faction Player => User.LocalUser.faction;

	internal static UNetGroupe Groupe => User.LocalUser.GetComponent<UNetGroupe>();

	internal static UNetProjectCmd ProjectCommands => User.LocalUser.GetComponent<UNetProjectCmd>();

	internal static Faction EnemyFaction()
	{
		return Faction.FindByName("Corruptrons") ?? Faction.DefaultEnemy;
	}

	internal static XYZ Home()
	{
		return WorldEndpoints.HomeVoxel();
	}

	// A flat open area of width x depth (centred on the returned voxel, the one a unit stands in) with clear empty
	// voxels above every column and no nature on it, near the home crystal but out of the way of the bricktrons
	// standing round it (about away voxels from it). Read from the terrain itself, not from the route graph, which
	// a new world fills in as it goes. Skips the test when there is none. accept, when given, must also agree to the
	// area's centre.
	internal static XYZ Site(TestContext t, int width, int depth, int clear = 4, int away = 12, Func<XYZ, bool> accept = null)
	{
		XYZ home = Home();
		List<Vector3> blueprints = new List<Vector3>();
		foreach (Blueprint blueprint in Object.FindObjectsOfType<Blueprint>())
		{
			if (blueprint != null && !blueprint.Released)
			{
				blueprints.Add(blueprint.transform.position);
			}
		}
		for (int ring = away; ring <= away + 48; ring++)
		{
			for (int dx = -ring; dx <= ring; dx++)
			{
				for (int dz = -ring; dz <= ring; dz++)
				{
					if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != ring)
					{
						continue;
					}
					if (!Overlaps(home.x + dx, home.z + dz, width, depth) && !NearAny(blueprints, home.x + dx, home.z + dz, width, depth) && IsOpenFlat(home.x + dx, home.z + dz, width, depth, clear, out XYZ center) && (accept == null || accept(center)))
					{
						int[] area = new int[4] { center.x - width / 2 - 2, center.z - depth / 2 - 2, center.x + width / 2 + 2, center.z + depth / 2 + 2 };
						Used.Add(area);
						// Given back when the test leaves it clean; kept when it leaves something behind. The corpses
						// of units that died on it are taken away (they stand in the way of what the next test builds).
						t.Cleanup(() =>
						{
							RemoveCorpses(area);
							if (!Messy.Remove(t))
							{
								Used.Remove(area);
							}
						});
						t.Log("Site of " + width + " x " + depth + " at " + center);
						return center;
					}
				}
			}
		}
		// In a parallel run the others may hold the ground: the runner tries again when they are done.
		throw new TestNoRoom("No flat open area of " + width + " x " + depth + " near the home crystal");
		return XYZ.zero;
	}

	// The areas earlier tests of this game used: what they leave (corpses, stumps, dug ground) is not cleared, so
	// each test gets ground of its own.
	private const int Spacing = 10;

	// Tests that leave something on their ground: corpses, dug ground.
	private static readonly HashSet<TestContext> Messy = new HashSet<TestContext>();

	// The test's ground stays taken for the rest of the run (what it leaves would be in the next test's way).
	internal static void LeavesMess(TestContext t)
	{
		Messy.Add(t);
	}

	// Each as min x, min z, max x, max z (inclusive).
	private static readonly List<int[]> Used = new List<int[]>();

	private static void RemoveCorpses(int[] area)
	{
		foreach (Corpse corpse in Object.FindObjectsOfType<Corpse>())
		{
			Vector3 at = (corpse != null) ? corpse.transform.position : Vector3.zero;
			if (corpse != null && at.x >= area[0] - 4 && at.x <= area[2] + 4 && at.z >= area[1] - 4 && at.z <= area[3] + 4)
			{
				Release(corpse.gameObject);
			}
		}
	}

	private static bool Overlaps(int cx, int cz, int width, int depth)
	{
		// Apart from the other tests' ground by Spacing, so their units, fights and falling blocks stay out of reach.
		int minX = cx - width / 2 - Spacing;
		int minZ = cz - depth / 2 - Spacing;
		int maxX = cx + width / 2 + Spacing;
		int maxZ = cz + depth / 2 + Spacing;
		foreach (int[] used in Used)
		{
			if (minX <= used[2] && maxX >= used[0] && minZ <= used[3] && maxZ >= used[1])
			{
				return true;
			}
		}
		return false;
	}

	// A blueprint (not a voxel yet) within the area or 2 round it.
	private static bool NearAny(List<Vector3> points, int cx, int cz, int width, int depth)
	{
		foreach (Vector3 point in points)
		{
			if (Mathf.Abs(point.x - cx) <= width / 2 + 3 && Mathf.Abs(point.z - cz) <= depth / 2 + 3)
			{
				return true;
			}
		}
		return false;
	}

	private static bool IsOpenFlat(int cx, int cz, int width, int depth, int clear, out XYZ center)
	{
		center = XYZ.zero;
		int top = WorldEndpoints.GroundY(cx, cz);
		if (top == int.MinValue)
		{
			return false;
		}
		// One more round the edge, so walls and units placed on its border have room.
		for (int x = cx - width / 2 - 1; x <= cx + width / 2 + 1; x++)
		{
			for (int z = cz - depth / 2 - 1; z <= cz + depth / 2 + 1; z++)
			{
				if (WorldEndpoints.GroundY(x, z) != top || Voxel.IsInvincible(new XYZ(x, top, z)))
				{
					return false;
				}
				NatureData nature = NatureRegistry.NatureAt(x, z);
				if (nature != null && nature.IsValid && nature.Type != NatureType.plant)
				{
					return false;
				}
				for (int y = 1; y <= clear; y++)
				{
					if (!Voxel.IsOpen(new XYZ(x, top + y, z)))
					{
						return false;
					}
				}
			}
		}
		if (WorldEndpoints.TreesOver(cx - width / 2 - 2, cz - depth / 2 - 2, cx + width / 2 + 2, cz + depth / 2 + 2).Count > 0)
		{
			return false;
		}
		center = new XYZ(cx, top + 1, cz);
		return true;
	}

	// The voxel a unit stands in on the column: the one above the top solid voxel.
	internal static XYZ Ground(int x, int z)
	{
		int top = WorldEndpoints.GroundY(x, z);
		return new XYZ(x, (top == int.MinValue) ? int.MinValue : top + 1, z);
	}

	// ---- units

	// A unit of the faction, spawned as the game's own spawn command does (UNetProjectCmd.SpawnUnit), with the
	// job's gear. Removed by the clean-up when it is still there.
	internal static Labor Spawn(TestContext t, Factory.AssetKey key, Faction faction, Occupation.Type job, XYZ at, int level = 1)
	{
		at = Walkable(at);
		GameObject go = Transactor.SpawnUNet(key, faction, at.ToVector3(), Quaternion.identity);
		t.NotNull(go, "The spawned " + key.Name);
		go.GetComponent<Occupation>().SetOccupation(job, true);
		Profession profession = go.GetComponent<Profession>();
		if (profession != null && level > 1)
		{
			profession.CharacterLevel = level;
		}
		Labor labor = go.GetComponent<Labor>();
		t.NotNull(labor, "The spawned unit's Labor");
		if (CastleStoryPlus.Plugin.IsEnabled(CastleStoryPlus.Core.Features.Healtron))
		{
			// Exactly what the test asked for: no raid roll into a Healtron.
			CastleStoryPlus.Combat.Healtron.MarkRolled(labor);
		}
		Contexts[labor] = t;
		t.Cleanup(() =>
		{
			Contexts.Remove(labor);
			Remove(labor);
		});
		t.Log("Spawned " + labor.name + " (" + job + ", " + faction.name + ") at " + at);
		return labor;
	}

	// The voxel of the route graph nearest the given one (a unit spawned off the graph cannot walk: PathInvalid),
	// within 3 voxels; else the voxel itself.
	// Walkable ground about distance from a place, the nearer the better (west of it first, as it used to be); the
	// home crystal of a small island can be closer than distance to the sea.
	internal static XYZ GroundAround(TestContext t, XYZ around, int distance)
	{
		int[][] directions = { new[] { -1, 0 }, new[] { 1, 0 }, new[] { 0, -1 }, new[] { 0, 1 } };
		for (int d = distance; d >= 5; d -= 5)
		{
			foreach (int[] direction in directions)
			{
				XYZ ground = Ground(around.x + direction[0] * d, around.z + direction[1] * d);
				if (ground.y != int.MinValue && PathProbe.Ground(ground.x, ground.z, out XYZ node))
				{
					return node;
				}
			}
		}
		t.Skip("No walkable ground within " + distance + " of " + around);
		return around;
	}

	internal static XYZ Walkable(XYZ at)
	{
		for (int r = 0; r <= 3; r++)
		{
			for (int dx = -r; dx <= r; dx++)
			{
				for (int dz = -r; dz <= r; dz++)
				{
					if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) == r && PathProbe.Ground(at.x + dx, at.z + dz, out XYZ ground) && Mathf.Abs(ground.y - at.y) <= 2)
					{
						return ground;
					}
				}
			}
		}
		return at;
	}

	// The player's bricktrons that are not this test's (the run's own builder, ...) are switched off for the test and
	// on again by its clean-up: a builder with nothing to do, idle or on a project without work, takes a workshop's
	// order by itself.
	internal static void SidelineOthers(TestContext t)
	{
		List<GameObject> others = new List<GameObject>();
		foreach (Labor labor in Object.FindObjectsOfType<Labor>())
		{
			if (labor != null && labor.gameObject.activeInHierarchy && labor.faction == Player && !Contexts.ContainsKey(labor) && Alive(labor))
			{
				labor.AssignToIdle();
				labor.gameObject.SetActive(false);
				others.Add(labor.gameObject);
			}
		}
		if (others.Count == 0)
		{
			return;
		}
		t.Log("Switched " + others.Count + " other bricktrons off for the test");
		t.Cleanup(() =>
		{
			foreach (GameObject go in others)
			{
				if (go != null)
				{
					go.SetActive(true);
				}
			}
		});
	}

	// One of the player's builders; when the world has none (the run took its units away), one of the test's own
	// next to the home crystal.
	internal static Labor EnsureBuilder(TestContext t)
	{
		Labor labor = Units.Find(0, false);
		if (labor != null)
		{
			return labor;
		}
		XYZ home = Home();
		return Bricktron(t, Occupation.Type.Builder, Walkable(Ground(home.x + 4, home.z + 2)));
	}

	// A unit of the enemy faction; when there is none, a corruptron of the test's own about 25 voxels from the home
	// crystal (it walks there on its own; it is not sent on a raid).
	internal static Labor EnsureEnemy(TestContext t)
	{
		Labor enemy = Units.Find(0, true);
		if (enemy != null)
		{
			return enemy;
		}
		Labor corruptron = Corruptron(t, GroundAround(t, Home(), 25));
		// Stays where it is: it is there to have routes searched for it, not to fight.
		corruptron.Autonomy = AutonomyStatus.WaitForOrders;
		return corruptron;
	}

	internal static Labor Bricktron(TestContext t, Occupation.Type job, XYZ at)
	{
		return Spawn(t, Test.Bricktron, Player, job, at);
	}

	internal static Labor Corruptron(TestContext t, XYZ at, int level = 1)
	{
		Faction enemy = EnemyFaction();
		if (enemy == null)
		{
			t.Skip("No enemy faction in this game");
		}
		return Spawn(t, Test.Corruptron, enemy, Occupation.Type.Corruptron, at, level);
	}

	internal static bool Alive(Labor labor)
	{
		if (labor == null || labor.gameObject.IsNullOrReleased() || !labor.gameObject.activeInHierarchy)
		{
			return false;
		}
		IDamageReceiver health = Health(labor);
		return health == null || health.CurrentHP > 0f;
	}

	internal static IDamageReceiver Health(Labor labor)
	{
		return (labor != null && !labor.gameObject.IsNullOrReleased()) ? labor.GetComponent<IDamageReceiver>() : null;
	}

	// The unit's hit points now, 0 once it is dead or gone.
	internal static float Hp(Labor labor)
	{
		IDamageReceiver health = Health(labor);
		return (health != null && labor.gameObject.activeInHierarchy) ? health.CurrentHP : 0f;
	}

	private static void Remove(Labor labor)
	{
		if (labor == null || !labor.gameObject.activeInHierarchy)
		{
			return;
		}
		Release(labor.gameObject);
	}

	// The test each spawned unit belongs to.
	private static readonly Dictionary<Labor, TestContext> Contexts = new Dictionary<Labor, TestContext>();

	private static readonly HashSet<TestContext> Quieted = new HashSet<TestContext>();

	// Those who want the world's tasks quiet now (tests, the runner), and the crews to put back after the last.
	private static int _quietUsers;

	private static readonly Dictionary<Project, int> QuietCrews = new Dictionary<Project, int>();

	// The world's standing repair and cleanup tasks (the mod's BaseJobs round the home crystal) take workers that
	// are on a project with nothing to do at the moment, the test's too, and use the test's material for repairs:
	// their crew is 0 while the test runs and put back by its clean-up (after the last test that wanted it).
	internal static void QuietWorldTasks(TestContext t)
	{
		if (!Quieted.Add(t))
		{
			return;
		}
		QuietWorld();
		t.Cleanup(() =>
		{
			Quieted.Remove(t);
			UnquietWorld();
		});
	}

	internal static void QuietWorld()
	{
		if (_quietUsers++ > 0)
		{
			return;
		}
		foreach (Project project in Object.FindObjectsOfType<Project>())
		{
			if (project == null || project.faction != Player || !(project is RepairProject || project is CleanupProject))
			{
				continue;
			}
			QuietCrews[project] = project.MaximumCrewSize;
			for (int i = 0; i < 8 && project.MaximumCrewSize > 0; i++)
			{
				Groupe.CmdRemoveProjectCapacity(project.gameObject);
			}
			foreach (Labor worker in Object.FindObjectsOfType<Labor>())
			{
				if (worker != null && worker.Project == project && Contexts.ContainsKey(worker))
				{
					worker.AssignToIdle();
				}
			}
		}
	}

	internal static void UnquietWorld()
	{
		if (_quietUsers == 0 || --_quietUsers > 0)
		{
			return;
		}
		foreach (KeyValuePair<Project, int> pair in QuietCrews)
		{
			for (int i = 0; i < 8 && pair.Key != null && pair.Key.MaximumCrewSize < pair.Value; i++)
			{
				Groupe.CmdAddProjectCapacity(pair.Key.gameObject);
			}
		}
		QuietCrews.Clear();
	}

	// Takes a spawned thing out of the game as the game does with its own (UNetGroupe.CmdDestroy): off the network,
	// back into its pool.
	internal static void Release(GameObject go)
	{
		if (go == null || go.IsNullOrReleased())
		{
			return;
		}
		if (NetworkServer.active && go.GetComponent<NetworkIdentity>() != null)
		{
			NetworkServer.UnSpawn(go);
		}
		ObjectPoolSingleton.Release(go);
	}

	// Takes the unit off every project and has it stand still (so the AI does not walk it elsewhere first).
	internal static void Idle(Labor labor)
	{
		labor.AssignToIdle();
	}

	// ---- items

	internal static List<GameObject> Items(TestContext t, Factory.AssetKey key, int count, Vector3 at)
	{
		List<GameObject> items = new List<GameObject>();
		for (int i = 0; i < count; i++)
		{
			GameObject item = Transactor.SpawnUNet(key, Player, SpawnSpread.Place(at, i, count), Quaternion.identity);
			t.NotNull(item, "The spawned " + key.Name);
			SpawnSpread.Calm(item);
			items.Add(item);
		}
		t.Cleanup(() =>
		{
			foreach (GameObject item in items)
			{
				if (item != null && item.activeInHierarchy)
				{
					Release(item);
				}
			}
		});
		t.Log("Spawned " + count + " " + key.Name + " at " + at);
		return items;
	}

	// How many items of the kind there are: stored (stockpiles, workshops), carried by bricktrons and loose in the
	// world, each counted once.
	internal static int Count(Factory.AssetKey key)
	{
		HashSet<GameObject> seen = new HashSet<GameObject>();
		foreach (Recepteur recepteur in Object.FindObjectsOfType<Recepteur>())
		{
			if (recepteur == null)
			{
				continue;
			}
			foreach (IDescriptor item in recepteur.StoredItems)
			{
				if (item != null && item.GameObject != null && Is(item.GameObject, key))
				{
					seen.Add(item.GameObject);
				}
			}
		}
		foreach (FactoryImprint imprint in Object.FindObjectsOfType<FactoryImprint>())
		{
			if (imprint != null && Is(imprint.gameObject, key))
			{
				seen.Add(imprint.gameObject);
			}
		}
		return seen.Count;
	}

	// Takes every item of the kind out of the world (stored, carried or loose); answers how many.
	internal static int RemoveAll(Factory.AssetKey key)
	{
		HashSet<GameObject> items = new HashSet<GameObject>();
		foreach (Recepteur recepteur in Object.FindObjectsOfType<Recepteur>())
		{
			if (recepteur == null)
			{
				continue;
			}
			foreach (IDescriptor item in new List<IDescriptor>(recepteur.StoredItems))
			{
				if (item != null && item.GameObject != null && Is(item.GameObject, key))
				{
					recepteur.ObjectUntracked(item.GameObject);
					items.Add(item.GameObject);
				}
			}
		}
		foreach (FactoryImprint imprint in Object.FindObjectsOfType<FactoryImprint>())
		{
			if (imprint != null && Is(imprint.gameObject, key))
			{
				items.Add(imprint.gameObject);
			}
		}
		foreach (GameObject item in items)
		{
			Release(item);
		}
		return items.Count;
	}

	// How many items of the kind the storages (stockpiles, not bricktrons or workshops) hold.
	internal static int Stored(Factory.AssetKey key)
	{
		int count = 0;
		foreach (Recepteur recepteur in Object.FindObjectsOfType<Recepteur>())
		{
			if (recepteur == null || recepteur.GetComponent<Labor>() != null || recepteur.GetComponent<CraftingStation>() != null)
			{
				continue;
			}
			foreach (IDescriptor item in recepteur.StoredItems)
			{
				if (item != null && item.GameObject != null && Is(item.GameObject, key))
				{
					count++;
				}
			}
		}
		return count;
	}

	internal static bool Is(GameObject go, Factory.AssetKey key)
	{
		FactoryImprint imprint = go.GetComponent<FactoryImprint>();
		return imprint != null && imprint.AssetKey != null && imprint.AssetKey.Name == key.Name && imprint.AssetKey.Factory == key.Factory;
	}

	// ---- projects

	// Runs one of the game's spawn commands (on the host they run at once) and finds the project it made; deleted
	// again by the clean-up (its bricktrons go idle).
	internal static T NewProject<T>(TestContext t, Action command) where T : Project
	{
		HashSet<Project> before = new HashSet<Project>(Object.FindObjectsOfType<Project>());
		command();
		foreach (Project project in Object.FindObjectsOfType<Project>())
		{
			if (!before.Contains(project) && project.faction == Player && project is T made)
			{
				t.Cleanup(() =>
				{
					if (made != null && made.gameObject.activeInHierarchy)
					{
						Groupe.CmdDelete(made.gameObject);
					}
				});
				return made;
			}
		}
		t.Fail("The game made no " + typeof(T).Name);
		return null;
	}

	internal static BuildProject BuildProject(TestContext t, XYZ at)
	{
		BuildProject project = NewProject<BuildProject>(t, () => Groupe.CmdSpawnGoupe("Groupes", "Construction", at.ToVector3(), Quaternion.identity, false));
		return project;
	}

	// Puts the bricktrons on the project as dragging them onto its task does, and keeps them there: otherwise the
	// world's own projects (repairs, cleanup) draw the test's workers away and the test waits for nothing.
	internal static void Hire(Project project, params Labor[] labors)
	{
		foreach (Labor labor in labors)
		{
			if (labor != null && Contexts.TryGetValue(labor, out TestContext t))
			{
				QuietWorldTasks(t);
			}
		}
		foreach (Labor labor in labors)
		{
			ProjectCommands.CmdChangeProject(labor.gameObject, project.gameObject);
			labor.Autonomy = AutonomyStatus.LockedToOperable;
		}
	}

	// ---- buildings

	// A building (stockpile, workshop, ...) at the voxel, finished at once as the last material arriving finishes
	// it; taken down again by the clean-up.
	internal static IEnumerator Building(TestContext t, Factory.AssetKey blueprint, XYZ at, Project project, Action<GameObject> built)
	{
		BlueprintPlacer.Result result = null;
		for (int turns = 0; turns < 4 && (result == null || !result.Placed); turns++)
		{
			result = BlueprintPlacer.Place(blueprint, at, turns, project, force: false);
		}
		t.True(result != null && result.Placed, blueprint.Name + " could not be placed at " + at + ": " + result?.Outcome + " " + result?.Detail);
		Vector3 position = result.Blueprint.transform.position;
		// The finished object is another asset than the blueprint (Blueprints/Pallet becomes ObjetsDynamiques/Palette).
		Factory.AssetKey resultKey = result.Blueprint.BlueprintResult;
		string madeName = (resultKey != null) ? resultKey.Name : blueprint.Name;
		HashSet<GameObject> before = new HashSet<GameObject>();
		foreach (FactoryImprint imprint in Object.FindObjectsOfType<FactoryImprint>())
		{
			before.Add(imprint.gameObject);
		}
		InstantBuild.Complete(new[] { result.Blueprint });
		GameObject made = null;
		yield return t.Until(() =>
		{
			made = FindNew(before, madeName, position);
			return made != null;
		}, 15f, blueprint.Name + " to be built");
		t.Cleanup(() =>
		{
			if (made != null && made.activeInHierarchy)
			{
				Release(made);
			}
		});
		t.Log(blueprint.Name + " built at " + XYZ.FromVector3(made.transform.position));
		built(made);
	}

	private static GameObject FindNew(HashSet<GameObject> before, string name, Vector3 near)
	{
		GameObject best = null;
		float bestDistance = 6f;
		foreach (FactoryImprint imprint in Object.FindObjectsOfType<FactoryImprint>())
		{
			if (imprint == null || before.Contains(imprint.gameObject) || imprint.AssetKey == null || imprint.AssetKey.Name != name)
			{
				continue;
			}
			float distance = Vector3.Distance(imprint.transform.position, near);
			if (distance < bestDistance)
			{
				best = imprint.gameObject;
				bestDistance = distance;
			}
		}
		return best;
	}
}
