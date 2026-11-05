# Roadmap

Features planned for Castle Story Plus over time. Nothing here is built yet. When a feature is done, it moves to
`CHANGELOG.md` and leaves this list.

## Blueprints

### Recipes kept in blueprints

- A blueprint remembers the crafting setup of the buildings in it: the recipes queued at each workshop (chopping block,
  furnace, forge, loom, research station, ...) and their settings (repeat, priority).
- When the blueprint is placed and a workshop of it is built, the workshop starts with those recipes, so a copied base
  produces at once without setting every workshop up again.
- To decide: whether placing a blueprint over an existing workshop changes its recipes, and how blueprints saved before
  this (without recipes) behave.

## Resource priorities

- A priority per resource (for example stone, iron, orange crystal, coal, wood) from 1 to 5, set by the player: wood at
  5 and coal at 1 means workers fetch, gather and carry wood before coal.
- Used where workers choose between resources: quarries and mining (which ore to dig first), clean-up and hauling to
  the stockpiles, gathering (trees, rocks), and workshops waiting for ingredients.
- Set in a small table in the in-game menu (next to the resource list), saved with the world, per team in multiplayer.
- To decide: whether priority 0 means "never", how it mixes with the task priorities and the distance to the resource,
  and whether QuarryLimit's per-resource limits move into the same table.

## Animals

The treasure beasts (Spirestalker, Cindermane, Boulderback, Ironhorn) are the starting point. Today they are corruptron
and biftron units with their own models and animations, guarding chests.

### Taming

- Make a wild beast the player's own, for example by feeding it, or by beating it without killing it.
- A tamed beast changes faction. It keeps its model, and gets a name and an icon in the player's lists.
- To decide: which beasts can be tamed (all kinds, or only some), what taming costs, and whether a beast can turn wild
  again.

### Houses and stables

- New buildings that house tamed animals: a stable (horse-like beasts), an enclosure or pen, and a den.
- A home gives the animals a place to rest and heal, and counts towards how many animals the player can keep.
- Built from blueprints like the mod's other custom buildings (see `docs/CustomBuildings.md`).

### Training

- Train tamed animals at their home or at a training ground to give them a role. Ideas:
  - **Mount:** a knight rides it.
  - **Pack animal:** it carries resources.
  - **Guard:** it fights alongside knights.
- Training takes time and resources, and raises the animal's level, like the bricktrons' upgrades.

## Portals

- A building that teleports bricktrons between two places.

### Linked portals in one world

- Two portals are linked. A bricktron that walks into one comes out of the other.
- The route search uses the link as a shortcut, so workers and armies take it when it is shorter.
- To decide: what portals cost to build and to run (energy, crystals), how many bricktrons can go through at once,
  whether enemies can use them, and whether they can be destroyed.

### Portals to other worlds

- A portal that sends bricktrons (and what they carry) to another world, for example another save or a random world.
- To decide:
  - how the other world is chosen;
  - what happens to the bricktrons that travel there (taken out of this save and added to the other one);
  - whether it works in multiplayer.

## Roads

- Stone roads that bricktrons walk faster on.
- Built like a floor of flat stone pieces on the ground, from stone blocks, in the build menu or as a task dragged along
  a line.
- Walking on a road is faster, for example 30-50% (a config setting). The route search counts road nodes as cheaper,
  so workers and armies choose roads when they are quicker overall.
- A road keeps its tiles from wearing into worn paths (WornPaths), and grass does not grow over it.
- To decide: what a road costs per tile, whether enemies get the bonus too, whether roads can be damaged, and whether
  wooden walkways and bridges count as roads.

## Building progress

- A building grows a little every time a worker delivers resources to its blueprint, as a slow building animation,
  instead of appearing all at once when the last resource is in.
- The share of the cost delivered decides how much stands: first the plinth or bottom layer, then walls or frame,
  last the roof, rising from the ground up. The part not yet built stays as the blueprint ghost.
