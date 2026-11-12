using System;
using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.External.Factories.Templates;
using Brix.Game.AI;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Lua;
using Brix.Transactions;
using Brix.UI.Icons;
using CastleStoryPlus.Building;
using CastleStoryPlus.Core;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Army;

// Every soldier kit is one item. The game's kit recipes queued one order per piece (sword, shield, helmet...), so the
// pieces were made, carried and stored apart, and a stand short of one helmet could not equip anybody. Now a kit
// recipe makes one bundle (Knight kit, Archer kit...) with the summed cost and work of its pieces; it is carried and
// stored on weapon stands as one item. A bricktron that equips a bundle unpacks it into the pieces (KitBundleGetKitPatch),
// one that hands in a whole kit packs it again (KitBundleStorePatch), and loose whole kits on a stand are packed by
// the host (KitBundlePacker), so racks from older saves turn into bundles too.
// A bundle is a copy of the fabric pile, tinted per class, holding one of six resource types the game never uses
// (kokiri and slinger gear: no item, recipe or soldier class has them; the weapon stand already has room for them).
// The items, their names, the Lua values and the unpacking are always there, also with the feature switched off, so
// a save with bundles still loads and its bundles are still handed out. The recipes, the packing and the stand's
// room for bundles come with the feature.
[Feature]
internal static class KitBundles
{
	internal sealed class Bundle
	{
		public Occupation.Job Job;

		// Item name in the BuildResources factory.
		public string Name;

		public string DisplayName;

		// The spare resource type the bundle holds, and the code name of its adjective info.
		public Ressource Resource;

		public string CodeName;

		// The kit's pieces (the game's own lists, so a kit list is found by reference) and the recipes making them.
		public List<Ressource> Pieces;

		public RecipeInfo[] PieceRecipes;

		public LuaCrafting.LuaResource LuaResource;

		public string LuaName;

		public LuaCrafting.LuaRecipe LuaRecipe;

		public IconKey Icon;

		// Containers that hold this much of the kit's main piece hold as many bundles.
		public Ressource MirrorFrom;

		// The workshop that makes the kit.
		public string Station;

		public string Info;

		public Color Tint;

		public Factory.AssetKey Key;

		public RecipeInfo Recipe;
	}

	internal const string FactoryName = "BuildResources";

	// The fabric pile: a plain item carried in the hands, without a tool or a terrain type of its own.
	private const string SourceItem = "Fabric";

	// Lua resource values: past the mod's other ones (Metallurgy 100 and 101, DarkCrystals 102).

	internal static readonly Bundle[] All = new Bundle[]
	{
		Make(Occupation.Job.Knight, 103, "KnightKitBundle", "Knight kit", Adjectif.kokiriSword, "KokiriSword", "KnightKit", LuaCrafting.LuaRecipe.KnightKit, IconKeys._Knight_Kit, Adjectif.sword, "Forge",
			"A knight's sword, shield and helmet in one bundle.", new Color(0.8f, 0.85f, 1f), CookBook.Sword, CookBook.Shield, CookBook.Helmet),
		Make(Occupation.Job.Archer, 104, "ArcherKitBundle", "Archer kit", Adjectif.kokiriShield, "KokiriShield", "ArcherKit", LuaCrafting.LuaRecipe.ArcherKit, IconKeys._Archer_Kit, Adjectif.bow, "Workbench",
			"An archer's bow, quiver and cap in one bundle.", new Color(0.65f, 1.1f, 0.55f), CookBook.Bow, CookBook.Quiver, CookBook.Cap),
		Make(Occupation.Job.Arbalist, 105, "ArbalistKitBundle", "Arbalist kit", Adjectif.kokiriHelmet, "KokiriHelmet", "ArbalistKit", LuaCrafting.LuaRecipe.ArbalistKit, IconKeys._Arbalist_Kit, Adjectif.crossbowWeapon, "MachineShop",
			"An arbalist's crossbow, pouch and kepi in one bundle.", new Color(1.2f, 0.85f, 0.5f), CookBook.CrossbowWeapon, CookBook.CrossbowPouch, CookBook.CrossbowMorion),
		Make(Occupation.Job.Alchemist, 106, "AlchemistKitBundle", "Alchemist kit", Adjectif.slingerWeapon, "SlingerWeapon", "AlchemistKit", LuaCrafting.LuaRecipe.AlchemistKit, IconKeys._Alchemist_Kit, Adjectif.alchemistPouch, "Lab",
			"An alchemist's backpack and bonnet in one bundle.", new Color(0.9f, 0.6f, 1.2f), CookBook.AlchemistPouch, CookBook.AlchemistHat),
		Make(Occupation.Job.Artificer, 107, "ArtificerKitBundle", "Artificer kit", Adjectif.slingerProp, "SlingerProp", "ArtificerKit", LuaCrafting.LuaRecipe.ArtificerKit, IconKeys._Artificer_Kit, Adjectif.geomancerWeapon, "Altar",
			"An artificer's staff, satchel and hat in one bundle.", new Color(1.3f, 1f, 0.45f), CookBook.Staff, CookBook.Sachel, CookBook.ArtificerHat),
		Make(Occupation.Job.Halberdier, 108, "HalberdierKitBundle", "Halberdier kit", Adjectif.slingerHat, "SlingerHat", "HalberdierKit", LuaCrafting.LuaRecipe.HalberdierKit, IconKeys._Halberdier_Kit, Adjectif.halbard, "Workbench",
			"A halberdier's poleaxe and kettle hat in one bundle.", new Color(1.2f, 0.55f, 0.5f), CookBook.Poleaxe, CookBook.KettleHat)
	};

	// Pieces spawned by the last unpacking, for KitBundleEquipPatch (pieces that could not be equipped go back).
	internal static readonly List<GameObject> Unpacked = new List<GameObject>();

	private static bool _luaDone;

	private static bool _recipesDone;

	private static Bundle Make(Occupation.Job job, int luaResource, string name, string displayName, Ressource resource, string codeName, string luaName, LuaCrafting.LuaRecipe luaRecipe, IconKey icon, Ressource mirrorFrom, string station, string info, Color tint, params RecipeInfo[] pieceRecipes)
	{
		return new Bundle
		{
			Job = job,
			Name = name,
			DisplayName = displayName,
			Resource = resource,
			CodeName = codeName,
			Pieces = Kits.KitFor(job),
			PieceRecipes = pieceRecipes,
			LuaResource = (LuaCrafting.LuaResource)luaResource,
			LuaName = luaName,
			LuaRecipe = luaRecipe,
			Icon = icon,
			MirrorFrom = mirrorFrom,
			Station = station,
			Info = info + " Stored on weapon stands; a bricktron that equips it unpacks it.",
			Tint = tint,
			Key = new Factory.AssetKey(FactoryName, name)
		};
	}

