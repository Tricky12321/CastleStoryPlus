# Unity navigation mesh

Whether Unity's own navigation mesh (`UnityEngine.AI`: Recast tiles built on Unity's worker threads, Detour queries
in native code) could take over the game's route search, which runs on the main thread over some 135 000 voxel
places. Unity 6 port only, on by default: `[Pathfinding] NavMeshExperiment` builds it when a game is loaded (or
`POST /api/navmesh/start` at any time), and `[Pathfinding] NavMeshRoutes` (also on by default) routes the
player's own units are routed over it (see [Routes for the player's units](#routes-for-the-players-units-navmeshroutes));
DevTools measures it against the game's A\*.

Code: `CastleStoryPlus/Pathfinding/NavMeshExperiment.cs`, `CastleStoryPlus/Pathfinding/NavMeshRoutes.cs` (both
`#if UNITY6_PLAYER`), `CastleStoryPlus.DevTools/Endpoints/NavMeshEndpoints.cs` and
`CastleStoryPlus.DevTools/Tests/NavMeshRouteTests.cs`.

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
  ladder, 8 a stockpile's pallets, 9 to 31 ground under a door, one area per faction that owns doors.
  - Ground under a building (an obstacle with the null pass-through, which no unit's search crosses) is left out of
    the mesh. As an area of its own (7) it bordered the walkable ground with no agent radius in between, so routes
    ran along a building's very edge and, on the voxels, through its first row.
  - Doors (`Porte`): the game lets a unit through a door that is open, or not locked and of an allied or neutral
    faction (`Porte.CanPassThrough`; enemies that break their way may search through any). The mesh keeps one area
    per door-owning faction (given out as doors are met, 23 at most; more are left out like buildings), and a
    unit's filter takes the door areas of the factions that are its friends (`Affiliation.IsAlliedOrNeutral`).
    Whether a door is locked or held shut is not in the mesh: the route's voxels are checked against the door by the
    unit's own branch evaluator, which asks the door, and a route through a door it may not pass is left to the A\*.
  - Pallets (`PassThroughStockpile`) are walkable only while empty, and nothing signals when that changes, so they
    are an area with cost 3 (routes keep off them where they can) and a route over a full one is mended on the graph
    or left to the A\*.
  - Other obstacles everybody passes (a plain `PassThroughObstacle`) are ground.
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
  - Obstacles change without a graph rebuild: a building, door or other volume placed, built (a blueprint becoming
    a building registers its volume again) or removed only changes its places' obstacle counts. Every volume
    registered or unregistered (`VolumeRegistrator.RegisterVolume`, `UnregisterVolume_Internal`) marks the chunks
    of its obstacle voxels.
  - Links reach out of their chunk (a climb onto the next chunk, a ladder up a whole wall). Each chunk keeps the
    chunks its links pass through; when one of those changes, the owner's links are made again too (without new
    meshes), so a ladder whose top changed no longer keeps a stale link at its bottom.
- **Queries:** `NavMesh.SamplePosition` at both ends (1 block), then `NavMesh.CalculatePath` with a filter from the
  unit's rules, as the game's `BranchEvaluator` has them: climb and ladder links only for units that may climb,
  forest only for units that walk in forest, the door areas of the unit's friends, pallets at their cost.

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
| `POST /api/navmesh/routes?enabled=&reset=` | switches `NavMeshRoutes` on or off for the session and/or zeroes its counters; answers with them |

`GET /api/navmesh` also has `routes` (the routes made over the mesh: attempts, routed, fallbacks and why, time per
route, voxels per route, stretches mended, climbs, steps down and ladders taken, the last 40 fallbacks) and, under
`updates`, `volumeMarks` (volumes registered or removed) and `relinkedChunks` (chunks whose links were made again for
a change elsewhere). `/api/path/stats` counts a search the mesh routed as outcome `navmesh`. In-game tests
`NavMeshRouteTests` (a walk routed by the mesh, a wall built across the way, a ledge climbed).

## Routes for the player's units (NavMeshRoutes)

`[Pathfinding] NavMeshRoutes` (on by default; needs `NavMeshExperiment`): the route searches of the player's own
units are answered by the mesh, and the game's A\* stays for enemies and as the fallback.

- **Which searches.** A `SearchPathRequest` of a unit whose faction is not the AI's, that does not break its way
  (`CanBreakVoxels`) and does not search away from something (`SearchAway`). The request is set up as usual
  (`InitSearch`); its first slice (`Work`, after the frame budget and the DevTools measurement) asks the mesh.
- **Goal.** The places of the graph round the target where the game's own search would stop
  (`Location.TargetReached`: an access voxel of a building, the voxel itself, a voxel next to a unit), in the same
  bounds as the A\*'s goal (the object's volume and one voxel round it), nearest to the unit first; a route is asked
  to up to 5 of them.
