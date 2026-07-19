using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace HoliestFluffiness.Windows;

// Active FATEs in the current zone, opened with /fates. Clicking a row flags the FATE on the map.
// Inspired by VanillaPlus' Fate List Window (MidoriKami).
public sealed class FateListWindow : Window, IDisposable
{
    private readonly IFateTable       fateTable;
    private readonly ITextureProvider textureProvider;

    // Remaining-time colour thresholds (seconds): yellow at 05:00, red at 02:30.
    private const long  YellowSeconds = 300;
    private const long  RedSeconds    = 150;
    private const float RowHeight     = 28f;
    private const float IconSize      = 22f;

    // Column indices, shared by header setup and the sort comparer.
    private const int ColIcon = 0, ColName = 1, ColLevel = 2, ColProgress = 3, ColTime = 4;

    // FATE handles wrap native pointers, so they are re-fetched each frame rather than cached
    private readonly List<IFate> fatesBuf = [];
    private int sortCol = ColTime;
    private bool sortAsc = true;
    private readonly Comparison<IFate> fateComparer;

    public FateListWindow(IFateTable fateTable, ITextureProvider textureProvider)
        : base("Active FATEs##HFFates")
    {
        this.fateTable       = fateTable;
        this.textureProvider = textureProvider;
        fateComparer         = CompareFates;

        Size          = new Vector2(440, 380);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        Common.PushWindowTheme();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
    }

    public override void PostDraw()
    {
        Common.PopWindowTheme();
        ImGui.PopStyleVar(2);
    }

    public override void Draw()
    {
        fatesBuf.Clear();
        foreach (var f in fateTable)
            if (f is { State: FateState.Running or FateState.Preparing })
                fatesBuf.Add(f);

        if (fatesBuf.Count == 0)
        {
            ImGui.Dummy(new Vector2(0, 12));
            const string msg = "No active FATEs in this area.";
            ImGui.SetCursorPosX((ImGui.GetWindowSize().X - ImGui.CalcTextSize(msg).X) * 0.5f);
            Common.DimmedText(msg);
            return;
        }

        DrawTable(fatesBuf);
    }

    private void DrawTable(List<IFate> fates)
    {
        var tableFlags = ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH |
                         ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable |
                         ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable;

        if (!ImGui.BeginTable("##fates", 5, tableFlags))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.NoReorder, IconSize);
        ImGui.TableSetupColumn("Name",     ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoHide, 3f);
        ImGui.TableSetupColumn("Lv",       ImGuiTableColumnFlags.WidthStretch, 0.7f);
        ImGui.TableSetupColumn("Prog%", ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableSetupColumn("Time",     ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort, 0.75f);

        Common.PushTableHeader();
        ImGui.TableHeadersRow();
        Common.PopTableHeader();

        SortFates(fates);

        foreach (var fate in fates)
            DrawRow(fate);

        ImGui.EndTable();
    }

