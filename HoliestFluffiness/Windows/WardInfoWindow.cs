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
// button and clicking a row to switch wards are the only user-triggered automation, and neither
// runs on its own.
public sealed class WardInfoWindow : Window
{
    private readonly Configuration config;
    private readonly WardInfoHandler handler;
    private readonly IGameGui gameGui;
    private readonly IDataManager dataManager;

    private const float WindowWidth = 430f;

    private string search = "";
    private bool hideOwned;
    private bool standaloneOpen;
    private bool wasOpen;
    private bool docked;
    private (short WorldId, short TerritoryTypeId)? selectedDistrict;
    private List<PlotRow> rowsBuf = [];
    private int rowsBufVersion = -1;
    private LandIdent? rowsBufContext;

    private readonly record struct PlotRow(short WardNumber, int PlotIndex, HouseInfoEntry Entry, byte? Size);

    private const uint ColWard = 0, ColPlot = 1, ColSize = 2, ColOwner = 3;

    private readonly TableColumn<PlotRow>[] columns;

    public WardInfoWindow(Configuration config, WardInfoHandler handler, IGameGui gameGui, IDataManager dataManager)
        : base("Ward Info##HFWardInfo", ImGuiWindowFlags.NoCollapse)
    {
        this.config      = config;
        this.handler     = handler;
        this.gameGui     = gameGui;
        this.dataManager = dataManager;

        columns =
        [
            new("Ward", ColWard, ImGuiTableColumnFlags.WidthFixed, 56f, r => r.WardNumber, r => ImGui.TextUnformatted($"{r.WardNumber + 1}")),
            new("Plot", ColPlot, ImGuiTableColumnFlags.WidthFixed, 50f, r => r.PlotIndex,  r => ImGui.TextUnformatted($"{r.PlotIndex + 1}")),
            new("Size", ColSize, ImGuiTableColumnFlags.WidthFixed, 50f, r => r.Size ?? -1, r => ImGui.TextUnformatted(SizeLabel(r.Size))),
            new("Owner/Price", ColOwner, ImGuiTableColumnFlags.WidthStretch, 1f, r => OwnerSortKey(r.Entry), DrawOwner),
        ];
    }

