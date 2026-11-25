using System.Collections;
using Brix.Engine;
using Brix.Game.AI;
using Brix.UI.GameSelector;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.GameUi;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// The selected task's popup over the task list is minimized per task (#198): the crystal's popup (the idle group)
// stays minimized while another task's popup shows open, and is minimized again when the crystal is selected again.
internal static class TaskPopupFoldTests
{
	private const string PopupName = "CS task popup";

	// Whether the popup shows its one minimized line (null: no popup in the menu).
	private static bool? PopupMinimized()
	{
		GameObject popup = GameObject.Find(PopupName);
		if (popup == null)
		{
			return null;
		}
		Transform line = popup.transform.Find("Minimized");
		return line != null && line.gameObject.activeSelf;
	}

	private static bool IdleSelected()
	{
		return GameUiState.Selected is IdleProject;
	}

	[GameTest("UI", "Minimizing the crystal's task popup keeps it minimized for the crystal only: another task's popup shows open, and the crystal's is minimized again when it is selected again (#198)", TimeoutSeconds = 60f)]
	private static IEnumerator CrystalPopupMinimizedPerTask(TestContext t)
	{
		if (CsGameMenu.Model == null || TaskPopupFold.Crystal == null)
		{
			t.Skip("The C# in-game menu is off");
		}
		bool crystalBefore = TaskPopupFold.Crystal.Value;
		BuildProject project = null;
		t.Cleanup(() =>
		{
			TaskPopupFold.Crystal.Value = crystalBefore;
			if (project != null)
			{
				TaskPopupFold.SetMinimized(project, false);
				if (project.selection)
				{
					UIGameSelector.Unselect(project.gameObject);
				}
			}
			Brix.UI.UIGameActor.SelectIdleProject();
		});
		XYZ site = Scenario.Site(t, 6, 6);
		project = Scenario.BuildProject(t, site);

		// The crystal, open, then minimized with the popup's button.
		TaskPopupFold.Crystal.Value = false;
		UIGameSelector.UnselectEverything();
		Brix.UI.UIGameActor.SelectIdleProject();
		yield return t.Until(IdleSelected, 5f, "the crystal (idle group) to be selected");
		yield return t.Until(() => PopupMinimized() == false, 5f, "the crystal's popup to show open");
		TaskPopupFold.Toggle();
		t.True(TaskPopupFold.Crystal.Value, "The minimize button did not keep the crystal's choice in [Menus] CrystalPopupMinimized");
		yield return t.Until(() => PopupMinimized() == true, 5f, "the crystal's popup to be minimized");

		// Another task: its own popup is open.
		UIGameSelector.Select(project.gameObject);
		yield return t.Until(() => GameUiState.Selected == project, 5f, "the build task to be selected");
		t.False(TaskPopupFold.CurrentMinimized, "The build task's popup is minimized because the crystal's is");
		yield return t.Until(() => PopupMinimized() == false, 5f, "the build task's popup to show open");

		// Back to the crystal: minimized again.
		UIGameSelector.Unselect(project.gameObject);
		Brix.UI.UIGameActor.SelectIdleProject();
		yield return t.Until(IdleSelected, 5f, "the crystal to be selected again");
		t.True(TaskPopupFold.CurrentMinimized, "The crystal's popup is no longer minimized after another task was selected");
		yield return t.Until(() => PopupMinimized() == true, 5f, "the crystal's popup to be minimized again");

		// And the other way round: the build task minimized, the crystal opened again, the build task still minimized.
		TaskPopupFold.SetMinimized(project, true);
		TaskPopupFold.Toggle();
		t.False(TaskPopupFold.Crystal.Value, "The button did not open the crystal's popup again");
		yield return t.Until(() => PopupMinimized() == false, 5f, "the crystal's popup to show open again");
		t.True(TaskPopupFold.IsMinimized(project), "Opening the crystal's popup opened the build task's too");
	}
}
