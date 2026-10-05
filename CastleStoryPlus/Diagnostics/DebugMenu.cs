using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Components;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.Damage;
using Brix.Game.Bricktron;
using Brix.Game.Components;
using Brix.Game.Names;
using Brix.Game.Semantique;
using Brix.Lifecycle.Pooling;
using Brix.Input;
using Brix.Transactions;
using Brix.Utils;
using CastleStoryPlus.Core;
using CastleStoryPlus.Giant;
using MoonSharp.Interpreter;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using static CastleStoryPlus.UI.UiKit;

namespace CastleStoryPlus.Diagnostics;

// Debug menu, toggled with F8 ([Debug] DebugMenuKey). Host only (single player or the host of a multiplayer game):
// - Energy: adds energy to the home crystal (an unnamed firefly stored in it, as brewing does);
// - Bricktrons: new builders at the home crystal, heal every own bricktron, the worker trace on or off
//   (BepInEx/workertrace.log: what the workers do, and why they reject or fail tasks; works on clients too);
// - Resources: drops 10, 50 or 100 of a resource on the ground where the camera looks;
// - Enemies: corruptrons where the camera looks, kill every enemy within 30 blocks of it;
// - Waves (invasion): next wave now, +1 or +5 minutes before the next wave, freeze the wave timer.
// Wave commands are queued here and carried out by the invasion server script on its next slow update.
[Feature(Features.DebugMenu, Features.DebugMenuInfo)]
internal class DebugMenu : MonoBehaviour
{
	private const float KillRadius = 30f;

	private const int WaveNext = -1;

	private const int WaveFreeze = -2;

	private static readonly KeyValuePair<string, Ressource>[] ResourceButtons =
	{
		new KeyValuePair<string, Ressource>("Logs", Adjectif.woodBlock),
		new KeyValuePair<string, Ressource>("Planks", Adjectif.plankBlock),
		new KeyValuePair<string, Ressource>("Stone", Adjectif.stones),
		new KeyValuePair<string, Ressource>("Bricks", Adjectif.stoneBlock),
		new KeyValuePair<string, Ressource>("Fibre", Adjectif.plant),
		new KeyValuePair<string, Ressource>("Rope", Adjectif.rope),
		new KeyValuePair<string, Ressource>("Fabric", Adjectif.fabric),
		new KeyValuePair<string, Ressource>("Raw iron", Adjectif.rawIron),
		new KeyValuePair<string, Ressource>("Iron", Adjectif.iron),
		new KeyValuePair<string, Ressource>("Cogs", Adjectif.cog),
		new KeyValuePair<string, Ressource>("Orange cr.", Adjectif.orangeCrystal),
		new KeyValuePair<string, Ressource>("Blue cr.", Adjectif.blueCrystal),
		new KeyValuePair<string, Ressource>("Glass", Adjectif.glass),
		new KeyValuePair<string, Ressource>("Coal", Adjectif.clay),
		new KeyValuePair<string, Ressource>("Steel", Adjectif.terracotta),
		new KeyValuePair<string, Ressource>("Dark cr.", Adjectif.purifiedBlueCrystal)
	};

	private static ConfigEntry<KeyCode> _key;

	// Wave state reported by the invasion server script, and the command waiting for it.
	private static float _waveRemaining = -1f;

	private static bool _waveFrozen;

	private static float _waveReportedAt = -100f;

	private static int _waveCommand;

	private GameObject _window;

	private Text _status;

	private Text _waveText;

	private Text _amountLabel;

	private Text _traceLabel;

	private int _amount = 10;

	private float _nextRefresh;