	private static void Enable()
	{
		foreach (Bundle bundle in All)
		{
			// The weapon stand lists two of the spare types as props (kokiri shield, slinger prop); a bundle is no
			// prop, it takes the room of a whole kit (MixedRacks), and the game's kit count would throw for it.
			ToolRack.Weapons.Remove(bundle.Resource);
			ToolRack.Props.Remove(bundle.Resource);
			ToolRack.Hats.Remove(bundle.Resource);
			// An empty stand allows every kit item: bundles too (the game's exclusivity, PickupBonuses).
			if (!ToolRack.AllItems.Contains(bundle.Resource))
			{
				ToolRack.AllItems.Add(bundle.Resource);
			}
			// Carried like the kit's main piece.
			if (Props.props.TryGetValue(bundle.MirrorFrom, out Func<Recepteur, Factory.AssetKey> prop))
			{
				Props.props[bundle.Resource] = prop;
			}
		}
		string entries = string.Empty;
		foreach (Bundle bundle in All)
		{
			string icon = "IconKeys." + bundle.Icon.Name + ":Get64()";
			entries += "_t:Add(Resource." + bundle.LuaName + ",\t{Name = ||\"" + bundle.DisplayName + "\",\tInfo = ||\"" + bundle.Info + "\",\tIcon = ||" + icon + ",\tStockpiledIcon = ||" + icon + "})\n";
		}
		LuaInjection.AddPatch(Features.KitBundles, "LUI/Meta/Meta_Resource.lua", "return _t", LuaInjection.Mode.InsertBefore, entries + "\n");
		// The C# in-game menu's workshop needs and products.
		foreach (Bundle bundle in All)
		{
			IconKey icon = bundle.Icon;
			GameUi.CraftingCatalog.AddResource(bundle.LuaResource, bundle.DisplayName, () => icon.Get64());
		}
	}

	internal static Bundle Of(Ressource resource)
	{
		if (resource == null)
		{
			return null;
		}
		foreach (Bundle bundle in All)
		{
			if (bundle.Resource.GetType() == resource.GetType())
			{
				return bundle;
			}
		}
		return null;
	}

	internal static bool IsBundle(Ressource resource)
	{
		return Of(resource) != null;
	}

	internal static Bundle ForKit(List<Ressource> kit)
	{
		foreach (Bundle bundle in All)
		{
			if (bundle.Pieces == kit)
			{
				return bundle;
			}
		}
		return null;
	}

	// Bundles of the kit's class in the container.
	internal static int CountFor(Recepteur recepteur, List<Ressource> kit)
	{
		Bundle bundle = ForKit(kit);
		return (bundle == null || recepteur == null) ? 0 : recepteur.ContentDescription.Value(bundle.Resource);
	}

	internal static int CountAll(Description content)
	{
		int count = 0;
		foreach (Bundle bundle in All)
		{
			count += content.Value(bundle.Resource);
		}
		return count;
	}

	// Caps the room for every bundle at left (MixedRacks: what is left of the stand's emptiest slot); true when some
	// bundle still fits.
	internal static bool CapRoom(Description capacity, int left)
	{
		bool any = false;
		foreach (Bundle bundle in All)
		{
			int room = capacity.Value(bundle.Resource);
			if (room > left)
			{
				capacity.Set(bundle.Resource, left);
				room = left;
			}
			if (room > 0)
			{
				any = true;
			}
		}
		return any;
	}

	// Called after every factory Awake: the bundle items and their names and icons.
	internal static void SetUpFactories()
	{
		Factory build = Metallurgy.Metallurgy.Ready(FactoryName, SourceItem);
		if (build != null)
		{
			foreach (Bundle bundle in All)
			{
				if (!build.cachedTemplates.ContainsKey(bundle.Name))
				{
					Metallurgy.Metallurgy.AddItem(build, SourceItem, bundle.Name, Adjectif.fabric.GetType(), bundle.Resource, bundle.Tint);
				}
			}
		}
		Factory adjectives = Metallurgy.Metallurgy.Ready("Adjectives", "RawIron");
		if (adjectives != null)
		{
			foreach (Bundle bundle in All)
			{
				Metallurgy.Metallurgy.SetUpInfo(adjectives, bundle.CodeName, "Sword", bundle.DisplayName, bundle.Icon, bundle.Icon);
			}
		}
	}

	// The Lua side (the resource values and their maps; without them the workshop and recipe panels throw for a
	// bundle) and the room for bundles, once the game has set up its own (the first Lua script set-up).
	internal static void RegisterLua()
	{
		if (_luaDone)
		{
			return;
		}
		_luaDone = true;
		Ressource[] from = new Ressource[All.Length];
		Ressource[] to = new Ressource[All.Length];
		for (int i = 0; i < All.Length; i++)
		{
			Bundle bundle = All[i];
			LuaCrafting._adjectiveToLuaResource[bundle.Resource.GetKey()] = bundle.LuaResource;
			LuaCrafting._luaResourceToResource[bundle.LuaResource] = bundle.Resource.GetKey();
			Metallurgy.Metallurgy.AddEnumValue(LuaCrafting.ResourceEnum.Table, "Iron", bundle.LuaName, bundle.LuaResource);
			from[i] = bundle.MirrorFrom;
			to[i] = bundle.Resource;
		}
		Metallurgy.Metallurgy.MirrorCapacities("KitBundles: kit bundle", from, to, Mirrored);
		int units = 0;
		foreach (Labor labor in Live<Labor>.Active())
		{
			if (AllowCarry(labor))
			{
				labor.recepteur.ResetDescriptions();
				units++;
			}
		}
		Plugin.Log.LogInfo("KitBundles: " + units + " units given room for a kit bundle");
		WarnIfUsed();
	}

