using System;
using System.Collections.Generic;
using Brix.Components;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Network;
using Brix.Game.Semantique;
using Brix.Lifecycle.Pooling;
using Brix.Game.Utils;
using Brix.IO.Serialization.Contracts;
using Brix.Lua;
using Brix.Utils;
using CastleStoryPlus.Core;
using HarmonyLib;
using MoonSharp.Interpreter;
using Motus.Behavior;
using Newtonsoft.Json.Serialization;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Economy;

// Looping a workshop's queue until there is enough in stock, per product: in the C# menu every recipe row of a
// workshop has its own "-  20  +" ("Off" while off; "+" from off turns it on, and looping with it). While the queue
// loops, a recipe only starts when the team's stockpiles (and tool racks) hold less than its limit of what it makes;
// a recipe without a limit always may. When every recipe in the queue has enough, the workshop waits with its queue
// and starts again by itself once a stock drops below its limit. Example: a loom looping fabric with limit 20 and
// rope with limit 5 keeps 20 fabric and 5 rope in stock.
// The limits are kept per workshop and recipe (the game's recipe id), saved with the workshop (an extra
// "loopLimits" property, ignored by the unmodded game) and synced to multiplayer clients; clients send changes to the
// host. A limit for the whole workshop (the Lua menu's single limit, and saves from before limits were per product:
// "loopLimit") counts for every recipe without one of its own. Clearing the queue clears the limits.
[Feature(Features.CraftLoopLimit, Features.CraftLoopLimitInfo)]
internal static class CraftLoopLimit
{
	private const int DefaultLimit = 5;

	private const int MaxLimit = 500;

	private const int SetLimitHash = 1129595230;

	// A sync var bit CraftingState does not use (it uses 0x01-0x10); setting it makes UNET send an update.
	internal const uint DirtyBit = 0x40000000u;

	private const float StockCacheSeconds = 1f;

	// The key of the limit for the whole workshop (recipe ids are 0 and up).
	internal const int AllRecipes = -1;

	// Per workshop and recipe id: above 0 the limit is on, below 0 it is off and the value is kept for turning it on
	// again.
	private static readonly Dictionary<CraftingState, Dictionary<int, int>> Limits = new Dictionary<CraftingState, Dictionary<int, int>>();

	private static readonly Dictionary<KeyValuePair<Faction, Type>, KeyValuePair<float, int>> StockCache = new Dictionary<KeyValuePair<Faction, Type>, KeyValuePair<float, int>>();

	private static readonly Dictionary<KeyValuePair<Faction, Type>, KeyValuePair<float, int>> ComingCache = new Dictionary<KeyValuePair<Faction, Type>, KeyValuePair<float, int>>();