	private static void Enable()
	{
		_key = Plugin.Cfg.Bind("Debug", "DebugMenuKey", KeyCode.F8, "Key that opens the debug menu (energy, bricktrons, resources, enemies, waves). Host only.");
		LuaInjection.AddFunction("DebugWave", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			DynValue remaining = args[0];
			_waveRemaining = (remaining.Type == DataType.Number) ? (float)remaining.Number : -1f;
			_waveFrozen = args[1].CastToBool();
			_waveReportedAt = Time.unscaledTime;
			int command = _waveCommand;
			_waveCommand = 0;
			return DynValue.NewNumber(command);
		});
		LuaInjection.AddPatch(Features.DebugMenu, "Gamemodes/invasion/serverside.lua", "Hooks.Connect(hk_OnSerializing, OnSerializing)\n", LuaInjection.Mode.InsertAfter, @"
---Castle Story Plus: wave commands of the debug menu (F8)
Hooks.Connect(hk_SlowUpdate, function()
	local t = Registry and Registry.waveTimer
	local remaining = -1
	if CSP_FrozenWave ~= nil then
		remaining = CSP_FrozenWave
	elseif t ~= nil and t.running then
		remaining = t:RemainingSeconds()
	end
	local command = CastleStoryPlus.DebugWave(remaining, CSP_FrozenWave ~= nil)
	if t == nil then
		return
	end
	if command == -1 then
		CSP_FrozenWave = nil
		NextWave()
		return
	elseif command == -2 then
		if CSP_FrozenWave ~= nil then
			CSP_FrozenWave = nil
		elseif t.running then
			CSP_FrozenWave = remaining
		end
	elseif command > 0 then
		if CSP_FrozenWave ~= nil then
			CSP_FrozenWave = CSP_FrozenWave + command
		elseif t.running then
			if remaining < 0 then
				remaining = 0
			end
			t:Start(remaining + command)
			_SendWaveTime(true)
		end
	end
	if CSP_FrozenWave ~= nil then
		t:Start(CSP_FrozenWave)
		_SendWaveTime(true)
	end
end)
");
		Plugin.Root.AddComponent<DebugMenu>();
	}

	private void Awake()
	{
		BuildWindow();
		_window.SetActive(false);
	}

	private void Update()
	{
		if (UnityEngine.Input.GetKeyDown(_key.Value))
		{
			SetVisible(!_window.activeSelf);
		}
		if (!_window.activeSelf)
		{
			return;
		}
		if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
		{
			SetVisible(false);
			return;
		}
		if (Time.unscaledTime >= _nextRefresh)
		{
			Refresh();
		}
	}

	private void SetVisible(bool visible)
	{
		_window.SetActive(visible);
		if (visible)
		{
			Refresh();
		}
	}

	private void BuildWindow()
	{
		GameObject canvas = CreateCanvas("DebugMenuCanvas", transform, 950);
		_window = canvas;
		GameObject window = CreatePanel("DebugMenuWindow", canvas.transform);
		SetRect(window.GetComponent<RectTransform>(), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-300f, 0f), new Vector2(560f, 680f));
		VerticalLayoutGroup layout = window.AddComponent<VerticalLayoutGroup>();
		layout.padding = new RectOffset(14, 14, 10, 14);
		layout.spacing = 6f;
		layout.childControlWidth = true;
		layout.childControlHeight = true;
		layout.childForceExpandWidth = true;
		layout.childForceExpandHeight = false;

		Transform header = CreateRow(window.transform, 28f);
		Flexible(CreateText(header, "Debug menu", 18, Yellow, TextAnchor.MiddleLeft).gameObject);
		CreateButton(header, "Close", 70f, () => SetVisible(false));
		_status = CreateText(CreateRow(window.transform, 20f), string.Empty, 12, Grey, TextAnchor.MiddleLeft);
		Flexible(_status.gameObject);

		Section(window.transform, "Energy (home crystal)");
		Transform energy = CreateRow(window.transform, 26f);
		foreach (int amount in new int[4] { 100, 500, 1000, 5000 })
		{
			int value = amount;
			CreateButton(energy, "+" + amount, 90f, () => Run(() => GiveEnergy(value)));
		}

		Section(window.transform, "Bricktrons");
		Transform units = CreateRow(window.transform, 26f);
		CreateButton(units, "+1 builder", 100f, () => Run(() => SpawnBuilders(1)));
		CreateButton(units, "+5 builders", 100f, () => Run(() => SpawnBuilders(5)));
		CreateButton(units, "Heal all mine", 120f, () => Run(HealOwn));
		Button trace = CreateButton(units, string.Empty, 150f, () =>
		{
			if (WorkerTrace.Active)
			{
				WorkerTrace.Stop();
			}
			else
			{
				WorkerTrace.Start();
			}
			SetStatus(WorkerTrace.Active ? "Worker trace on: BepInEx/workertrace.log" : "Worker trace off.");
			Refresh();
		});
		_traceLabel = trace.GetComponentInChildren<Text>();

