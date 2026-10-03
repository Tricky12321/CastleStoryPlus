require("LUI2.lua")
-------
-- Resource list for the top-right corner: one row per stocked resource with icon, name and count.
-- Replaces the icon grid that used to sit in the faction panel under the minimap.
-- Resources that have been stocked once stay listed for the rest of the session, in red at 0.
-- Above the list: stockpile room used / total and free (CastleStoryPlus.StorageUsed/StorageCapacity).
-------



-------
local _t = {}

_t.rowHeight = 24
_t.iconSize = 22
_t.width = 220

--excluded resources (same as Panel_Storage)
_t.excludedResources = {}
_t.excludedResources[Resource.SentinelWard] = true
_t.excludedResources[Resource.HealingWard] = true
_t.excludedResources[Resource.CaptureWard] = true
_t.excludedResources[Resource.LanternWard] = true
_t.excludedResources[Resource.PylonWard] = true
_t.excludedResources[Resource.BearTrap] = true
_t.excludedResources[Resource.None] = true

--resources stocked at least once since the map was loaded
_t.seen = {}

_t.normalNameColor = Color.New(0.85, 0.85, 0.85, 1)
-------



-------
--panels: root
_t.CreateRootPanel =
	function(self)
		local p =
			LayoutPanels.Vertical:New()
			.SetWidgetHandle(Widgets.LayoutElement, Handles.Visible, ||true, true)
			.SetWidgetHandle(Widgets.LayoutElement, Handles.MinWidth, ||self.width, true)
			.SetWidgetHandle(Widgets.LayoutElement, Handles.PreferredWidth, ||self.width, true)
			.SetWidgetHandle(Widgets.LayoutElement, Handles.FlexibleWidth, ||-1, true)
			.SetWidgetHandle(Widgets.LayoutElement, Handles.FlexibleHeight, ||-1, true)
			.SetWidgetHandle(Widgets.LayoutGroup, Handles.Spacing, ||1, true)
			.SetWidgetHandle(Widgets.LayoutGroup, Handles.Padding, ||Vector4.New(8, 8, 6, 6), true)
			.SetWidgetHandle(Widgets.LayoutGroup, Handles.Alignment, ||Alignment.UpperLeft, true)
			.SetWidgetHandle(Widgets.Image, Handles.Enabled, ||true, true)
			.SetWidgetHandle(Widgets.Image, Handles.Color, ||_bgColorDark, true)

		p._t = self
		return p
	end

--panels: header
_t.CreateHeaderPanel =
	function(self, p_parent)
		local p =
			Panels.Label:New()
			.SetWidgetHandle(Widgets.LabelLayoutElement, Handles.MinHeight, ||self.rowHeight, true)
			.SetWidgetHandle(Widgets.LabelLayoutElement, Handles.PreferredHeight, ||self.rowHeight, true)
			.SetWidgetHandle(Widgets.LabelLayoutElement, Handles.FlexibleWidth, ||1, true)
			.SetWidgetHandle(Widgets.Label, Handles.Text, ||"RESOURCES", true)
			.SetWidgetHandle(Widgets.Label, Handles.Alignment, ||Alignment.MiddleLeft, true)
			.SetWidgetHandle(Widgets.Label, Handles.Color, ||Color.New(0.6, 0.6, 0.6, 1), true)
			.SetWidgetHandle(Widgets.Label, Handles.FontSize, ||12, true)
			.SetWidgetHandle(Widgets.Label, Handles.Font, ||Font.ProximaNovaRegular, true)

		p_parent.AddPanel(p)
	end

--storage room (stockpile encumbrance units)
_t.StorageUsed =
	function(self)
		return CastleStoryPlus.StorageUsed()
	end
_t.StorageCapacity =
	function(self)
		return CastleStoryPlus.StorageCapacity()
	end
_t.StorageFree =
	function(self)
		return math.max(0, self:StorageCapacity() - self:StorageUsed())
	end
_t.StorageFreeColor =
	function(self)
		local capacity = self:StorageCapacity()
		if capacity <= 0 or self:StorageFree() <= 0 then
			return CastleRed
		end
		if self:StorageUsed() / capacity >= 0.9 then
			return Color.New(1, 0.55, 0.1, 1)
		end
		return CastleYellow
	end

