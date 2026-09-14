using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using HoliestFluffiness.Handlers;

namespace HoliestFluffiness.Windows;

// Docks to the right of the native "Select Residential Ward" (HousingSelectBlock) menu and shows
// every plot captured so far this session: owner/FC and price/availability. Data comes from
// WardInfoHandler, which is purely in-memory and session-scoped - nothing here touches config or
// the database. See feedback-dalamud-plugin-constraints: capture is always passive; the Sweep
// button is the only user-triggered automation here, and it never runs on its own. Standalone row
// clicks hand off to Lifestream's /li command (via the injected `teleport` action) - that is
// Lifestream's own automation, not this plugin's, and only ever fires from an explicit click.
public sealed class WardInfoWindow : Window
{
    private readonly Configuration config;
    private readonly WardInfoHandler handler;
    private readonly IGameGui gameGui;
    private readonly IDataManager dataManager;
    private readonly Action<string> teleport;
    private readonly Func<bool> isLifestreamBusy;

    private const float WindowWidth = 430f;

    private string search = "";
    private bool hideOwned;
    private TenantType? tenantFilter;
    private bool standaloneOpen;
    private bool wasOpen;
    private bool docked;
    private (short WorldId, short TerritoryTypeId)? selectedDistrict;
    private bool allIndexesSelected;
    private List<PlotRow> rowsBuf = [];
    private int rowsBufVersion = -1;
    private LandIdent? rowsBufContext;
    private bool rowsBufIsAll;

    // Tenant is per-ward (every plot in a ward shares the same restriction), captured straight from
    // the game's own HousingWardInfo blob - not guessed from ward numbers, which the assignment of
    // FC-only/personal-only wards isn't fixed enough to hardcode reliably.
    private readonly record struct PlotRow(short WorldId, short TerritoryTypeId, short WardNumber, int PlotIndex, HouseInfoEntry Entry, TenantType Tenant, byte? Size);

    private const uint ColWard = 0, ColPlot = 1, ColSize = 2, ColOwner = 3;

    private readonly TableColumn<PlotRow>[] columns;

    public WardInfoWindow(Configuration config, WardInfoHandler handler, IGameGui gameGui, IDataManager dataManager,
        Action<string> teleport, Func<bool> isLifestreamBusy)
        : base("Ward Info##HFWardInfo", ImGuiWindowFlags.NoCollapse)
    {
        this.config           = config;
        this.handler          = handler;
        this.gameGui          = gameGui;
        this.dataManager      = dataManager;
        this.teleport         = teleport;
        this.isLifestreamBusy = isLifestreamBusy;

        columns =
        [
            new("Ward", ColWard, ImGuiTableColumnFlags.WidthFixed, 50f, r => r.WardNumber, DrawWardCell),
            new("Plot", ColPlot, ImGuiTableColumnFlags.WidthFixed, 34f, r => r.PlotIndex,  r => ImGui.TextUnformatted($"{r.PlotIndex + 1}")),
            new("Size", ColSize, ImGuiTableColumnFlags.WidthFixed, 34f, r => r.Size ?? -1, r => ImGui.TextUnformatted(SizeLabel(r.Size))),
            new("Owner/Price", ColOwner, ImGuiTableColumnFlags.WidthStretch, 1f, r => OwnerSortKey(r.Entry), DrawOwner),
        ];
    }

    // /wardinfo (and /wi) show this: a normal, resizable, freely-movable window usable anywhere,
    // still reading from the same session-scoped cache. It layers on top of the auto-docked
    // behavior rather than replacing it - if the native menu happens to be open at the same time,
    // docking still wins (see PreDraw). Once shown this way, it only closes via its own close
    // button (or Escape) - re-running /wardinfo does not hide it again. Gated on the same "Show
    // ward info panel" setting as docking, so turning that off really does disable the feature.
    public void ShowStandalone()
    {
        if (config.WardInfoWindowEnabled) standaloneOpen = true;
    }

    public override unsafe void PreOpenCheck()
    {
        var addonOpen = config.WardInfoWindowEnabled && GetAddon() != null;

        // A transition from open to closed that we didn't cause (the close button, Escape - only
        // reachable while not docked, since the close button is hidden while docked) means the
        // user just closed it manually. Without checking wasOpen here, standaloneOpen would still
        // be true the moment it's first set (before IsOpen has caught up to it), and this same
        // check would wrongly read that as "already closed itself" and undo the open before it
        // ever appeared; requiring an actual true-to-false transition avoids that false positive.
        if (wasOpen && !IsOpen && !addonOpen) standaloneOpen = false;

        IsOpen = addonOpen || standaloneOpen;
        wasOpen = IsOpen;
    }