		Section(window.transform, "Resources (dropped where the camera looks)");
		Transform amountRow = CreateRow(window.transform, 26f);
		_amountLabel = CreateText(amountRow, string.Empty, 13, TextColor, TextAnchor.MiddleLeft);
		Fixed(_amountLabel.gameObject, 90f, 26f);
		foreach (int amount in new int[3] { 10, 50, 100 })
		{
			int value = amount;
			CreateButton(amountRow, value.ToString(), 60f, () =>
			{
				_amount = value;
				Refresh();
			});
		}
		Transform row = null;
		for (int i = 0; i < ResourceButtons.Length; i++)
		{
			if (i % 5 == 0)
			{
				row = CreateRow(window.transform, 26f);
			}
			KeyValuePair<string, Ressource> resource = ResourceButtons[i];
			CreateButton(row, resource.Key, 100f, () => Run(() => DropResource(resource.Value, resource.Key)));
		}

		Section(window.transform, "Enemies (where the camera looks)");
		Transform enemies = CreateRow(window.transform, 26f);
		CreateButton(enemies, "+1 corruptron", 120f, () => Run(() => SpawnEnemies(1)));
		CreateButton(enemies, "+5 corruptrons", 120f, () => Run(() => SpawnEnemies(5)));
		CreateButton(enemies, "Kill enemies near", 150f, () => Run(KillEnemies));

