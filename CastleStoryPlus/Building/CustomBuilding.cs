using System;
using System.Collections.Generic;
using System.Reflection;
using Brix.Components;
using Brix.Engine;
using Brix.External.Factories;
using Brix.External.Factories.Templates;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using Brix.Game.Semantique;
using Brix.Game.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CastleStoryPlus.Building;

// A mod building with a crafting station, added to the game's factories when they load: a blueprint
// (Blueprints/<Name>) and the building (ObjetsDynamiques/<Name>), both cloned from the workbench, resized to
// Width x Depth blocks and Height high, with their own model and build cost. The building gets the mod's station
// in place of the workbench station. Saved buildings load through the same factory keys. Used by the market, the
// smithy and the armoury; call AddToFactories from a Factory.Awake postfix.
internal sealed class CustomBuilding
{
	// The templates the building is cloned from (in the blueprint and the building factory).
	public string BlueprintSource = "Workbench";

	public string BuildingSource = "Workbench";

	// Children of the source kept as they are; null hides only the source's "Visuals" (the workbench's model),
	// otherwise every other child is hidden (e.g. a pallet's boards and piles). "OverheadTarget" (where the
	// game shows icons and counts) is moved over the roof.
	public string[] KeepChildren;

	// Optional: the blueprint gets the placement check of this blueprint template instead of its source's (the
	// stockpile's lets it be placed over other buildings).
	public string ValidatorFrom;

	public readonly string Name;

	public readonly int Width;

	public readonly int Depth;

	public readonly int Height;

	public readonly Factory.AssetKey BlueprintKey;

	public readonly Factory.AssetKey BuildingKey;

	// (parent, centre, material, ghost): builds the model under parent; ghost is set for the blueprint.
	public Func<Transform, Vector3, Material, Material, GameObject> BuildModel;

	// Fills the blueprint's cost (its emptied capacity).
	public Action<Description> BuildCost;

	// Sets up the building: its station (see ReplaceStation) and what it can hold.
	public Action<GameObject> SetupBuilding;

	// Optional: makes the building walk-in (see ApplyLayout). One string per row of the footprint, from the back
	// (-z) to the front (+z), one character per block from left (-x) to right (+x):
	//   '#' wall or furniture (blocks walking), '.' floor, 'A' floor where workers stand to deliver and fetch,
	//   'O' floor where a worker stands to work the station (also delivers there).
	public string[] Layout;

	// A walk-in building's model is lowered by this much, so its floor (built on a raised plinth) is at the
	// ground the workers walk on.
	private const float WalkInFloorDrop = 0.28f;

	// The lowest corner of the body after Resize.
	private XYZ _bodyMin;

	public CustomBuilding(string name, int width, int depth, int height)
	{
		Name = name;
		Width = width;
		Depth = depth;
		Height = height;
		BlueprintKey = new Factory.AssetKey("Blueprints", name);
		BuildingKey = new Factory.AssetKey("ObjetsDynamiques", name);
	}

	// Factories with the same name are merged: a later one moves its templates into the first and is destroyed
	// inside its own Awake. So look at the registered factories (not the instance) after every Awake.
	public void AddToFactories()
	{
		TryCreate(BlueprintKey.Factory, blueprint: true);
		TryCreate(BuildingKey.Factory, blueprint: false);
	}

	private void TryCreate(string name, bool blueprint)
	{
		if (!Factory.Factories.TryGetValue(name, out Factory factory) || factory == null)
		{
			return;
		}
		try
		{
			if (factory.cachedTemplates.Count == 0)
			{
				factory.ResetTemplateCache();
			}
			if (factory.cachedTemplates.ContainsKey(Name) || !factory.cachedTemplates.ContainsKey(blueprint ? BlueprintSource : BuildingSource))
			{
				return;
			}
			Create(factory, blueprint);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError(Name + ": could not add the building to factory " + name + ": " + ex);
		}
	}

