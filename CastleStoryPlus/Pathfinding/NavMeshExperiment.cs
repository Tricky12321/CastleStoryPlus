#if UNITY6_PLAYER
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Configuration;
using Brix.Engine;
using Brix.Game.Components.Volumes;
using Brix.Pathfinding;
using Brix.Pathfinding.Test;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Networking;

namespace CastleStoryPlus.Pathfinding;

// Experiment (Unity 6 port only, [Pathfinding] NavMeshExperiment, off by default): Unity's own navigation mesh built
// next to the game's voxel graph, to measure whether it could take over the route searches. Nothing walks by it; the
// DevTools API (/api/navmesh/...) builds it, asks it for routes and compares them with the game's A*.
//   Mesh: the top surface of every place of the game's level-0 graph (a TerrainPathNode) that some walk leads to, as
//   two triangles with the voxel's four corner heights (its niveaux), merged into one mesh per 16 x 16 columns and
//   area: plain ground, ground near trees (forest, only for units that may walk there), ground with a building's
//   obstacle on it and ground under a door (its faction walks through). See docs/NavMesh.md. The graph already applies the bricktrons' rules (headroom, stairs under walkways, nature
//   blockers), so the agent is kept small enough that Unity drops nothing of it: height under the 1 block between two
//   stacked floors, a step under the 2/3 block where the game's walking turns into climbing, a small radius.
//   Links: the graph's climbing, stepping down and ladder branches become one-way links between the two places' top
//   centres, each with its own area so a query leaves out what the unit may not do, and the game's cost.
//   Updates: the voxels the game rebuilds its graph for (RefreshLod) mark their 16 x 16 chunks; once no build is
//   running, those chunks get new meshes and links and Unity rebuilds the tiles over them on its worker threads
//   (UpdateNavMeshDataAsync), keeping the other tiles.
// Every step is timed for the API: main-thread work per frame, build and update times, query times.
[Feature(Features.Pathfinding, Features.PathfindingInfo)]
internal sealed class NavMeshExperiment : MonoBehaviour
{
	internal const int Chunk = 16;

	// Area indices of the experiment's agent (Unity has 32; 0 is its walkable, 1 not walkable, 2 jump).
	internal const int AreaWalk = 0;

	internal const int AreaForest = 3;

	internal const int AreaClimb = 4;

	internal const int AreaStepDown = 5;

	internal const int AreaLadder = 6;

	internal const int AreaObstacle = 7;

	// Ground a door stands on: its own faction walks through, others do not.
	internal const int AreaDoor = 8;

	// Main-thread time a frame the chunk meshes may take while they are made.
	private const double MeshBudgetMs = 3.0;

	internal static ConfigEntry<bool> Enabled;

	internal sealed class ChunkData
	{
		public int X;

		public int Z;

		public readonly HashSet<XYZ> Nodes = new HashSet<XYZ>();

		public readonly Mesh[] Meshes = new Mesh[4];

		public readonly List<NavMeshLinkInstance> Links = new List<NavMeshLinkInstance>();

		public int MinY = int.MaxValue;

		public int MaxY = int.MinValue;

		public int Quads;
	}

	// What a build or update cost; the API reads these.
	internal sealed class Stats
	{
		public int Nodes;

		public int Chunks;

		public int Quads;

		public int Links;

		public double IndexMs;

		public double MeshMs;

		public int MeshFrames;

		public double MeshWorstFrameMs;

		public double DispatchMs;

		public double BuildWallMs;

		public int BuildFrames;

		public double LinkMs;

		public double TotalWallMs;

		public int Updates;

		public int UpdatedChunks;

		public double UpdateMainMs;

		public double UpdateMainWorstMs;

		public double UpdateLatencyMs;

		public double UpdateLatencyWorstMs;

		public double LastUpdateLatencyMs;

		public double LastUpdateAsyncMs;

		public double MarkMs;

		public int Marks;

		public long FramesWithWork;

		public double FrameWorkMs;

		public double FrameWorkWorstMs;

		public long FramesWithAsync;

		public double FrameDeltaAsyncMs;

		public long FramesIdle;

		public double FrameDeltaIdleMs;
	}

	internal static NavMeshBuildSettings Settings;

	internal static bool Active;

	internal static string Phase = "off";

	internal static string Problem;

	internal static Stats Measured = new Stats();

	internal static readonly Dictionary<long, ChunkData> Chunks = new Dictionary<long, ChunkData>();

