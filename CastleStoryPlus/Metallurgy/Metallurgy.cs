using System;
using System.Collections.Generic;
using System.IO;
using Brix.External.Factories;
using Brix.External.Factories.Templates;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Lua;
using Brix.UI.Icons;
using CastleStoryPlus.Core;
using HarmonyLib;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Interop;
using UnityEngine;
using Object = UnityEngine.Object;
using Path = System.IO.Path;

namespace CastleStoryPlus.Metallurgy;

// Coal and steel. The game has two resource types it never uses, Clay and Terracotta: they have their adjectives,
// their info, their room on the stockpiles (240 like raw resources, 48 like ingots) and a name the network and saves
// already know. Coal is Clay and steel is Terracotta, with their own items: coal is a clone of raw iron turned black,
// steel a clone of the iron ingot turned light silver.
// - Coal is mined from veins in the deep rock (CoalVeins), or burnt from logs in the furnace (1 log + heat -> 4
//   coal).
// - Steel is forged from iron and coal at the forge (1 iron + 4 coal -> 1 steel).
// - The upgrades' steel tier costs steel (UpgradeLines).
// The game's Lua UI knows resources and recipes by two fixed enums; coal, steel and their recipes get values past
// the end of those enums, and entries in the resource, recipe and station lists.
[Feature(Features.Metallurgy, Features.MetallurgyInfo)]
internal static class Metallurgy
{
	internal const string CoalName = "Coal";

	internal const string SteelName = "SteelIngot";

	internal static readonly Factory.AssetKey CoalKey = new Factory.AssetKey("RawResources", CoalName);

	internal static readonly Factory.AssetKey SteelKey = new Factory.AssetKey("BuildResources", SteelName);

	internal const LuaCrafting.LuaResource LuaCoal = (LuaCrafting.LuaResource)100;

	internal const LuaCrafting.LuaResource LuaSteel = (LuaCrafting.LuaResource)101;

	internal const LuaCrafting.LuaRecipe LuaCharcoalRecipe = (LuaCrafting.LuaRecipe)100;

	internal const LuaCrafting.LuaRecipe LuaSteelRecipe = (LuaCrafting.LuaRecipe)101;

	// Coal pieces per burnt log, and per steel ingot.
	internal const int CoalPerLog = 4;

	internal const int CoalPerSteel = 4;

	internal const string IconDatabase = "CastleStoryPlus";

	internal static Ressource Coal => Adjectif.clay;

	internal static Ressource Steel => Adjectif.terracotta;

	internal static bool Enabled;

	internal static IconKey CoalIcon;

	internal static IconKey CoalStockpiledIcon;

	internal static IconKey SteelIcon;

	internal static IconKey SteelStockpiledIcon;

	internal static RecipeInfo CharcoalRecipe;

	internal static RecipeInfo SteelRecipe;

	private static readonly Dictionary<string, Sprite> Sprites = new Dictionary<string, Sprite>();

	// Multiplies the item's material colours.
	private static readonly Color CoalTint = new Color(0.2f, 0.2f, 0.22f);

	private static readonly Color SteelTint = new Color(1.35f, 1.45f, 1.6f);

