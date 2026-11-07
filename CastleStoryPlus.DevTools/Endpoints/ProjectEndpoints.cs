using CastleStoryPlus.Core;
using System;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Engine.Nature;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Beacons;
using Brix.Game.Network;
using Brix.Input;
using Brix.Legacy;
using CastleStoryPlus.DevTools.Api;
using UnityEngine;
using UnityEngine.Networking;
using static CastleStoryPlus.DevTools.Endpoints.Json;
using Object = UnityEngine.Object;

namespace CastleStoryPlus.DevTools.Endpoints;

// The player's projects (the task bar): list them, make a build or quarry project, set a project's priority and
// crew size. Everything goes through the game's own project commands (UNetGroupe), run directly on the host.
internal static class ProjectEndpoints
{
	// The build project /api/blueprints/place used last, its default for the next call.
	private static int _lastBuildProject;

	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/projects", "The player's projects: id, type, priority, crew and crew size, goals (blueprints, ...; harvest: the things of its accepted kinds in its circle, harvest.inRange per kind; gameGoals: the game's own count, which fills a few frames after create), committed, position and radius of its marker", Projects)
		{
			Parameters = "q (part of the type name)"
		});
		routes.Add(new Route("POST", "/api/projects/create", "Makes a project like the task bar does. build: an empty build project (blueprints are added by /api/blueprints/place). quarry: digs out the box from-to with its stairs. harvest: gathers trees, plants, boulders, iron or pyrite around 'at'. treefarm: a tree farm (saplings planted and felled in its box) around 'at'", Create)
		{
			Parameters = "type (build, quarry, harvest or treefarm, required), at (x,y,z: where its marker stands; default the home crystal), from, to (x,y,z, quarry: opposite corners of the box to dig), clear (quarry: true cuts the trees on it first with a harvest project; without it a quarry under trees is refused), what (harvest: comma separated tree, plant, boulder, iron, pyrite or everything; default tree)"
		});
		routes.Add(new Route("POST", "/api/projects/hire", "Puts bricktrons on a project, as dragging them onto its task does (the crew size grows when full). Without units, takes idle bricktrons", Hire)
		{
			Parameters = "id (project id, required), count (idle bricktrons to take, default 1), units (comma separated bricktron ids instead)"
		});
		routes.Add(new Route("POST", "/api/projects/delete", "Deletes a project, as its task's delete button does; its bricktrons go idle", Delete)
		{
			Parameters = "id (project id, required)"
		});
		routes.Add(new Route("POST", "/api/projects/commit", "Commits a project (as clicking away from it does): its new blueprints or area get worked on", CommitRoute)
		{
			Parameters = "id (project id, required)"
		});
		routes.Add(new Route("POST", "/api/projects/priority", "Sets a project's priority with the game's raise/lower priority", Priority)
		{
			Parameters = "id (project id, required), priority (locked, low, medium or high, required)"
		});
		routes.Add(new Route("POST", "/api/projects/crew", "Sets how many bricktrons may work on a project (0 to 8), like the + and - on its task", Crew)
		{
			Parameters = "id (project id, required), size (0 to 8, required)"
		});
	}

	private static void NeedHost()
	{
		if (!NetworkServer.active)
		{
			throw new ApiException(409, "Only on the host (single player or hosting a game)");
		}
		NeedGame();
	}

	// Host or client: the game's commands go to the host as the player's clicks do.
	private static void NeedGame()
	{
		if (User.LocalUser == null || User.LocalUser.faction == null || !Architecte.commence)
		{
			throw new ApiException(409, "No game in progress (or still loading)");
		}
	}

	internal static List<object> List(Faction faction, string filter)
	{
		List<object> result = new List<object>();
		foreach (Project project in Object.FindObjectsOfType<Project>())
		{
			if (project == null || project.faction != faction || project is IdleProject)
			{
				continue;
			}
			string type = project.GetType().Name;
			if (!string.IsNullOrEmpty(filter) && type.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
			{
				continue;
			}
			result.Add(Describe(project));
		}
		return result;
	}

	// goals: the game's own count (GoalCounts), except for a harvest project: there the things of its accepted kinds in
	// its circle, counted from the world. The game's harvest count leaves out other kinds (a tree project's circle
	// holds goal objects for every plant and boulder too) and its goal providers only fill a few frames after the
	// project is made, so a create answer would always say 0. A new quarry's goals come the same way; quarry.toDig
	// counts the voxels left to dig at once.
	internal static object Describe(Project project)
	{
		object harvest = Harvest(project, out int harvestGoals);
		return Obj(
			"id", project.gameObject.InstanceId(),
			"type", project.GetType().Name,
			"priority", project.PriorityBias.ToString(),
			"crew", project.CrewCount(),
			"maxCrew", project.MaximumCrewSize,
			"goals", (harvest != null) ? harvestGoals : project.GoalCounts(),
			"gameGoals", project.GoalCounts(),
			"committed", project.Commited.ToString(),
			"position", Vec(Marker(project)),
			"radius", MarkerRadius(project),
			"lastPlaceTarget", project.gameObject.InstanceId() == _lastBuildProject,
			"harvest", harvest,
			"quarry", Quarry(project));
	}

	// Where the project's marker stands. The project's own object stays at 0,0,0 (UNetGroupe.CreateGroup); the
	// marker is its beacon. Without one, the centre of its goals.
	private static Vector3 Marker(Project project)
	{
		BeaconDriver driver = project.GetComponent<BeaconDriver>();
		if (driver != null)
		{
			if (driver.Beacon != null)
			{
				return driver.Beacon.transform.position;
			}
			if (driver.NetworkPosition != Vector3.zero)
			{
				return driver.NetworkPosition;
			}
		}
		Vector3 sum = Vector3.zero;
		int count = 0;
		foreach (Goal goal in project.GetComponentsInChildren<Goal>())
		{
			if (goal != null)
			{
				sum += goal.transform.position;
				count++;
			}
		}
		return (count > 0) ? sum / count : project.transform.position;
	}

	private static int MarkerRadius(Project project)
	{
		BeaconDriver driver = project.GetComponent<BeaconDriver>();
		if (driver == null)
		{
			return 0;
		}
		return (driver.Beacon != null) ? (int)driver.Beacon.radius : driver.NetworkRadius;
	}

	// A harvest project's circle: what it accepts and how many trees, plants, boulders, iron and pyrite stand in it,
	// counted as its goal providers do (TreeGoalProvider: trees by root within the radius; HarvestGoalProvider: the
	// nature of each column within the flat radius, once per thing).
	private static object Harvest(Project project, out int goals)
	{
		goals = 0;
		HarvestProject harvest = project as HarvestProject;
		if (harvest == null)
		{
			return null;
		}
		NatureType accepted = harvest.AcceptedTypes;
		XYZ center = XYZ.FromVector3(Marker(project));
		BeaconDriver driver = project.GetComponent<BeaconDriver>();
		if (driver != null && driver.Beacon != null)
		{
			center = driver.Beacon.position;
		}
		int radius = MarkerRadius(project);
		Dictionary<string, int> inRange = new Dictionary<string, int>();
		int trees = 0;
		TreeRegistry registry = TreeRegistry.Instance;
		if (registry != null)
		{
			foreach (XYZ tree in registry._treeData.Keys)
			{
				if (TreeRegistry.IsInRadius(tree, center, radius))
				{
					trees++;
				}
			}
		}
		inRange[NatureType.tree.ToString()] = trees;
		if ((accepted & NatureType.tree) != 0)
		{
			goals += trees;
		}
		HashSet<NatureData> natures = new HashSet<NatureData>();
		for (int dx = -radius; dx <= radius; dx++)
		{
			for (int dz = -radius; dz <= radius; dz++)
			{
				if (dx * dx + dz * dz > radius * radius)
				{
					continue;
				}
				NatureData nature = NatureRegistry.NatureAt(center.x + dx, center.z + dz);
				if (nature != null && nature.IsValid && natures.Add(nature))
				{
					string kind = nature.Type.ToString();
					inRange.TryGetValue(kind, out int n);
					inRange[kind] = n + 1;
					if ((accepted & nature.Type) != 0)
					{
						goals++;
					}
				}
			}
		}
		return Obj("accepts", accepted.ToString(), "center", PathEndpoints.Voxel(center), "inRange", inRange);
	}

	private static object Quarry(Project project)
	{
		QuarryData quarry = project.GetComponent<QuarryData>();
		if (quarry == null)
		{
			return null;
		}
		VoxelBounds bounds = quarry.Bounds;
		StairOrientation stairs = quarry.StairOrientation;
		// The ground left in the box (no blocks, nothing invincible), as QuarryGoalProvider2 would dig it.
		int toDig = 0;
		for (int x = bounds.min.x; x <= bounds.max.x; x++)
		{
			for (int y = bounds.min.y; y <= bounds.max.y; y++)
			{
				for (int z = bounds.min.z; z <= bounds.max.z; z++)
				{
					XYZ voxel = new XYZ(x, y, z);
					if (Voxel.IsFull(voxel) && !Voxel.IsBlock(voxel) && !Voxel.IsInvincible(voxel))
					{
						toDig++;
					}
				}
			}
		}
		return Obj(
			"toDig", toDig,
			"from", PathEndpoints.Voxel(bounds.min),
			"to", PathEndpoints.Voxel(bounds.max),
			"stairCorner", PathEndpoints.Voxel(stairs.corner),
			"stairDirection", PathEndpoints.Voxel(stairs.direction),
			"stairStartY", quarry.StairY,
			"started", quarry.Commited);
	}

	private static object Projects(ApiRequest request)
	{
		NeedGame();
		List<object> projects = List(User.LocalUser.faction, request.String("q", string.Empty));
		return Obj("count", projects.Count, "projects", projects);
	}

	internal static Project Find(int id)
	{
		foreach (Project project in Object.FindObjectsOfType<Project>())
		{
			if (project != null && project.gameObject.InstanceId() == id)
			{
				if (project.faction != User.LocalUser.faction)
				{
					throw new ApiException(409, "Project " + id + " is not the player's");
				}
				return project;
			}
		}
		throw new ApiException(404, "No project " + id + " (GET /api/projects lists them)");
	}

	// The build project for placing: the given one, else the one used last, else the last selected build project,
	// else a new one (like the bricks hotkey makes one when there is none, Eyedropper.SelectBuildProject).
	internal static Project BuildProject(int id, out bool created)
	{
		created = false;
		Project project;
		if (id != 0)
		{
			project = Find(id);
			if (!(project is BuildProject))
			{
				throw new ApiException(409, "Project " + id + " is a " + project.GetType().Name + ", not a build project");
			}
		}
		else
		{
			project = Alive(_lastBuildProject);
			Project selected = UIGameObserver.projects.LastSelectedBuild;
			if (project == null && selected != null && selected is BuildProject && selected.gameObject.activeInHierarchy && selected.faction == User.LocalUser.faction)
			{
				project = selected;
			}
			if (project == null)
			{
				project = Spawn(() => User.LocalUser.GetComponent<UNetGroupe>().CallCmdSpawnGoupe("Groupes", "Construction", HomePosition(), Quaternion.identity, false));
				created = true;
			}
			if (project == null)
			{
				throw new ApiException(409, "A client: make the build project first (POST /api/projects/create?type=build) and pass its id as 'project' once GET /api/projects lists it");
			}
		}
		_lastBuildProject = project.gameObject.InstanceId();
		return project;
	}

	private static Project Alive(int id)
	{
		if (id == 0)
		{
			return null;
		}
		foreach (Project project in Object.FindObjectsOfType<Project>())
		{
			if (project != null && project.gameObject.InstanceId() == id && project is BuildProject && project.gameObject.activeInHierarchy && project.faction == User.LocalUser.faction)
			{
				return project;
			}
		}
		return null;
	}

	private static Vector3 HomePosition()
	{
		return WorldEndpoints.HomeVoxel().ToVector3();
	}

	// Runs one of the game's spawn commands (on the host they run at once) and finds the project it made.
	private static Project Spawn(Action command)
	{
		HashSet<int> before = new HashSet<int>();
		foreach (Project project in Object.FindObjectsOfType<Project>())
		{
			before.Add(project.gameObject.InstanceId());
		}
		command();
		if (!NetworkServer.active)
		{
			// A client: the host makes the project, and it arrives a moment later (GET /api/projects).
			return null;
		}
		foreach (Project project in Object.FindObjectsOfType<Project>())
		{
			if (!before.Contains(project.gameObject.InstanceId()) && project.faction == User.LocalUser.faction)
			{
				return project;
			}
		}
		throw new ApiException(500, "The game made no project");
	}

	private static object Create(ApiRequest request)
	{
		NeedGame();
		string type = request.Required("type").ToLowerInvariant();
		Vector3 at = (request.Get("at") != null) ? request.Vector("at") : HomePosition();
		UNetGroupe groupe = User.LocalUser.GetComponent<UNetGroupe>();
		Project project;
		Project clearing = null;
		switch (type)
		{
			case "build":
				project = Spawn(() => groupe.CallCmdSpawnGoupe("Groupes", "Construction", at, Quaternion.identity, false));
				if (project != null)
				{
					_lastBuildProject = project.gameObject.InstanceId();
				}
				break;
			case "quarry":
			case "mine":
				{
					NeedHost();
					Vector3 from = request.Vector("from");
					Vector3 to = request.Vector("to");
					Vector3 min = Vector3.Min(from, to);
					Vector3 max = Vector3.Max(from, to);
					// The quarry tool's box reaches 2 above the ground (VoxelGoalProvider.DefaultBounds); a box that ends at
					// the ground gets only its stairs, which wait for digging that never comes.
					int top = int.MinValue;
					for (int x = (int)min.x; x <= (int)max.x; x++)
					{
						for (int z = (int)min.z; z <= (int)max.z; z++)
						{
							top = Mathf.Max(top, WorldEndpoints.GroundY(x, z));
						}
					}
					if (top != int.MinValue)
					{
						max.y = Mathf.Max(max.y, top + 2);
					}
					if (request.Get("at") == null)
					{
						at = new Vector3(min.x, max.y, min.z);
					}
					// Trees on the quarry (or next to it, where the stairs may go) keep the workers from it.
					List<XYZ> trees = WorldEndpoints.TreesOver((int)min.x - 1, (int)min.z - 1, (int)max.x + 1, (int)max.z + 1);
					if (trees.Count > 0 && !request.Bool("clear", false))
					{
						throw new ApiException(409, trees.Count + " trees stand on the quarry or next to it, and the workers cannot dig under them. Pass clear=true to have them cut first (a harvest project for trees), or pick a place without trees (GET /api/ground?flat=6&noTrees=true)");
					}
					if (trees.Count > 0)
					{
						Vector3 middle = new Vector3((min.x + max.x) / 2f, max.y + 1f, (min.z + max.z) / 2f);
						clearing = Spawn(() => groupe.CallCmdSpawnHarvestProject(middle, Quaternion.identity, false, (int)NatureType.tree));
						Commit(clearing);
					}
					project = Spawn(() => groupe.CallCmdSpawnMineProject(at, Quaternion.identity, false));
					// As the quarry tool does: first uncommitted, so the game works out valid stairs (with 'commited' it keeps
					// the stairs it has, none yet, and every dig goal stays invalid), then committed with those stairs.
					StairOrientation stairs = StairOrientation.Default;
					groupe.CallCmdSetMine(project.gameObject, min, max, stairs.corner.ToVector3(), stairs.direction.ToVector3(), false);
					QuarryData quarry = project.GetComponent<QuarryData>();
					if (quarry != null)
					{
						VoxelBounds bounds = quarry.Bounds;
						stairs = quarry.StairOrientation;
						groupe.CallCmdSetMine(project.gameObject, bounds.min.ToVector3(), bounds.max.ToVector3(), stairs.corner.ToVector3(), stairs.direction.ToVector3(), true);
					}
					Commit(project);
					break;
				}
			case "harvest":
				{
					int accepted = 0;
					foreach (string part in request.String("what", "tree").Split(','))
					{
						try
						{
							accepted |= (int)(NatureType)Enum.Parse(typeof(NatureType), part.Trim(), true);
						}
						catch (ArgumentException)
						{
							throw new ApiException(400, "'what' takes tree, plant, boulder, iron, pyrite or everything, was " + part);
						}
					}
					if (accepted == 0)
					{
						throw new ApiException(400, "'what' is empty");
					}
					project = Spawn(() => groupe.CallCmdSpawnHarvestProject(at, Quaternion.identity, false, accepted));
					// The game commits a project when the player clicks away from it; until then nobody works on it.
					if (project != null)
					{
						Commit(project);
					}
					break;
				}
			case "treefarm":
				// As the "Tree farm" button places it: a landscape task with the farm's rotation (TreeFarm.FarmRotation).
				project = Spawn(() => groupe.CallCmdSpawnLandscapeProject(at, new Quaternion(0f, 0f, 1f, 0f), false));
				if (project != null)
				{
					Commit(project);
				}
				break;
			default:
				throw new ApiException(400, "'type' must be build, quarry, harvest or treefarm, was " + type);
		}
		if (project == null)
		{
			return Obj("sent", true, "note", "A client: the host makes the project; commit it with POST /api/projects/commit once GET /api/projects lists it");
		}
		return Obj("created", true, "project", Describe(project), "clearingTrees", (clearing != null) ? Describe(clearing) : null);
	}

	private static object Delete(ApiRequest request)
	{
		NeedGame();
		Project project = Find(request.Int("id", 0));
		string type = project.GetType().Name;
		int id = project.gameObject.InstanceId();
		User.LocalUser.GetComponent<UNetGroupe>().CallCmdDelete(project.gameObject);
		return Obj("deleted", id, "type", type);
	}

	private static object CommitRoute(ApiRequest request)
	{
		NeedGame();
		Project project = Find(request.Int("id", 0));
		Commit(project);
		return Obj("project", Describe(project));
	}

	private static object Hire(ApiRequest request)
	{
		NeedGame();
		Project project = Find(request.Int("id", 0));
		List<Labor> labors = new List<Labor>();
		string units = request.String("units", string.Empty);
		if (units.Length > 0)
		{
			foreach (string part in units.Split(','))
			{
				int id = int.Parse(part.Trim());
				Labor labor = null;
				foreach (Labor candidate in Object.FindObjectsOfType<Labor>())
				{
					if (candidate != null && candidate.gameObject.InstanceId() == id)
					{
						labor = candidate;
						break;
					}
				}
				if (labor == null)
				{
					throw new ApiException(404, "No bricktron " + id + " (GET /api/bricktrons lists them)");
				}
				labors.Add(labor);
			}
		}
		else
		{
			int count = request.Int("count", 1);
			HashSet<Labor> operators = WorkshopEndpoints.Operators();
			foreach (Labor candidate in Object.FindObjectsOfType<Labor>())
			{
				if (labors.Count >= count)
				{
					break;
				}
				if (candidate != null && candidate.faction == User.LocalUser.faction && !operators.Contains(candidate) && (candidate.Project == null || candidate.Project is IdleProject))
				{
					labors.Add(candidate);
				}
			}
		}
		UNetProjectCmd command = User.LocalUser.GetComponent<UNetProjectCmd>();
		List<object> hired = new List<object>();
		foreach (Labor labor in labors)
		{
			command.CallCmdChangeProject(labor.gameObject, project.gameObject);
			hired.Add(Obj("id", labor.gameObject.InstanceId(), "name", labor.name, "onProject", labor.Project == project));
		}
		return Obj("hired", hired, "project", Describe(project));
	}

	private static object Priority(ApiRequest request)
	{
		NeedHost();
		Project project = Find(request.Int("id", 0));
		string text = request.Required("priority");
		ProjectPriority.Bias wanted;
		try
		{
			wanted = (ProjectPriority.Bias)Enum.Parse(typeof(ProjectPriority.Bias), text, true);
		}
		catch (ArgumentException)
		{
			throw new ApiException(400, "'priority' must be locked, low, medium or high, was " + text);
		}
		UNetGroupe groupe = User.LocalUser.GetComponent<UNetGroupe>();
		// Locked < Low < Medium < High: a few steps of the game's raise/lower reach any of them.
		for (int i = 0; i < 4 && project.PriorityBias != wanted; i++)
		{
			if (Rank(project.PriorityBias) < Rank(wanted))
			{
				groupe.CallCmdRaisePriority(project.gameObject);
			}
			else
			{
				groupe.CallCmdLowerPriority(project.gameObject);
			}
		}
		return Obj("project", Describe(project));
	}

	private static int Rank(ProjectPriority.Bias bias)
	{
		switch (bias)
		{
			case ProjectPriority.Bias.Locked:
				return 0;
			case ProjectPriority.Bias.Low:
				return 1;
			case ProjectPriority.Bias.Medium:
				return 2;
			default:
				return 3;
		}
	}

	private static object Crew(ApiRequest request)
	{
		NeedHost();
		Project project = Find(request.Int("id", 0));
		int size = request.Int("size", -1);
		if (size < 0 || size > 8)
		{
			throw new ApiException(400, "'size' must be from 0 to 8");
		}
		UNetGroupe groupe = User.LocalUser.GetComponent<UNetGroupe>();
		for (int i = 0; i < 8 && project.MaximumCrewSize != size; i++)
		{
			if (project.MaximumCrewSize < size)
			{
				groupe.CallCmdAddProjectCapacity(project.gameObject);
			}
			else
			{
				groupe.CallCmdRemoveProjectCapacity(project.gameObject);
			}
		}
		return Obj("project", Describe(project));
	}

	// Commits a build project's new blueprints, as unselecting it in the game does (SelectableProjectBehaviour).
	internal static void Commit(Project project)
	{
		User.LocalUser.GetComponent<UNetGroupe>().CallCmdCommit(project.gameObject);
	}
}