- **Corners to voxels.** `CalculatePath` gives corners; each stretch between two corners becomes the voxels a
  straight line passes through (4-connected), each reached by a branch of the graph from the one before, at the height
  the mesh has there, and one the unit's own `BranchEvaluator` accepts (branch types it may use, doors it may pass,
  units in the way). A line that cuts a corner closely goes round it through the other voxel beside it. A corner
  where the route bends is moved 0.15 block away from the bend first: where two areas meet (forest, a pallet, a door
  of a foe) Unity keeps no distance from the border, and the line along it ran through the voxels on the far side as
  often as not. A pair of corners that is one of the mesh's links becomes that branch of the graph (climbing,
  stepping down) or the run of ladder branches to the exit (a search over ladder branches only).
- **Mending.** Where the line is blocked (a full pallet, a unit, a door the unit may not pass) the shortest way over
  the graph to a voxel of the line up to 16 further on is searched, within 10 blocks and 400 places, by the unit's
  rules. Only when that fails is the request left to the A\*.
- **Result.** The voxels up to the first one that reaches the target become the search's result, a chain of the
  graph's own links (`PathLink`, the result accepter's `Result`), and the search is done; the finer search
  (`LoopImprovePath`) is skipped. The rest is the game's: `PathLodUnroller` unrolls it (nothing to unroll at the
  finest level), `PathUnroll` checks it, `StraightPaths` pulls it straight and `Locomotion4` walks it.
- **Fallback.** No route on the mesh, a goal or origin off the mesh, a voxel with no branch, a link gone stale, a
  blocked line with no way round: the request goes on as the game's A\* search, as if nothing had happened.

### Measured (31-10-2026)

Unity 6 port player (OpenGL Core, release), A/B windows of 150 s on one running game, `NavMeshRoutes` switched
between windows, another test player running on the same machine (frame rates are noisy).

Benchmark save (one faction, 41 bricktrons, digging, building, a stockpile field, 7 gates, ladders):

| | routes off (2 windows) | routes on (2 windows) |
|---|---|---|
| route searches | 290, 335 | 337, 349 |
| search CPU on the main thread | 2.59 s, 2.63 s | 0.57 s, 0.22 s |
| CPU per search (mean / p95) | 8.9 / 37 ms, 7.9 / 40 ms | 1.7 / 17 ms, 0.6 / 1.2 ms |
| frames until the route is there (p50 / p95) | 14 / 266, 6 / 275 | 2 / 109, 2 / 2 |
| routed by the mesh | | 305 of 337 (9.5% to the A\*), 338 of 349 (3.2%) |
| mesh route time (mean / worst) | | 0.32 / 2.0 ms, 0.29 / 1.6 ms |
| route length per distance (mean) | 1.37, 1.29 | 1.19, 1.22 |
| stuck units (walking 20 s without moving a voxel) | 0, 0 | 0, 0 |
| units waiting or without a task (share of samples) | 0.578, 0.574 | 0.585, 0.579 |
| walks the game found blocked | 42, 44 | 33, 58 |

Another session on the same save: of 380 mesh routes 356 were routed (6.3% to the A\*), with 324 climbs, 175 steps
down and 99 ladder runs taken; the in-game tests `StraightPathTests` (pillar, wall built on the line, pit dug on the
line) and `PathfindingTests.DetourAroundWall` passed with the mesh routing.