    private void DrawRow(IFate fate)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.None, RowHeight);
        var lineH = ImGui.GetTextLineHeight();
        var rowY  = -1f;
        // Screen-space bounds of the icon+name span, which is the clickable label region.
        var labelLeftX  = float.MaxValue;
        var labelRightX = -1f;
        var padX        = ImGui.GetStyle().CellPadding.X;

        // Cells are addressed by logical index, not sequential TableNextColumn(): with columns
        // hidden that would run past the last visible one and pile the centering offset into the row.

        // Icon (part of the clickable label span)
        if (ImGui.TableSetColumnIndex(ColIcon))
        {
            var start = ImGui.GetCursorScreenPos();
            if (rowY < 0) rowY = start.Y;
            labelLeftX  = Math.Min(labelLeftX, start.X - padX);
            labelRightX = Math.Max(labelRightX, start.X + ImGui.GetContentRegionAvail().X + padX);
            var tex = textureProvider.GetFromGameIcon(fate.MapIconId).GetWrapOrEmpty();
            CenterCursorY(IconSize);
            ImGui.Image(tex.Handle, new Vector2(IconSize, IconSize));
        }

        // Name (part of the clickable label span; bonus EXP FATEs render in gold)
        if (ImGui.TableSetColumnIndex(ColName))
        {
            var start = ImGui.GetCursorScreenPos();
            if (rowY < 0) rowY = start.Y;
            labelLeftX  = Math.Min(labelLeftX, start.X - padX);
            labelRightX = Math.Max(labelRightX, start.X + ImGui.GetContentRegionAvail().X + padX);
            CenterCursorY(lineH);
            ImGui.PushStyleColor(ImGuiCol.Text, fate.HasBonus ? Theme.ColGold : Theme.ColWhite);
            ImGui.TextUnformatted(fate.Name.TextValue);
            ImGui.PopStyleColor();
        }

        if (ImGui.TableSetColumnIndex(ColLevel))
        {
            if (rowY < 0) rowY = ImGui.GetCursorScreenPos().Y;
            CenterCursorY(lineH);
            Common.DimmedText(LevelLabel(fate));
        }

        // The cell background fills like a bar in proportion to completion, gold from 50% and red
        // from 90%. The fill stays translucent so the percentage text reads on top.
        if (ImGui.TableSetColumnIndex(ColProgress))
        {
            var start = ImGui.GetCursorScreenPos();
            if (rowY < 0) rowY = start.Y;
            var cellLeft  = start.X - padX;
            var cellRight = start.X + ImGui.GetContentRegionAvail().X + padX;
            var frac = Math.Clamp(fate.Progress / 100f, 0f, 1f);

            var fill = fate.Progress >= 90 ? Theme.ColRed  with { W = 0.45f }
                     : fate.Progress >= 50 ? Theme.ColGold with { W = 0.40f }
                     :                       Theme.ColGold with { W = 0.15f };
            ImGui.GetWindowDrawList().AddRectFilled(
                new Vector2(cellLeft, rowY),
                new Vector2(cellLeft + (cellRight - cellLeft) * frac, rowY + RowHeight),
                ImGui.GetColorU32(fill));

            CenterCursorY(lineH);
            ImGui.TextUnformatted($"{fate.Progress}%");
        }

        if (ImGui.TableSetColumnIndex(ColTime))
        {
            if (rowY < 0) rowY = ImGui.GetCursorScreenPos().Y;
            CenterCursorY(lineH);
            DrawTime(fate);
        }

        // Scoped to the icon+name span. CellBg fills behind cell content regardless of draw order.
        if (rowY >= 0 && labelRightX >= 0)
        {
            var min = new Vector2(labelLeftX, rowY);
            var max = new Vector2(labelRightX, rowY + RowHeight);
            // clip: false, or the test is clipped to the last cell's rect (the right-hand Time
            // column), which never overlaps this left-side span.
            if (ImGui.IsMouseHoveringRect(min, max, false))
            {
                var col = ImGui.GetColorU32(Theme.ColGoldSub);
                ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, col, ColIcon);
                ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, col, ColName);
                ImGui.SetTooltip($"{fate.Name.TextValue}\nClick to flag on the map.");
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    FocusFateOnMap(fate);
            }
        }
    }

    private static void CenterCursorY(float contentHeight)
        => ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Math.Max(0f, (RowHeight - contentHeight) * 0.5f));

    private static void DrawTime(IFate fate)
    {
        if (fate.State == FateState.Preparing || fate.TimeRemaining <= 0)
        {
            Common.DimmedText("Pending");
            return;
        }

        var text  = TimeSpan.FromSeconds(fate.TimeRemaining).ToString(@"mm\:ss");
        var color = fate.TimeRemaining <= RedSeconds    ? Theme.ColRed
                  : fate.TimeRemaining <= YellowSeconds ? Theme.ColGold
                  : Theme.ColWhite;
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    // Sorts by the table's active sort column, falling back to time remaining.
    private void SortFates(List<IFate> fates)
    {
        var specs = ImGui.TableGetSortSpecs();
        sortCol = ColTime;
        sortAsc = true;
        if (!specs.IsNull && specs.SpecsCount > 0)
        {
            sortCol = specs.Specs.ColumnIndex;
            sortAsc = specs.Specs.SortDirection != ImGuiSortDirection.Descending;
        }

        fates.Sort(fateComparer);
    }

    private int CompareFates(IFate a, IFate b)
    {
        var cmp = sortCol switch
        {
            ColName     => string.Compare(a.Name.TextValue, b.Name.TextValue, StringComparison.OrdinalIgnoreCase),
            ColLevel    => a.Level.CompareTo(b.Level),
            ColProgress => a.Progress.CompareTo(b.Progress),
            _           => a.TimeRemaining.CompareTo(b.TimeRemaining),
        };
        return sortAsc ? cmp : -cmp;
    }

    private static string LevelLabel(IFate fate)
    {
        if (fate is { Level: 1, MaxLevel: 255 }) return "?";
        return fate.MaxLevel > fate.Level
            ? $"{fate.Level}-{fate.MaxLevel}"
            : $"{fate.Level}";
    }

    private static unsafe void FocusFateOnMap(IFate fate)
    {
        var agent = AgentMap.Instance();
        if (agent == null) return;
        // The default map flag leaves the map's own FATE icon intact
        agent->SetFlagMapMarker(agent->CurrentTerritoryId, agent->CurrentMapId, fate.Position);
        agent->OpenMap(agent->CurrentMapId, agent->CurrentTerritoryId);
    }
}