	private void Create(Factory factory, bool blueprint)
	{
		GameObject source = factory.GetTemplate(blueprint ? BlueprintSource : BuildingSource).gameObject;
		Material material = FirstMaterial(source, blueprint);
		GameObject building = Object.Instantiate(source, factory.transform);
		building.name = Name;
		Vector3 centre = Resize(building);
		if (Layout != null)
		{
			centre.y -= WalkInFloorDrop;
			if (!blueprint)
			{
				ApplyLayout(building);
			}
		}
		HideSourceModel(building, centre);
		if (blueprint)
		{
			BuildModel(building.transform, centre, null, material);
			building.GetComponent<Blueprint>().BlueprintResult = BuildingKey;
			CopyValidator(factory, building);
			// A source that is the same every way round (the stockpile) has its turning locked; ours are not.
			AbstractFitter fitter = building.GetComponent<AbstractFitter>();
			if (fitter != null && (fitter._lockRotation || fitter._oneRotationPerAxis))
			{
				fitter._lockRotation = false;
				fitter._oneRotationPerAxis = false;
				Plugin.Log.LogInfo(Name + ": blueprint can be turned all four ways");
			}
			Recepteur recepteur = building.GetComponent<Recepteur>();
			if (recepteur != null)
			{
				RemoveResources(recepteur._baseCapacity);
				BuildCost(recepteur._baseCapacity);
				Plugin.Log.LogInfo(Name + ": blueprint cost " + Describe(recepteur._baseCapacity));
			}
		}
		else
		{
			BuildModel(building.transform, centre, material, null);
			HideChild(building, "Blueprints");
			HideChild(building, "ProducedItems");
			SetupBuilding(building);
		}
		// The clone carries the workbench's imprint, which the template takes its key from (and saves use).
		FactoryImprint imprint = building.GetComponent<FactoryImprint>();
		if (imprint != null)
		{
			imprint.AssetKey = new Factory.AssetKey(factory.name, Name);
		}
		Factory.AssetKey key = factory.AddAsset<CloneTemplate>(building);
		Brix.Network.FactoryRegistrator.RegisterHandlerForTemplate(key);
		if (blueprint)
		{
			AddBlueprintMapping();
		}
		Plugin.Log.LogInfo(Name + ": added " + key);
	}

	// The material of the workbench's own model. Not just the first renderer: the building also carries the
	// blueprint ghost (child "Blueprints"), whose striped see-through shader made the built building look unbuilt.
	private Material FirstMaterial(GameObject source, bool blueprint)
	{
		Transform visuals = source.transform.Find("Visuals") ?? source.transform.Find("Visual");
		Material found = (!blueprint && visuals != null) ? FirstMaterialUnder(visuals, null) : null;
		if (found == null)
		{
			found = FirstMaterialUnder(source.transform, blueprint ? null : source.transform.Find("Blueprints"));
		}
		Plugin.Log.LogInfo(Name + ": " + (blueprint ? "blueprint" : "building") + " material " + ((found != null) ? (found.name + " (" + found.shader.name + ")") : "none"));
		return found;
	}