	// A bundle is carried in the hands like any resource. The game's units never carry a tool that way (a sword is
	// equipped straight from the forge or the stand, Knowledge.ClosestToolFrom), so no unit has room for the kit's
	// main piece to mirror, and without room of its own a finished bundle stayed in the workshop for good (the
	// workshop filled up, its recipe stopped, and a call to arms found no kit). One bundle per trip. True when the
	// unit's room was changed.
	internal static bool AllowCarry(Labor labor)
	{
		Recepteur recepteur = (labor != null) ? labor.GetComponent<Recepteur>() : null;
		Description capacity = (recepteur != null) ? recepteur._baseCapacity : null;
		if (capacity == null)
		{
			return false;
		}
		bool changed = false;
		foreach (Bundle bundle in All)
		{
			Type type = bundle.Resource.GetType();
			if (capacity.DicoAdjectif.TryGetValue(type, out Adjectif current) && current.quantifiable != null && current.quantifiable.valeur >= 1)
			{
				continue;
			}
			// A new entry, not a changed one: the old Adjectif may be shared with the bricktron's template.
			capacity.Remove(type);
			capacity.DicoAdjectif.Add(Adjectif.New(type, 1));
			changed = true;
		}
		if (changed)
		{
			capacity.DicoAdjectif.Preloaded = false;
		}
		return changed;
	}

	// Kits go to weapon stands, not stockpiles; a blueprint's capacity is its build cost (see Metallurgy.Mirrored).
	private static bool Mirrored(Factory factory, FactoryTemplate template)
	{
		if (factory.name == "Blueprints")
		{
			return false;
		}
		string name = template.gameObject.name;
		return name != "Palette" && name != "LargePalette" && name != "Warehouse";
	}

	// The spare types are believed unused by the game's items; say so in the log if one is not.
	private static void WarnIfUsed()
	{
		Factory accessories = Metallurgy.Metallurgy.Ready("Accessories", "Sword");
		if (accessories == null)
		{
			return;
		}
		foreach (FactoryTemplate template in accessories.cachedTemplates.Values)
		{
			Apparence apparence = (template != null) ? template.gameObject.GetComponent<Apparence>() : null;
			if (apparence == null || apparence.description == null)
			{
				continue;
			}
			foreach (Bundle bundle in All)
			{
				if (apparence.description.Has(bundle.Resource))
				{
					Plugin.Log.LogWarning("KitBundles: the game's item " + template.gameObject.name + " has " + bundle.CodeName + ", the type of the " + bundle.DisplayName);
				}
			}
		}
	}

	// One recipe per kit: the summed ingredients, heat and work of its pieces. Made after every other recipe of the
	// game and the mod (KitBundleRecipesPatch), so no recipe id made before moves; every machine makes them at the
	// same point, so the ids match on the network and in saves. The pieces' own recipes stay (saved queues).
	internal static void RegisterRecipes()
	{
		if (_recipesDone || CookBook.Sword == null)
		{
			return;
		}
		_recipesDone = true;
		string ids = string.Empty;
		foreach (Bundle bundle in All)
		{
			List<Ressource> resources = new List<Ressource>();
			List<int> counts = new List<int>();
			int work = 0;
			int heat = 0;
			foreach (RecipeInfo piece in bundle.PieceRecipes)
			{
				work += piece.work;
				heat += piece.heat;
				foreach (KeyValuePair<Type, Adjectif> pair in piece.ingredients.DicoAdjectif)
				{
					Ressource resource = pair.Value as Ressource;
					if (resource == null || resource.quantifiable == null)
					{
						continue;
					}
					int index = resources.FindIndex((Ressource r) => r.GetType() == resource.GetType());
					if (index < 0)
					{
						resources.Add(resource);
						counts.Add(resource.quantifiable.valeur);
					}
					else
					{
						counts[index] += resource.quantifiable.valeur;
					}
				}
			}
			RecipeMaker maker = new RecipeMaker
			{
				Work = work,
				Heat = heat,
				Creates = bundle.Key,
				QteCreated = 1,
				DropOnCompletion = false,
				Icon = bundle.Icon,
				Drawing = bundle.Icon
			};
			for (int i = 0; i < resources.Count; i++)
			{
				maker.With(resources[i], counts[i]);
			}
			bundle.Recipe = maker.Make();
			if (bundle.Recipe.id > WideRecipeIds.MaxId)
			{
				Plugin.Log.LogError("KitBundles: recipe id " + bundle.Recipe.id + " is too high for the crafting queue");
			}
			ids += ((ids.Length > 0) ? ", " : string.Empty) + bundle.Name + " " + bundle.Recipe.id;
		}
		Plugin.Log.LogInfo("KitBundles: recipes registered (" + ids + ")");
	}

	// The kit recipes of the workshop menus make the bundle; the workshops hold the summed ingredients and the bundle.
	internal static void UseRecipes()
	{
		foreach (Bundle bundle in All)
		{
			if (bundle.Recipe == null)
			{
				continue;
			}
			LuaCrafting._luaRecipeToRecipeInfo[bundle.LuaRecipe] = new List<RecipeInfo> { bundle.Recipe };
			LuaCrafting._recipeInfoToLuaRecipe[bundle.Recipe] = bundle.LuaRecipe;
			Factory buildings = Metallurgy.Metallurgy.Ready("ObjetsDynamiques", bundle.Station);
			if (buildings == null)
			{
				Plugin.Log.LogWarning("KitBundles: workshop " + bundle.Station + " not found, its room is left as it was");
				continue;
			}
			foreach (KeyValuePair<Type, Adjectif> pair in bundle.Recipe.ingredients.DicoAdjectif)
			{
				Ressource resource = pair.Value as Ressource;
				if (resource != null && resource.quantifiable != null)
				{
					Metallurgy.Metallurgy.RaiseCapacity(buildings, bundle.Station, resource, resource.quantifiable.valeur);
				}
			}
			Metallurgy.Metallurgy.RaiseCapacity(buildings, bundle.Station, bundle.Resource, 2);
		}
	}

	// One stored or equipped item of every piece of a kit, the first kit found whole; null when there is none.
	internal static Bundle CompleteSet(List<GameObject> items, List<GameObject> pieces)
	{
		foreach (Bundle bundle in All)
		{
			pieces.Clear();
			foreach (Ressource piece in bundle.Pieces)
			{
				foreach (GameObject go in items)
				{
					if (go != null && !pieces.Contains(go) && !IsMagical(go) && Apparence.Is(go, piece))
					{
						pieces.Add(go);
						break;
					}
				}
			}
			if (pieces.Count == bundle.Pieces.Count)
			{
				return bundle;
			}
		}
		pieces.Clear();
		return null;
	}