	private static NavMeshData _data;

	private static NavMeshDataInstance _instance;

	private static TestPathfinding _graph;

	private static readonly Queue<long> MeshQueue = new Queue<long>();

	private static readonly HashSet<long> Queued = new HashSet<long>();

	// Chunks changed since the last update was sent, and when the first of them changed.
	private static readonly HashSet<long> Dirty = new HashSet<long>();

	private static long _dirtySince;

	// Chunks the running update covers, and when their change was first seen.
	private static readonly List<long> Updating = new List<long>();

	private static long _updatingSince;

	private static double _updatingMainMs;

	private static AsyncOperation _op;

	private static long _opStart;

	private static long _buildStart;

	private static bool _initial;

	private static readonly List<NavMeshBuildSource> Sources = new List<NavMeshBuildSource>();

	private static readonly List<Vector3>[] Vertices = { new List<Vector3>(), new List<Vector3>(), new List<Vector3>(), new List<Vector3>() };

	private static readonly List<int>[] Triangles = { new List<int>(), new List<int>(), new List<int>(), new List<int>() };

	private static readonly int[] MeshAreas = { AreaWalk, AreaForest, AreaObstacle, AreaDoor };

	// Agent shape, kept small enough that every place of the graph survives (see the header). Set from the API.
	internal static float Radius = 0.25f;

	internal static float Height = 0.9f;

	internal static float Climb = 0.6f;

	internal static float Slope = 60f;

	internal static float VoxelSize = 0.125f;

	internal static int TileSize = 128;

	private static readonly Stopwatch FrameWatch = new Stopwatch();

	private static void Enable()
	{
		Enabled = Plugin.Cfg.Bind("Pathfinding", "NavMeshExperiment", false, "Experiment, Unity 6 port only: builds Unity's navigation mesh from the route graph next to it and keeps it up to date, to measure it against the game's route search (DevTools: /api/navmesh). Nothing walks by it.");
		GameSession.OnLeave(Leave);
		Plugin.Root.AddComponent<NavMeshExperiment>();
		Active = Enabled.Value;
	}

	// A scene change drops the mesh; it is built again for the next game when the experiment is on.
	private static void Leave()
	{
		bool active = Active;
		Stop();
		Active = active;
	}

	private static void Disable()
	{
		Stop();
	}

	internal static long Key(int cx, int cz)
	{
		return ((long)cx << 32) ^ (uint)cz;
	}

	internal static long KeyOf(XYZ p)
	{
		return Key(FloorDiv(p.x), FloorDiv(p.z));
	}

	private static int FloorDiv(int v)
	{
		return (v >= 0) ? v / Chunk : ((v + 1) / Chunk) - 1;
	}

	internal static bool Ready => _data != null && Phase == "ready";

	internal static int AgentTypeId => Settings.agentTypeID;

	// Starts (or starts again) a full build with the current agent shape.
	internal static void Start()
	{
		Stop();
		Active = true;
	}

	internal static void Stop()
	{
		if (_instance.valid)
		{
			NavMesh.RemoveNavMeshData(_instance);
		}
		_instance = default;
		foreach (ChunkData chunk in Chunks.Values)
		{
			foreach (NavMeshLinkInstance link in chunk.Links)
			{
				NavMesh.RemoveLink(link);
			}
			foreach (Mesh mesh in chunk.Meshes)
			{
				if (mesh != null)
				{
					Object.Destroy(mesh);
				}
			}
		}
		if (_data != null)
		{
			Object.Destroy(_data);
		}
		if (Settings.agentTypeID != 0)
		{
			NavMesh.RemoveSettings(Settings.agentTypeID);
			Settings = default;
		}
		_data = null;
		_graph = null;
		_op = null;
		Chunks.Clear();
		MeshQueue.Clear();
		Queued.Clear();
		Dirty.Clear();
		Updating.Clear();
		Sources.Clear();
		Active = false;
		Phase = "off";
		Problem = null;
		Measured = new Stats();
	}