    // While the native "Select Residential Ward" menu is open, this window locks to its right edge
    // and current height every frame and loses its close/pin chrome, so it reads as "attached to"
    // that window rather than a separate floating panel. The moment the native menu closes (or was
    // never open - opened via /wardinfo instead), it behaves like any other resizable plugin window.
    //
    // Docked positioning uses WindowNode (the actual visible frame) rather than RootNode, which
    // carries extra invisible margin on every side that would otherwise leak past the native
    // window's real edges, and walks the full parent chain via Common.GetNodePosition rather than
    // a single-level addon->Scale multiply, since that's the same helper ReadyCheckOverlay already
    // relies on for precise native-node screen positions.
    // True while either our own auto-sweep-all or Lifestream itself is actively driving the
    // character/menus - the window must not dock or accept input during this, since none of it is
    // the user manually browsing right now.
    private bool IsBusy => handler.IsAutoSweeping || isLifestreamBusy();

    public override unsafe void PreDraw()
    {
        Common.PushWindowTheme();

        // Suppressed while busy even if the native menu happens to be open (auto-sweep-all drives
        // it directly) - docking mid-automation would reposition/reshape the window and hide its
        // close button right as the user has the least ability to react to that.
        var addon = GetAddon();
        docked = !IsBusy && addon != null && addon->WindowNode != null;

        Flags             = docked ? ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize : ImGuiWindowFlags.NoCollapse;
        ShowCloseButton   = !docked;
        AllowPinning      = !docked;
        AllowClickthrough = !docked;

        if (docked)
        {
            var win = &addon->WindowNode->AtkResNode;
            var topLeft = Common.GetNodePosition(win);
            var pos = new Vector2(topLeft.X + win->Width * addon->Scale, topLeft.Y);
            ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(WindowWidth, win->Height * addon->Scale), ImGuiCond.Always);
        }
        else
        {
            // Appearing (not FirstUseEver) so every standalone (/wardinfo) open starts at this
            // default size rather than remembering whatever it was last manually resized to.
            ImGui.SetNextWindowSize(new Vector2(500, 500), ImGuiCond.Appearing);
        }
    }

    public override void PostDraw() => Common.PopWindowTheme();

    public override void Draw()
    {
        var busy = IsBusy;

        // Everything except the toolbar (which still needs its Cancel button live) is disabled
        // while busy - there's no manual browsing to do while auto-sweep-all or Lifestream is
        // actively driving the character, and leaving it interactive risks a click fighting with
        // whatever the automation is doing this same frame.
        ImGui.BeginDisabled(busy);
        if (docked)
            DrawHeader(handler.LastLandIdent);
        else
            DrawDistrictDropdown();

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputTextWithHint("##wardinfosearch", "Search owner/FC...", ref search, 64);
        ImGui.EndDisabled();

        DrawToolbar();

        ImGui.Dummy(new Vector2(0, 4));

        ImGui.BeginDisabled(busy);
        try
        {
            // "All indexes" only makes sense standalone - docked always follows a single ward.
            var showAll = !docked && allIndexesSelected;
            var current = showAll ? null : ResolveCurrentContext();

            if (!showAll && current == null)
            {
                ImGui.Dummy(new Vector2(0, 12));
                Common.DimmedText("No wards recorded yet - browse wards or press Sweep.");
                return;
            }

            // Rebuilding from GetPlots()/GetAllPlots() every frame would hand ConfigTable a fresh,
            // unsorted list each time and undo the user's column sort the moment they picked one -
            // only rebuild when the underlying data (or which view we're looking at) actually changed.
            if (showAll)
            {
                if (handler.Version != rowsBufVersion || !rowsBufIsAll)
                {
                    rowsBuf = handler.GetAllPlots()
                        .Select(p => new PlotRow(p.Ward.WorldId, p.Ward.TerritoryTypeId, p.Ward.WardNumber, p.PlotIndex, p.Plot, p.Tenant,
                            HousingDistricts.PlotSize(dataManager, p.Ward.TerritoryTypeId, p.PlotIndex)))
                        .ToList();
                    rowsBufVersion = handler.Version;
                    rowsBufContext = null;
                    rowsBufIsAll   = true;
                }
            }
            else if (handler.Version != rowsBufVersion || rowsBufIsAll || rowsBufContext != current)
            {
                rowsBuf = handler.GetPlots(current!.WorldId, current.TerritoryTypeId)
                    .Select(p => new PlotRow(current.WorldId, current.TerritoryTypeId, p.Ward.WardNumber, p.PlotIndex, p.Plot, p.Tenant,
                        HousingDistricts.PlotSize(dataManager, current.TerritoryTypeId, p.PlotIndex)))
                    .ToList();
                rowsBufVersion = handler.Version;
                rowsBufContext = current;
                rowsBufIsAll   = false;
            }

            if (rowsBuf.Count == 0)
            {
                ImGui.Dummy(new Vector2(0, 12));
                Common.DimmedText("No wards recorded yet - browse wards or press Sweep.");
                return;
            }

            bool Filter(PlotRow r)
            {
                if (hideOwned && r.Entry.IsOwned) return false;
                // Most wards are unrestricted (neither FreeCompany nor Personal) - a tenant filter
                // only ever excludes the OPPOSITE restriction, never the unrestricted majority.
                if (tenantFilter == TenantType.FreeCompany && r.Tenant == TenantType.Personal) return false;
                if (tenantFilter == TenantType.Personal && r.Tenant == TenantType.FreeCompany) return false;
                return string.IsNullOrWhiteSpace(search) ||
                       r.Entry.EstateOwnerName.Contains(search, StringComparison.OrdinalIgnoreCase);
            }

            if (showAll)
                ConfigTable.DrawDataTable("##wardinfotableall", columns, ref rowsBuf,
                    stableTieBreak: r => $"{r.WorldId:D5}_{r.TerritoryTypeId:D5}_{r.WardNumber:D3}_{r.PlotIndex:D3}",
                    filter: Filter);
            else
                ConfigTable.DrawDataTable("##wardinfotable", columns, ref rowsBuf,
                    stableTieBreak: r => (r.WardNumber * 100) + r.PlotIndex,
                    filter: Filter);
        }
        finally
        {
            ImGui.EndDisabled();
        }
    }

    // While docked, always follows whatever ward is currently open in the native menu. Otherwise
    // (standalone via /wardinfo) an explicit dropdown pick wins, falling back to the last-browsed
    // ward if nothing's been picked yet.
    private LandIdent? ResolveCurrentContext()
    {
        if (docked) return handler.LastLandIdent;
        if (selectedDistrict is { } sel) return new LandIdent(-1, -1, sel.TerritoryTypeId, sel.WorldId);
        return handler.LastLandIdent;
    }

    private void DrawHeader(LandIdent? current)
    {
        if (current == null)
        {
            CenteredText("Not browsing a residential ward yet.", null);
            return;
        }

        var wardsCaptured = handler.CapturedWardCount(current.WorldId, current.TerritoryTypeId);
        var captureText = wardsCaptured > 0 ? $"({wardsCaptured} ward(s) captured this session)" : null;
        CenteredText(DistrictLabel(current.WorldId, current.TerritoryTypeId), captureText);
    }

    // Replaces the plain header when not docked: lets a standalone viewer pick any previously-
    // captured (world, district) to browse, or "All indexes" to combine every captured district/
    // world into one table, instead of only ever showing whatever was most recently browsed live.
    private void DrawDistrictDropdown()
    {
        var districts = handler.GetCachedDistricts().ToList();
        if (districts.Count == 0)
        {
            CenteredText("Not browsing a residential ward yet.", null);
            return;
        }

        var current = ResolveCurrentContext();
        var currentLabel = allIndexesSelected
            ? "All indexes"
            : current != null ? DistrictLabel(current.WorldId, current.TerritoryTypeId, withCount: true) : "Select district...";

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.BeginCombo("##wardinfodistrict", currentLabel))
        {
            if (ImGui.Selectable("All indexes##wardinfoallindexes", allIndexesSelected))
                allIndexesSelected = true;

            ImGui.Separator();

            foreach (var d in districts)
            {
                var isSelected = !allIndexesSelected && current != null && current.WorldId == d.WorldId && current.TerritoryTypeId == d.TerritoryTypeId;
                if (ImGui.Selectable(DistrictLabel(d.WorldId, d.TerritoryTypeId, withCount: true), isSelected))
                {
                    selectedDistrict = d;
                    allIndexesSelected = false;
                }
            }
            ImGui.EndCombo();
        }
    }

    private string DistrictName(short territoryTypeId) => HousingDistricts.FromTerritoryId((ushort)territoryTypeId) ?? $"Territory {territoryTypeId}";

    private string WorldName(short worldId) =>
        dataManager.GetExcelSheet<Lumina.Excel.Sheets.World>()?.GetRowOrDefault((uint)worldId)?.Name.ToString() ?? "?";

    private string DistrictLabel(short worldId, short territoryTypeId, bool withCount = false)
    {
        var label = $"{DistrictName(territoryTypeId)} ({WorldName(worldId)})";
        if (!withCount) return label;

        var count = handler.CapturedWardCount(worldId, territoryTypeId);
        return count > 0 ? $"{label} - {count} captured" : label;
    }

    // Draws `main` in normal text and, if given, `dimmed` right after it in dimmed text, with the
    // whole combined line horizontally centered in the window.
    private static void CenteredText(string main, string? dimmed)
    {
        var spacing = dimmed == null ? 0f : ImGui.GetStyle().ItemSpacing.X;
        var totalWidth = ImGui.CalcTextSize(main).X + spacing + (dimmed == null ? 0f : ImGui.CalcTextSize(dimmed).X);
        var startX = Math.Max(ImGui.GetCursorPosX(), (ImGui.GetWindowSize().X - totalWidth) * 0.5f);
        ImGui.SetCursorPosX(startX);

        ImGui.TextUnformatted(main);
        if (dimmed == null) return;
        ImGui.SameLine(0, spacing);
        Common.DimmedText(dimmed);
    }

    // Hide-owned toggle on the left (gold-themed like NearbyWindow's pin button); the right side
    // depends on mode - Sweep while docked (that's the only time it can do anything, since it
    // drives the currently-open native menu), or "Sweep all districts" while standalone (that one
    // drives its own travel via Lifestream instead, so it makes no sense docked).
    private void DrawToolbar()
    {
        DrawHideOwnedButton();
        ImGui.SameLine();
        DrawTenantFilterDropdown();
        ImGui.SameLine();
        if (docked)
            DrawSweepButtonRightAligned();
        else
            DrawAutoSweepAllButtonRightAligned();
    }

    private void DrawHideOwnedButton()
    {
        var label = hideOwned ? "Show all##wardinfohideowned" : "Hide owned##wardinfohideowned";
        ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ColGold);
        ImGui.PushStyleColor(ImGuiCol.Text,          Theme.ColGold);
        if (ImGui.Button(label))
            hideOwned = !hideOwned;
        ImGui.PopStyleColor(4);
    }

    // Every plot in a ward shares one tenant restriction, so filtering by it is really filtering by
    // ward - lets a house hunter looking only for an FC (or only a personal) plot skip straight past
    // wards that could never have what they want, without reading each row's tag individually.
    private void DrawTenantFilterDropdown()
    {
        var preview = tenantFilter switch
        {
            TenantType.FreeCompany => "FC only",
            TenantType.Personal    => "Private only",
            _                      => "FC + Private",
        };

        ImGui.SetNextItemWidth(120);
        if (ImGui.BeginCombo("##wardinfotenantfilter", preview))
        {
            if (ImGui.Selectable("FC + Private", tenantFilter == null))
                tenantFilter = null;
            if (ImGui.Selectable("FC only", tenantFilter == TenantType.FreeCompany))
                tenantFilter = TenantType.FreeCompany;
            if (ImGui.Selectable("Private only", tenantFilter == TenantType.Personal))
                tenantFilter = TenantType.Personal;
            ImGui.EndCombo();
        }
    }

    private void DrawSweepButtonRightAligned()
    {
        var label = handler.IsSweeping
            ? $"Cancel ({handler.SweptCount}/{handler.SweepTotal})##wardinfosweep"
            : "Sweep this ward list##wardinfosweep";

        var width = ImGui.CalcTextSize(label.Split('#')[0]).X + ImGui.GetStyle().FramePadding.X * 2;
        var avail = ImGui.GetContentRegionAvail().X;
        if (avail > width)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - width);

        if (handler.IsSweeping)
        {
            if (ImGui.Button(label))
                handler.CancelSweep();
            return;
        }

        ImGui.BeginDisabled(!handler.CanSweep);
        if (ImGui.Button(label))
            handler.StartSweep();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clicks through the wards currently shown in the open menu to fill in the table faster.\nOnly runs while you keep the menu open.");
    }

    // Standalone-only counterpart to Sweep: teleports through every housing district on this world
    // via Lifestream, sweeping each one before moving to the next. Runs unattended once started -
    // Cancel stops it at any point, same as the docked Sweep button's Cancel.
    private void DrawAutoSweepAllButtonRightAligned()
    {
        var label = handler.IsAutoSweeping
            ? $"Cancel ({handler.AutoSweepCurrentDistrict}: {handler.SweptCount}/{handler.SweepTotal})##wardinfoautosweep"
            : "Sweep all districts##wardinfoautosweep";

        var width = ImGui.CalcTextSize(label.Split('#')[0]).X + ImGui.GetStyle().FramePadding.X * 2;
        var avail = ImGui.GetContentRegionAvail().X;
        if (avail > width)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - width);

        if (handler.IsAutoSweeping)
        {
            if (ImGui.Button(label))
                handler.CancelAutoSweepAll();
            return;
        }

        ImGui.BeginDisabled(!handler.CanAutoSweepAll);
        if (ImGui.Button(label))
            handler.StartAutoSweepAll();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Teleports to each housing district's gateway aetheryte, opens its ward menu, and sweeps it - repeating until every district is done.\nRuns unattended once started; press Cancel to stop early.");
    }

    // First column's cell: an invisible Selectable spanning every column, matching NearbyWindow's
    // full-row hover treatment (see NearbyWindow.DrawNearbyTable) instead of only highlighting this
    // one cell. Hovering shows the plot's full address as a tooltip.
    //
    // Clicking only does something while standalone: it hands the address straight to Lifestream
    // (/li), which already handles teleporting to the right aetheryte first if needed - the same
    // mechanism GoToBid uses in Plugin.cs. While docked to the native menu this is disabled - the
    // player is already looking at that menu live, so a click here would just be a distraction.
    private void DrawWardCell(PlotRow r)
    {
        var world    = WorldName(r.WorldId);
        var district = DistrictName(r.TerritoryTypeId);

        ImGui.PushStyleColor(ImGuiCol.Header,        Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Theme.ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive,  Theme.ColGoldMid);
        var clicked = ImGui.Selectable(
            $"{r.WardNumber + 1}##row{r.WorldId}_{r.TerritoryTypeId}_{r.WardNumber}_{r.PlotIndex}",
            false, ImGuiSelectableFlags.SpanAllColumns);
        ImGui.PopStyleColor(3);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"{world}, {district}, Ward {r.WardNumber + 1} ({TenantTag(r.Tenant)}), Plot {r.PlotIndex + 1}");

        if (clicked && !docked)
            teleport($"{world}, {district}, ward {r.WardNumber + 1}, plot {r.PlotIndex + 1}");
    }

    // Mirrors the native "Owner/Price" column exactly for owned plots: green name if the house
    // currently allows visitors, red if it doesn't, FC names keep their guillemets. Unowned plots
    // additionally tag the price with which tenant type(s) the ward allows (FC, Private, or
    // FC+Private for the unrestricted majority).
    private static void DrawOwner(PlotRow r)
    {
        string label;
        Vector4 color;
        if (!r.Entry.IsOwned)
        {
            label = $"{FormatPrice(r.Entry.HousePrice)} ({TenantTag(r.Tenant)})";
            color = Theme.ColWhite;
        }
        else
        {
            var isFc = (r.Entry.InfoFlags & HousingFlags.OwnedByFC) != 0;
            label = isFc ? $"«{r.Entry.EstateOwnerName}»" : r.Entry.EstateOwnerName;
            var open = (r.Entry.InfoFlags & HousingFlags.VisitorsAllowed) != 0;
            color = open ? Theme.ColGreen : Theme.ColRed;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();
    }

    private static string TenantTag(TenantType tenant) => tenant switch
    {
        TenantType.FreeCompany => "FC",
        TenantType.Personal    => "Private",
        _                      => "FC+Private",
    };

    // "17.000m" reads as more precise than the game ever actually shows; trim to "17m", "3.562m", etc.
    private static string FormatPrice(uint price) =>
        $"{(price / 1000000f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}m";

    private static string OwnerSortKey(HouseInfoEntry entry) => entry.IsOwned
        ? entry.EstateOwnerName
        : $"{entry.HousePrice:D10}"; // zero-padded so price sorts numerically as text

    private static string SizeLabel(byte? size) => size switch
    {
        0 => "S",
        1 => "M",
        2 => "L",
        _ => "?",
    };

    private unsafe AtkUnitBase* GetAddon()
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(WardInfoHandler.AddonName).Address;
        return addon != null && Common.IsAddonVisible(addon) ? addon : null;
    }
}