	private static Material FirstMaterialUnder(Transform root, Transform skip)
	{
		foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(includeInactive: true))
		{
			if (renderer.sharedMaterial == null || (skip != null && renderer.transform.IsChildOf(skip)))
			{
				continue;
			}
			return renderer.sharedMaterial;
		}
		return null;
	}

	private void CopyValidator(Factory factory, GameObject go)
	{
		ValidatorComponent validator = go.GetComponent<ValidatorComponent>();
		if (validator == null || ValidatorFrom == null || !factory.cachedTemplates.ContainsKey(ValidatorFrom))
		{
			return;
		}
		ValidatorComponent from = factory.GetTemplate(ValidatorFrom).gameObject.GetComponent<ValidatorComponent>();
		if (from == null)
		{
			return;
		}
		Plugin.Log.LogInfo(Name + ": placement check " + Describe(validator) + " replaced by " + ValidatorFrom + "'s " + Describe(from));
		validator.validatorType = from.validatorType;
		validator.MaxMissingSupport = from.MaxMissingSupport;
		validator.CanPlaceInvalid = from.CanPlaceInvalid;
		validator._validator = null;
	}

	private static string Describe(ValidatorComponent validator)
	{
		Type type = (validator.validatorType != null) ? validator.validatorType.Type : null;
		return ((type != null) ? type.Name : "none") + " (max missing support " + validator.MaxMissingSupport + ")";
	}

	private void HideSourceModel(GameObject go, Vector3 centre)
	{
		if (KeepChildren == null)
		{
			HideChild(go, "Visuals");
			return;
		}
		foreach (Transform child in go.transform)
		{
			if (child.name == "OverheadTarget")
			{
				child.localPosition = new Vector3(centre.x, centre.y + Height, centre.z);
			}
			if (Array.IndexOf(KeepChildren, child.name) < 0)
			{
				child.gameObject.SetActive(false);
			}
		}
	}

	private static void HideChild(GameObject go, string child)
	{
		Transform found = go.transform.Find(child);
		if (found != null)
		{
			found.gameObject.SetActive(false);
		}
	}

	// Puts the mod's station on the building in place of the workbench station, keeping its crafting state and labor.
	public static T ReplaceStation<T>(GameObject go) where T : CraftingStation
	{
		Workbench workbench = go.GetComponent<Workbench>();
		T station = go.AddComponent<T>();
		if (workbench != null)
		{
			station.state = workbench.state;
			station.craftingLabor = workbench.craftingLabor;
			PointSiblingsAtSelf(go, station);
			Object.DestroyImmediate(workbench);
		}
		else
		{
			station.state = go.GetComponent<CraftingState>();
			station.craftingLabor = go.GetComponent<CraftingLabor>();
		}
		return station;
	}

	// Components that referenced the replaced station (the overhead queue display, and the selection behaviour
	// nested in the selectable injector) now reference the new one.
	public static void PointSiblingsAtSelf(GameObject go, CraftingStation station)
	{
		foreach (MonoBehaviour behaviour in go.GetComponentsInChildren<MonoBehaviour>(includeInactive: true))
		{
			if (behaviour == null || behaviour == station)
			{
				continue;
			}
			PointFieldsAt(behaviour, station, 0);
		}
	}

	private static void PointFieldsAt(object target, CraftingStation station, int depth)
	{
		foreach (FieldInfo field in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
		{
			if (typeof(CraftingStation).IsAssignableFrom(field.FieldType))
			{
				if ((CraftingStation)field.GetValue(target) != station)
				{
					field.SetValue(target, station);
				}
			}
			else if (depth < 2 && field.FieldType.IsClass && field.FieldType.IsSerializable && field.FieldType.Assembly != typeof(object).Assembly && !typeof(Object).IsAssignableFrom(field.FieldType))
			{
				// Serialized plain classes inside a component, e.g. SelectableCraftingStationBehaviour.station.
				object nested = field.GetValue(target);
				if (nested != null)
				{
					PointFieldsAt(nested, station, depth + 1);
				}
			}
		}
	}

	// Replaces the resources the building's receptor holds with the given ones; other properties stay.
	public static void SetStorage(GameObject go, string logName, IEnumerable<KeyValuePair<Ressource, int>> resources)
	{
		Recepteur recepteur = go.GetComponent<Recepteur>();
		if (recepteur == null)
		{
			return;
		}
		Description capacity = recepteur._baseCapacity;
		Plugin.Log.LogInfo(logName + ": workbench storage was " + Describe(capacity));
		RemoveResources(capacity);
		foreach (KeyValuePair<Ressource, int> resource in resources)
		{
			capacity.Add(Adjectif.New(resource.Key.GetType(), resource.Value));
		}
		if (capacity.Has(Adjectif.encombrement))
		{
			capacity.Set(Adjectif.encombrement, 100000);
		}
		recepteur._acceptMultipleResouceType = true;
	}

	public static void RemoveResources(Description description)
	{
		List<Type> remove = new List<Type>();
		foreach (KeyValuePair<Type, Adjectif> entry in description.DicoAdjectif)
		{
			if (entry.Value is Ressource)
			{
				remove.Add(entry.Key);
			}
		}
		foreach (Type type in remove)
		{
			description.Remove(type);
		}
	}

	public static string Describe(Description description)
	{
		List<string> parts = new List<string>();
		foreach (KeyValuePair<Type, Adjectif> entry in description.DicoAdjectif)
		{
			parts.Add(entry.Key.Name + ((entry.Value.quantifiable != null) ? ("=" + entry.Value.quantifiable.valeur) : string.Empty));
		}
		return string.Join(", ", parts.ToArray());
	}

	// Grows the workbench's volume to Width x Depth x Height. Its body (obstacle cells; phantom cells on the
	// blueprint) becomes a box, the layers under it (support required) cover the new footprint, and the cells
	// around it (where workers stand and work) keep their side of the box. Returns the centre of the new box in
	// local coordinates, where the model goes.
	private Vector3 Resize(GameObject go)
	{
		const long body = (long)(VPropertyMask.obstacle | VPropertyMask.fantome);
		VolumeDataComponent volume = go.GetComponent<VolumeDataComponent>();
		BoxCollider collider = go.GetComponent<BoxCollider>();
		Dictionary<XYZ, long> cells = volume.IndexedVolumetricProperties;
		List<string> dump = new List<string>();
		int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
		int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
		foreach (KeyValuePair<XYZ, long> cell in cells)
		{
			dump.Add(cell.Key.x + "," + cell.Key.y + "," + cell.Key.z + "=" + (VPropertyMask)cell.Value);
			if ((cell.Value & body) == 0)
			{
				continue;
			}
			minX = Mathf.Min(minX, cell.Key.x);
			minY = Mathf.Min(minY, cell.Key.y);
			minZ = Mathf.Min(minZ, cell.Key.z);
			maxX = Mathf.Max(maxX, cell.Key.x);
			maxY = Mathf.Max(maxY, cell.Key.y);
			maxZ = Mathf.Max(maxZ, cell.Key.z);
		}
		Plugin.Log.LogInfo(Name + ": " + go.name + " source volume " + string.Join(" ", dump.ToArray()) + (collider != null ? (" collider " + collider.center + " " + collider.size) : string.Empty));
		if (minX == int.MaxValue)
		{
			Plugin.Log.LogWarning(Name + ": no body cells in the source volume, size unchanged");
			return Vector3.zero;
		}
		// Local position of a cell centre = cell + offset, taken from the source collider around its body.
		Vector3 oldCentre = new Vector3((minX + maxX) / 2f, 0f, (minZ + maxZ) / 2f);
		Vector3 offset = (collider != null) ? new Vector3(collider.center.x - oldCentre.x, 0f, collider.center.z - oldCentre.z) : Vector3.zero;
		int growX = Width - (maxX - minX + 1);
		int growY = Height - (maxY - minY + 1);
		int growZ = Depth - (maxZ - minZ + 1);
		IndexedVolumetricProperties resized = new IndexedVolumetricProperties();
		HashSet<int> layersBelow = new HashSet<int>();
		foreach (KeyValuePair<XYZ, long> cell in cells)
		{
			bool inColumn = cell.Key.x >= minX && cell.Key.x <= maxX && cell.Key.z >= minZ && cell.Key.z <= maxZ;
			if (inColumn && cell.Key.y < minY)
			{
				layersBelow.Add(cell.Key.y);
			}
		}
		for (int x = minX; x < minX + Width; x++)
		{
			for (int z = minZ; z < minZ + Depth; z++)
			{
				int fromX = Mathf.Min(x, maxX);
				int fromZ = Mathf.Min(z, maxZ);
				foreach (int y in layersBelow)
				{
					long below;
					if (cells.TryGetValue(new XYZ(fromX, y, fromZ), out below))
					{
						resized[new XYZ(x, y, z)] = below;
					}
				}
				for (int y = minY; y < minY + Height; y++)
				{
					long mask;
					if (!cells.TryGetValue(new XYZ(fromX, Mathf.Min(y, maxY), fromZ), out mask) || (mask & body) == 0)
					{
						mask = cells[new XYZ(minX, minY, minZ)];
					}
					resized[new XYZ(x, y, z)] = mask;
				}
			}
		}
		foreach (KeyValuePair<XYZ, long> cell in cells)
		{
			bool inColumn = cell.Key.x >= minX && cell.Key.x <= maxX && cell.Key.z >= minZ && cell.Key.z <= maxZ;
			if (inColumn)
			{
				continue;
			}
			XYZ moved = new XYZ(cell.Key.x > maxX ? cell.Key.x + growX : cell.Key.x, cell.Key.y > maxY ? cell.Key.y + growY : cell.Key.y, cell.Key.z > maxZ ? cell.Key.z + growZ : cell.Key.z);
			if (!resized.ContainsKey(moved))
			{
				resized[moved] = cell.Value;
			}
		}
		volume._indexedVolumetricProperties = resized;
		_bodyMin = new XYZ(minX, minY, minZ);
		Vector3 centre = new Vector3(minX + (Width - 1) / 2f, 0f, minZ + (Depth - 1) / 2f) + offset;
		if (collider != null)
		{
			float bottom = collider.center.y - collider.size.y / 2f;
			collider.size = new Vector3(Width, Height, Depth);
			collider.center = new Vector3(centre.x, bottom + Height / 2f, centre.z);
			centre.y = bottom;
		}
		return centre;
	}

	// Makes the building walk-in. The pathfinder only looks at the cell a worker's feet are in, and an empty cell
	// over the ground is walkable unless a volume marks it as an obstacle. So the floor cells of the body's bottom
	// layer (and the layer above) lose their obstacle bit; they keep "occupy", so nothing can be built or dropped
	// inside. Workers deliver, fetch and work at access cells, which are now on the floor inside (the game's ring
	// of access cells round the outside is removed, or workers would stop there). The blueprint keeps the game's
	// cells, so builders still work from outside.
	private void ApplyLayout(GameObject go)
	{
		const long clear = (long)(VPropertyMask.obstacle | VPropertyMask.character);
		const long stand = (long)(VPropertyMask.access | VPropertyMask.operation | VPropertyMask.breakableAccess);
		VolumeDataComponent volume = go.GetComponent<VolumeDataComponent>();
		IndexedVolumetricProperties cells = volume._indexedVolumetricProperties;
		// A pallet's cells are walkable (over the pallet); the walk-in floor is the ground instead.
		List<XYZ> walkable = new List<XYZ>();
		List<XYZ> outside = new List<XYZ>();
		foreach (KeyValuePair<XYZ, long> cell in cells)
		{
			if ((cell.Value & (long)VPropertyMask.walkable) != 0)
			{
				walkable.Add(cell.Key);
			}
			if ((cell.Value & stand) != 0)
			{
				outside.Add(cell.Key);
			}
		}
		foreach (XYZ position in walkable)
		{
			cells[position] &= ~(long)VPropertyMask.walkable;
		}
		foreach (XYZ position in outside)
		{
			long mask = cells[position] & ~stand;
			if (mask == 0)
			{
				cells.Remove(position);
			}
			else
			{
				cells[position] = mask;
			}
		}
		int access = 0;
		for (int row = 0; row < Layout.Length && row < Depth; row++)
		{
			string line = Layout[row];
			for (int column = 0; column < line.Length && column < Width; column++)
			{
				char kind = line[column];
				if (kind == '#')
				{
					continue;
				}
				for (int y = 0; y < 2; y++)
				{
					XYZ position = new XYZ(_bodyMin.x + column, _bodyMin.y + y, _bodyMin.z + row);
					long mask;
					if (!cells.TryGetValue(position, out mask))
					{
						continue;
					}
					mask = (mask & ~clear) | (long)VPropertyMask.occupy;
					if (y == 0 && (kind == 'A' || kind == 'O'))
					{
						mask |= (long)VPropertyMask.access;
						access++;
						if (kind == 'O')
						{
							mask |= (long)VPropertyMask.operation;
						}
					}
					cells[position] = mask;
				}
			}
		}
		// The workers' knowledge keeps a building's access cells as bits of an int.
		if (access > 32)
		{
			Plugin.Log.LogWarning(Name + ": " + access + " access cells, only 32 are used");
		}
		Plugin.Log.LogInfo(Name + ": walk-in, " + access + " access cells inside, " + outside.Count + " outside removed");
	}

	// The game maps blueprints to buildings once; add this one if that has already happened.
	private void AddBlueprintMapping()
	{
		BlueprintMapper mapper = BrixSingleton<BlueprintMapper>.Instance;
		if (mapper == null)
		{
			return;
		}
		if (!mapper.blueprint2Concrete.ContainsKey(BlueprintKey))
		{
			mapper.blueprint2Concrete.Add(BlueprintKey, BuildingKey);
		}
		if (!mapper.concrete2Blueprint.ContainsKey(BuildingKey))
		{
			mapper.concrete2Blueprint.Add(BuildingKey, BlueprintKey);
		}
	}
}
