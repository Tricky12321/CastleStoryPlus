# Unity navigation mesh (experiment)

Whether Unity's own navigation mesh (`UnityEngine.AI`: Recast tiles built on Unity's worker threads, Detour queries
in native code) could take over the game's route search, which runs on the main thread over some 135 000 voxel
places. Unity 6 port only, off by default: `[Pathfinding] NavMeshExperiment = true` builds it when a game is loaded,
or `POST /api/navmesh/start` at any time. Nothing walks by it yet; DevTools measures it against the game's A\*.

Code: `CastleStoryPlus/Pathfinding/NavMeshExperiment.cs` (`#if UNITY6_PLAYER`) and
`CastleStoryPlus.DevTools/Endpoints/NavMeshEndpoints.cs`.

## How it is built

- **From the game's graph, not from the voxels.** Every place of the level-0 graph (`TerrainPathNode`) that a
  bricktron stands on becomes a quad: the voxel's top, with its four corner heights from the node's niveaux (bits 0,
  4, 8 and 12 are the corners (-x,+z), (+x,+z), (+x,-z), (-x,-z), a third of a block each; split along the same
  diagonal as `ComputeSurfaceHeightFromNiveaux`). The graph already holds the bricktrons' rules (2 blocks of headroom,
  1 on stairs, nature blockers, walkable objects such as bridges), so Unity's agent is made small enough to keep every
  quad: height 0.9 (two floors are at least 1 block apart), step 0.6 (the game's walking turns into climbing at an
  edge 2/3 of a block high; denivelation 4), slope 60°, radius 0.25, cell 0.125, tiles of 128 cells (16 blocks).
- "Stands on" means: a walk other than breaking leads out of it, and its voxel is not full. A full voxel's place is a
  block, kept by the graph only for breaking into it and for walking off it; its top as a floor let routes onto
  blocks under a low ceiling (the place to stand on a block is the empty voxel above it).
- **Areas** (Unity has 32 per agent): 0 ground, 3 near trees (the game's `Forest` branches), 4 climb, 5 step down, 6
  ladder, 7 ground under a building's obstacle, 8 ground under a door (an obstacle some units pass).
- **Links** for the graph's `Climbing`, `StepDown` and `Ladder` branches, one-way between the two places' top
  centres, cost = the game's weight / 10 per metre (a flat step is 10). A ladder block's place (a 1 x 1 floor 1 block
  above the next) is dropped by Recast as a ledge on every side, so a run of ladder places becomes one link from each
  place it is entered at to each place it is left at.
- **One mesh per 16 x 16 columns and area** (`NavMeshBuildSourceShape.Mesh`), made on the main thread at most 3 ms a
  frame, then `NavMeshBuilder.UpdateNavMeshDataAsync` builds every tile on Unity's workers.
- **Updates:** the voxels the game rebuilds its graph for (`TestPathfinding.RefreshLod`, postfix) mark the chunks
  within 2 blocks (the graph rebuilds branches one further). Once no build runs, those chunks get new meshes and
  links, and `UpdateNavMeshDataAsync` rebuilds the tiles over them (`preserveTilesOutsideBounds`), with the sources of
  the chunks round them.
- **Queries:** `NavMesh.SamplePosition` at both ends (1 block), then `NavMesh.CalculatePath` with a filter from the
  unit's rules, as the game's `BranchEvaluator` has them: climb and ladder links only for units that may climb,
  forest only for units that walk in forest, doors only for player units, building ground never by default.

## Measured (31-10-2026)

Benchmark save (a big base, 135 000 graph places, island about 370 x 370 blocks), Unity 6 port player (OpenGL Core,
release build) on the developer's machine, with two other test players running at the same time.

| What | Result |
|---|---|
| Mesh | 128 000 quads in 540 chunks, 775 links; Unity's mesh 6 100 polygons-triangles, 13 800 vertices |
| Full build, main thread | index 17-34 ms (one frame), meshes 104-113 ms over 34-37 frames (3.6 ms worst frame), dispatch 0.1-2.8 ms, links 23-34 ms (one frame, `NavMesh.AddLink`) |
| Full build, Unity's workers | 150-172 ms (cell 0.125); 88 ms with cell 0.25 |
| Full build, start to usable | 0.8-1.1 s in a running game, about 4 s while the game is still loading |
| Memory | about 17 MB more resident memory per build (meshes, navigation data, chunk index); `Profiler.GetRuntimeMemorySizeLong` answers 0 in a release player |
| Updates (a quarry dug at 5x speed for 90 s) | 137 updates over 402 chunks; main thread 1.0 ms per update (3.3 ms worst), 0.05-0.13 ms per graph rebuild to mark chunks; from the graph's rebuild to the new tiles 87 ms on average, 169 ms worst (Unity's own part 10-47 ms) |
| A wall built across a path | rerouted in the next update; the mesh's route round the wall's end is 25.8 blocks against the A\*'s 33.1 |
| Query (200 random pairs within 150 blocks) | mesh 0.23 ms mean, 0.13 median, 0.63 p90, 1.4 max (both ends sampled and the path), against the game's A\* run in one go 9.1 ms mean, 8.8 median, 16.7 p90, 24 max (the game spreads it over frames, 100 nodes a slice) |
| Route | 6 corners on average (A\*: 183 voxel steps); 18% shorter than the A\*'s route (mesh / A\* length 0.82 mean, 0.80 median), as the mesh goes straight across open ground instead of along the voxel grid |
| Agrees with the whole graph (a Dijkstra over every place, same rules) on whether the goal is reached | mesh 99% (195 of 197), A\* 94%: the game's A\* (coarse levels first) fails 13 of 197 routes the graph has |