	// The layers of loose items (as the game's cleanup zones use).
	private const int ItemLayers = 1073152;

	private static readonly Collider[] Hits = new Collider[128];

	// Server: whole kits of loose pieces lying together on the ground (within GroundReach blocks of each other, e.g. a
	// soldier's kit that fell piece by piece) become one bundle each, where the first piece lay. Loose pieces have no
	// room in a bricktron's hands, so they were never carried to a stand; a bundle is. Only the pieces within
	// SearchRadius of the given place (where gear was just dropped) are looked at, or with no place every tool in the
	// world (once when a game starts: that search is slow). Returns the bundles made.
	internal static int PackGround(Vector3? around)
	{
		const float GroundReach = 3f;
		const float SearchRadius = 6f;
		List<GameObject> loose = new List<GameObject>();
		if (around.HasValue)
		{
			int count = Physics.OverlapSphereNonAlloc(around.Value, SearchRadius, Hits, ItemLayers);
			for (int i = 0; i < count; i++)
			{
				Tool tool = (Hits[i] != null) ? Hits[i].GetComponentInParent<Tool>() : null;
				if (tool != null && !tool.Magical && tool.gameObject.activeInHierarchy && OnGround(tool.gameObject) && !loose.Contains(tool.gameObject))
				{
					loose.Add(tool.gameObject);
				}
			}
			System.Array.Clear(Hits, 0, count);
		}
		else
		{
			foreach (Tool tool in UnityEngine.Object.FindObjectsOfType<Tool>())
			{
				GameObject go = tool.gameObject;
				if (!tool.Magical && go.activeInHierarchy && OnGround(go))
				{
					loose.Add(go);
				}
			}
		}
		int packed = 0;
		List<GameObject> near = new List<GameObject>();
		List<GameObject> pieces = new List<GameObject>();
		for (int i = 0; i < loose.Count; i++)
		{
			GameObject first = loose[i];
			if (first == null)
			{
				continue;
			}
			near.Clear();
			Vector3 at = first.transform.position;
			foreach (GameObject other in loose)
			{
				if (other != null && (other.transform.position - at).sqrMagnitude <= GroundReach * GroundReach)
				{
					near.Add(other);
				}
			}
			Bundle bundle = CompleteSet(near, pieces);
			if (bundle == null)
			{
				continue;
			}
			if (Spawn(bundle.Key, first.GetComponent<GameComponent>(), at + Vector3.up * 0.5f) == null)
			{
				continue;
			}
			foreach (GameObject piece in pieces)
			{
				loose[loose.IndexOf(piece)] = null;
			}
			TakeOff(null, pieces);
			packed++;
		}
		return packed;
	}

	// Lying loose: not held by a bricktron, a stand or any other container.
	private static bool OnGround(GameObject go)
	{
		Transform parent = go.transform.parent;
		return parent == null || (parent.GetComponentInParent<Recepteur>() == null && parent.GetComponentInParent<Toolbag>() == null && parent.GetComponentInParent<Labor>() == null);
	}

	// A whole kit in the bricktron's hands, on its head and back.
	internal static Bundle CompleteSetIn(Toolbag bag, List<GameObject> pieces, List<GameObject> others)
	{
		if (bag == null)
		{
			return null;
		}
		List<GameObject> tools = new List<GameObject>();
		bag.Tools(ref tools);
		Bundle bundle = CompleteSet(tools, pieces);
		if (others != null)
		{
			others.Clear();
			foreach (GameObject tool in tools)
			{
				if (!pieces.Contains(tool))
				{
					others.Add(tool);
				}
			}
		}
		return bundle;
	}

	private static bool IsMagical(GameObject go)
	{
		Tool tool = go.GetComponent<Tool>();
		return tool != null && tool.Magical;
	}

	// The stand has room for the bricktron's whole kit as a bundle, and for its other tools as they are.
	internal static bool HasRoomForBundleOf(ToolRack rack, Labor labor)
	{
		if (rack == null || rack.recepteur == null || labor == null || labor.Profession == null)
		{
			return false;
		}
		List<GameObject> pieces = new List<GameObject>();
		List<GameObject> others = new List<GameObject>();
		Bundle bundle = CompleteSetIn(labor.Profession.toolbag, pieces, others);
		if (bundle == null || !rack.recepteur.HasRoomFor(bundle.Key))
		{
			return false;
		}
		foreach (GameObject tool in others)
		{
			if (!rack.recepteur.HasRoomFor(tool))
			{
				return false;
			}
		}
		return true;
	}

	// A new item of the game object owner's faction (synced to the clients). Server only.
	private static GameObject Spawn(Factory.AssetKey key, GameComponent owner, Vector3 at)
	{
		GameObject go = Transactor.SpawnUNet(key, (owner != null) ? owner.faction : null, at, Quaternion.identity);
		if (go == null)
		{
			Plugin.Log.LogWarning("KitBundles: could not spawn " + key);
			return null;
		}
		if (owner != null && owner.affiliation != null)
		{
			owner.affiliation.CopyFactionTo(new List<GameObject> { go });
		}
		Tool tool = go.GetComponent<Tool>();
		if (tool != null)
		{
			tool.NetworkMagical = false;
		}
		return go;
	}

	// Takes the tools off the bricktron and removes them (on every machine).
	private static void TakeOff(Toolbag bag, List<GameObject> pieces)
	{
		foreach (GameObject piece in pieces)
		{
			Tool tool = piece.GetComponent<Tool>();
			if (tool != null)
			{
				tool.OnDropBehaviour = Tool.OnDropBehaviours.Transfer;
			}
			if (bag != null && bag.HasTool(piece))
			{
				bag.UnequipTool(piece, Tool.OnDropBehaviours.Transfer);
			}
			piece.SafeNetworkDestroy();
		}
	}