	private static void Enable()
	{
		Enabled = true;
		EnsureIcons();
		LuaInjection.AddFunction("ResourceIcon", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			Sprite sprite = SpriteFor(args.Count > 0 ? args[0].CastToString() : null);
			return (sprite != null) ? DynValue.FromObject(context.OwnerScript, sprite) : DynValue.Nil;
		});
		LuaInjection.AddPatch(Features.Metallurgy, "LUI/Meta/Meta_Resource.lua", "return _t", LuaInjection.Mode.InsertBefore,
			"_t:Add(Resource.Coal,\t{Name = ||\"Coal\",\tInfo = ||\"Mined from black veins in the deep rock, or burnt from logs in the furnace. Forged with iron into steel.\",\tIcon = ||CastleStoryPlus.ResourceIcon(\"coal\"),\tStockpiledIcon = ||CastleStoryPlus.ResourceIcon(\"coal_stockpiled\")})\n"
			+ "_t:Add(Resource.Steel,\t{Name = ||\"Steel\",\tInfo = ||\"Iron forged with coal at the forge. Used by the steel tier of the smithy and armoury upgrades.\",\tIcon = ||CastleStoryPlus.ResourceIcon(\"steel\"),\tStockpiledIcon = ||CastleStoryPlus.ResourceIcon(\"steel_stockpiled\")})\n\n");
		LuaInjection.AddPatch(Features.Metallurgy, "LUI/Meta/Meta_Recipe.lua", "return _t", LuaInjection.Mode.InsertBefore,
			"_t:Add(Recipe.Charcoal,\t{Name = ||\"Charcoal\",\tInfo = ||\"Burns a log into " + CoalPerLog + " coal.\",\tIcon = ||CastleStoryPlus.ResourceIcon(\"coal\")})\n"
			+ "_t:Add(Recipe.Steel,\t{Name = ||\"Steel ingot\",\tInfo = ||\"Forges an iron ingot and " + CoalPerSteel + " coal into a steel ingot.\",\tIcon = ||CastleStoryPlus.ResourceIcon(\"steel\")})\n\n");
		LuaInjection.AddPatch(Features.Metallurgy, "LUI/Meta/Meta_Operable.lua", "Recipes = { Recipe.IronIngot, Recipe.GlassIngot },", LuaInjection.Mode.Replace,
			"Recipes = { Recipe.IronIngot, Recipe.GlassIngot, Recipe.Charcoal },");
		LuaInjection.AddPatch(Features.Metallurgy, "LUI/Meta/Meta_Operable.lua", "Recipes = { Recipe.KnightKit, Recipe.Cog },", LuaInjection.Mode.Replace,
			"Recipes = { Recipe.KnightKit, Recipe.Cog, Recipe.Steel },");
	}

	// Whether the feature is switched on, also before its Enable ran (the upgrades ask while they set up their costs).
	internal static bool IsOn()
	{
		return Plugin.Cfg.Bind("Features", Features.Metallurgy, true, Features.MetallurgyInfo).Value;
	}

	// Icons from Assets/Resources (made by tools/make_resource_icons.py from the game's iron icons), in an icon
	// database of the mod's own, so C# UI (stockpile content, upgrade costs) can use them as icon keys.
	internal static void EnsureIcons()
	{
		if (CoalIcon != null)
		{
			return;
		}
		// The game's icon keys get their ids in creation order; make sure they all exist before adding the mod's.
		_ = IconKeys.Missing;
		GameObject holder = new GameObject("CastleStoryPlus Icons");
		holder.SetActive(false);
		holder.transform.SetParent(Plugin.Root.transform, false);
		IconDB database = holder.AddComponent<IconDB>();
		database.UniqueName = IconDatabase;
		CoalIcon = AddIcon(database, "coal");
		CoalStockpiledIcon = AddIcon(database, "coal_stockpiled");
		SteelIcon = AddIcon(database, "steel");
		SteelStockpiledIcon = AddIcon(database, "steel_stockpiled");
		IconDB._databases[IconDatabase] = database;
	}

	internal static IconKey AddIcon(IconDB database, string name)
	{
		Sprite sprite = SpriteFor(name);
		string key = "CastleStoryPlus_" + name;
		database._icons[key] = new MetaIcon
		{
			Name = key,
			Image64 = sprite,
			Image32 = sprite
		};
		return new IconKey(IconDatabase, key);
	}

	internal static Sprite SpriteFor(string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return null;
		}
		if (Sprites.TryGetValue(name, out Sprite sprite))
		{
			return sprite;
		}
		string path = Path.Combine(Path.Combine(Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "Assets"), "Resources"), name + ".png");
		Texture2D texture = new Texture2D(2, 2, TextureFormat.ARGB32, mipmap: false);
		if (File.Exists(path) && texture.LoadImage(File.ReadAllBytes(path)))
		{
			texture.name = "Resource " + name;
			texture.filterMode = FilterMode.Bilinear;
			sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
			Object.DontDestroyOnLoad(texture);
		}
		else
		{
			Plugin.Log.LogWarning("Metallurgy: icon " + path + " not found");
			Object.Destroy(texture);
			sprite = null;
		}
		Sprites[name] = sprite;
		return sprite;
	}

	// Furnace: 1 log + 1 heat -> 4 coal. Forge: 1 iron + 4 coal + work -> 1 steel. Made after every other recipe of
	// the game and the mod (the first time the Lua UI is set up), so their ids do not move; every machine makes them
	// at the same point, so the ids match on the network.
	internal static void RegisterRecipes()
	{
		if (CharcoalRecipe != null)
		{
			return;
		}
		CharcoalRecipe = new RecipeMaker
		{
			Work = 0,
			Heat = 1,
			Creates = CoalKey,
			QteCreated = CoalPerLog,
			DropOnCompletion = false,
			Icon = CoalIcon,
			Drawing = CoalIcon
		}.With(Adjectif.woodBlock, 1).Make();
		SteelRecipe = new RecipeMaker
		{
			Work = 8,
			Heat = 0,
			Creates = SteelKey,
			QteCreated = 1,
			DropOnCompletion = false,
			Icon = SteelIcon,
			Drawing = SteelIcon
		}.With(Adjectif.iron, 1).With(Adjectif.clay, CoalPerSteel).Make();
		if (SteelRecipe.id > Core.WideRecipeIds.MaxId)
		{
			Plugin.Log.LogError("Metallurgy: recipe id " + SteelRecipe.id + " is too high for the crafting queue");
		}
		Plugin.Log.LogInfo("Metallurgy: recipes charcoal (" + CharcoalRecipe.id + ") and steel (" + SteelRecipe.id + ") registered");
	}

	// The Lua side: resource and recipe values, and the maps between them and the game's types.
	internal static void RegisterLua()
	{
		LuaCrafting._adjectiveToLuaResource[Adjectif.clay.GetKey()] = LuaCoal;
		LuaCrafting._luaResourceToResource[LuaCoal] = Adjectif.clay.GetKey();
		LuaCrafting._adjectiveToLuaResource[Adjectif.terracotta.GetKey()] = LuaSteel;
		LuaCrafting._luaResourceToResource[LuaSteel] = Adjectif.terracotta.GetKey();
		LuaCrafting._luaRecipeToRecipeInfo[LuaCharcoalRecipe] = new List<RecipeInfo> { CharcoalRecipe };
		LuaCrafting._recipeInfoToLuaRecipe[CharcoalRecipe] = LuaCharcoalRecipe;
		LuaCrafting._luaRecipeToRecipeInfo[LuaSteelRecipe] = new List<RecipeInfo> { SteelRecipe };
		LuaCrafting._recipeInfoToLuaRecipe[SteelRecipe] = LuaSteelRecipe;
		AddEnumValue(LuaCrafting.ResourceEnum.Table, "Iron", "Coal", LuaCoal);
		AddEnumValue(LuaCrafting.ResourceEnum.Table, "Iron", "Steel", LuaSteel);
		AddEnumValue(LuaCrafting.RecipeEnum.Table, "IronIngot", "Charcoal", LuaCharcoalRecipe);
		AddEnumValue(LuaCrafting.RecipeEnum.Table, "IronIngot", "Steel", LuaSteelRecipe);
		GoesInBag(Coal);
	}

	// Loose items of the resource go in the bag, as raw iron and crystals do: cleanup areas' workers only take in
	// passing what goes in the bag, so without this they walked to loose coal (or dark crystal) and left it there.
	internal static void GoesInBag(Ressource resource)
	{
		Apparence.BagStuff.Add(resource);
		if (!Apparence.BagKeys.Contains(resource.GetKey()))
		{
			Apparence.BagKeys.Add(resource.GetKey());
		}
	}

	// A value in one of the Lua enum tables (Resource.X, Recipe.X), made like the table's own entries.
	internal static void AddEnumValue<T>(Table table, string existing, string name, T value) where T : struct
	{
		DynValue sample = table.Get(existing);
		if (sample.Type != DataType.UserData)
		{
			Plugin.Log.LogError("Metallurgy: Lua enum entry " + existing + " not found, " + name + " left out");
			return;
		}
		table[name] = UserData.CreateWithDescriptor(new LuaEnumProxy<T>(value), sample.UserData.Descriptor);
	}

	// Called after every factory Awake (factories with the same name are merged, so look at the registered ones).
	internal static void SetUpFactories()
	{
		Factory raw = Ready("RawResources", "RawIron");
		if (raw != null && !raw.cachedTemplates.ContainsKey(CoalName))
		{
			AddItem(raw, "RawIron", CoalName, typeof(RawIron), Coal, CoalTint);
		}
		Factory build = Ready("BuildResources", "IronIngot");
		if (build != null && !build.cachedTemplates.ContainsKey(SteelName))
		{
			AddItem(build, "IronIngot", SteelName, typeof(Iron), Steel, SteelTint);
		}
		Factory adjectives = Ready("Adjectives", "RawIron");
		if (adjectives != null)
		{
			SetUpInfo(adjectives, "Clay", "RawIron", "Coal", CoalIcon, CoalStockpiledIcon);
			SetUpInfo(adjectives, "Terracotta", "Iron", "Steel", SteelIcon, SteelStockpiledIcon);
		}
		Factory buildings = Ready("ObjetsDynamiques", "Furnace");
		if (buildings != null)
		{
			RaiseCapacity(buildings, "Furnace", Adjectif.woodBlock, 6);
			RaiseCapacity(buildings, "Furnace", Coal, CoalPerLog * 4);
		}
		buildings = Ready("ObjetsDynamiques", "Forge");
		if (buildings != null)
		{
			RaiseCapacity(buildings, "Forge", Adjectif.iron, 2);
			RaiseCapacity(buildings, "Forge", Coal, CoalPerSteel * 2);
			RaiseCapacity(buildings, "Forge", Steel, 4);
		}
	}

	internal static Factory Ready(string name, string template)
	{
		if (!Factory.Factories.TryGetValue(name, out Factory factory) || factory == null)
		{
			return null;
		}
		if (factory.cachedTemplates.Count == 0)
		{
			factory.ResetTemplateCache();
		}
		return factory.cachedTemplates.ContainsKey(template) ? factory : null;
	}

	// A copy of the source item (same model, physics and behaviour) holding the new resource, with tinted materials.
	internal static void AddItem(Factory factory, string source, string name, Type sourceResource, Ressource resource, Color tint, Action<GameObject> adjust = null)
	{
		try
		{
			GameObject template = factory.GetTemplate(source).gameObject;
			GameObject item = Object.Instantiate(template, factory.transform);
			item.name = name;
			int swapped = 0;
			foreach (Apparence apparence in item.GetComponentsInChildren<Apparence>(true))
			{
				if (SwapResource(apparence.description, sourceResource, resource))
				{
					apparence._principale = resource;
					swapped++;
				}
			}
			Tint(item, tint);
			// Left on the ground, raw items merge back into the terrain as the type they came from.
			if (resource == Coal)
			{
				foreach (Brix.Legacy.Garnotte garnotte in item.GetComponentsInChildren<Brix.Legacy.Garnotte>(true))
				{
					garnotte.terrainType = (Brix.Engine.VoxelTerrainType)CoalVeins.CoalType;
				}
			}
			if (adjust != null)
			{
				adjust(item);
			}
			FactoryImprint imprint = item.GetComponent<FactoryImprint>();
			if (imprint != null)
			{
				imprint.AssetKey = new Factory.AssetKey(factory.name, name);
			}
			Factory.AssetKey key = factory.AddAsset<CloneTemplate>(item);
			Brix.Network.FactoryRegistrator.RegisterHandlerForTemplate(key);
			if (Description.reps == null)
			{
				Description.reps = Description.InitReps();
			}
			Description.reps[resource.GetKey()] = key;
			Plugin.Log.LogInfo("Metallurgy: added " + key + " (" + swapped + " descriptions)");
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("Metallurgy: could not add " + name + " to factory " + factory.name + ": " + ex);
		}
	}

	private static bool SwapResource(Description description, Type from, Ressource to)
	{
		if (description == null || !description.DicoAdjectif.TryGetValue(from, out Adjectif old))
		{
			return false;
		}
		int quantity = (old.quantifiable != null) ? old.quantifiable.valeur : 1;
		description.Remove(from);
		description.DicoAdjectif.Add(Adjectif.New(to.GetType(), quantity));
		// Serialize the changed content when the template is copied, instead of the content it was loaded with.
		description.DicoAdjectif.Preloaded = false;
		return true;
	}

	private static void Tint(GameObject item, Color tint)
	{
		Dictionary<Material, Material> tinted = new Dictionary<Material, Material>();
		foreach (Renderer renderer in item.GetComponentsInChildren<Renderer>(true))
		{
			Material[] materials = renderer.sharedMaterials;
			for (int i = 0; i < materials.Length; i++)
			{
				Material original = materials[i];
				if (original == null)
				{
					continue;
				}
				if (!tinted.TryGetValue(original, out Material material))
				{
					material = new Material(original);
					material.name = original.name + " (" + item.name + ")";
					if (material.HasProperty("_Color"))
					{
						Color color = original.GetColor("_Color");
						material.SetColor("_Color", new Color(color.r * tint.r, color.g * tint.g, color.b * tint.b, color.a));
					}
					tinted[original] = material;
				}
				materials[i] = material;
			}
			renderer.sharedMaterials = materials;
		}
	}

	// The resource's info (name and icons in the selection panel). The game ships one for Clay and Terracotta; if it
	// is missing, one is copied from the source resource's info.
	internal static void SetUpInfo(Factory factory, string codeName, string sourceCodeName, string displayName, IconKey icon, IconKey storedIcon)
	{
		AdjectiveInfo info = null;
		AdjectiveInfo source = null;
		foreach (AdjectiveInfo candidate in factory.GetComponentsInChildren<AdjectiveInfo>(true))
		{
			if (candidate.codeName == codeName)
			{
				info = candidate;
			}
			else if (candidate.codeName == sourceCodeName)
			{
				source = candidate;
			}
		}
		if (info == null)
		{
			if (source == null)
			{
				return;
			}
			info = Object.Instantiate(source.gameObject, factory.transform).GetComponent<AdjectiveInfo>();
			info.gameObject.name = codeName;
			info.codeName = codeName;
			Plugin.Log.LogInfo("Metallurgy: adjective info " + codeName + " copied from " + sourceCodeName);
		}
		if (info.descriptiveName == displayName)
		{
			return;
		}
		info.descriptiveName = displayName;
		info.UIRelevant = true;
		info.transferable = true;
		if (icon != null)
		{
			info._icon = new IconKey.IconKeyLink { key = icon.Name };
		}
		if (storedIcon != null)
		{
			info._storedIcon = new IconKey.IconKeyLink { key = storedIcon.Name };
		}
		AdjectiveInfo._allInfos[codeName] = info;
		Plugin.Log.LogInfo("Metallurgy: adjective info " + codeName + " shown as " + displayName);
	}

	// Everything that can hold raw iron can hold as much coal, and everything that can hold iron as much steel: the
	// bricktrons' own capacity, carts, racks... are set per resource in the templates, which do not know clay and
	// terracotta (the resource types coal and steel use), so nothing could carry coal out of the furnace.
	internal static void MirrorCapacities()
	{
		MirrorCapacities("Metallurgy: coal and steel", new Ressource[] { Adjectif.rawIron, Adjectif.iron }, new Ressource[] { Coal, Steel });
	}

	// For each pair: every template container that can hold from[i] can hold as much of to[i].
	internal static void MirrorCapacities(string what, Ressource[] from, Ressource[] to)
	{
		int changed = 0;
		List<string> names = new List<string>();
		foreach (Factory factory in Factory.Factories.Values)
		{
			if (factory == null)
			{
				continue;
			}
			if (factory.cachedTemplates.Count == 0)
			{
				factory.ResetTemplateCache();
			}
			foreach (FactoryTemplate template in factory.cachedTemplates.Values)
			{
				if (template == null)
				{
					continue;
				}
				foreach (Recepteur recepteur in template.GetComponentsInChildren<Recepteur>(true))
				{
					bool any = false;
					for (int i = 0; i < from.Length; i++)
					{
						any |= Mirror(recepteur._baseCapacity, from[i], to[i]);
					}
					if (any)
					{
						changed++;
						if (names.Count < 40)
						{
							names.Add(factory.name + "/" + template.gameObject.name);
						}
					}
				}
			}
		}
		Plugin.Log.LogInfo(what + " capacity added to " + changed + " containers: " + string.Join(", ", names.ToArray()));
	}

	private static bool Mirror(Description capacity, Ressource from, Ressource to)
	{
		if (capacity == null || !capacity.DicoAdjectif.TryGetValue(from.GetType(), out Adjectif source) || source.quantifiable == null || source.quantifiable.valeur <= 0)
		{
			return false;
		}
		if (capacity.DicoAdjectif.TryGetValue(to.GetType(), out Adjectif current) && current.quantifiable != null && current.quantifiable.valeur >= source.quantifiable.valeur)
		{
			return false;
		}
		capacity.Remove(to.GetType());
		capacity.DicoAdjectif.Add(Adjectif.New(to.GetType(), source.quantifiable.valeur));
		capacity.DicoAdjectif.Preloaded = false;
		return true;
	}

	private static void RaiseCapacity(Factory factory, string template, Ressource resource, int amount)
	{
		Recepteur recepteur = factory.GetTemplate(template).GetComponent<Recepteur>();
		if (recepteur == null)
		{
			return;
		}
		Description capacity = recepteur._baseCapacity;
		if (capacity.DicoAdjectif.TryGetValue(resource.GetType(), out Adjectif current) && current.quantifiable != null && current.quantifiable.valeur >= amount)
		{
			return;
		}
		capacity.Remove(resource.GetType());
		capacity.DicoAdjectif.Add(Adjectif.New(resource.GetType(), amount));
		capacity.DicoAdjectif.Preloaded = false;
	}
}

[Feature(Features.Metallurgy, Features.MetallurgyInfo)]
[HarmonyPatch(typeof(Factory), nameof(Factory.Awake))]
internal static class MetallurgyFactoryPatch
{
	private static void Postfix()
	{
		Metallurgy.SetUpFactories();
	}
}

// The recipes and the Lua values, once the game has made its own (the first Lua script set-up).
[Feature(Features.Metallurgy, Features.MetallurgyInfo)]
[HarmonyPatch(typeof(LuaCrafting), nameof(LuaCrafting.Register))]
internal static class MetallurgyLuaPatch
{
	private static bool _done;

	private static void Postfix()
	{
		Run();
	}

	// Also run by the later recipe makers (research station, market) before their own, so the steel and charcoal
	// recipes always get the same ids, whatever order the postfixes run in.
	internal static void Run()
	{
		if (_done || !Metallurgy.IsOn())
		{
			return;
		}
		_done = true;
		try
		{
			Metallurgy.RegisterRecipes();
			Metallurgy.RegisterLua();
			Metallurgy.MirrorCapacities();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("Metallurgy: could not register the recipes: " + ex);
		}
	}
}