--panels: one storage row (label left, value right)
_t.CreateStorageRowPanel =
	function(self, p_parent, text, valueFunc, colorFunc)
		local row =
			LayoutPanels.Horizontal:New()
			.SetWidgetHandle(Widgets.LayoutElement, Handles.MinHeight, ||self.rowHeight, true)
			.SetWidgetHandle(Widgets.LayoutElement, Handles.PreferredHeight, ||self.rowHeight, true)
			.SetWidgetHandle(Widgets.LayoutElement, Handles.FlexibleWidth, ||1, true)
			.SetWidgetHandle(Widgets.LayoutGroup, Handles.Spacing, ||6, true)
			.SetWidgetHandle(Widgets.LayoutGroup, Handles.Padding, ||Vector4.zero, true)
			.SetWidgetHandle(Widgets.LayoutGroup, Handles.Alignment, ||Alignment.MiddleLeft, true)
			.SetWidgetHandle(Widgets.Image, Handles.Enabled, ||false, true)

		local name =
			Panels.Label:New()
			.SetWidgetHandle(Widgets.LabelLayoutElement, Handles.MinHeight, ||self.rowHeight, true)
			.SetWidgetHandle(Widgets.LabelLayoutElement, Handles.PreferredHeight, ||self.rowHeight, true)
			.SetWidgetHandle(Widgets.LabelLayoutElement, Handles.FlexibleWidth, ||1, true)
			.SetWidgetHandle(Widgets.Label, Handles.Text, ||text, true)
			.SetWidgetHandle(Widgets.Label, Handles.Alignment, ||Alignment.MiddleLeft, true)
			.SetWidgetHandle(Widgets.Label, Handles.Color, ||self.normalNameColor, true)
			.SetWidgetHandle(Widgets.Label, Handles.FontSize, ||14, true)
			.SetWidgetHandle(Widgets.Label, Handles.Font, ||Font.ProximaNovaRegular, true)
		row.AddPanel(name)

		--value (numbers are right-aligned)
		local value =
			Panels.Label:New()
			.SetWidgetHandle(Widgets.LabelLayoutElement, Handles.MinWidth, ||90, true)
			.SetWidgetHandle(Widgets.LabelLayoutElement, Handles.PreferredWidth, ||90, true)
			.SetWidgetHandle(Widgets.LabelLayoutElement, Handles.MinHeight, ||self.rowHeight, true)
			.SetWidgetHandle(Widgets.LabelLayoutElement, Handles.PreferredHeight, ||self.rowHeight, true)
			.SetWidgetHandle(Widgets.LabelLayoutElement, Handles.FlexibleWidth, ||-1, true)
			.SetWidgetHandle(Widgets.Label, Handles.Text, valueFunc)
			.SetWidgetHandle(Widgets.Label, Handles.Alignment, ||Alignment.MiddleRight, true)
			.SetWidgetHandle(Widgets.Label, Handles.Color, colorFunc)
			.SetWidgetHandle(Widgets.Label, Handles.FontSize, ||14, true)
			.SetWidgetHandle(Widgets.Label, Handles.Font, ||Font.ProximaNovaRegular, true)
		row.AddPanel(value)

		Data.Storage.ev_onSetContent:AddListener(||value.Refresh())
		p_parent.AddPanel(row)
	end

_t.CreateStoragePanels =
	function(self, p_parent)
		self:CreateStorageRowPanel(p_parent, "Storage used", ||tostring(self:StorageUsed()) .. " / " .. tostring(self:StorageCapacity()), ||CastleYellow)
		self:CreateStorageRowPanel(p_parent, "Storage free", ||tostring(self:StorageFree()), ||self:StorageFreeColor())
	end

--panels: empty hint
_t.CreateEmptyPanel =
	function(self, p_parent)
		local p =
			Panels.Label:New()
			.SetWidgetHandle(Widgets.LabelLayoutElement, Handles.Visible, ||not self:HasAnyRow())
			.SetWidgetHandle(Widgets.LabelLayoutElement, Handles.MinHeight, ||self.rowHeight, true)
			.SetWidgetHandle(Widgets.LabelLayoutElement, Handles.PreferredHeight, ||self.rowHeight, true)
			.SetWidgetHandle(Widgets.LabelLayoutElement, Handles.FlexibleWidth, ||1, true)
			.SetWidgetHandle(Widgets.Label, Handles.Text, ||"-", true)
			.SetWidgetHandle(Widgets.Label, Handles.Alignment, ||Alignment.MiddleLeft, true)
			.SetWidgetHandle(Widgets.Label, Handles.Color, ||CastleRed, true)
			.SetWidgetHandle(Widgets.Label, Handles.FontSize, ||14, true)
			.SetWidgetHandle(Widgets.Label, Handles.Font, ||Font.ProximaNovaRegular, true)

		Data.Storage.ev_onSetContent:AddListener(||p.Refresh())
		p_parent.AddPanel(p)
	end

_t.HasAnyRow =
	function(self)
		for i, resource in ipairs(Meta.Resource) do
			if not self.excludedResources[resource] and self:IsListed(resource) then
				return true
			end
		end
		return false
	end

--listed while stocked, and afterwards once it has been stocked
_t.IsListed =
	function(self, resource)
		if Data.Storage:HasResourceCount(resource) and Data.Storage:GetResourceCount(resource) > 0 then
			self.seen[resource] = true
		end
		return self.seen[resource] == true
	end

_t.IsEmpty =
	function(self, resource)
		return Data.Storage:GetResourceCount(resource) <= 0
	end