	// Server: one bundle of the class off the stand, unpacked into its pieces (lying at the stand, ready to be
	// equipped). False when the stand has none.
	internal static bool Unpack(ToolRack rack, Bundle bundle, List<GameObject> gos)
	{
		if (bundle == null || !rack.recepteur.FirstObjectOf(bundle.Resource, out GameObject go))
		{
			return false;
		}
		Vector3 at = go.transform.position;
		go.SafeNetworkDestroy();
		int spawned = 0;
		foreach (RecipeInfo recipe in bundle.PieceRecipes)
		{
			GameObject piece = Spawn(recipe.Creates, rack, at);
			if (piece != null)
			{
				gos.Add(piece);
				Unpacked.Add(piece);
				spawned++;
			}
		}
		return spawned == bundle.Pieces.Count;
	}

	// Server: the bricktron's whole kit packed into a bundle on the stand. False when it has no whole kit or the stand
	// has no room for the bundle; its other tools are left to the game.
	internal static bool Pack(Labor labor, ToolRack rack)
	{
		Toolbag bag = labor.Profession.toolbag;
		List<GameObject> pieces = new List<GameObject>();
		Bundle bundle = CompleteSetIn(bag, pieces, null);
		if (bundle == null || !rack.recepteur.HasRoomFor(bundle.Key))
		{
			return false;
		}
		TakeOff(bag, pieces);
		GameObject go = Spawn(bundle.Key, rack, rack.transform.position);
		if (go != null)
		{
			rack.recepteur.AddObject(go);
		}
		bag.CompileToolStatistics();
		return true;
	}

	// Server: a whole kit the bricktron drops lands as one bundle in front of it.
	internal static void PackDropped(Toolbag bag)
	{
		List<GameObject> pieces = new List<GameObject>();
		Bundle bundle = CompleteSetIn(bag, pieces, null);
		if (bundle == null)
		{
			return;
		}
		Transform user = bag.transform;
		Vector3 at = user.position + user.forward + user.up * 1.5f;
		TakeOff(bag, pieces);
		Spawn(bundle.Key, bag.GetComponent<Labor>(), at);
		bag.CompileToolStatistics();
	}

	// Server: whole kits of loose pieces on the stand become bundles, while the stand takes the bundle (a stand
	// still locked to one class by the game, without MixedRacks, may refuse it).
	internal static int PackLoose(ToolRack rack)
	{
		Recepteur recepteur = rack.recepteur;
		int packed = 0;
		List<GameObject> pieces = new List<GameObject>();
		foreach (Bundle bundle in All)
		{
			for (int guard = 0; guard < 32; guard++)
			{
				pieces.Clear();
				if (!MixedRacks.Collect(rack, bundle.Pieces, pieces) || recepteur.BaseCapacityWithExclusions.Value(bundle.Resource) <= recepteur.ContentDescription.Value(bundle.Resource))
				{
					break;
				}
				TakeOff(null, pieces);
				GameObject go = Spawn(bundle.Key, rack, rack.transform.position);
				if (go == null)
				{
					break;
				}
				recepteur.AddObject(go);
				packed++;
			}
		}
		return packed;
	}
}

// Server: on a stand with bundles, a bricktron gets a whole kit of a single class: loose pieces when the stand has
// the class whole (the class a call to arms asked for, else the one with the most kits, as MixedRacks picks), else one
// bundle of it, unpacked. Without bundles on the stand the game (or MixedRacks) hands out the kit as before.
[Feature]
[HarmonyPatch(typeof(ToolRack), nameof(ToolRack.GetKit))]
[HarmonyPriority(Priority.High)]
internal static class KitBundleGetKitPatch
{
	private static bool Prefix(ToolRack __instance, ref List<GameObject> gos, ref bool __result)
	{
		if (!NetworkServer.active || __instance.recepteur == null || KitBundles.CountAll(__instance.recepteur.ContentDescription) == 0)
		{
			return true;
		}
		List<Ressource> kit = MixedRacks.KitToTake(__instance, out bool strict);
		if (kit == null && strict)
		{
			// The class asked for is gone from the stand: no other class instead.
			__result = false;
			return false;
		}
		List<GameObject> loose = new List<GameObject>();
		if (kit != null && MixedRacks.Collect(__instance, kit, loose))
		{
			gos.AddRange(loose);
			__result = true;
			return false;
		}
		KitBundles.Bundle bundle = (kit != null) ? KitBundles.ForKit(kit) : null;
		if (bundle == null || __instance.recepteur.ContentDescription.Value(bundle.Resource) <= 0)
		{
			if (strict)
			{
				__result = false;
				return false;
			}
			bundle = null;
			foreach (KitBundles.Bundle candidate in KitBundles.All)
			{
				if (__instance.recepteur.ContentDescription.Value(candidate.Resource) > 0)
				{
					bundle = candidate;
					break;
				}
			}
		}
		__result = KitBundles.Unpack(__instance, bundle, gos);
		return false;
	}
}

// Pieces of an unpacked bundle the bricktron could not put on (its slot held a real tool) go back on the stand.
[Feature]
[HarmonyPatch(typeof(ToolNode), "EquipRackContent")]
internal static class KitBundleEquipPatch
{
	private static void Prefix()
	{
		KitBundles.Unpacked.Clear();
	}

	private static void Postfix(Labor labor, GameObject _rack)
	{
		if (KitBundles.Unpacked.Count == 0)
		{
			return;
		}
		ToolRack rack = (_rack != null) ? _rack.GetComponent<ToolRack>() : null;
		Toolbag bag = (labor != null && labor.Profession != null) ? labor.Profession.toolbag : null;
		foreach (GameObject piece in KitBundles.Unpacked)
		{
			if (piece.IsNullOrReleased() || (bag != null && bag.HasTool(piece)))
			{
				continue;
			}
			if (rack != null && rack.recepteur != null && rack.recepteur.HasRoomFor(piece))
			{
				rack.recepteur.AddObject(piece);
			}
		}
		KitBundles.Unpacked.Clear();
	}
}

// A stand with a bundle has a whole kit (equip button, call to arms, the AI's choice of a stand).
[Feature]
[HarmonyPatch(typeof(ToolRack), nameof(ToolRack.HasFullKit))]
internal static class KitBundleFullKitPatch
{
	private static void Postfix(ToolRack __instance, ref bool __result)
	{
		if (!__result && __instance.recepteur != null && KitBundles.CountAll(__instance.recepteur.ContentDescription) > 0)
		{
			__result = true;
		}
	}
}

