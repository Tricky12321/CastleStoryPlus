using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using Brix.Engine;
using Brix.Engine.Environment;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using Brix.IO.Serialization.Contracts;
using Brix.Legacy;
using Brix.Utils;
using CastleStoryPlus.Building;
using CastleStoryPlus.Core;
using HarmonyLib;
using Newtonsoft.Json.Serialization;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Nature;

// Bricktrons plant trees: "Sapling" in the build menu's structures, a 1 x 1 blueprint on the ground that costs
// nothing but a builder's work. The planted sapling shows the map's own tree, very small, and grows slowly to full
// size over [Saplings] GrowMinutes of game time (30; paused with the game, faster at a higher game speed). Then it
// becomes an ordinary tree of the map (TreeRegistry), which harvest areas fell like any other. How far each sapling
// has grown is saved with the game (on the faction, as the team upgrades are). The sapling building is a clone of
// the knight statue (a plain decoration), with its model replaced. Added to the factories also with the feature
// switched off, so saved saplings load (SavedContent).
[Feature(Features.Saplings, Features.SaplingsInfo)]
internal static class Saplings
{
	internal const string Name = "Sapling";

	internal static ConfigEntry<float> GrowMinutes;

	// Size of the young tree when planted and when grown (1 = the map's full grown tree).
	internal const float StartScale = 0.06f;

	internal const float EndScale = 1f;

	// Growth of every sapling, by the cell it stands in (0 planted, 1 grown); filled from saves too.
	internal static readonly Dictionary<XYZ, float> Growth = new Dictionary<XYZ, float>();

	// The faction of each sapling, for saving them with the right faction.
	internal static readonly Dictionary<XYZ, Faction> Owners = new Dictionary<XYZ, Faction>();

	internal static readonly CustomBuilding Building = new CustomBuilding(Name, 1, 1, 1)
	{
		BlueprintSource = "KnightStatue",
		BuildingSource = "KnightStatue",
		KeepChildren = new string[1] { "OverheadTarget" },
		BuildModel = BuildModel,
		BuildCost = (Description cost) => { },
		SetupBuilding = (GameObject go) => go.AddComponent<SaplingGrowth>()
	};

	private static void Enable()
	{
		GrowMinutes = Plugin.Cfg.Bind("Saplings", "GrowMinutes", 30f, new ConfigDescription("Minutes of game time a planted sapling takes to grow into a full tree.", new AcceptableValueRange<float>(1f, 240f)));
		UI.BuildIcons.Register();
		GameUi.BuildCatalog.AddStructure(Features.Saplings, Name, "Tree sapling", GameUi.BuildCatalog.ModIcon("sapling", "_Knight_Statue"), 1, "Pallet");
		LuaInjection.AddPatch(Features.Saplings, "LUI/Meta/Meta_Structure.lua", "Hotkey = \"project_Stockpile\",\t\tgroupId = 1 })\n", LuaInjection.Mode.InsertAfter,
			"_t.Add(AssetKey.New(\"Blueprints\", \"" + Name + "\"),\t\t{ Name = ||\"Tree sapling\",\t\tIcon = " + UI.BuildIcons.Lua("sapling", "_Knight_Statue") + ",\tHotkey = \"\",\t\tgroupId = 1 })\n");
	}

	internal static void AddToFactories()
	{
		Building.AddToFactories();
	}

	// The blueprint's ghost: a small trunk with a tuft of leaves. The built sapling draws the map's tree instead
	// (SaplingGrowth), so it gets an empty model.
	private static GameObject BuildModel(Transform parent, Vector3 centre, Material material, Material ghost)
	{
		BoxModel m = new BoxModel("Sapling", 7);
		if (ghost != null)
		{
			m.Box(0.12f, 0.6f, 0.12f, BoxModel.WoodDark, 0f, 0.3f, 0f);
			m.Box(0.45f, 0.35f, 0.45f, BoxModel.LeatherGreen, 0f, 0.7f, 0f);
		}
		else
		{
			m.Box(0.02f, 0.02f, 0.02f, BoxModel.WoodDark, 0f, 0.01f, 0f);
		}
		return m.Build(parent, centre, material, ghost);
	}