		Section(window.transform, "Invasion waves");
		_waveText = CreateText(CreateRow(window.transform, 20f), string.Empty, 13, TextColor, TextAnchor.MiddleLeft);
		Flexible(_waveText.gameObject);
		Transform waves = CreateRow(window.transform, 26f);
		CreateButton(waves, "Next wave now", 120f, () => QueueWave(WaveNext));
		CreateButton(waves, "+1 min", 70f, () => QueueWave(60));
		CreateButton(waves, "+5 min", 70f, () => QueueWave(300));
		CreateButton(waves, "Freeze / resume", 130f, () => QueueWave(WaveFreeze));
	}

	private static void Section(Transform parent, string title)
	{
		Text text = CreateText(CreateRow(parent, 24f), title, 14, Yellow, TextAnchor.LowerLeft);
		Flexible(text.gameObject);
	}

	private void Refresh()
	{
		_nextRefresh = Time.unscaledTime + 0.5f;
		_amountLabel.text = "Amount: " + _amount;
		_traceLabel.text = WorkerTrace.Active ? "Worker trace: on" : "Worker trace: off";
		if (!NetworkServer.active)
		{
			SetStatus("Host only: these commands run on the host.");
		}
		bool waveKnown = Time.unscaledTime - _waveReportedAt < 5f;
		if (!waveKnown)
		{
			_waveText.text = "No invasion wave timer (invasion games only, host only).";
		}
		else if (_waveRemaining < 0f)
		{
			_waveText.text = "The wave timer has not started yet.";
		}
		else
		{
			int seconds = Mathf.Max(0, Mathf.CeilToInt(_waveRemaining));
			_waveText.text = "Next wave in " + (seconds / 60) + ":" + (seconds % 60).ToString("00") + (_waveFrozen ? " (frozen)" : string.Empty) + ((_waveCommand != 0) ? " - command sent" : string.Empty);
		}
	}

	private void SetStatus(string message)
	{
		_status.text = message;
	}

	private void Run(System.Func<string> command)
	{
		if (!NetworkServer.active)
		{
			SetStatus("Host only: these commands run on the host.");
			return;
		}
		string result;
		try
		{
			result = command();
		}
		catch (System.Exception e)
		{
			Plugin.Log.LogError("Debug menu: " + e);
			result = "Failed: " + e.Message;
		}
		SetStatus(result);
		Plugin.Log.LogInfo("Debug menu: " + result);
	}

	private void QueueWave(int command)
	{
		if (!NetworkServer.active)
		{
			SetStatus("Host only: these commands run on the host.");
			return;
		}
		if (Time.unscaledTime - _waveReportedAt >= 5f)
		{
			SetStatus("No invasion wave timer in this game.");
			return;
		}
		_waveCommand = command;
		SetStatus((command == WaveNext) ? "Next wave: starting." : ((command == WaveFreeze) ? "Wave timer: freeze / resume." : ("Wave timer: +" + (command / 60) + " min.")));
		Refresh();
	}

	private static Faction LocalFaction()
	{
		return (User.LocalUser != null) ? User.LocalUser.faction : null;
	}

	private static string GiveEnergy(int amount)
	{
		FireflyNest nest = GiantBricktron.HomeNest(LocalFaction());
		if (nest == null)
		{
			return "No home crystal.";
		}
		// As brewing does it: an unnamed firefly holding the energy, stored in the crystal.
		GameObject go = Transactor.SpawnUNet(Brix.External.Factories.Environment.Firefly, nest.faction, nest.transform.position + Vector3.up, Quaternion.identity);
		if (go == null)
		{
			return "Could not make a firefly.";
		}
		go.GetComponent<Firefly>().pureEnergy = amount;
		nest.mainRecepteur.AddObject(go, true);
		return "+" + amount + " energy (" + nest.GetAvailablePurifiedEnergy() + " in the crystal).";
	}

	private static string SpawnBuilders(int count)
	{
		Faction faction = LocalFaction();
		FireflyNest nest = GiantBricktron.HomeNest(faction);
		if (nest == null)
		{
			return "No home crystal.";
		}
		for (int i = 0; i < count; i++)
		{
			Vector3 position = nest.transform.position + Vector3.up * 4f + new Vector3(Random.Range(-1.5f, 1.5f), 0f, Random.Range(-1.5f, 1.5f));
			GameObject go = Transactor.SpawnUNet(Test.Bricktron, faction, position, Quaternion.identity);
			if (go == null)
			{
				continue;
			}
			Nom nom = go.GetComponent<Nom>();
			if (nom != null)
			{
				nom.SetNom(NameDispenserGO.GetDispenserGO(NameDispenserGO.Dispensers.Bricktrons).GetName());
			}
			go.GetComponent<Occupation>().SetOccupation(Occupation.Type.Builder, true);
		}
		return "+" + count + " builder" + ((count == 1) ? string.Empty : "s") + " at the home crystal.";
	}

	private static string HealOwn()
	{
		Faction faction = LocalFaction();
		int healed = 0;
		foreach (Labor labor in FindObjectsOfType<Labor>())
		{
			if (labor == null || faction == null || !faction.IsSame(labor.gameObject))
			{
				continue;
			}
			BricktronDamageReceiver receiver = labor.GetComponent<BricktronDamageReceiver>();
			if (receiver != null && receiver.CurrentHP > 0f && receiver.CurrentHP < receiver.MaxHP)
			{
				receiver.CurrentHP = receiver.MaxHP;
				healed++;
			}
		}
		return "Healed " + healed + " bricktrons.";
	}

	private string DropResource(Ressource resource, string name)
	{
		Factory.AssetKey key = Description.RepresentativeOf(resource);
		if (key == null)
		{
			return "No item for " + name + ".";
		}
		Vector3 center = Target();
		List<GameObject> dropped = new List<GameObject>();
		for (int i = 0; i < _amount; i++)
		{
			GameObject go = Transactor.SpawnUNet(key, null, center + Vector3.up * 0.5f, Quaternion.identity);
			if (go != null)
			{
				dropped.Add(go);
			}
		}
		if (dropped.Count > 0)
		{
			Recepteur.ScatterObjects(dropped, center);
			LogStorage(dropped[0], resource, name);
		}
		return "Dropped " + dropped.Count + " " + name + ".";
	}

	// Where the dropped kind could be stored: the item, every own stockpile's capacity for it and whether it has room,
	// and whether the workers can carry it (the log line "storage check").
	private static void LogStorage(GameObject item, Ressource resource, string name)
	{
		Faction faction = LocalFaction();
		IDescriptor descriptor = item.GetComponent<IDescriptor>();
		string text = name + ": item " + item.name + " holds " + ((descriptor != null && descriptor.Description != null) ? WorkerTraceDetails.Describe(descriptor.Description) : "?");
		AutoList list = BrixSingleton<AutoList>.Instance;
		HashSet<GameObject> piles = (list != null) ? list.GetInstances(ObjetsDynamiques.Palette) : null;
		int own = 0;
		int withRoom = 0;
		List<string> details = new List<string>();
		if (piles != null)
		{
			foreach (GameObject pile in piles)
			{
				Recepteur recepteur = (pile != null && pile.activeInHierarchy && Faction.GetFaction(pile) == faction) ? pile.GetComponent<Recepteur>() : null;
				if (recepteur == null)
				{
					continue;
				}
				own++;
				bool room = recepteur.HasRoomFor(item);
				if (room)
				{
					withRoom++;
				}
				if (details.Count < 12)
				{
					details.Add((room ? "room" : "no room") + " (base " + recepteur.BaseCapacity.Value(resource) + ", with exclusions " + recepteur.BaseCapacityWithExclusions.Value(resource) + ", current " + recepteur.CurrentCapacity.Value(resource) + ", holds " + WorkerTraceDetails.Describe(recepteur.ContentDescription) + ")");
				}
			}
		}
		Labor worker = null;
		foreach (Labor labor in UnityEngine.Object.FindObjectsOfType<Labor>())
		{
			if (labor != null && labor.faction == faction && labor.recepteur != null)
			{
				worker = labor;
				break;
			}
		}
		text += " | own stockpiles " + own + ", with room " + withRoom + ": " + string.Join("; ", details.ToArray());
		if (worker != null)
		{
			text += " | worker " + worker.name + " can carry it: " + worker.recepteur.HasRoomFor(item) + ", best storage: " + ((worker.BestRecepteurToStore(item) != null) ? worker.BestRecepteurToStore(item).name : "none");
		}
		Plugin.Log.LogInfo("Debug menu: storage check " + text);
	}

	private static string SpawnEnemies(int count)
	{
		Faction enemy = Faction.FindByName("Corruptrons") ?? Faction.DefaultEnemy;
		if (enemy == null)
		{
			return "No enemy faction in this game.";
		}
		Vector3 center = Target();
		for (int i = 0; i < count; i++)
		{
			Vector3 position = center + Vector3.up + new Vector3(Random.Range(-2f, 2f), 0f, Random.Range(-2f, 2f));
			GameObject go = Transactor.SpawnUNet(Test.Corruptron, enemy, position, Quaternion.identity);
			if (go != null)
			{
				go.GetComponent<Occupation>().SetOccupation(Occupation.Type.Corruptron, true);
			}
		}
		return "+" + count + " corruptron" + ((count == 1) ? string.Empty : "s") + ".";
	}

	private static string KillEnemies()
	{
		Faction local = LocalFaction();
		Vector3 center = Target();
		int killed = 0;
		foreach (Locomotion4 unit in FindObjectsOfType<Locomotion4>())
		{
			if (unit == null || unit.IsCorpse || !unit.gameObject.activeInHierarchy || (local != null && local.IsSame(unit.gameObject)))
			{
				continue;
			}
			Faction faction = Faction.GetFaction(unit.gameObject);
			if (faction == null || !faction.isAI || (unit.transform.position - center).sqrMagnitude > KillRadius * KillRadius)
			{
				continue;
			}
			unit.Kill(center, Vector3.up);
			killed++;
		}
		return "Killed " + killed + " enemies within " + KillRadius + " blocks.";
	}

	// The ground under the point the camera looks at.
	private static Vector3 Target()
	{
		Vector3 target = CameraScript.Target;
		VoxelRaycastHit hit = new VoxelRaycastHit();
		LayerBundle layers = new LayerBundle((int)UnityLayer.Terrain, (int)UnityLayer.FreeBlocks);
		if (hit.PlaceAtRaycast(new Ray(target + Vector3.up * 60f, Vector3.down), 200f, layers))
		{
			return hit.WorldHitVector + Vector3.up * 0.5f;
		}
		return target;
	}
}