// Workers weigh stands by the kits on them (ToolRack.KitCount), which knows weapons, props and hats only and threw for
// any other tool, a bundle too (Recepteur.DistanceHackForToolRack).
[Feature]
[HarmonyPatch(typeof(ToolRack))]
internal static class KitBundleBonusPatch
{
	[HarmonyPatch(nameof(ToolRack.StoreBonusFor))]
	[HarmonyPrefix]
	private static bool Store(Outil tool, ref float __result)
	{
		if (!KitBundles.IsBundle(tool))
		{
			return true;
		}
		__result = PickupBonuses.ForStoring(tool, true, 0f);
		return false;
	}

	[HarmonyPatch(nameof(ToolRack.PickUpBonusFor))]
	[HarmonyPrefix]
	private static bool PickUp(Outil tool, ref float __result)
	{
		if (!KitBundles.IsBundle(tool))
		{
			return true;
		}
		__result = PickupBonuses.ForPickingUp(tool, true, 0f);
		return false;
	}
}

// The items, once their factory is awake (factories with the same name are merged, see Metallurgy).
[Feature]
[HarmonyPatch(typeof(Factory), nameof(Factory.Awake))]
internal static class KitBundleFactoryPatch
{
	private static void Postfix()
	{
		KitBundles.SetUpFactories();
	}
}

// Every unit can carry a bundle, also those spawned or loaded after the Lua set-up (KitBundles.AllowCarry).
[Feature]
[HarmonyPatch(typeof(Labor), "Awake")]
internal static class KitBundleCarryPatch
{
	private static void Postfix(Labor __instance)
	{
		KitBundles.AllowCarry(__instance);
	}
}

[Feature]
[HarmonyPatch(typeof(LuaCrafting), nameof(LuaCrafting.Register))]
internal static class KitBundleLuaPatch
{
	private static void Postfix()
	{
		try
		{
			KitBundles.RegisterLua();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("KitBundles: could not register the Lua values: " + ex);
		}
	}
}

// The kit recipes, after every other recipe: the same calls as EfficientMiningRecipesPatch first (each makes its
// recipes once; they do nothing for a feature that is off), so the bundles' ids come last whatever order the
// postfixes run in.
[Feature(Features.KitBundles, Features.KitBundlesInfo)]
[HarmonyPatch(typeof(LuaCrafting), nameof(LuaCrafting.Register))]
[HarmonyPriority(Priority.Last)]
internal static class KitBundleRecipesPatch
{
	private static bool _done;

	private static void Postfix()
	{
		if (_done)
		{
			return;
		}
		_done = true;
		try
		{
			Metallurgy.MetallurgyLuaPatch.Run();
			Upgrades.UpgradeLines.RegisterRecipes(Upgrades.UpgradeHall.Research);
			Market.MarketPrices.RegisterLateTrades();
			Upgrades.UpgradeLines.RegisterLateRecipes();
			KitBundles.RegisterRecipes();
			KitBundles.UseRecipes();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("KitBundles: could not register the recipes: " + ex);
		}
	}
}

// Server: a bricktron handing in a whole kit packs it into a bundle on the stand. When it has nothing else, it is
// done (the game would find the stand full after the bundle and count the hand-in as failed).
[Feature(Features.KitBundles, Features.KitBundlesInfo)]
[HarmonyPatch(typeof(ToolNode), "StoreToolsIn")]
internal static class KitBundleStorePatch
{
	private static bool Prefix(Labor labor, GameObject rackGo, ref bool __result)
	{
		if (!NetworkServer.active || labor == null || labor.Profession == null || rackGo == null)
		{
			return true;
		}
		ToolRack rack = rackGo.GetComponent<ToolRack>();
		if (rack == null || rack.recepteur == null || !KitBundles.Pack(labor, rack))
		{
			return true;
		}
		List<GameObject> left = new List<GameObject>();
		labor.Profession.toolbag.Tools(ref left);
		if (left.Count > 0)
		{
			return true;
		}
		__result = true;
		return false;
	}
}

// Room for a whole kit as one bundle counts as room for the bricktron's gear (store order, the AI's choice of a
// stand).
[Feature(Features.KitBundles, Features.KitBundlesInfo)]
[HarmonyPatch]
internal static class KitBundleRoomPatch
{
	[HarmonyPatch(typeof(ToolRack), nameof(ToolRack.HasRoomForEquipmentOf))]
	[HarmonyPostfix]
	private static void StandRoom(ToolRack __instance, Labor labor, ref bool __result)
	{
		if (!__result && KitBundles.HasRoomForBundleOf(__instance, labor))
		{
			__result = true;
		}
	}

	[HarmonyPatch(typeof(ToolNode), nameof(ToolNode.CanStoreToolOnRack))]
	[HarmonyPostfix]
	private static void StoreRoom(Labor labor, GameObject _rack, ref bool __result)
	{
		if (__result || labor == null || _rack == null || !labor.faction.IsSame(_rack))
		{
			return;
		}
		if (KitBundles.HasRoomForBundleOf(_rack.GetComponent<ToolRack>(), labor))
		{
			__result = true;
		}
	}
}

// Server: a whole kit a bricktron drops lands as one bundle.
[Feature(Features.KitBundles, Features.KitBundlesInfo)]
[HarmonyPatch(typeof(Toolbag), nameof(Toolbag.DropAllTools))]
internal static class KitBundleDropPatch
{
	private static void Prefix(Toolbag __instance)
	{
		if (NetworkServer.active)
		{
			KitBundles.PackDropped(__instance);
		}
	}
}

// Server: a whole kit of a bricktron that dies lands as one bundle (dying releases the bricktron, and each piece
// fell on its own, not through DropAllTools).
[Feature(Features.KitBundles, Features.KitBundlesInfo)]
[HarmonyPatch(typeof(Locomotion4), "Die")]
internal static class KitBundleDeathPatch
{
	// Before DropGearOnDeath, which lets go of every piece on its own and left no whole kit to pack.
	[HarmonyPriority(Priority.First)]
	private static void Prefix(Locomotion4 __instance)
	{
		if (!NetworkServer.active)
		{
			return;
		}
		Toolbag bag = __instance.GetComponent<Toolbag>() ?? __instance.GetComponentInParent<Toolbag>();
		if (bag != null)
		{
			KitBundles.PackDropped(bag);
		}
	}
}