70-unit world (140 units in all, 70 of them an enemy army whose searches stay with the A\*):

| | routes off | routes on |
|---|---|---|
| route searches (player side / enemies) | about 1 200 to 1 400 / 3 500 | 1 351 / 3 556 |
| routed by the mesh | | 1 038 of 1 312 (21% to the A\*) |
| main-thread search CPU (all) | 27.5 s, 30.1 s | 28.4 s, 30.1 s |
| stuck units | 0, 0 | 0, 0 |

Here the enemies' searches are three quarters of the search CPU, and of the player side's fallbacks nearly all were
"no route on the mesh" for targets the A\* did not reach either (its search ran on and was abandoned: 216 searches,
most of the player side's remaining 9.3 s). Frame times went up and down with the machine's load in both modes (in
4 windows of 60 s: 28.7 and 25.9 fps off, 24.5 and 28.1 on; 1% lows 10.8 and 9.6 off, 15.5 and 16.5 on); no frame
cost of the routing itself shows in the frame's systems.

Mesh updates while the game runs (both saves): 0.9 ms main thread per update (3.2 ms worst), 25 to 60 ms from the
graph's change to the new tiles (190 ms worst on the big world).

## What is missing

- **Doors' state.** A locked door, or one held shut (`ForceClose`), is still its faction's door area; a route
  through it is refused at the voxels and mended or left to the A\*. Lock changes could mark the door's chunk (a
  closed-door area), so the mesh routes round it.
- **Pallets.** Whether a pallet is empty is not in the mesh (no signal); a route over a full stockpile field with no
  way round within 10 blocks goes to the A\* (most of the benchmark save's fallbacks).
- **Unreachable targets.** The mesh agrees with the whole graph on reachability (99%); a target it has no route to is
  still handed to the A\*, which searches on until it gives up. Failing those requests at once (exact ones, mesh not
  busy) would save most of the player side's remaining search CPU on the 70-unit world.
- **Breaking (enemies).** Corruptrons that break their way route through blocks (the graph's `Breaking` branches,
  cost by the block's health). A mesh of floors cannot say that. Either a second agent type whose mesh also has every
  breakable block's top as an area with a cost per block (walls become floor with a price), or the enemies keep the
  A\*. Enemies that do not break could use the workers' mesh with their own filter.
- **Queries on the main thread.** `CalculatePath` is synchronous; at 0.3 ms a route (corners to voxels included) it
  needs no slicing over frames. Moving it off the main thread needs `Unity.Experimental.AI.NavMeshQuery` (jobs,
  experimental) or a native Detour query.
- **The repath while walking** (`Locomotion4.RepathMoveNext`, a blocked walk searching the rest of its route again)
  is still the game's own search.
- **Unit size.** Radius 0.25 keeps the corner points 0.25 to 0.4 blocks from a wall's corner. Bricktrons walk
  1-block corridors, which a much larger radius erodes to nothing.

## Assessment

Unity's navigation mesh answers the same reachability as the game's graph (99%, better than the game's own A\*), 40
times faster per route, with routes about a fifth shorter and straight, while its build runs on Unity's workers and
costs the main thread about 3 ms a frame for half a second when built from scratch and about 1 ms per change.

With `NavMeshRoutes` the player's units get their routes in 2 frames instead of 6 to 14 (median), the search CPU of a
one-faction base drops by 80 to 90%, and in the runs above no unit got stuck and as many worked. Both switches are on
by default since 31-10-2026. On the Benchmark save (6 runs of 4 minutes, OpenGL Core and Vulkan) 91 to 95% of the
requests were routed by the mesh and the search CPU fell from 3.5 s to 0.5 s per run, while the frame rate stayed the
same within the runs' spread (OpenGL 1x 170 and 169 fps with routes against 177 and 170 without): the game's A\* was
already spread over many frames, about 0.14 ms a frame. Still open: 3 to 21% of the requests go to the A\* (full
stockpile fields, unreachable targets), a door's locked state is not in the mesh, and in an enemy-heavy world the
enemies' searches, which keep the A\*, are most of the cost.