	private void Update()
	{
		if (!Active)
		{
			return;
		}
		TestPathfinding graph = TestPathfinding.Instance;
		if (graph == null || !graph.Initialized || graph.terrainLod == null || !NetworkServer.active)
		{
			return;
		}
		if (_graph != graph)
		{
			if (_graph != null)
			{
				Stop();
				Active = true;
			}
			_graph = graph;
			BeginBuild(graph);
		}
		FrameWatch.Reset();
		FrameWatch.Start();
		try
		{
			Step();
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogError("NavMeshExperiment: " + ex);
			Problem = ex.Message;
			Phase = "failed";
			Active = false;
		}
		FrameWatch.Stop();
		double ms = FrameWatch.Elapsed.TotalMilliseconds;
		double delta = Time.unscaledDeltaTime * 1000.0;
		if (_op != null)
		{
			Measured.FramesWithAsync++;
			Measured.FrameDeltaAsyncMs += delta;
		}
		else if (Phase == "ready")
		{
			Measured.FramesIdle++;
			Measured.FrameDeltaIdleMs += delta;
		}
		if (ms > 0.01)
		{
			Measured.FramesWithWork++;
			Measured.FrameWorkMs += ms;
			if (ms > Measured.FrameWorkWorstMs)
			{
				Measured.FrameWorkWorstMs = ms;
			}
		}
	}

	private static double Since(long ticks)
	{
		return (Stopwatch.GetTimestamp() - ticks) * 1000.0 / Stopwatch.Frequency;
	}

	private static void BeginBuild(TestPathfinding graph)
	{
		_buildStart = Stopwatch.GetTimestamp();
		Settings = NavMesh.CreateSettings();
		Settings.agentRadius = Radius;
		Settings.agentHeight = Height;
		Settings.agentClimb = Climb;
		Settings.agentSlope = Slope;
		Settings.overrideVoxelSize = true;
		Settings.voxelSize = VoxelSize;
		Settings.overrideTileSize = true;
		Settings.tileSize = TileSize;
		Settings.minRegionArea = 0.05f;
		Settings.preserveTilesOutsideBounds = true;
		_data = new NavMeshData(Settings.agentTypeID);
		_instance = NavMesh.AddNavMeshData(_data);
		// Index every place of the graph by chunk, in one go (one pass over the graph's dictionary).
		long start = Stopwatch.GetTimestamp();
		foreach (KeyValuePair<XYZ, TerrainPathNode> entry in graph.TerrainNodes)
		{
			if (entry.Value == null || entry.Value.IsEmpty)
			{
				continue;
			}
			long key = KeyOf(entry.Key);
			if (!Chunks.TryGetValue(key, out ChunkData chunk))
			{
				chunk = new ChunkData { X = FloorDiv(entry.Key.x), Z = FloorDiv(entry.Key.z) };
				Chunks[key] = chunk;
			}
			chunk.Nodes.Add(entry.Key);
			Measured.Nodes++;
		}
		Measured.IndexMs = Since(start);
		Measured.Chunks = Chunks.Count;
		foreach (long key in Chunks.Keys)
		{
			Queue(key);
		}
		_initial = true;
		Phase = "meshing";
		Plugin.Log.LogInfo("NavMeshExperiment: indexed " + Measured.Nodes + " places in " + Chunks.Count + " chunks in " + Measured.IndexMs.ToString("0.0") + " ms; agent type " + Settings.agentTypeID);
	}

	private static void Queue(long key)
	{
		if (Queued.Add(key))
		{
			MeshQueue.Enqueue(key);
		}
	}

	private static void Step()
	{
		if (_op != null)
		{
			if (!_op.isDone)
			{
				return;
			}
			_op = null;
			Finish();
			return;
		}
		if (_initial)
		{
			MeshSome();
			if (MeshQueue.Count == 0)
			{
				Dispatch(InitialBounds(), all: true);
			}
			return;
		}
		if (Dirty.Count == 0)
		{
			return;
		}
		// An update: new meshes for the changed chunks (all in this frame; they are few), then Unity's async rebuild.
		long start = Stopwatch.GetTimestamp();
		Updating.Clear();
		Updating.AddRange(Dirty);
		Dirty.Clear();
		_updatingSince = _dirtySince;
		Bounds bounds = default;
		bool first = true;
		foreach (long key in Updating)
		{
			if (!Chunks.TryGetValue(key, out ChunkData chunk))
			{
				continue;
			}
			MakeMeshes(chunk);
			Bounds b = ChunkBounds(chunk);
			if (first)
			{
				bounds = b;
				first = false;
			}
			else
			{
				bounds.Encapsulate(b);
			}
		}
		if (first)
		{
			return;
		}
		_updatingMainMs = Since(start);
		Dispatch(bounds, all: false);
		_updatingMainMs += Measured.DispatchMs;
	}