--panels: one row per resource
_t.CreateRowPanel =
	function(self, p_parent, resource)

		--row
		local row =
			LayoutPanels.Horizontal:New()
			.SetWidgetHandle(Widgets.LayoutElement, Handles.Visible, ||self:IsListed(resource))
			.SetWidgetHandle(Widgets.LayoutElement, Handles.MinHeight, ||self.rowHeight, true)
			.SetWidgetHandle(Widgets.LayoutElement, Handles.PreferredHeight, ||self.rowHeight, true)
			.SetWidgetHandle(Widgets.LayoutElement, Handles.FlexibleWidth, ||1, true)
			.SetWidgetHandle(Widgets.LayoutGroup, Handles.Spacing, ||6, true)
			.SetWidgetHandle(Widgets.LayoutGroup, Handles.Padding, ||Vector4.zero, true)
			.SetWidgetHandle(Widgets.LayoutGroup, Handles.Alignment, ||Alignment.MiddleLeft, true)
			.SetWidgetHandle(Widgets.Image, Handles.Enabled, ||false, true)

		--icon
		local icon =
			Panels.Image:New()
			.SetWidgetHandle(Widgets.LayoutElement, Handles.MinWidth, ||self.iconSize, true)
			.SetWidgetHandle(Widgets.LayoutElement, Handles.MinHeight, ||self.iconSize, true)
			.SetWidgetHandle(Widgets.LayoutElement, Handles.PreferredWidth, ||self.iconSize, true)
			.SetWidgetHandle(Widgets.LayoutElement, Handles.PreferredHeight, ||self.iconSize, true)
			.SetWidgetHandle(Widgets.LayoutElement, Handles.FlexibleWidth, ||-1, true)
			.SetWidgetHandle(Widgets.Image, Handles.Enabled, ||true, true)
			.SetWidgetHandle(Widgets.Image, Handles.Sprite, ||Data.Resource:GetStockpiledIcon(resource), true)
			.SetWidgetHandle(Widgets.Image, Handles.Color, ||Color.New(1, 1, 1, 1), true)
		row.AddPanel(icon)

		--name
		local name =
			Panels.Label:New()
			.SetWidgetHandle(Widgets.LabelLayoutElement, Handles.MinHeight, ||self.rowHeight, true)
			.SetWidgetHandle(Widgets.LabelLayoutElement, Handles.PreferredHeight, ||self.rowHeight, true)
			.SetWidgetHandle(Widgets.LabelLayoutElement, Handles.FlexibleWidth, ||1, true)
			.SetWidgetHandle(Widgets.Label, Handles.Text, ||Data.Resource:GetName(resource), true)
			.SetWidgetHandle(Widgets.Label, Handles.Alignment, ||Alignment.MiddleLeft, true)
			.SetWidgetHandle(Widgets.Label, Handles.Color, ||self:IsEmpty(resource) and CastleRed or self.normalNameColor)
			.SetWidgetHandle(Widgets.Label, Handles.FontSize, ||14, true)
			.SetWidgetHandle(Widgets.Label, Handles.Font, ||Font.ProximaNovaRegular, true)
		row.AddPanel(name)

		--count (numbers are right-aligned)
		local count =
			Panels.Label:New()
			.SetWidgetHandle(Widgets.LabelLayoutElement, Handles.MinWidth, ||48, true)
			.SetWidgetHandle(Widgets.LabelLayoutElement, Handles.PreferredWidth, ||48, true)
			.SetWidgetHandle(Widgets.LabelLayoutElement, Handles.MinHeight, ||self.rowHeight, true)
			.SetWidgetHandle(Widgets.LabelLayoutElement, Handles.PreferredHeight, ||self.rowHeight, true)
			.SetWidgetHandle(Widgets.LabelLayoutElement, Handles.FlexibleWidth, ||-1, true)
			.SetWidgetHandle(Widgets.Label, Handles.Text, ||tostring(Data.Storage:GetResourceCount(resource)))
			.SetWidgetHandle(Widgets.Label, Handles.Alignment, ||Alignment.MiddleRight, true)
			.SetWidgetHandle(Widgets.Label, Handles.Color, ||self:IsEmpty(resource) and CastleRed or CastleYellow)
			.SetWidgetHandle(Widgets.Label, Handles.FontSize, ||14, true)
			.SetWidgetHandle(Widgets.Label, Handles.Font, ||Font.ProximaNovaRegular, true)
		row.AddPanel(count)

		Data.Storage.ev_onSetContent:AddListener(||row.Refresh())
		Data.Storage.ev_onSetContent:AddListener(||name.Refresh())
		Data.Storage.ev_onSetContent:AddListener(||count.Refresh())

		p_parent.AddPanel(row)
	end

_t.CreateRows =
	function(self, p_parent)
		for i, resource in ipairs(Meta.Resource) do
			if not self.excludedResources[resource] then
				self:CreateRowPanel(p_parent, resource)
			end
		end
	end
-------



-------
--root: build
local _p = _t:CreateRootPanel()
_t:CreateHeaderPanel(_p)
_t:CreateStoragePanels(_p)
_t:CreateEmptyPanel(_p)
_t:CreateRows(_p)
-------



-------
--return
return _p
