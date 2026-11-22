using System;
using System.Collections.Generic;
using Brix.External.Factories;
using Brix.UI.Icons;
using UnityEngine;

namespace CastleStoryPlus.GameUi;

// The build menu's blocks and structures (LUI/Meta/Meta_StoneBlock.lua and Meta_Structure.lua) as C# data: each
// blueprint's name, icon, hotkey action and group (a section of its catalogue). The mod's own buildings and blocks
// are added by their features (AddStructure, AddBlock) before or after an entry of the same group.
internal static class BuildCatalog
{
	internal sealed class Entry
	{
		public Factory.AssetKey Key;

		public Func<string> Name;

		public Func<Sprite> Icon;

		// The Rewired action that picks it while its catalogue is open (Projects category), or empty.
		public string Hotkey = string.Empty;

		public int Group;

		// What it is for, shown on hover with its name (BuildMenuTooltips), or null.
		public Func<string> Tooltip;

		// The mod feature that added it ([Features]), or null for the game's own: hidden while that feature is off.
		public string Feature;

		public bool Shown()
		{
			return Feature == null || Plugin.IsEnabled(Feature);
		}
	}

	internal sealed class Group
	{
		public int Id;

		public string Title;
	}

	internal static readonly List<Entry> Blocks = new List<Entry>();

	// The blueprints the mod added (AddStructure, AddBlock), not the game's own.
	internal static readonly HashSet<string> Added = new HashSet<string>();

	internal static readonly List<Entry> Structures = new List<Entry>();

	// Stone blocks: 1 standard, 2 plugs, 3 round, 4 crenelations, 5 wood.
	internal static readonly Group[] BlockGroups = new Group[5]
	{
		new Group { Id = 1, Title = "##gamemenu_taskbar_stoneblocks" },
		new Group { Id = 2, Title = "##gamemenu_taskbar_plugs" },
		new Group { Id = 3, Title = "##gamemenu_taskbar_roundbricks" },
		new Group { Id = 4, Title = "##gamemenu_taskbar_crenelations" },
		new Group { Id = 5, Title = "##gamemenu_taskbar_woodblocks" }
	};

	// Structures: 1 storage, 2 production, 3 crafting, 4 structures, 5 wards, 6 decorations.
	internal static readonly Group[] StructureGroups = new Group[6]
	{
		new Group { Id = 1, Title = "##gamemenu_taskbar_storage" },
		new Group { Id = 2, Title = "##gamemenu_taskbar_production" },
		new Group { Id = 3, Title = "##gamemenu_taskbar_crafting" },
		new Group { Id = 4, Title = "##gamemenu_taskbar_structures" },
		new Group { Id = 5, Title = "##gamemenu_taskbar_wards" },
		new Group { Id = 6, Title = "##gamemenu_taskbar_decorations" }
	};