	private static void MeshSome()
	{
		long start = Stopwatch.GetTimestamp();
		while (MeshQueue.Count > 0 && Since(start) < MeshBudgetMs)
		{
			long key = MeshQueue.Dequeue();
			Queued.Remove(key);
			if (Chunks.TryGetValue(key, out ChunkData chunk))
			{
				MakeMeshes(chunk);
			}
		}
		double ms = Since(start);
		Measured.MeshMs += ms;
		Measured.MeshFrames++;
		if (ms > Measured.MeshWorstFrameMs)
		{
			Measured.MeshWorstFrameMs = ms;
		}
	}

	private static Bounds InitialBounds()
	{
		Bounds bounds = default;
		bool first = true;
		foreach (ChunkData chunk in Chunks.Values)
		{
			if (chunk.Quads == 0)
			{
				continue;
			}
			Bounds b = ChunkBounds(chunk);
			if (first)
			{
				bounds = b;
				first = false;
			}
			else
			{
				bounds.Encapsulate(b);
			}
		}
		return bounds;
	}

	private static Bounds ChunkBounds(ChunkData chunk)
	{
		int minY = (chunk.MinY <= chunk.MaxY) ? chunk.MinY : 0;
		int maxY = (chunk.MinY <= chunk.MaxY) ? chunk.MaxY : 0;
		Vector3 min = new Vector3(chunk.X * Chunk - 0.5f, minY - 1f, chunk.Z * Chunk - 0.5f);
		Vector3 max = new Vector3((chunk.X + 1) * Chunk - 0.5f, maxY + 2f, (chunk.Z + 1) * Chunk - 0.5f);
		Bounds bounds = new Bounds();
		bounds.SetMinMax(min, max);
		return bounds;
	}

	// The sources of the chunks touching the bounds (the whole island for the first build).
	private static void Dispatch(Bounds bounds, bool all)
	{
		long start = Stopwatch.GetTimestamp();
		Sources.Clear();
		Bounds reach = bounds;
		reach.Expand(new Vector3(Chunk * 2, 0f, Chunk * 2));
		foreach (ChunkData chunk in Chunks.Values)
		{
			if (!all && !reach.Intersects(ChunkBounds(chunk)))
			{
				continue;
			}
			for (int i = 0; i < chunk.Meshes.Length; i++)
			{
				Mesh mesh = chunk.Meshes[i];
				if (mesh == null || mesh.vertexCount == 0)
				{
					continue;
				}
				Sources.Add(new NavMeshBuildSource
				{
					shape = NavMeshBuildSourceShape.Mesh,
					sourceObject = mesh,
					transform = Matrix4x4.identity,
					area = MeshAreas[i]
				});
			}
		}
		bounds.Expand(new Vector3(1f, 1f, 1f));
		_op = NavMeshBuilder.UpdateNavMeshDataAsync(_data, Settings, Sources, bounds);
		_opStart = Stopwatch.GetTimestamp();
		Measured.DispatchMs = Since(start);
		if (_initial)
		{
			Phase = "building";
		}
	}

	private static void Finish()
	{
		double asyncMs = Since(_opStart);
		if (_initial)
		{
			_initial = false;
			Measured.BuildWallMs = asyncMs;
			long start = Stopwatch.GetTimestamp();
			foreach (ChunkData chunk in Chunks.Values)
			{
				AddLinks(chunk);
			}
			Measured.LinkMs = Since(start);
			Measured.TotalWallMs = Since(_buildStart);
			Phase = "ready";
			Plugin.Log.LogInfo("NavMeshExperiment: built " + Measured.Quads + " quads, " + Measured.Links + " links: meshes " + Measured.MeshMs.ToString("0") + " ms main thread over " + Measured.MeshFrames + " frames, dispatch " + Measured.DispatchMs.ToString("0.0") + " ms, async build " + asyncMs.ToString("0") + " ms, links " + Measured.LinkMs.ToString("0.0") + " ms, total " + Measured.TotalWallMs.ToString("0") + " ms");
			return;
		}
		long linkStart = Stopwatch.GetTimestamp();
		foreach (long key in Updating)
		{
			if (Chunks.TryGetValue(key, out ChunkData chunk))
			{
				AddLinks(chunk);
			}
		}
		double main = _updatingMainMs + Since(linkStart);
		double latency = Since(_updatingSince);
		Measured.Updates++;
		Measured.UpdatedChunks += Updating.Count;
		Measured.UpdateMainMs += main;
		Measured.UpdateLatencyMs += latency;
		Measured.LastUpdateLatencyMs = latency;
		Measured.LastUpdateAsyncMs = asyncMs;
		if (main > Measured.UpdateMainWorstMs)
		{
			Measured.UpdateMainWorstMs = main;
		}
		if (latency > Measured.UpdateLatencyWorstMs)
		{
			Measured.UpdateLatencyWorstMs = latency;
		}
		Updating.Clear();
	}