	internal static float GrowSeconds => ((GrowMinutes != null) ? GrowMinutes.Value : 30f) * 60f;

	// The tree a grown sapling becomes: the map's own look for that spot, at full size.
	internal static int TreeInfo(XYZ cell)
	{
		return (TreeRegistry.GetPseudoRandomArbreInfo(cell.ToScalaire()) & 0xF) | (15 << 4);
	}

	// Server and clients: the grown sapling becomes a tree of the map.
	internal static void AddTree(XYZ cell)
	{
		TreeRegistry registry = TreeRegistry.Instance;
		if (registry == null || registry.HasTree(cell))
		{
			return;
		}
		if (registry.Add(cell, TreeInfo(cell)))
		{
			Architecte.AppliquerChangements();
			Plugin.Log.LogInfo("Saplings: a tree has grown at " + cell);
		}
	}

	// Save text for one faction's saplings: "x,y,z,growth;...".
	internal static string Encode(Faction faction)
	{
		StringBuilder text = new StringBuilder();
		foreach (KeyValuePair<XYZ, float> sapling in Growth)
		{
			if (Owners.TryGetValue(sapling.Key, out Faction owner) && owner != faction)
			{
				continue;
			}
			if (text.Length > 0)
			{
				text.Append(';');
			}
			text.Append(sapling.Key.x).Append(',').Append(sapling.Key.y).Append(',').Append(sapling.Key.z).Append(',').Append(sapling.Value.ToString("0.####", CultureInfo.InvariantCulture));
		}
		return text.ToString();
	}

	internal static void Decode(Faction faction, string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return;
		}
		foreach (string entry in text.Split(';'))
		{
			string[] parts = entry.Split(',');
			if (parts.Length != 4 || !int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y) || !int.TryParse(parts[2], out int z) || !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float growth))
			{
				continue;
			}
			XYZ cell = new XYZ(x, y, z);
			Growth[cell] = Mathf.Clamp01(growth);
			if (faction != null)
			{
				Owners[cell] = faction;
			}
		}
	}
}

// On a planted sapling: grows it and draws it, as the game draws a single tree (DamagedTree).
internal class SaplingGrowth : MonoBehaviour
{
	private XYZ _cell;

	private bool _placed;

	private bool _grown;

	private float _growth;

	private Transform _tree;

	private Material _material;

	private Texture2D _warp;

	private float _shownScale = -1f;

	private void Start()
	{
		GameComponent component = GetComponent<GameComponent>();
		if (component == null || GetComponent<Blueprint>() != null)
		{
			enabled = false;
			return;
		}
		_cell = Cell(component);
		_placed = true;
		if (Saplings.Growth.TryGetValue(_cell, out float saved))
		{
			_growth = saved;
		}
		Saplings.Growth[_cell] = _growth;
		if (component.faction != null)
		{
			Saplings.Owners[_cell] = component.faction;
		}
		MakeTree();
	}

	// The cell the sapling stands in: the lowest of its body.
	private static XYZ Cell(GameComponent component)
	{
		IVolume volume = component.volume;
		bool found = false;
		XYZ best = XYZ.FromVector3(component.transform.position);
		if (volume != null)
		{
			foreach (XYZ cell in volume.GetCases(VProperty.occupy))
			{
				if (!found || cell.y < best.y)
				{
					best = cell;
					found = true;
				}
			}
		}
		return best;
	}

