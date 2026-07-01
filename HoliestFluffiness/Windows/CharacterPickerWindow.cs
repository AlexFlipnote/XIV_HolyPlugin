using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace HoliestFluffiness.Windows;

public class CharacterPickerWindow : Window
{
    private List<CharacterRecord> records = [];
    private string search = "";
    private readonly Action<string, string> onLogin;

    private bool needsMeasure;
    private bool pendingApply;
    private bool forceCenter;
    private float col0Width = 145f;
    private float col1Width = 140f;
    private float col2Width = 100f;

    public CharacterPickerWindow(Action<string, string> onLogin)
        : base("Choose a character##CharPickerPopup",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse)
    {
        this.onLogin       = onLogin;
        RespectCloseHotkey = true;
        Size               = new Vector2(430, 310);
        SizeCondition      = ImGuiCond.Appearing;
        SizeConstraints    = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(280, 160),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Show(List<CharacterRecord> chars)
    {
        records       = [.. chars.OrderByDescending(r => r.LastSeen)];
        search        = "";
        needsMeasure  = true;
        pendingApply  = false;
        forceCenter   = true;
        Size          = new Vector2(430, 310);
        SizeCondition = ImGuiCond.Appearing;
        IsOpen        = true;
    }

    public override void PreDraw()
    {
        // forceCenter=true on first Show() so re-opens always land on center even within the same session
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), forceCenter ? ImGuiCond.Always : ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        forceCenter = false;
        Common.PushWindowTheme();
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding,   new Vector2(8, 5));
    }

    public override void PostDraw()
    {
        Common.PopWindowTheme();
        ImGui.PopStyleVar(3);
    }

    private List<TableColumn<CharacterRecord>> BuildColumns(Action<CharacterRecord> onPick)
    {
        return
        [
            // Hosts the row-wide click target (SpanAllColumns), so it must stay visible even if
            // the user right-clicks the header to hide/reorder the other columns.
            new("Last login", 0, ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending | ImGuiTableColumnFlags.NoHide, col0Width,
                r => r.LastSeen,
                r =>
                {
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);
                    ImGui.PushStyleColor(ImGuiCol.Header,        Theme.ColGoldSub);
                    ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Theme.ColGoldSub);
                    ImGui.PushStyleColor(ImGuiCol.HeaderActive,  Theme.ColGoldMid);
                    ImGui.PushStyleColor(ImGuiCol.Text,          Theme.ColWhiteDim);
                    bool clicked = ImGui.Selectable(
                        $"{r.LastSeen.ToLocalTime():yyyy-MM-dd HH:mm}##pick{r.Key}",
                        false,
                        ImGuiSelectableFlags.SpanAllColumns);
                    ImGui.PopStyleColor(4);
                    if (clicked) onPick(r);
                },
                HeaderPadLeft: 8f),
            new("Name", 1, ImGuiTableColumnFlags.None, col1Width,
                r => r.Name,
                r => Common.GoldText(r.Name)),
            new("World/Slot", 2, ImGuiTableColumnFlags.None, col2Width,
                r => r.World,
                r => Common.DimmedText(r.Slot > 0 ? $"{r.World}/{r.Slot}" : r.World)),
        ];
    }

    public override void Draw()
    {
        if (needsMeasure && records.Count > 0)
        {
            MeasureAndResize();
            needsMeasure = false;
            pendingApply = true;
        }
        else if (pendingApply)
        {
            pendingApply  = false;
            SizeCondition = ImGuiCond.Appearing;
        }

        var footerH = ImGui.GetStyle().ItemSpacing.Y + ImGui.GetFrameHeightWithSpacing();
        var tableH  = ImGui.GetContentRegionAvail().Y - footerH;

        CharacterRecord? picked = null;
        var columns = BuildColumns(r => picked = r);

        var filter = search.Trim();
        ConfigTable.DrawDataTable(
            "##charpicker",
            columns,
            ref records,
            r => r.Slot == 0 ? int.MaxValue : r.Slot,
            r => filter.Length == 0
                || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.World.Contains(filter, StringComparison.OrdinalIgnoreCase),
            tableFlags: ImGuiTableFlags.Sortable | ImGuiTableFlags.Hideable | ImGuiTableFlags.Reorderable
                | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp,
            heightOverride: tableH);

        const float margin = 30f;
        ImGui.SetCursorPosX(margin);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - margin);
        Common.PushSearchInput();
        ImGui.InputTextWithHint("##pickersearch", "Search...", ref search, 128);
        Common.PopSearchInput();

        if (picked != null)
        {
            IsOpen = false;
            onLogin(picked.Name, picked.World);
        }
    }

    private void MeasureAndResize()
    {
        const float cellPad = 16f; // CellPadding.X (8px) × 2 sides

        float dateW    = ImGui.CalcTextSize("2026-06-26 14:17").X;
        col0Width = Math.Max(ImGui.CalcTextSize("Last login").X, dateW) + cellPad;

        float maxNameW = records.Count > 0 ? records.Max(r => ImGui.CalcTextSize(r.Name).X) : 0f;
        col1Width = Math.Max(ImGui.CalcTextSize("Name").X, maxNameW) + cellPad;

        float maxWorldW = records.Count > 0
            ? records.Max(r => ImGui.CalcTextSize(r.Slot > 0 ? $"{r.World}/{r.Slot}" : r.World).X)
            : 0f;
        col2Width = Math.Max(ImGui.CalcTextSize("World/Slot").X, maxWorldW) + cellPad;

        var style   = ImGui.GetStyle();
        float total = col0Width + col1Width + col2Width
                    + style.ScrollbarSize
                    + 4f; // outer borders

        Size          = new Vector2(Math.Max(280f, total), Size?.Y ?? 310f);
        SizeCondition = ImGuiCond.Always;
    }
}