	// The graph rebuilt its places round these voxels (and their branches one further): the places within two voxels
	// are looked up again and their chunks are made anew.
	internal static void OnVoxelsChanged(HashSet<XYZ> voxels)
	{
		if (!Active || _graph == null || voxels == null || voxels.Count == 0)
		{
			return;
		}
		long start = Stopwatch.GetTimestamp();
		if (Dirty.Count == 0)
		{
			_dirtySince = start;
		}
		foreach (XYZ voxel in voxels)
		{
			for (int dx = -2; dx <= 2; dx++)
			{
				for (int dz = -2; dz <= 2; dz++)
				{
					for (int dy = -2; dy <= 2; dy++)
					{
						XYZ p = new XYZ(voxel.x + dx, voxel.y + dy, voxel.z + dz);
						long key = KeyOf(p);
						bool present = _graph.TerrainNodes.TryGetValue(p, out TerrainPathNode node) && node != null && !node.IsEmpty;
						if (!Chunks.TryGetValue(key, out ChunkData chunk))
						{
							if (!present)
							{
								continue;
							}
							chunk = new ChunkData { X = FloorDiv(p.x), Z = FloorDiv(p.z) };
							Chunks[key] = chunk;
						}
						if (present)
						{
							chunk.Nodes.Add(p);
						}
						else
						{
							chunk.Nodes.Remove(p);
						}
						if (_initial)
						{
							Queue(key);
						}
						else
						{
							Dirty.Add(key);
						}
					}
				}
			}
		}
		Measured.Marks++;
		Measured.MarkMs += Since(start);
	}

	// Whether the place is one a bricktron stands on: a walk (not only breaking through) leads from it. A ladder
	// block's place, with only ladder branches, is not: it is a 1 x 1 floor between two others 1 block apart, which
	// Unity drops as a ledge on every side, so its links would end nowhere. Ladders become one link from each place the ladder is entered at to each place it is left at.
	internal static bool IsStandable(TerrainPathNode node)
	{
		// A full voxel's place (a block, for breaking into it) only has walks out of it: the game's walks lead only
		// into open voxels, and the place to stand on top of a block is the empty voxel above it, when it has the
		// headroom. Its top as a floor let routes onto blocks under a low ceiling.
		if (node == null || node.IsEmpty || node.siblings == null || (node.niveaux & 0x3333) == 0x3333)
		{
			return false;
		}
		foreach (PathBranch branch in node.siblings)
		{
			if (branch.Type != PathBranch.BranchType.Breaking && branch.Type != PathBranch.BranchType.Ladder)
			{
				return true;
			}
		}
		return false;
	}

	private static readonly Queue<KeyValuePair<TerrainPathNode, int>> LadderOpen = new Queue<KeyValuePair<TerrainPathNode, int>>();

	private static readonly Dictionary<TerrainPathNode, int> LadderSeen = new Dictionary<TerrainPathNode, int>();

	// Up a ladder from a place on the mesh: the places on the mesh at the other end of the run of ladder places,
	// each with the sum of the ladder branches' weights to it.
	private static void LadderExits(TerrainPathNode from, TerrainPathNode first, int weight, List<KeyValuePair<TerrainPathNode, int>> exits)
	{
		exits.Clear();
		LadderOpen.Clear();
		LadderSeen.Clear();
		LadderOpen.Enqueue(new KeyValuePair<TerrainPathNode, int>(first, weight));
		LadderSeen[first] = weight;
		while (LadderOpen.Count > 0 && LadderSeen.Count < 128)
		{
			KeyValuePair<TerrainPathNode, int> here = LadderOpen.Dequeue();
			if (IsStandable(here.Key))
			{
				if (here.Key != from)
				{
					exits.Add(here);
				}
				continue;
			}
			foreach (PathBranch branch in here.Key.siblings)
			{
				if (branch.Type != PathBranch.BranchType.Ladder || !(branch.Node is TerrainPathNode next) || next.IsEmpty || LadderSeen.ContainsKey(next))
				{
					continue;
				}
				int total = here.Value + branch.Weight;
				LadderSeen[next] = total;
				LadderOpen.Enqueue(new KeyValuePair<TerrainPathNode, int>(next, total));
			}
		}
	}