	private void MakeTree()
	{
		try
		{
			MegaVoxelTreePrinter drawer = MegaVoxel.Common.treeDrawer;
			Material[] materials = MegaVoxel.Common.treeMaterials;
			if (drawer == null || materials == null || materials.Length == 0)
			{
				return;
			}
			GameObject tree = new GameObject("SaplingTree");
			_tree = tree.transform;
			_tree.SetParent(transform, worldPositionStays: false);
			_tree.position = _cell.ToVector3() - Vector3.up / 2f;
			_tree.rotation = Quaternion.identity;
			byte info = (byte)Saplings.TreeInfo(_cell);
			NatureBatcher.GetWarpTex(ref _warp, 1, (int i) => new Color32(0, 0, 0, info));
			_material = new Material(materials[0]);
			_material.SetTexture("_WarpTex", _warp);
			_material.SetVector("_TCenter", -Vector3.up / 2f);
			tree.AddComponent<MeshFilter>().sharedMesh = drawer.GetCombinedMesh(1, 1, 0, _warp.width, 0);
			tree.AddComponent<MeshRenderer>().sharedMaterial = _material;
			ShowGrowth();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("Saplings: could not draw the sapling at " + _cell + ": " + ex.Message);
		}
	}

	private void ShowGrowth()
	{
		if (_tree == null)
		{
			return;
		}
		// Slow at first, as a young tree: the size follows the growth eased in.
		float scale = Mathf.Lerp(Saplings.StartScale, Saplings.EndScale, _growth * _growth * (3f - 2f * _growth));
		if (Mathf.Abs(scale - _shownScale) < 0.002f)
		{
			return;
		}
		_shownScale = scale;
		_tree.localScale = Vector3.one * scale;
	}

	private void Update()
	{
		if (!_placed || _grown)
		{
			return;
		}
		_growth = Mathf.Min(1f, _growth + Time.deltaTime / Saplings.GrowSeconds);
		Saplings.Growth[_cell] = _growth;
		ShowGrowth();
		if (_growth < 1f)
		{
			return;
		}
		_grown = true;
		Saplings.Growth.Remove(_cell);
		Saplings.Owners.Remove(_cell);
		Saplings.AddTree(_cell);
		if (NetworkServer.active)
		{
			gameObject.SafeNetworkDestroy();
		}
	}

	private void OnDestroy()
	{
		if (_placed && !_grown)
		{
			// Taken down (or, on a client, removed by the host as it finished growing there first).
			if (!NetworkServer.active && _growth > 0.97f)
			{
				Saplings.AddTree(_cell);
			}
			Saplings.Growth.Remove(_cell);
			Saplings.Owners.Remove(_cell);
		}
		if (_material != null)
		{
			Destroy(_material);
		}
		if (_warp != null)
		{
			Destroy(_warp);
		}
	}
}

// Saves the saplings' growth as an extra property on each faction ("saplings": "x,y,z,growth;..."). The unmodded
// game ignores unknown properties. Always on, as the sapling building is (SavedContent): planted saplings keep growing
// with the feature switched off, and a save made then keeps their growth.
[Feature]
[HarmonyPatch(typeof(DefaultGameContractResolver), nameof(DefaultGameContractResolver.GenerateDefaultMonoBehaviourContract))]
internal static class SaplingsSave
{
	private const string Property = "saplings";

	private static void Enable()
	{
		GameSession.OnLeave(() =>
		{
			Saplings.Growth.Clear();
			Saplings.Owners.Clear();
		});
	}

	private class GrowthProvider : IValueProvider
	{
		public object GetValue(object target)
		{
			return Saplings.Encode(target as Faction);
		}

		public void SetValue(object target, object value)
		{
			Saplings.Decode(target as Faction, Convert.ToString(value));
		}
	}

	private static void Postfix(Type objectType, JsonObjectContract __result)
	{
		if (objectType != typeof(Faction) || __result == null || __result.Properties.Contains(Property))
		{
			return;
		}
		__result.Properties.AddProperty(new JsonProperty
		{
			PropertyName = Property,
			UnderlyingName = Property,
			PropertyType = typeof(string),
			DeclaringType = typeof(Faction),
			ValueProvider = new GrowthProvider(),
			Readable = true,
			Writable = true
		});
	}
}