// Server: loose whole kits on the ground are packed into bundles (KitBundles.PackGround): round every place gear is
// dropped, SettleSeconds later (the pieces fall and roll first), and once all over the world when a game starts.
[Feature(Features.KitBundles, Features.KitBundlesInfo)]
internal class KitBundleGroundPacker : MonoBehaviour
{
	private const float SettleSeconds = 2f;

	private static readonly List<KeyValuePair<Vector3, float>> Drops = new List<KeyValuePair<Vector3, float>>();

	// The level is ready, and whether the whole world has been looked at since.
	private static bool _ready;

	private static bool _scanned;

	private static void Enable()
	{
		Plugin.Root.AddComponent<KitBundleGroundPacker>();
		GameSession.OnLevelReady(() =>
		{
			_ready = true;
			_scanned = false;
		});
		GameSession.OnLeave(() =>
		{
			Drops.Clear();
			_ready = false;
		});
	}

	// Gear was dropped here (server).
	internal static void Dropped(Vector3 at)
	{
		if (!NetworkServer.active)
		{
			return;
		}
		foreach (KeyValuePair<Vector3, float> drop in Drops)
		{
			if ((drop.Key - at).sqrMagnitude < 4f)
			{
				return;
			}
		}
		Drops.Add(new KeyValuePair<Vector3, float>(at, Time.time + SettleSeconds));
	}

	private void Update()
	{
		if (!NetworkServer.active || !_ready)
		{
			return;
		}
		try
		{
			int packed = 0;
			if (!_scanned)
			{
				_scanned = true;
				packed += KitBundles.PackGround(null);
			}
			for (int i = Drops.Count - 1; i >= 0; i--)
			{
				if (Time.time >= Drops[i].Value)
				{
					Vector3 at = Drops[i].Key;
					Drops.RemoveAt(i);
					packed += KitBundles.PackGround(at);
				}
			}
			if (packed > 0)
			{
				Plugin.Log.LogInfo("KitBundles: " + packed + " whole kits on the ground packed into bundles");
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("KitBundles: packing kits on the ground failed: " + ex.Message);
			Drops.Clear();
		}
	}
}

// Every piece of gear a bricktron lets fall (dropping it, dying) is a place to look for a whole kit to pack.
[Feature(Features.KitBundles, Features.KitBundlesInfo)]
[HarmonyPatch(typeof(Toolbag), nameof(Toolbag.UnequipTool), new Type[] { typeof(GameObject), typeof(Tool.OnDropBehaviours) })]
internal static class KitBundleUnequipPatch
{
	private static void Postfix(Toolbag __instance, Tool.OnDropBehaviours onDrop)
	{
		if (onDrop == Tool.OnDropBehaviours.Drop && __instance != null)
		{
			KitBundleGroundPacker.Dropped(__instance.transform.position);
		}
	}
}

// A cleanup task's item the workers' knowledge does not track (kit bundles, gear): the game asks the knowledge whether
// the worker can get to it, which always says no for those, so a cleanup task never picked up a bundle. For such
// items the mod's own reach test (UnreachableTasks.ItemReachable) is asked instead; the game's other checks stay.
// That test cannot tell every place a worker cannot walk to: an item a worker failed to walk to is left alone for
// FailedSeconds, twice as long after each further failure (up to MaxFailedSeconds), so the workers do not try it again and again (a helmet out of reach cost a worker 5 seconds a try).
[Feature(Features.KitBundles, Features.KitBundlesInfo)]
[HarmonyPatch(typeof(CleanUpGoal), nameof(CleanUpGoal.IsMatchFor))]
internal static class KitBundleCleanupPatch
{
	private const float FailedSeconds = 60f;

	private const float MaxFailedSeconds = 960f;

	private static readonly Dictionary<GameObject, float> Failed = new Dictionary<GameObject, float>();

	// How long the item is left alone after its last failure: doubled with every failure in a row.
	private static readonly Dictionary<GameObject, float> FailedFor = new Dictionary<GameObject, float>();

	// Seconds the item is still left alone after a failed walk to it, 0 when it is not.
	internal static float WaitingFor(GameObject item)
	{
		return (item != null && Failed.TryGetValue(item, out float until)) ? Mathf.Max(0f, until - Time.time) : 0f;
	}

	internal static void MarkFailed(GameObject item)
	{
		if (Failed.Count > 500)
		{
			Failed.Clear();
			FailedFor.Clear();
		}
		float seconds = FailedFor.TryGetValue(item, out float last) ? Mathf.Min(last * 2f, MaxFailedSeconds) : FailedSeconds;
		FailedFor[item] = seconds;
		Failed[item] = Time.time + seconds;
		Plugin.Log.LogInfo("KitBundles: no way to " + item.name + " at " + item.transform.position + ", left alone for " + seconds + " s");
	}