	private static readonly List<KeyValuePair<TerrainPathNode, int>> Exits = new List<KeyValuePair<TerrainPathNode, int>>();

	// The height of the place's top at its centre, in world units.
	internal static Vector3 TopCentre(TerrainPathNode node)
	{
		XYZ p = node.position;
		return new Vector3(p.x, p.y + Voxel.ComputeSurfaceHeightFromNiveaux(node.niveaux, 0f, 0f), p.z);
	}

	// An obstacle some units may pass (a door lets its own faction through): the game's presence test asks the volume
	// each time; here it is an area of its own, which a query includes for the faction it would let through. Doors
	// of several factions would need an area each (or the faction's doors as a query cost).
	private static bool IsDoor(XYZ p)
	{
		foreach (IVolume volume in VolumeRegistry.VolumesWithPropertyAt(p, VProperty.obstacle))
		{
			if (volume != null && volume.PassThrough != null)
			{
				return true;
			}
		}
		return false;
	}

	private static float Corner(int niveaux, int shift)
	{
		return ((niveaux >> shift) & 3) / 3f - 0.5f;
	}

	private static void MakeMeshes(ChunkData chunk)
	{
		Measured.Quads -= chunk.Quads;
		chunk.Quads = 0;
		chunk.MinY = int.MaxValue;
		chunk.MaxY = int.MinValue;
		List<Vector3>[] vertices = Vertices;
		for (int i = 0; i < vertices.Length; i++)
		{
			vertices[i].Clear();
			Triangles[i].Clear();
		}
		foreach (XYZ p in chunk.Nodes)
		{
			if (!_graph.TerrainNodes.TryGetValue(p, out TerrainPathNode node) || !IsStandable(node))
			{
				continue;
			}
			int kind = 0;
			if (node.ObstacleCount > 0 && TestPathfinding.IsAPermanentObstacle(p))
			{
				kind = IsDoor(p) ? 3 : 2;
			}
			else if (TestPathfinding.IsSemiWalkableNatureBlocker(p))
			{
				kind = 1;
			}
			int n = node.niveaux;
			List<Vector3> v = vertices[kind];
			List<int> t = Triangles[kind];
			int b = v.Count;
			// Corners: bits 0 (-x, +z), 4 (+x, +z), 8 (+x, -z), 12 (-x, -z); split along (-x, +z) to (+x, -z), as the
			// game's ComputeSurfaceHeightFromNiveaux. Wound so the faces look up.
			v.Add(new Vector3(p.x - 0.5f, p.y + Corner(n, 0), p.z + 0.5f));
			v.Add(new Vector3(p.x + 0.5f, p.y + Corner(n, 4), p.z + 0.5f));
			v.Add(new Vector3(p.x + 0.5f, p.y + Corner(n, 8), p.z - 0.5f));
			v.Add(new Vector3(p.x - 0.5f, p.y + Corner(n, 12), p.z - 0.5f));
			t.Add(b);
			t.Add(b + 1);
			t.Add(b + 2);
			t.Add(b);
			t.Add(b + 2);
			t.Add(b + 3);
			chunk.Quads++;
			if (p.y < chunk.MinY)
			{
				chunk.MinY = p.y;
			}
			if (p.y > chunk.MaxY)
			{
				chunk.MaxY = p.y;
			}
		}
		for (int i = 0; i < chunk.Meshes.Length; i++)
		{
			Mesh mesh = chunk.Meshes[i];
			if (vertices[i].Count == 0)
			{
				if (mesh != null)
				{
					mesh.Clear();
				}
				continue;
			}
			if (mesh == null)
			{
				mesh = new Mesh { name = "NavMeshExperiment " + chunk.X + "," + chunk.Z + " " + i };
				mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
				chunk.Meshes[i] = mesh;
			}
			mesh.Clear();
			mesh.SetVertices(vertices[i]);
			mesh.SetTriangles(Triangles[i], 0, false);
			mesh.RecalculateBounds();
		}
		Measured.Quads += chunk.Quads;
	}