    // /houses always shows this: a normal, resizable, freely-movable window usable anywhere, still
    // reading from the same session-scoped cache. It layers on top of the auto-docked behavior
    // rather than replacing it - if the native menu happens to be open at the same time, docking
    // still wins (see PreDraw). Once shown this way, it only closes via its own close button (or
    // Escape) - re-running /houses does not hide it again.
    public void ShowStandalone() => standaloneOpen = true;

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
    // never open - opened via /houses instead), it behaves like any other resizable plugin window.
    //
    // Docked positioning uses WindowNode (the actual visible frame) rather than RootNode, which
    // carries extra invisible margin on every side that would otherwise leak past the native
    // window's real edges, and walks the full parent chain via Common.GetNodePosition rather than
    // a single-level addon->Scale multiply, since that's the same helper ReadyCheckOverlay already
    // relies on for precise native-node screen positions.
    public override unsafe void PreDraw()
    {
        Common.PushWindowTheme();

        var addon = GetAddon();
        docked = addon != null && addon->WindowNode != null;

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
            // Appearing (not FirstUseEver) so every standalone (/houses) open starts at this
            // default size rather than remembering whatever it was last manually resized to.
            ImGui.SetNextWindowSize(new Vector2(500, 500), ImGuiCond.Appearing);
        }
    }

    public override void PostDraw() => Common.PopWindowTheme();

    public override void Draw()
    {
        var current = ResolveCurrentContext();
        if (docked)
            DrawHeader(current);
        else
            DrawDistrictDropdown(current);

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputTextWithHint("##wardinfosearch", "Search owner/FC...", ref search, 64);

        DrawToolbar();

        ImGui.Dummy(new Vector2(0, 4));

        if (current == null)
        {
            ImGui.Dummy(new Vector2(0, 12));
            Common.DimmedText("No wards recorded yet - browse wards or press Sweep.");
            return;
        }

        // Rebuilding from GetPlots() every frame would hand ConfigTable a fresh, unsorted list each
        // time and undo the user's column sort the moment they picked one - only rebuild when the
        // underlying data (or which district we're looking at) actually changed.
        if (handler.Version != rowsBufVersion || rowsBufContext != current)
        {
            rowsBuf = handler.GetPlots(current.WorldId, current.TerritoryTypeId)
                .Select(p => new PlotRow(p.Ward.WardNumber, p.PlotIndex, p.Plot,
                    HousingDistricts.PlotSize(dataManager, current.TerritoryTypeId, p.PlotIndex)))
                .ToList();
            rowsBufVersion = handler.Version;
            rowsBufContext = current;
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
            return string.IsNullOrWhiteSpace(search) ||
                   r.Entry.EstateOwnerName.Contains(search, StringComparison.OrdinalIgnoreCase);
        }

        ConfigTable.DrawDataTable("##wardinfotable", columns, ref rowsBuf,
            stableTieBreak: r => (r.WardNumber * 100) + r.PlotIndex,
            filter: Filter);
    }

    // While docked, always follows whatever ward is currently open in the native menu. Otherwise
    // (standalone via /houses) an explicit dropdown pick wins, falling back to the last-browsed
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
    // captured (world, district) to browse, instead of only ever showing whatever was most
    // recently browsed live.
    private void DrawDistrictDropdown(LandIdent? current)
    {
        var districts = handler.GetCachedDistricts().ToList();
        if (districts.Count == 0)
        {
            CenteredText("Not browsing a residential ward yet.", null);
            return;
        }

        var currentLabel = current != null ? DistrictLabel(current.WorldId, current.TerritoryTypeId, withCount: true) : "Select district...";
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.BeginCombo("##wardinfodistrict", currentLabel))
        {
            foreach (var d in districts)
            {
                var isSelected = current != null && current.WorldId == d.WorldId && current.TerritoryTypeId == d.TerritoryTypeId;
                if (ImGui.Selectable(DistrictLabel(d.WorldId, d.TerritoryTypeId, withCount: true), isSelected))
                    selectedDistrict = d;
            }
            ImGui.EndCombo();
        }
    }

    private string DistrictLabel(short worldId, short territoryTypeId, bool withCount = false)
    {
        var districtName = HousingDistricts.FromTerritoryId((ushort)territoryTypeId) ?? $"Territory {territoryTypeId}";
        var worldName = dataManager.GetExcelSheet<Lumina.Excel.Sheets.World>()?.GetRowOrDefault((uint)worldId)?.Name.ToString() ?? "?";
        var label = $"{districtName} ({worldName})";
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

    // Hide-owned toggle on the left (gold-themed like NearbyWindow's pin button), Sweep pushed to
    // the far right.
    private void DrawToolbar()
    {
        DrawHideOwnedButton();
        ImGui.SameLine();
        DrawSweepButtonRightAligned();
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

    // Mirrors the native "Owner/Price" column exactly: white price while unowned, green name if
    // the house currently allows visitors, red if it doesn't - FC names keep their guillemets.
    // The whole cell is clickable (not just the glyphs) and switches the open menu straight to
    // that plot's ward - no travel, no third-party integration.
    private void DrawOwner(PlotRow r)
    {
        string label;
        Vector4 color;
        if (!r.Entry.IsOwned)
        {
            label = FormatPrice(r.Entry.HousePrice);
            color = Theme.ColWhite;
        }
        else
        {
            var isFc = (r.Entry.InfoFlags & HousingFlags.OwnedByFC) != 0;
            label = isFc ? $"«{r.Entry.EstateOwnerName}»" : r.Entry.EstateOwnerName;
            var open = (r.Entry.InfoFlags & HousingFlags.VisitorsAllowed) != 0;
            color = open ? Theme.ColGreen : Theme.ColRed;
        }

        // Selectable (not Text) so the click/hover region spans the full cell width, not just the
        // rendered glyphs.
        ImGui.PushStyleColor(ImGuiCol.Text,          color);
        ImGui.PushStyleColor(ImGuiCol.Header,        Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Theme.ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive,  Theme.ColGoldMid);
        var clicked = ImGui.Selectable($"{label}##r{r.WardNumber}_{r.PlotIndex}");
        ImGui.PopStyleColor(4);

        if (clicked)
            handler.EnterWard(r.WardNumber);
    }

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