	static BuildCatalog()
	{
		Block("Stone_DoubleBrick", "brick", IconKeys._Brick, "project_StandardBrick", 1);
		Block("Stone_Brick", "halfbrick", IconKeys._Block, "project_HalfBrick", 1);
		Block("Stone_Staircase", "stair", IconKeys._Stair, "project_StairBrick", 1);
		Block("Stone_StairSingle", "stairsingle", IconKeys._Half_Stair, "project_HalfStair", 1);
		Block("Stone_StairSpiralLeft", "stairspiralleft", IconKeys._Spiral_Stair_A, "project_StairSpiralLeft", 1);
		Block("Stone_StairSpiralRight", "stairspiralright", IconKeys._Spiral_Stair_B, "project_StairSpiralRight", 1);
		Block("Stone_StairCornerLeft", "staircornerleft", IconKeys._Corner_Stair_A, "project_StairCornerLeft", 1);
		Block("Stone_StairCornerRight", "staircornerright", IconKeys._Corner_Stair_B, "project_StairCornerRight", 1);
		Block("Stone_StairCornerSingle", "staircornersingle", IconKeys._Half_Corner_Stair, "project_StairCornerSingle", 1);
		Block("Stone_InvertStairCornerSingle", "invertstaircornersingle", IconKeys._Half_Inverted_Corner_Stair, string.Empty, 1);
		Block("Stone_Column_Base", "stonecolumn_basic", IconKeys._Stone_Column_Base, "project_Pillar", 1);
		Block("Stone_Column2_Base", "stonecolumn_fluted", IconKeys._Stone_Column_Fluted, string.Empty, 1);
		Block("Stone_Column3_Base", "stonecolumn_pillar", IconKeys._Stone_Column_Pillar, string.Empty, 1);
		Block("Stone_Column4_Base", "stonecolumn_round", IconKeys._Stone_Column_Round, string.Empty, 1);
		Block("Stone_Column5_Base", "stonecolumn_square", IconKeys._Stone_Column_Square, string.Empty, 1);
		Block("Stone_BeamPlug_OneSide", "plug", IconKeys._Plug_Block, "project_PlugBrick", 2);
		Block("Stone_BeamPlug_TwoSides", "plugh", IconKeys._Plug_H, "project_PlugH", 2);
		Block("Stone_BeamPlug_Corner", "plugl", IconKeys._Plug_L, "project_PlugL", 2);
		Block("Stone_BeamPlug_ThreeSides", "plugt", IconKeys._Plug_T, "project_PlugT", 2);
		Block("Stone_BeamPlug_AllSides", "plugx", IconKeys._Plug_X, "project_PlugX", 2);
		Block("Stone_DoubleBrick_RoundR", "curvedright", IconKeys._Round_Right, "project_RoundRight", 3);
		Block("Stone_DoubleBrick_RoundL", "curvedleft", IconKeys._Round_Left, "project_RoundLeft", 3);
		Block("Stone_DoubleBrick_RoundLR", "curvedbrick", IconKeys._Round_Half, "project_RoundHalf", 3);
		Block("Stone_Brick_Round", "roundbrick", IconKeys._Round_Quarter, "project_RoundBrick", 3);
		Block("Stone_Arch", "arch", IconKeys._Arch, "project_ArchBrick", 4);
		Block("Stone_Brace1", "corbel1", IconKeys._Corbel_1, "project_Corbel", 4);
		Block("Stone_Brace2", "corbel2", IconKeys._Corbel_2, "project_Corbel2", 4);
		Block("Stone_Merlon", "merlon", IconKeys._Merlon, "project_Merlon", 4);
		Block("Stone_MerlonCorner", "merloncorner", IconKeys._Merlon_Corner, "project_MerlonCorner", 4);
		Block("Stone_MerlonDouble", "merlondouble", IconKeys._Merlon_Double, "project_MerlonDouble", 4);
		Block("Stone_MerlonInverseCorner", "merlonconcave", IconKeys._Merlon_Concave, "project_MerlonConcave", 4);
		Block("Stone_Parapet1", "parapet1", IconKeys._Parapet_1, "project_Parapet", 4);
		Block("Stone_Parapet2", "parapet2", IconKeys._Parapet_2, "project_Parapet2", 4);
		Block("Stone_Parapet3", "parapet3", IconKeys._Parapet_3, "project_Parapet3", 4);
		Block("Wood_SingleLog", "log", IconKeys._Log, "project_Log", 5);
		Block("Wood_Beam", "beam", IconKeys._Beam, "project_WoodBeam", 5);
		Block("Wood_VerticalBeam", "woodcolumn", IconKeys._Vertical_Beam, "project_WoodColumn", 5);
		Block("Wood_Brace", "brace", IconKeys._Brace, "project_Brace", 5);
		Block("Wood_SinglePlank", "plank", IconKeys._Plank, "project_Plank", 5);
		Block("Wood_Ramp_TopA", "ramp", IconKeys._Ramp, "project_WoodenRamp", 5);
		Block("Wood_Ramp_JunctionA", "rampjunction", IconKeys._Ramp_Junction, "project_RampJunction", 5);

		Structure("Pallet", "stockpile", IconKeys._Stockpiled_Brick, "project_Stockpile", 1);
		Structure("Toolrack", "toolrack", IconKeys._Tool_Rack, "project_WeaponRack", 1);
		Structure("ChoppingBlock", "choppingblock", IconKeys._Chopping_Block, "project_ChoppingBlock", 2);
		Structure("Furnace", "furnace", IconKeys._Furnace, "project_Furnace", 2);
		Structure("Loom", "loom", IconKeys._Loom, "project_Loom", 2);
		Structure("Workbench", "workbench", IconKeys._Workbench, "project_Workbench", 3);
		Structure("Forge", "forge", IconKeys._Forge, "project_Forge", 3);
		Structure("Lab", "laboratory", IconKeys._Lab, "project_Laboratory", 3);
		Structure("Altar", "altar", IconKeys._Altar, "project_Altar", 3);
		Structure("MachineShop", "machineshop", IconKeys._Machine_Shop, "project_MachineShop", 3);
		Structure("RopeBridgePlacement", "ropebridge", IconKeys._Rope_Bridge, "project_RopeBridge", 4);
		Structure("ShakyDoor", "shakydoor", IconKeys._Shabby_Door, string.Empty, 4);
		Structure("Door", "door", IconKeys._Door, "project_Door", 4);
		Structure("HeavyDoor", "heavydoor", IconKeys._Heavy_Door, string.Empty, 4);
		Structure("Catapult", "catapult", IconKeys._Catapult, "project_Catapult", 4);
		Structure("Barrel", "explosivebarrel", IconKeys._Explosive_Barrel, "project_ExplosiveBarrel", 4);
		Structure("BearTrap", "beartrap", IconKeys._Beartrap, "project_BearTrap", 4);
		Structure("PylonGround", "pylonward", IconKeys._Pylon, "project_Pylon", 5);
		Structure("LanternWard", "lanternward", IconKeys._Lantern, "project_Lantern", 5);
		Structure("SentinelWard", "sentinelward", IconKeys._Sentinel, "project_Sentinel", 5);
		Structure("HealingWard", "healingward", IconKeys._Healing_Ward, "project_Healing", 5);
		Structure("CaptureWard", "captureward", IconKeys._Capture_Stone, "project_Capture", 5);
		Structure("Window", "window", IconKeys._Window, string.Empty, 6);
		Structure("DoubleWindow", "doublewindow", IconKeys._Double_Window, string.Empty, 6);
		Structure("KnightStatue", "knightstatue", IconKeys._Knight_Statue, string.Empty, 6);
		Structure("PaintingSmall", "paintingsmall", IconKeys._Painting_Small, string.Empty, 6);
		Structure("PaintingBig", "paintingbig", IconKeys._Painting_Big, string.Empty, 6);
		Structure("PaintingWide", "paintingwide", IconKeys._Painting_Wide, string.Empty, 6);
		Structure("PaintingTall", "paintingtall", IconKeys._Painting_Tall, string.Empty, 6);
	}

