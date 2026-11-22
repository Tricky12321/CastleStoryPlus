using System;
using Brix.Audio;
using Brix.Game;
using Brix.Game.AI;
using Brix.Input;
using Brix.Legacy;
using Brix.Lua;
using Brix.UI.Builder.Menu.Component;
using CastleStoryPlus.LuaUi;
using UnityEngine;

namespace CastleStoryPlus.GameUi;

// What the menu's buttons do, where GameMenu.lua called into the game through UIGame, MenuCalls and the like.
internal static class GameActions
{
	private static LuaMenuComponent _scoreboard;

	// UIGame.BackToWork: every soldier back to work.
	internal static void BackToWork()
	{
		SoundEngine.Play("Back_To_Work");
		if (UIGameObserver.bricktrons == null)
		{
			return;
		}
		foreach (Labor labor in UIGameObserver.bricktrons.CheckCreation())
		{
			if (labor != null && labor.Profession.OccupationType != Occupation.Type.Builder)
			{
				DirectOrderProject.AddBackToworkGoal(labor);
			}
		}
	}

	// UIGame.CallToArms(the local player's crystal).
	internal static void CallToArms()
	{
#if !ESSENTIALS
		Army.CallToArms.Execute(OwnCrystal());
#else
		// The game's own call to arms: every builder takes gear and runs to the crystal.
		SoundEngine.Play("Call_To_Arm");
		GameObject crystal = OwnCrystal();
		if (UIGameObserver.bricktrons == null)
		{
			return;
		}
		foreach (Labor labor in UIGameObserver.bricktrons.CheckCreation())
		{
			if (labor != null && labor.Profession.OccupationType == Occupation.Type.Builder)
			{
				DirectOrderProject.AddCallToArmsGoal(labor, crystal);
			}
		}
#endif
	}

	// The local player's team's crystal (GameMenu's userCrystal).
	internal static GameObject OwnCrystal()
	{
		Faction faction = (User.LocalUser != null) ? User.LocalUser.faction : null;
		foreach (SpawnerCristal crystal in UnityEngine.Object.FindObjectsOfType<SpawnerCristal>())
		{
			if (crystal != null && crystal.faction == faction)
			{
				return crystal.gameObject;
			}
		}
		return null;
	}

	// UIGame.CountBricktrons: the team's bricktrons.
	internal static int CountBricktrons()
	{
		return (UIGameObserver.bricktrons != null) ? UIGameObserver.bricktrons.CheckCreation().Count : 0;
	}

	internal static void ToggleEditorCamera()
	{
		CameraScript camera = (Camera.main != null) ? Camera.main.GetComponent<CameraScript>() : null;
		if (camera != null)
		{
			camera.ToggleEditorCamera(!camera.editorCamera);
		}
	}

	// UIGame.hk_SetSidebarSpacer: the gamemodes' scoreboards move aside while a task is selected.
	internal static void SetSidebarSpacer(bool on)
	{
		try
		{
			LuaHookBase.InvokeFromCLR(UIGameObserver.lua_SetSidebarSpacer, on);
		}
		catch (Exception ex)
		{
			LuaBridge.LogOnce("GameUi sidebar spacer", ex.Message);
		}
	}

	// GS_Scoreboard:SetVisible: the gamemode's scoreboard (Lua or C#) shown or hidden.
	internal static void SetScoreboardVisible(bool visible)
	{
		if (_scoreboard == null)
		{
			foreach (LuaMenuComponent menu in UnityEngine.Object.FindObjectsOfType<LuaMenuComponent>())
			{
				if (menu.gameObject.name == "Menu: Scoreboard (Lua)")
				{
					_scoreboard = menu;
					break;
				}
			}
		}
		if (_scoreboard != null && _scoreboard.CanvasGroup != null)
		{
			_scoreboard.CanvasGroup.alpha = visible ? 1f : 0f;
			_scoreboard.CanvasGroup.blocksRaycasts = visible;
		}
	}

	internal static void Forget()
	{
		_scoreboard = null;
	}
}