	private static bool Prefix(CleanUpGoal __instance, Labor labor, AIErrorsStore errorDestination, ref float time, ref bool __result)
	{
		GameObject item = __instance.gameObject;
		if (labor == null || Brix.Game.AI.KnowledgeSpace.Knowledge.Instance == null || Brix.Game.AI.KnowledgeSpace.Knowledge.Instance.KnowsAbout(item))
		{
			return true;
		}
		__result = false;
		if (__instance.HasOtherWorkerAssigned(labor))
		{
			errorDestination.AddDecisionError(SharedWorkException<Exceptions.GoalTooCrowded>.Get.Problem, labor);
			return false;
		}
		time += 0.05f;
		if ((Failed.TryGetValue(item, out float until) && Time.time <= until) || !WorkerAI.UnreachableTasks.ItemReachable(labor, item))
		{
			errorDestination.AddDecisionError(SharedWorkException<Exceptions.CannotFindWhatINeedToPickUp>.Get.Problem, labor);
			return false;
		}
		Labor holder = item.GetComponentInParent<Labor>();
		if (holder != null && holder != labor)
		{
			errorDestination.AddDecisionError(SharedWorkException<Exceptions.ObjectIsInSomeoneElseBag>.Get.Problem, labor);
			return false;
		}
		if (!labor.recepteur.HasRoomFor(item))
		{
			errorDestination.AddDecisionError(SharedWorkException<Exceptions.InventoryIsFull>.Get.Problem, labor);
			return false;
		}
		time += 1f;
		if (labor.BestRecepteurToStore(item) == null)
		{
			errorDestination.AddDecisionError(SharedWorkException<Exceptions.NoCorrespondingStorage>.Get.Problem, labor);
			return false;
		}
		__result = true;
		return false;
	}
}

// A worker that could not walk to a cleanup task's untracked item (see KitBundleCleanupPatch) marks it.
[Feature(Features.KitBundles, Features.KitBundlesInfo)]
[HarmonyPatch(typeof(Task), "OnFinally")]
internal static class KitBundleCleanupFailPatch
{
	private static void Prefix(Task __instance)
	{
		GameObject item = __instance.Giver;
		if (__instance.result != TaskResult.PathFailed || item == null || item.GetComponent<CleanUpGoal>() == null)
		{
			return;
		}
		Brix.Game.AI.KnowledgeSpace.Knowledge knowledge = Brix.Game.AI.KnowledgeSpace.Knowledge.Instance;
		if (knowledge != null && !knowledge.KnowsAbout(item))
		{
			KitBundleCleanupPatch.MarkFailed(item);
		}
	}
}

// A cleanup task's untracked item (gear, kit bundles) was walked to as an object, which needs the item's shape to be
// registered in the world. Gear that has lain still since it was dropped or loaded is not (it registers only when it
// moves), so the walk failed before the worker took a step, every time. Such an item is now picked up as the idle
// workers' cleanup does: from a walkable place right beside or below it (AutoCleanup.FindFoot); the game's walk is
// kept when there is no such place.
[Feature(Features.KitBundles, Features.KitBundlesInfo)]
[HarmonyPatch(typeof(CleanUpGoal), nameof(CleanUpGoal.GetWorkNode))]
internal static class KitBundleCleanupWalkPatch
{
	private static void Postfix(CleanUpGoal __instance, Labor Worker, ref Node __result)
	{
		GameObject item = (__instance != null) ? __instance.gameObject : null;
		Brix.Game.AI.KnowledgeSpace.Knowledge knowledge = Brix.Game.AI.KnowledgeSpace.Knowledge.Instance;
		if (item == null || Worker == null || knowledge == null || knowledge.KnowsAbout(item))
		{
			return;
		}
		if (!WorkerAI.AutoCleanup.FindFoot(item, out XYZ foot))
		{
			return;
		}
		__result = WorkerAI.AutoCleanup.PickUpFromFoot(Worker, __instance, foot);
	}
}

// Every add to and take from a stand ends in RefreshExclusivity: the stand is packed a frame later.
[Feature(Features.KitBundles, Features.KitBundlesInfo)]
[HarmonyPatch(typeof(ToolRack), nameof(ToolRack.RefreshExclusivity))]
internal static class KitBundleRackPatch
{
	private static void Postfix(ToolRack __instance)
	{
		KitBundlePacker.Schedule(__instance);
	}
}

// Server: packs the loose whole kits of the stands that changed, a frame after the change (not inside the game's
// add or take, and never in reaction to its own packing).
[Feature(Features.KitBundles, Features.KitBundlesInfo)]
internal class KitBundlePacker : MonoBehaviour
{
	private static readonly HashSet<ToolRack> Pending = new HashSet<ToolRack>();

	private static readonly List<ToolRack> Batch = new List<ToolRack>();

	private static bool _packing;

	private static void Enable()
	{
		Plugin.Root.AddComponent<KitBundlePacker>();
		GameSession.OnLeave(() => Pending.Clear());
	}

	internal static void Schedule(ToolRack rack)
	{
		if (!_packing && rack != null && NetworkServer.active)
		{
			Pending.Add(rack);
		}
	}

	private void Update()
	{
		if (Pending.Count == 0)
		{
			return;
		}
		Batch.Clear();
		Batch.AddRange(Pending);
		Pending.Clear();
		if (!NetworkServer.active)
		{
			return;
		}
		_packing = true;
		try
		{
			foreach (ToolRack rack in Batch)
			{
				if (rack == null || rack.IsNullOrReleased() || rack.recepteur == null)
				{
					continue;
				}
				int packed = KitBundles.PackLoose(rack);
				if (packed > 0)
				{
					Plugin.Log.LogInfo("KitBundles: packed " + packed + " loose kit(s) on " + rack.name);
				}
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("KitBundles: packing failed: " + ex);
		}
		finally
		{
			_packing = false;
			Batch.Clear();
		}
	}
}

// A weapon stand shows what it holds as rows of models, one row per kind of piece (swords, shields, helmets...),
// lit up to the count it holds of that kind. A bundle is a kind of its own with no row, so a stand full of bundles
// looked empty. Each bundle now lights one model in the rows of the pieces it holds, as the loose kit would. Always
// on, as the bundles themselves are (a save with bundles shows them also with the feature off).
[Feature]
[HarmonyPatch(typeof(RecepteurTracksDisplay.DisplayTrack), nameof(RecepteurTracksDisplay.DisplayTrack.UpdateDisplay))]
internal static class KitBundleRackDisplayPatch
{
	private static void Postfix(RecepteurTracksDisplay.DisplayTrack __instance, Recepteur targetRecepteur)
	{
		if (targetRecepteur == null || __instance.adjs == null || __instance.gos == null)
		{
			return;
		}
		int bundles = BundlesFor(__instance.adjs, targetRecepteur);
		if (bundles == 0)
		{
			return;
		}
		int shown = bundles;
		foreach (Adjectif adj in __instance.adjs)
		{
			shown += targetRecepteur.ContentDescription.Value(adj);
		}
		for (int i = 0; i < __instance.gos.Count; i++)
		{
			if (__instance.gos[i] != null)
			{
				__instance.gos[i].SetActive(i < shown);
			}
		}
	}

	// The bundles on the stand holding a piece the row shows.
	internal static int BundlesFor(List<Adjectif> row, Recepteur recepteur)
	{
		int bundles = 0;
		foreach (KitBundles.Bundle bundle in KitBundles.All)
		{
			if (bundle.Resource == null || bundle.Pieces == null || !Shows(row, bundle.Pieces))
			{
				continue;
			}
			bundles += recepteur.ContentDescription.Value(bundle.Resource);
		}
		return bundles;
	}

	// Whether the row shows one of the kit's pieces.
	private static bool Shows(List<Adjectif> row, List<Ressource> pieces)
	{
		foreach (Adjectif adj in row)
		{
			foreach (Ressource piece in pieces)
			{
				if (adj != null && piece != null && adj.GetType() == piece.GetType())
				{
					return true;
				}
			}
		}
		return false;
	}
}