- Each delivery plays a short animation where the new part rises or fades in, with a little dust and the hammer sound.
- Works for the game's buildings (stockpiles, workshops, warehouses, doors) and the mod's own (market, research
  station, stands); block blueprints keep how they work now.
- To decide: whether the parts are cut from the finished model by height or each building gets its own stages, whether
  the hammering time (not only the delivery) also moves it on, whether a half built building can be used or attacked,
  and what happens to the shown progress when resources are taken back (demolish, cancel).

## Weather

### Rain

- Rain is a natural event: it starts at random, lasts a while (for example a few game hours), and comes about every
  3 in-game days on average.
- While it rains, grass spreads faster onto bare earth (WornPaths), worn paths grow back faster, and plants seed and
  grow faster (WildPlants).
- Shown with rain falling, a darker sky and a rain sound; a short message when it starts and stops.
- The host decides when it rains and tells the other players; the weather is saved with the world.
- To decide: whether rain slows bricktrons or puts out fires, and whether it also comes in winter and desert biomes
  (snow, or no rain).

## Enemies

### Mega corruptron

- A new, rare enemy: a huge corruptron, several times the size and health of a normal one, that comes with the
  later invasion waves (for example from wave 10, or as the last enemy of every fifth wave).
- It breaks through walls and blocks faster than other corruptrons, and knocks bricktrons back when it hits them.
- Slow, so the players can see it coming (the wave arrow and a warning when it spawns), and it shows a health bar.
- When killed it drops a big soul (much crystal energy for the killer's crystal) and some rare loot.
- Built on the mod's giant bricktron (GiantBricktron: a bricktron made larger) and the guardians' own models and
  health bars (`docs/Guardians.md`).
- To decide: from which wave and how often, whether it can be stunned or slowed, and whether it has its own attack
  (for example a ground slam that hits all round it).

### Guardian sounds

- The treasure guardians (Spirestalker, Cindermane, Boulderback, Ironhorn) make no sounds of their own today. Give each
  one its own sounds: an idle call now and then, a roar when it spots bricktrons, footsteps that fit its size and gait,
  attack and hit sounds, and a death sound.
- The material should be heard too: crystal chimes for the Spirestalker, crackling embers for the Cindermane, grinding
  stone for the Boulderback, clanking iron for the Ironhorn.
- Played in the world like the game's other unit sounds (positioned, quieter with distance), and part of the guardian
  rules in `docs/Guardians.md` so every new guardian gets them.
- To decide: where the sounds come from (made for the mod or from a free library with a licence that allows it; never
  the game's own files shipped), and how they are played next to the game's Wwise sound banks.

## Research

### Sprint

- A new colony upgrade at the research station: bricktrons can sprint for a short while, then the sprint goes on
  cooldown.
- While sprinting a bricktron walks much faster, for example +50% for 5 seconds, then cannot sprint again for
  30-60 seconds (config settings). Higher tiers make the sprint longer or the cooldown shorter.
- Paid in dark crystals and researched like the other colony upgrades (free in sandbox); counts for the whole team and
  is kept in the save.
- Stacks with Light Boots and roads.
- To decide: whether the player triggers it (a key or a button for the selected bricktrons, for example to flee or
  reach a breach) or it starts by itself (soldiers charging, workers fleeing enemies), whether it costs stamina or only
  has a cooldown, whether it shows on the bricktron (dust trail, a small bar), and whether enemies can sprint too.

## Achievements

- More achievements beside the game's own: the mod's features get their own (for example trade 100 times at the
  market, research every colony upgrade, survive wave 20, kill a guardian or a mega corruptron, tame an animal, build
  a road between two bases, flatten a large area).
- Shown in a list in the menu with name, text, icon and progress (for example 37/100 trades), locked ones greyed out,
  and a short message with a sound when one is unlocked.
- Kept per player in the mod's own file, not in the save and not on Steam, so saves stay loadable without the mod.
- To decide: whether they also show on Steam (the game's Steam app cannot take new ones), whether sandbox and cheats
  count, whether they count in multiplayer for every player or only the host, and whether some unlock a reward (a
  banner colour, a bricktron hat).