	private static void Block(string blueprint, string nameKey, IconKey icon, string hotkey, int group)
	{
		Blocks.Add(Game(blueprint, nameKey, icon, hotkey, group));
	}

	private static void Structure(string blueprint, string nameKey, IconKey icon, string hotkey, int group)
	{
		Structures.Add(Game(blueprint, nameKey, icon, hotkey, group));
	}

	private static Entry Game(string blueprint, string nameKey, IconKey icon, string hotkey, int group)
	{
		string key = "##gamemenu_constructionwheel_" + nameKey;
		return new Entry
		{
			Key = new Factory.AssetKey("Blueprints", blueprint),
			Name = () => GameUiState.Text(key),
			Icon = () => GameUiState.Icon(icon),
			Hotkey = hotkey,
			Group = group
		};
	}

	// One of the mod's structures, in a group, after the given blueprint (or at the end of the group).
	internal static void AddStructure(string feature, string blueprint, string name, Func<Sprite> icon, int group, string after = null)
	{
		Added.Add(blueprint);
		Add(Structures, feature, blueprint, name, icon, group, after);
	}

	// One of the mod's blocks, in a group, before the given blueprint (or at the end of the group).
	internal static void AddBlock(string feature, string blueprint, string name, Func<Sprite> icon, int group, string before = null)
	{
		Added.Add(blueprint);
		if (Blocks.Exists((Entry e) => e.Key.Name == blueprint))
		{
			return;
		}
		Entry entry = Entry_(blueprint, name, icon, group);
		entry.Feature = feature;
		int at = (before != null) ? Blocks.FindIndex((Entry e) => e.Key.Name == before) : -1;
		if (at < 0)
		{
			at = Blocks.FindLastIndex((Entry e) => e.Group == group) + 1;
		}
		Blocks.Insert(Mathf.Clamp(at, 0, Blocks.Count), entry);
	}

	private static void Add(List<Entry> list, string feature, string blueprint, string name, Func<Sprite> icon, int group, string after)
	{
		if (list.Exists((Entry e) => e.Key.Name == blueprint))
		{
			return;
		}
		Entry entry = Entry_(blueprint, name, icon, group);
		entry.Feature = feature;
		int at = (after != null) ? list.FindIndex((Entry e) => e.Key.Name == after) : -1;
		if (at < 0)
		{
			at = list.FindLastIndex((Entry e) => e.Group == group);
		}
		list.Insert(Mathf.Clamp(at + 1, 0, list.Count), entry);
	}

	private static Entry Entry_(string blueprint, string name, Func<Sprite> icon, int group)
	{
		return new Entry
		{
			Key = new Factory.AssetKey("Blueprints", blueprint),
			Name = () => name,
			Icon = icon,
			Group = group
		};
	}

	// The mod's icon file (Assets/Build/<file>.png), else the game's icon.
	internal static Func<Sprite> ModIcon(string file, IconKey fallback)
	{
		return () => UI.BuildIcons.SpriteFor(file) ?? GameUiState.Icon(fallback);
	}

	// The same with the game's icon by its IconKeys name.
	internal static Func<Sprite> ModIcon(string file, string fallback)
	{
		IconKey key = GameUiState.IconNamed(fallback);
		return () => (string.IsNullOrEmpty(file) ? null : UI.BuildIcons.SpriteFor(file)) ?? GameUiState.Icon(key);
	}

	internal static bool IsBlock(Factory.AssetKey key)
	{
		return key != null && Blocks.Exists((Entry e) => Equals(e.Key, key));
	}

	internal static bool IsStructure(Factory.AssetKey key)
	{
		return key != null && Structures.Exists((Entry e) => Equals(e.Key, key));
	}
}