	private static void Enable()
	{
		GameSession.OnLeave(() =>
		{
			Labors.Clear();
			StockCache.Clear();
			ComingCache.Clear();
		});
		NetworkBehaviour.RegisterCommandDelegate(typeof(UNetProjectCmd), SetLimitHash, InvokeSetLimit);
		LuaInjection.AddFunction("LoopLimit", (ScriptExecutionContext context, CallbackArguments args) => DynValue.NewNumber(Get(Selected())));
		LuaInjection.AddFunction("LoopLimitOn", (ScriptExecutionContext context, CallbackArguments args) => DynValue.NewBoolean(Get(Selected()) > 0));
		LuaInjection.AddFunction("LoopLimitLabel", (ScriptExecutionContext context, CallbackArguments args) => DynValue.NewString(Label(Selected())));
		LuaInjection.AddAction("ToggleLoopLimit", Toggle);
		LuaInjection.AddAction("LoopLimitDown", () => Step(-1));
		LuaInjection.AddAction("LoopLimitUp", () => Step(1));
		GameUi.GameMenuItems.Extend(AddToMenu);
		// The menu's buttons show only their icon; the loop tooltip (shared by these buttons) tells the limit.
		LuaInjection.AddPatch(Features.CraftLoopLimit, "LUI/Article/Tooltip/Operable_Queue_ToggleLooping.lua", "Text = ||GetLocalized(\"##operable_queue_toggle_looping_info\"),\n\t},\n", LuaInjection.Mode.InsertAfter,
			"\t{\n\t\ttype = \"Spacer\",\n\t\theight = 4,\n\t},\n"
			+ "\t{\n\t\ttype = \"MiniLabel\",\n\t\tText = ||CastleStoryPlus.LoopLimitLabel(),\n\t\tColor = ||CastleYellow,\n\t},\n");
		LuaInjection.AddPatch(Features.CraftLoopLimit, "LUI/Menus/GameMenu.lua", "_m.mh.toggleLoop = h\nend\n", LuaInjection.Mode.InsertAfter, @"
--operable: queue: loop until stock: - / on-off / + (Castle Story Plus)
do
local handles = {}
local RefreshAll = function() for i = 1, #handles do handles[i].ev_refresh:Invoke() end end
local Queue = ||Data.Operable:HasSelected() and Data.Operable:HasQueue()
local On = ||Queue() and CastleStoryPlus.LoopLimitOn()
local Make = function(icon, visible, color, highlight, action)
	local h = MenuHandle.New()
	h.Visible = visible
	h.TooltipPath = ||{""Operable"", ""Queue"", ""ToggleLooping""}
	h.Label = ||CastleStoryPlus.LoopLimitLabel()
	h.LabelColor = ||CastleYellow
	h.Icon = icon
	h.IconColor = color
	h.IconSize = ||32
	h.Highlight = highlight
	h.ev_refresh = Event.New()
	h.OnAction = function() action() RefreshAll() end
	_m.sh.operableQueue.ev_onLoad:AddListener(function(m, sh) m.AddMenuHandleToggle(h, sh) end)
	_m.mg.projectContext:AddChild(h)
	handles[#handles + 1] = h
end
Make(||IconKeys._UI_Minus:Get64(), On, ||CastleYellow, ||HighlightMode.None, function() CastleStoryPlus.LoopLimitDown() end)
Make(||IconKeys._UI_Stockpile:Get64(), Queue,
	function() if CastleStoryPlus.LoopLimitOn() then return CastleYellow end return Gray end,
	function() if Queue() and CastleStoryPlus.LoopLimitOn() then return HighlightMode.All end return HighlightMode.None end,
	function()
		CastleStoryPlus.ToggleLoopLimit()
		if CastleStoryPlus.LoopLimitOn() and not Data.Operable:GetIsQueueLooping() then Data.Operable:SetIsQueueLooping(true) end
	end)
Make(||IconKeys._UI_Plus:Get64(), On, ||CastleYellow, ||HighlightMode.None, function() CastleStoryPlus.LoopLimitUp() end)
Data.Operable.ev_onSetSelected:AddListener(RefreshAll)
Data.Operable.ev_onSetQueue:AddListener(RefreshAll)
Data.Operable.ev_onSetIsQueueLooping:AddListener(RefreshAll)
end
");
	}

	// The workshop-wide limit while it is on, else 0.
	internal static int Get(CraftingState state)
	{
		int raw = Raw(state, AllRecipes);
		return (raw > 0) ? raw : 0;
	}

	// The workshop-wide stored value.
	internal static int Raw(CraftingState state)
	{
		return Raw(state, AllRecipes);
	}

	// The stored value of a recipe (or AllRecipes): above 0 on, below 0 off with the value kept, 0 never set.
	internal static int Raw(CraftingState state, int recipe)
	{
		return (state != null && Limits.TryGetValue(state, out Dictionary<int, int> limits) && limits.TryGetValue(recipe, out int limit)) ? limit : 0;
	}

	// The limit that counts for a recipe: its own when it has one (0 while off), else the workshop-wide one.
	internal static int LimitFor(CraftingState state, int recipe)
	{
		if (state != null && Limits.TryGetValue(state, out Dictionary<int, int> limits) && limits.TryGetValue(recipe, out int own))
		{
			return (own > 0) ? own : 0;
		}
		return Get(state);
	}

	internal static void Set(CraftingState state, int limit)
	{
		Set(state, AllRecipes, limit);
	}

	internal static void Set(CraftingState state, int recipe, int limit)
	{
		if (state == null)
		{
			return;
		}
		limit = Mathf.Clamp(limit, -MaxLimit, MaxLimit);
		if (Raw(state, recipe) == limit)
		{
			return;
		}
		if (!Limits.TryGetValue(state, out Dictionary<int, int> limits))
		{
			if (limit == 0)
			{
				return;
			}
			limits = new Dictionary<int, int>();
			Limits[state] = limits;
		}
		if (limit == 0)
		{
			limits.Remove(recipe);
			if (limits.Count == 0)
			{
				Limits.Remove(state);
			}
		}
		else
		{
			limits[recipe] = limit;
		}
		if (NetworkServer.active)
		{
			state.SetDirtyBit(DirtyBit);
		}
	}

	// Every limit of a workshop at once (from the network or a save); null or empty clears them.
	internal static void SetAll(CraftingState state, Dictionary<int, int> limits)
	{
		if (state == null)
		{
			return;
		}
		Limits.Remove(state);
		if (limits != null && limits.Count > 0)
		{
			Limits[state] = new Dictionary<int, int>(limits);
		}
		if (NetworkServer.active)
		{
			state.SetDirtyBit(DirtyBit);
		}
	}

	internal static Dictionary<int, int> All(CraftingState state)
	{
		return (state != null && Limits.TryGetValue(state, out Dictionary<int, int> limits)) ? limits : null;
	}

	// The save's "loopLimits": "id:limit,id:limit".
	internal static string Format(CraftingState state)
	{
		Dictionary<int, int> limits = All(state);
		if (limits == null)
		{
			return string.Empty;
		}
		List<string> parts = new List<string>();
		foreach (KeyValuePair<int, int> pair in limits)
		{
			parts.Add(pair.Key + ":" + pair.Value);
		}
		return string.Join(",", parts.ToArray());
	}

	internal static Dictionary<int, int> Parse(string text)
	{
		Dictionary<int, int> limits = new Dictionary<int, int>();
		if (string.IsNullOrEmpty(text))
		{
			return limits;
		}
		foreach (string part in text.Split(','))
		{
			string[] pair = part.Split(':');
			if (pair.Length == 2 && int.TryParse(pair[0], out int recipe) && int.TryParse(pair[1], out int limit) && limit != 0)
			{
				limits[recipe] = Mathf.Clamp(limit, -MaxLimit, MaxLimit);
			}
		}
		return limits;
	}

	// The C# in-game menu: "-  20  +" on every recipe row of a workshop ("Off" while off). "+" from off turns the
	// recipe's limit on (and looping with it), "-" below 1 turns it off.
	private static void AddToMenu(GameUi.GameMenuModel menu)
	{
		GameUi.UiSection recipes = menu.Popup.Find("operableRecipes");
		if (recipes == null)
		{
			return;
		}
		foreach (GameUi.UiItem item in recipes.Items)
		{
			GameUi.UiButton button = item as GameUi.UiButton;
			if (button == null || !button.Id.StartsWith("operableRecipe", StringComparison.Ordinal) || !int.TryParse(button.Id.Substring("operableRecipe".Length), out int number))
			{
				continue;
			}
			int slot = number - 1;
			button.StepValue = () =>
			{
				int limit = RecipeLimit(Selected(), RecipeIds(slot));
				return (limit > 0) ? limit.ToString() : "Off";
			};
			button.StepTooltip = () => StepTooltip(slot);
			button.Step = (int delta) => StepRecipe(slot, delta);
		}
	}

	// The game's recipe ids of the selected workshop's recipe row (a kit is several recipes).
	private static List<int> RecipeIds(int slot)
	{
		List<int> ids = new List<int>();
		List<LuaCrafting.LuaRecipe> recipes = GameUi.CraftingCatalog.RecipesOf(CraftingStation.SelectedStation());
		if (recipes == null || slot >= recipes.Count)
		{
			return ids;
		}
		GameUi.CraftingCatalog.EnsureMapped();
		List<RecipeInfo> infos = LuaCrafting.ToRecipeInfo(recipes[slot]);
		if (infos != null)
		{
			foreach (RecipeInfo info in infos)
			{
				ids.Add(info.id);
			}
		}
		return ids;
	}

	// The limit a row shows: that of its first recipe (a kit's recipes are set together).
	private static int RecipeLimit(CraftingState state, List<int> ids)
	{
		return (ids.Count > 0) ? LimitFor(state, ids[0]) : 0;
	}

	private static string StepTooltip(int slot)
	{
		CraftingState state = Selected();
		List<int> ids = RecipeIds(slot);
		int limit = RecipeLimit(state, ids);
		if (limit <= 0)
		{
			return "Loop until stock: off. \"+\" keeps this many in stock while the queue loops.";
		}
		string text = "Loop until " + limit + " in stock";
		RecipeInfo recipe = RowRecipe(slot);
		Ressource product = Product(recipe);
		if (product != null && state != null)
		{
			Faction faction = Faction.GetFaction(state.gameObject);
			int coming = Coming(faction, product, recipe.Creates, cached: true);
			text += " (" + Stock(faction, product, cached: true) + " now" + ((coming > 0) ? ", " + coming + " being made or on the way" : string.Empty) + ")";
		}
		int batch = Batch(recipe);
		if (batch > 1)
		{
			text += ", in steps of " + batch + " (one order makes " + batch + ")";
		}
		return text + ": the recipe waits while there is enough.";
	}

	// 1 at a time under 10, 5 under 50, then 10 (Shift: ten steps); a recipe that makes several at once (3 planks,
	// 4 glass) steps by what one order makes. "+" from off turns it on and the queue's looping with it, and puts the
	// recipe in the queue when it is not there; "-" at the lowest step turns it off.
	private static bool StepRecipe(int slot, int delta)
	{
		CraftingState state = Selected();
		List<int> ids = RecipeIds(slot);
		if (state == null || ids.Count == 0 || GameUi.GameUiState.Paused || delta == 0)
		{
			return false;
		}
		int direction = Math.Sign(delta);
		int batch = Batch(RowRecipe(slot));
		int own = Raw(state, ids[0]);
		int raw = (own != 0) ? own : Get(state);
		int value = RoundUp(Value(raw), batch);
		if (raw <= 0)
		{
			if (direction < 0)
			{
				return false;
			}
			raw = value;
			CraftingStation station = CraftingStation.SelectedStation();
			if (station != null && !station.Looping)
			{
				GameUi.GamePopupMenu.ToggleLoop();
			}
		}
		else
		{
			for (int i = 0; i < Math.Abs(delta); i++)
			{
				if (direction < 0 && value <= batch)
				{
					value = -1;
					break;
				}
				value = Next(value, direction, batch);
			}
			raw = (value < 0) ? -1 : value;
		}
		foreach (int id in ids)
		{
			Change(state, id, raw);
		}
		// A recipe with a limit is put in the queue when it is not there yet, so the limit has something to loop.
		CraftingStation selected = CraftingStation.SelectedStation();
		if (raw > 0 && selected != null && !IsQueued(selected, ids))
		{
			List<LuaCrafting.LuaRecipe> recipes = GameUi.CraftingCatalog.RecipesOf(selected);
			if (recipes != null && slot < recipes.Count)
			{
				GameUi.GamePopupMenu.AddToQueue(recipes[slot]);
			}
		}
		return true;
	}

	private static bool IsQueued(CraftingStation station, List<int> ids)
	{
		if (station.craftingLabor == null)
		{
			return true;
		}
		foreach (RecipeInfo info in station.QueuedRecipes)
		{
			if (ids.Contains(info.id))
			{
				return true;
			}
		}
		return false;
	}

	internal static int Next(int value, int direction)
	{
		int step = (direction > 0) ? ((value < 10) ? 1 : ((value < 50) ? 5 : 10)) : ((value <= 10) ? 1 : ((value <= 50) ? 5 : 10));
		return Mathf.Clamp(value + direction * step, 1, MaxLimit);
	}

	// The next limit for a recipe that makes batch at once: whole orders only (a limit of 5 planks, 3 at a time, ends
	// at 6 anyway). A value between steps goes to the next whole order up or down.
	internal static int Next(int value, int direction, int batch)
	{
		if (batch <= 1)
		{
			return Next(value, direction);
		}
		int top = MaxLimit / batch * batch;
		int next = (direction > 0) ? (value / batch + 1) * batch : ((value + batch - 1) / batch - 1) * batch;
		return Mathf.Clamp(next, batch, top);
	}

	// value up to whole orders of batch.
	internal static int RoundUp(int value, int batch)
	{
		if (batch <= 1 || value <= 0)
		{
			return value;
		}
		return Mathf.Min((value + batch - 1) / batch * batch, MaxLimit / batch * batch);
	}

	// What one order of the recipe makes, counted as the stock counts it.
	internal static int Batch(RecipeInfo recipe)
	{
		Ressource product = Product(recipe);
		if (product == null)
		{
			return 1;
		}
		return Mathf.Max(1, recipe.QtyCreated * PerItem(recipe.Creates, product));
	}

	// How much of the product one made item is (1 for the game's recipes).
	private static int PerItem(Factory.AssetKey creates, Ressource product)
	{
		GameObject template = Factory.Peek(creates);
		IDescriptor descriptor = (template != null) ? template.GetComponent<IDescriptor>() : null;
		int value = (descriptor != null && descriptor.Description != null) ? descriptor.Description.Value(product) : 0;
		return (value > 0) ? value : 1;
	}

	// The first recipe of the selected workshop's recipe row (a kit is several), or null.
	private static RecipeInfo RowRecipe(int slot)
	{
		List<LuaCrafting.LuaRecipe> recipes = GameUi.CraftingCatalog.RecipesOf(CraftingStation.SelectedStation());
		if (recipes == null || slot >= recipes.Count)
		{
			return null;
		}
		GameUi.CraftingCatalog.EnsureMapped();
		List<RecipeInfo> infos = LuaCrafting.ToRecipeInfo(recipes[slot]);
		return (infos != null && infos.Count > 0) ? infos[0] : null;
	}

	// " (12 now)" while the limit is on and the queue's product is known.
	private static string StockText(CraftingState state)
	{
		if (Get(state) <= 0)
		{
			return string.Empty;
		}
		CraftingLabor labor = state.GetComponent<CraftingLabor>();
		Ressource product = (labor != null) ? Product(labor.FirstRecipe) : null;
		return (product == null) ? string.Empty : " (" + Stock(Faction.GetFaction(state.gameObject), product, cached: true) + " now)";
	}

	private static CraftingState Selected()
	{
		CraftingStation station = CraftingStation.SelectedStation();
		return (station != null) ? station.state : null;
	}

	private static int Value(int raw)
	{
		return (raw != 0) ? Mathf.Abs(raw) : DefaultLimit;
	}

	private static string Label(CraftingState state)
	{
		int raw = Raw(state);
		if (raw <= 0)
		{
			return "Loop until stock: off (" + Value(raw) + ")";
		}
		CraftingLabor labor = (state != null) ? state.GetComponent<CraftingLabor>() : null;
		Ressource product = (labor != null) ? Product(labor.FirstRecipe) : null;
		if (product == null)
		{
			return "Loop until stock: " + raw;
		}
		return "Loop until stock: " + raw + " (" + Stock(Faction.GetFaction(state.gameObject), product, cached: false) + " now)";
	}

	private static void Toggle()
	{
		CraftingState state = Selected();
		if (state == null)
		{
			return;
		}
		int raw = Raw(state);
		Change(state, AllRecipes, (raw > 0) ? -raw : Value(raw));
	}

	// 1 at a time under 10, 5 under 50, then 10.
	private static void Step(int direction)
	{
		CraftingState state = Selected();
		if (state == null)
		{
			return;
		}
		int raw = Raw(state);
		int value = Next(Value(raw), direction);
		Change(state, AllRecipes, (raw > 0) ? value : -value);
	}

	// Shown at once; the host's value follows.
	internal static void Change(CraftingState state, int recipe, int raw)
	{
		Set(state, recipe, raw);
		UNetProjectCmd cmd = (User.LocalUser != null) ? User.LocalUser.GetComponent<UNetProjectCmd>() : null;
		if (cmd == null || cmd.isServer)
		{
			return;
		}
		NetworkWriter writer = new NetworkWriter();
		writer.Write((short)0);
		writer.Write((short)5);
		writer.WritePackedUInt32((uint)SetLimitHash);
		writer.Write(cmd.GetComponent<NetworkIdentity>().netId);
		writer.Write(state.gameObject);
		writer.WritePackedUInt32(unchecked((uint)recipe));
		writer.WritePackedUInt32(unchecked((uint)raw));
		cmd.SendCommandInternal(writer, 0, "CmdSetCraftLoopLimit");
	}

	private static void InvokeSetLimit(NetworkBehaviour obj, NetworkReader reader)
	{
		if (!NetworkServer.active)
		{
			return;
		}
		GameObject station = reader.ReadGameObject();
		int recipe = unchecked((int)reader.ReadPackedUInt32());
		int limit = unchecked((int)reader.ReadPackedUInt32());
		if (station == null || !Affiliation.IsOwnedBy(station, obj.gameObject))
		{
			return;
		}
		Set(station.GetComponent<CraftingState>(), recipe, limit);
	}

	// Server: whether the queue may start its first recipe. While it loops, the first recipe without a limit or whose
	// product is below its limit is moved to the front; if none is, the workshop waits.
	internal static bool MayStart(CraftingLabor labor)
	{
		if (!labor.LoopRecipe || All(labor.state) == null)
		{
			return true;
		}
		List<RecipeInfo> queue = labor.QueuedRecipes;
		Faction faction = Faction.GetFaction(labor.gameObject);
		for (int i = 0; i < queue.Count; i++)
		{
			if (HasEnough(labor, queue[i], faction))
			{
				continue;
			}
			Ressource product = Product(queue[i]);
			if (i > 0)
			{
				List<RecipeInfo> waiting = queue.GetRange(0, i);
				queue.RemoveRange(0, i);
				queue.AddRange(waiting);
				labor.state.SetRecipes(queue);
			}
			// The order starts now: the next workshop that asks counts it (another looping workshop making the same
			// product within the cache's second would otherwise start one too many).
			if (product != null)
			{
				Forget(faction, product);
			}
			return true;
		}
		return false;
	}

	// Whether the stock already holds the recipe's limit of what it makes (never while the queue does not loop). What
	// is being made, or was made and is not stored yet, counts too (Coming): an order runs for a while, its product
	// waits in the workshop and is then carried to a stockpile, and counting only the stockpiles started more orders
	// meanwhile, so the stock went past its limit (#82).
	internal static bool HasEnough(CraftingLabor labor, RecipeInfo recipe, Faction faction)
	{
		if (!labor.LoopRecipe)
		{
			return false;
		}
		int limit = LimitFor(labor.state, recipe.id);
		Ressource product = Product(recipe);
		if (limit <= 0 || product == null)
		{
			return false;
		}
		int stock = Stock(faction, product, cached: true);
		return stock >= limit || stock + Coming(faction, product, recipe.Creates, cached: true) >= limit;
	}

	// The cached counts of a product (after an order of it started).
	private static void Forget(Faction faction, Ressource product)
	{
		KeyValuePair<Faction, Type> key = new KeyValuePair<Faction, Type>(faction, product.GetType());
		StockCache.Remove(key);
		ComingCache.Remove(key);
	}

	// What the team has of the product outside its stockpiles and tool racks, and what its workshops are making of it:
	// - made items in a workshop that makes them (waiting to be carried away), in a bricktron's bag or on the ground;
	//   not in a workshop that does not make it (there it is an ingredient about to be used);
	// - the order a workshop is working on, and every queued order of a workshop that does not loop (a looping
	//   workshop asks again before each order).
	internal static int Coming(Faction faction, Ressource product, Factory.AssetKey creates, bool cached)
	{
		KeyValuePair<Faction, Type> key = new KeyValuePair<Faction, Type>(faction, product.GetType());
		if (cached && ComingCache.TryGetValue(key, out KeyValuePair<float, int> entry) && Time.time - entry.Key < StockCacheSeconds)
		{
			return entry.Value;
		}
		int count = 0;
		AutoList list = BrixSingleton<AutoList>.Instance;
		HashSet<GameObject> items = (list != null && creates != null && !creates.IsEmpty()) ? list.GetInstances(creates) : null;
		if (items != null)
		{
			foreach (GameObject item in items)
			{
				if (item == null || Faction.GetFaction(item) != faction || !IsComing(item, creates))
				{
					continue;
				}
				IDescriptor descriptor = item.GetComponent<IDescriptor>();
				count += (descriptor != null && descriptor.Description != null) ? Mathf.Max(0, descriptor.Description.Value(product)) : 1;
			}
		}
		Labors.RemoveWhere((CraftingLabor labor) => labor == null);
		foreach (CraftingLabor labor in Labors)
		{
			if (!labor.gameObject.activeInHierarchy || labor.state == null || Faction.GetFaction(labor.gameObject) != faction)
			{
				continue;
			}
			if (labor.LoopRecipe)
			{
				Recipe current = labor.CurrentRecipe;
				if (current != null && current.Info != null && !current.Finished && current.Creates == creates)
				{
					count += Batch(current.Info);
				}
				continue;
			}
			foreach (RecipeInfo queued in labor.QueuedRecipes)
			{
				if (queued != null && queued.Creates == creates)
				{
					count += Batch(queued);
				}
			}
		}
		ComingCache[key] = new KeyValuePair<float, int>(Time.time, count);
		return count;
	}

	// An item of the product that is not in a stockpile or tool rack (Stock counts those), nor an ingredient in a
	// workshop that does not make it.
	private static bool IsComing(GameObject item, Factory.AssetKey creates)
	{
		Transform parent = item.transform.parent;
		Recepteur holder = (parent != null) ? parent.GetComponentInParent<Recepteur>() : null;
		if (holder == null)
		{
			return item.activeInHierarchy;
		}
		// The storage's own object (its recepteur may sit on a part of it).
		FactoryImprint imprint = holder.GetComponentInParent<FactoryImprint>();
		if (imprint != null && (imprint.AssetKey == ObjetsDynamiques.Palette || imprint.AssetKey == ObjetsDynamiques.Toolrack || imprint.AssetKey == ObjetsDynamiques.SingleToolrack))
		{
			return false;
		}
		CraftingLabor workshop = holder.GetComponentInParent<CraftingLabor>();
		if (workshop == null)
		{
			return true;
		}
		Recipe current = workshop.CurrentRecipe;
		if (current != null && current.Creates == creates)
		{
			return true;
		}
		foreach (RecipeInfo queued in workshop.QueuedRecipes)
		{
			if (queued != null && queued.Creates == creates)
			{
				return true;
			}
		}
		return false;
	}

	// The workshops' crafting (CraftLoopLimitLaborPatch adds each as it starts).
	internal static readonly HashSet<CraftingLabor> Labors = new HashSet<CraftingLabor>();

	internal static Ressource Product(RecipeInfo recipe)
	{
		if (recipe == null || recipe.Creates == null || recipe.Creates.IsEmpty())
		{
			return null;
		}
		GameObject template = Factory.Peek(recipe.Creates);
		return (template != null) ? Apparence.PrincipaleOf(template) : null;
	}

	// What the team's stockpiles and tool racks hold of the resource.
	internal static int Stock(Faction faction, Ressource resource, bool cached)
	{
		KeyValuePair<Faction, Type> key = new KeyValuePair<Faction, Type>(faction, resource.GetType());
		if (cached && StockCache.TryGetValue(key, out KeyValuePair<float, int> entry) && Time.time - entry.Key < StockCacheSeconds)
		{
			return entry.Value;
		}
		int count = 0;
		AutoList list = BrixSingleton<AutoList>.Instance;
		if (list != null)
		{
			HashSet<GameObject> seen = new HashSet<GameObject>();
			foreach (Factory.AssetKey storage in new[] { ObjetsDynamiques.Palette, ObjetsDynamiques.Toolrack, ObjetsDynamiques.SingleToolrack })
			{
				HashSet<GameObject> instances = list.GetInstances(storage);
				if (instances == null)
				{
					continue;
				}
				foreach (GameObject go in instances)
				{
					if (go == null || !go.activeInHierarchy || !seen.Add(go) || Faction.GetFaction(go) != faction)
					{
						continue;
					}
					Recepteur recepteur = go.GetComponent<Recepteur>();
					if (recepteur != null)
					{
						count += recepteur.ContentDescription.Value(resource);
					}
				}
			}
		}
		StockCache[key] = new KeyValuePair<float, int>(Time.time, count);
		return count;
	}
}

// The queue only starts a recipe that MayStart allows (the game's node, with that condition added).
[Feature(Features.CraftLoopLimit, Features.CraftLoopLimitInfo)]
[HarmonyPatch(typeof(CraftingLabor), "ProcessQueue")]
internal static class CraftLoopLimitQueuePatch
{
	private static bool Prefix(CraftingLabor __instance, ref StaticNode __result)
	{
		__result = Node.Selector.If((CraftingLabor aCraftingLabor) => aCraftingLabor.CurrentWork == null && aCraftingLabor.FirstRecipe != null && CraftLoopLimit.MayStart(aCraftingLabor), __instance).Do((CraftingLabor aCraftingLabor) =>
		{
			aCraftingLabor.PopRecipe();
		}, __instance).Do((CraftingLabor aCraftingLabor) =>
		{
			aCraftingLabor.instructions.CookPackage(aCraftingLabor.CurrentRecipe).Send(aCraftingLabor);
		}, __instance);
		return false;
	}
}

// Every workshop's crafting, for CraftLoopLimit.Coming (what the workshops are making).
[Feature(Features.CraftLoopLimit, Features.CraftLoopLimitInfo)]
[HarmonyPatch(typeof(CraftingLabor), nameof(CraftingLabor.Initialize))]
internal static class CraftLoopLimitLaborPatch
{
	private static void Postfix(CraftingLabor __instance)
	{
		CraftLoopLimit.Labors.Add(__instance);
	}
}

// Clearing the queue (which also stops looping) clears the limit.
[Feature(Features.CraftLoopLimit, Features.CraftLoopLimitInfo)]
[HarmonyPatch(typeof(CraftingLabor), nameof(CraftingLabor.ClearOrderQueue))]
internal static class CraftLoopLimitClearPatch
{
	private static void Postfix(CraftingLabor __instance)
	{
		CraftLoopLimit.SetAll(__instance.state, null);
	}
}

// Network: the limit is appended to the workshop's own state data, in the initial state and every update.
[Feature(Features.CraftLoopLimit, Features.CraftLoopLimitInfo)]
[HarmonyPatch(typeof(CraftingState), nameof(CraftingState.OnSerialize))]
internal static class CraftLoopLimitWritePatch
{
	private static void Postfix(CraftingState __instance, NetworkWriter writer, ref bool __result)
	{
		Dictionary<int, int> limits = CraftLoopLimit.All(__instance);
		writer.WritePackedUInt32((uint)((limits != null) ? limits.Count : 0));
		if (limits != null)
		{
			foreach (KeyValuePair<int, int> pair in limits)
			{
				writer.WritePackedUInt32(unchecked((uint)pair.Key));
				writer.WritePackedUInt32(unchecked((uint)pair.Value));
			}
		}
		__result = true;
	}
}

[Feature(Features.CraftLoopLimit, Features.CraftLoopLimitInfo)]
[HarmonyPatch(typeof(CraftingState), nameof(CraftingState.OnDeserialize))]
internal static class CraftLoopLimitReadPatch
{
	private static void Postfix(CraftingState __instance, NetworkReader reader)
	{
		int count = (int)reader.ReadPackedUInt32();
		Dictionary<int, int> limits = new Dictionary<int, int>();
		for (int i = 0; i < count; i++)
		{
			int recipe = unchecked((int)reader.ReadPackedUInt32());
			limits[recipe] = unchecked((int)reader.ReadPackedUInt32());
		}
		CraftLoopLimit.SetAll(__instance, limits);
	}
}

// Saves: an extra "loopLimits" property on the workshop's CraftingState ("id:limit,..."), and the workshop-wide
// "loopLimit" as before (saves from before per-product limits have only that one).
[Feature(Features.CraftLoopLimit, Features.CraftLoopLimitInfo)]
[HarmonyPatch(typeof(DefaultGameContractResolver), nameof(DefaultGameContractResolver.GenerateDefaultMonoBehaviourContract))]
internal static class CraftLoopLimitSave
{
	private const string Property = "loopLimit";

	private const string PerRecipe = "loopLimits";

	private class LimitProvider : IValueProvider
	{
		public object GetValue(object target)
		{
			return CraftLoopLimit.Raw(target as CraftingState);
		}

		public void SetValue(object target, object value)
		{
			CraftLoopLimit.Set(target as CraftingState, Convert.ToInt32(value));
		}
	}

	private class LimitsProvider : IValueProvider
	{
		public object GetValue(object target)
		{
			return CraftLoopLimit.Format(target as CraftingState);
		}

		public void SetValue(object target, object value)
		{
			CraftingState state = target as CraftingState;
			foreach (KeyValuePair<int, int> pair in CraftLoopLimit.Parse(value as string))
			{
				CraftLoopLimit.Set(state, pair.Key, pair.Value);
			}
		}
	}

	private static void Postfix(Type objectType, JsonObjectContract __result)
	{
		if (objectType != typeof(CraftingState) || __result == null)
		{
			return;
		}
		Add(__result, Property, typeof(int), new LimitProvider());
		Add(__result, PerRecipe, typeof(string), new LimitsProvider());
	}

	private static void Add(JsonObjectContract contract, string name, Type type, IValueProvider provider)
	{
		if (contract.Properties.Contains(name))
		{
			return;
		}
		contract.Properties.AddProperty(new JsonProperty
		{
			PropertyName = name,
			UnderlyingName = name,
			PropertyType = type,
			DeclaringType = typeof(CraftingState),
			ValueProvider = provider,
			Readable = true,
			Writable = true
		});
	}
}