	// One-way links for the graph's climbing, stepping down and ladder branches out of the chunk's places.
	private static void AddLinks(ChunkData chunk)
	{
		foreach (NavMeshLinkInstance link in chunk.Links)
		{
			NavMesh.RemoveLink(link);
		}
		Measured.Links -= chunk.Links.Count;
		chunk.Links.Clear();
		foreach (XYZ p in chunk.Nodes)
		{
			if (!_graph.TerrainNodes.TryGetValue(p, out TerrainPathNode node) || node == null || node.IsEmpty || node.siblings == null)
			{
				continue;
			}
			foreach (PathBranch branch in node.siblings)
			{
				int area;
				if (branch.Type == PathBranch.BranchType.Climbing)
				{
					area = AreaClimb;
				}
				else if (branch.Type == PathBranch.BranchType.StepDown)
				{
					area = AreaStepDown;
				}
				else if (branch.Type == PathBranch.BranchType.Ladder)
				{
					area = AreaLadder;
				}
				else
				{
					continue;
				}
				if (!(branch.Node is TerrainPathNode target) || target.IsEmpty || !IsStandable(node))
				{
					continue;
				}
				if (area == AreaLadder)
				{
					LadderExits(node, target, branch.Weight, Exits);
					foreach (KeyValuePair<TerrainPathNode, int> exit in Exits)
					{
						AddLink(chunk, node, exit.Key, area, exit.Value);
					}
					continue;
				}
				AddLink(chunk, node, target, area, branch.Weight);
			}
		}
		Measured.Links += chunk.Links.Count;
	}

	private static void AddLink(ChunkData chunk, TerrainPathNode node, TerrainPathNode target, int area, int weight)
	{
		Vector3 from = TopCentre(node);
		Vector3 to = TopCentre(target);
		float length = Mathf.Max(0.5f, Vector3.Distance(from, to));
		NavMeshLinkData data = new NavMeshLinkData
		{
			startPosition = from,
			endPosition = to,
			width = 0f,
			bidirectional = false,
			area = area,
			agentTypeID = Settings.agentTypeID,
			// The game's weight per block of flat walking is 10; Unity's cost is per metre.
			costModifier = weight / 10f / length
		};
		NavMeshLinkInstance instance = NavMesh.AddLink(data);
		if (instance.valid)
		{
			chunk.Links.Add(instance);
		}
	}

	// A query filter for a unit: what it may cross, as the game's BranchEvaluator decides it.
	internal static NavMeshQueryFilter Filter(bool canClimb, bool forest, bool obstacles, bool doors)
	{
		NavMeshQueryFilter filter = new NavMeshQueryFilter { agentTypeID = Settings.agentTypeID };
		int mask = (1 << AreaWalk) | (1 << AreaStepDown);
		if (canClimb || forest)
		{
			mask |= (1 << AreaClimb) | (1 << AreaLadder);
		}
		if (forest)
		{
			mask |= 1 << AreaForest;
		}
		if (obstacles)
		{
			mask |= 1 << AreaObstacle;
		}
		if (doors)
		{
			mask |= 1 << AreaDoor;
		}
		filter.areaMask = mask;
		return filter;
	}

	// Unity's main-thread share of a build: meshes it holds and the navigation data's own size.
	internal static long MeshBytes()
	{
		long bytes = 0;
		foreach (ChunkData chunk in Chunks.Values)
		{
			foreach (Mesh mesh in chunk.Meshes)
			{
				if (mesh != null)
				{
					bytes += UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(mesh);
				}
			}
		}
		return bytes;
	}

	internal static long DataBytes()
	{
		return (_data != null) ? UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(_data) : 0;
	}

	internal static bool Busy => _op != null || MeshQueue.Count > 0 || Dirty.Count > 0;
}

// Every batch of changed voxels the route graph is rebuilt for, after the rebuild (the places are as they are now).
[Feature(Features.Pathfinding, Features.PathfindingInfo)]
[HarmonyPatch(typeof(TestPathfinding), "RefreshLod")]
internal static class NavMeshExperimentRebuildPatch
{
	private static void Postfix(HashSet<XYZ> dirtyVoxels)
	{
		if (!NavMeshExperiment.Active)
		{
			return;
		}
		try
		{
			NavMeshExperiment.OnVoxelsChanged(dirtyVoxels);
		}
		catch (System.Exception ex)
		{
			Plugin.Log.LogWarning("NavMeshExperiment: " + ex.Message);
		}
	}
}
#endif