The frame time while Unity's workers build could not be judged: other players shared the machine, and Unity's own
navigation markers are not in a release player. Measure it with the profiling player (`Build/Profiling/Linux`).

Fixed while measuring: ladder places (dropped by Recast; now chained into links), block places (routes onto blocks
under a ceiling), doors (an area of their own; before that every route through a gate failed).

### DevTools

| Endpoint | What it does |
|---|---|
| `GET /api/navmesh?triangles=` | phase, agent shape, the build's and the updates' times, frames with work, memory; `triangles=true` counts Unity's mesh |
| `POST /api/navmesh/start?radius=&height=&climb=&slope=&voxel=&tile=` | builds it again with that agent shape |
| `POST /api/navmesh/stop` | removes it |
| `GET /api/navmesh/path?to=&from=&unit=&enemy=&obstacles=&shortest=&nodes=` | one route over the mesh next to the game's A\* (and with `shortest=true` the whole graph's answer): time, length, corners |
| `GET /api/navmesh/bench?count=&radius=&seed=&unit=&enemy=&obstacles=&samples=` | random pairs of places round the bricktron, one pair a frame: the three answers, times, lengths, agreement, and the pairs they disagree on |

## What is missing before anything walks by it

- **Obstacles that change without a terrain signal.** Buildings and doors register their volumes
  (`TestPathfinding.RegisterVolume`/`UnregisterVolume`) without a graph rebuild; their chunks must be marked too.
  Units on a voxel (`VProperty.character`) are ignored, as `PassThroughAllies` lets allies walk through each other.
- **Doors of several factions.** One area marks every door; a door that lets only its own faction through needs an
  area per faction (Unity has 32 areas) or the other factions' doors as a high query cost.
- **Breaking (enemies).** Corruptrons that break their way route through blocks (the graph's `Breaking` branches,
  cost by the block's health). A mesh of floors cannot say that. Either a second agent type whose mesh also has every
  breakable block's top as an area with a cost per block (walls become floor with a price), or the enemies keep the
  A\*. Enemies that do not break could use the workers' mesh with their own filter.
- **Ladders changed far away.** A ladder's link reaches up the whole ladder; a change at its top marks only the chunks
  near the top, so a long ladder's bottom chunk may keep a stale link until it is rebuilt itself.
- **Queries on the main thread.** `CalculatePath` is synchronous; at 0.2 ms it is 40 times cheaper than the A\* and
  needs no slicing over frames. Moving it off the main thread needs `Unity.Experimental.AI.NavMeshQuery` (jobs,
  experimental) or a native Detour query; not needed at these times.
- **Unit size.** Radius 0.25 keeps the corner points 0.25 to 0.4 blocks from a wall's corner. Bricktrons walk
  1-block corridors, which a much larger radius erodes to nothing, so the walker has to accept corners this close.

## How walking would follow it (Locomotion4)

Locomotion4 walks the game's route as a list of voxel places and occupies each voxel it enters (`VoxelPresence`),
with climbing and stepping down played as their own moves (target matching). The least invasive way:
1. Ask the mesh for the route (`CalculatePath`), then walk straight from corner to corner on the horizontal plane,
   with the height taken from the voxels under the feet (`GetSurfaceHeightDelta`) instead of the mesh's polygons
   (Recast heights are approximate).
2. Turn each straight segment back into the voxels it crosses (a 2D line walk, 4-connected) and hand those to the
   existing walker as its path nodes: occupancy, the game's per-voxel checks (`PopOuterOfObstacle`, blocked paths and
   their repath) and the animations stay as they are, only the line between them is straight.
3. A corner pair that is a link's two ends (kept in a dictionary by position) is played as the game's climb, step
   down or ladder move between those two places.
4. A route the mesh has but the walk cannot follow (a stale tile, a unit in a doorway) falls back to the A\* for that
   walk.

## Assessment

Unity's navigation mesh answers the same reachability as the game's graph (99%, better than the game's own A\*), 40
times faster per route, with routes about a fifth shorter and straight, while its build runs on Unity's workers and
costs the main thread about 3 ms a frame for half a second when built from scratch and about 1 ms per terrain change.

Recommendation: do not replace the A\* in one step. Use the mesh for player units (workers, soldiers that do not
break) first, with the gaps above closed (volume signals, doors per faction) and walking as described, the A\* staying
as the fallback and for enemies that break their way. The ReachMap flood (up to 0.4 ms a frame) is a candidate to
follow: Unity does not expose the mesh's connected regions, but a route query to each candidate is cheap enough to
ask per task instead of keeping a map.
