using BepInEx.Configuration;
using Brix.UI.Builder.Lui;
using Brix.UI.Builder.Menu;
using CastleStoryPlus.Core;
using MoonSharp.Interpreter;

namespace CastleStoryPlus.Loading;

// The game UI makes a small menu for every tooltip article (twice: one set for the task popup, one for the task
// list) and every help article, and opens all of them while a map loads, so each one's panels and widgets are
// created up front although only one tooltip shows at a time. With LazyArticles on, an article's menu is still made
// while loading but only opened (its panels created) the first time it is shown: a tooltip when it is first
// hovered, a help article when it is first selected or is the current one when the help opens. Once shown it
// stays open as before. The game's own "opened by its parent" flag is given a condition for this, so the parent
// menu opening and closing works as it did.
[Feature(Features.FasterLoading, Features.FasterLoadingInfo)]
internal static class LazyArticles
{
	internal static ConfigEntry<bool> Enabled;

	private static void Enable()
	{
		Enabled = Plugin.Cfg.Bind("LuaUi", "LazyArticles", false, "Tooltip and help articles get their panels the first time they are shown instead of while a map loads (shorter loading, a tooltip's first showing builds it).");
		// With the tooltips and the help drawn in C# no article menu is made at all.
		if (LuaUi.LuaUiConfig.CsTooltips)
		{
			return;
		}
		LuaInjection.AddFunction("LazyArticles", (ScriptExecutionContext context, CallbackArguments args) => DynValue.NewBoolean(Enabled.Value));
		LuaInjection.AddFunction("OpenLazyMenu", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			Open((args.Count > 0) ? args[0].Table : null, (args.Count > 1) ? args[1].Table : null);
			return DynValue.Void;
		});
		// Tooltips: opened with their parent only once shown; shown for the first time, opened at once.
		LuaInjection.AddPatch(Features.FasterLoading, "LUI/Menus/Game/Menu_Tooltip.lua", "Data.Article:ToMenu(func_getData)()", LuaInjection.Mode.InsertAfter, @"
			-- Castle Story Plus: LazyArticles
			if CastleStoryPlus.LazyArticles() then
				_m.lazyShown = _m.lazyShown or {}
				m.SetFlag(MenuFlag.OpenedByParentMenu, ||_m.lazyShown[func_getData] == true)
			end");
		LuaInjection.AddPatch(Features.FasterLoading, "LUI/Menus/Game/Menu_Tooltip.lua", "local m_dst = func_dst and _m.tooltipMenus[func_dst]", LuaInjection.Mode.InsertAfter, @"
		-- Castle Story Plus: LazyArticles
		if m_dst and _m.lazyShown and not _m.lazyShown[func_dst] then
			_m.lazyShown[func_dst] = true
			CastleStoryPlus.OpenLazyMenu(m_dst, _m)
		end");
		// Help: also the current article, so it opens with the help.
		LuaInjection.AddPatch(Features.FasterLoading, "LUI/Menus/Game/Menu_Help.lua", "Data.Article:ToMenu(func_getData, self)()", LuaInjection.Mode.InsertAfter, @"
			-- Castle Story Plus: LazyArticles
			if CastleStoryPlus.LazyArticles() then
				_m.lazyShown = _m.lazyShown or {}
				m.SetFlag(MenuFlag.OpenedByParentMenu, ||_m.lazyShown[func_getData] == true or Data.Article:GetFunc(_m._article_currentPath) == func_getData)
			end");
		LuaInjection.AddPatch(Features.FasterLoading, "LUI/Menus/Game/Menu_Help.lua", "local dstMenu = self._articleMenus[dstGetData]", LuaInjection.Mode.InsertAfter, @"
		-- Castle Story Plus: LazyArticles
		if dstMenu and _m.lazyShown and not _m.lazyShown[dstGetData] then
			_m.lazyShown[dstGetData] = true
			CastleStoryPlus.OpenLazyMenu(dstMenu, _m)
		end");
	}

	// Opens an article's menu now when its parent is open (its panel, where the article goes, exists); otherwise
	// the parent opens it with itself.
	private static void Open(Table article, Table parent)
	{
		if (article == null || parent == null || !LuiBuilder.MapHelper.HasMenu(article) || !LuiBuilder.MapHelper.HasMenu(parent))
		{
			return;
		}
		IMenu menu = LuiBuilder.MapHelper.GetMenu(article);
		IMenu owner = LuiBuilder.MapHelper.GetMenu(parent);
		if (owner.Opened && menu.Loaded && !menu.Opened)
		{
			menu.Open();
		}
	}
}
