using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace HoliestFluffiness.Windows;

public class CharacterPickerWindow : Window
{
    private static readonly Vector4 ColBg       = new(24f / 255f,  24f / 255f,  24f / 255f,  1f);
    private static readonly Vector4 ColBgDeep   = new(18f / 255f,  18f / 255f,  18f / 255f,  1f);
    private static readonly Vector4 ColSection  = new(40f / 255f,  40f / 255f,  40f / 255f,  1f);
    private static readonly Vector4 ColWhite    = new(249f / 255f, 248f / 255f, 244f / 255f, 1f);
    private static readonly Vector4 ColWhiteDim = new(249f / 255f, 248f / 255f, 244f / 255f, 0.55f);
    private static readonly Vector4 ColGold     = new(235f / 255f, 230f / 255f, 114f / 255f, 1f);
    private static readonly Vector4 ColGoldSub  = new(235f / 255f, 230f / 255f, 114f / 255f, 0.18f);
    private static readonly Vector4 ColGoldMid  = new(235f / 255f, 230f / 255f, 114f / 255f, 0.35f);

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
        ImGui.PushStyleColor(ImGuiCol.WindowBg,             ColBg);
        ImGui.PushStyleColor(ImGuiCol.Text,                 ColWhite);
        ImGui.PushStyleColor(ImGuiCol.TitleBg,              ColBg);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive,        ColBg);
        ImGui.PushStyleColor(ImGuiCol.FrameBg,              ColBgDeep);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered,       ColSection);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,          ColBgDeep);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab,        ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, ColGold);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive,  ColGold);
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip,           ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered,    ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive,     ColGold);
        ImGui.PushStyleColor(ImGuiCol.Border,               ColGoldMid);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding,    4f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize,  1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,     new Vector2(10, 10));
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding,       new Vector2(8, 5));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(14);
        ImGui.PopStyleVar(4);
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
        var flags   = ImGuiTableFlags.ScrollY
                    | ImGuiTableFlags.BordersInnerH
                    | ImGuiTableFlags.SizingStretchProp
                    | ImGuiTableFlags.Sortable;

        string? nextName  = null;
        string? nextWorld = null;

        if (ImGui.BeginTable("##charpicker", 3, flags, new Vector2(0, tableH)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            // 3rd param = initial fixed width, 4th = user ID for sort specs
            // Measured pixel widths used as proportional stretch weights — all columns scale together
            ImGui.TableSetupColumn("Last login", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending, col0Width, 0);
            ImGui.TableSetupColumn("Name",       ImGuiTableColumnFlags.None,                                                     col1Width, 1);
            ImGui.TableSetupColumn("World/Slot", ImGuiTableColumnFlags.None,                                                     col2Width, 2);

            ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, ColSection);
            ImGui.PushStyleColor(ImGuiCol.Text,          ColGold);
            ImGui.TableHeadersRow();
            ImGui.PopStyleColor(2);

            // Sort — must happen after TableHeadersRow so specs are populated
            var sortSpecs = ImGui.TableGetSortSpecs();
            if (sortSpecs.SpecsDirty && sortSpecs.SpecsCount > 0)
            {
                var spec = sortSpecs.Specs;
                bool desc = spec.SortDirection == ImGuiSortDirection.Descending;
                records = (int)spec.ColumnUserID switch
                {
                    0 => [.. desc ? records.OrderByDescending(r => r.LastSeen)
                                  : records.OrderBy(r => r.LastSeen)],
                    1 => [.. desc ? records.OrderByDescending(r => r.Name)
                                  : records.OrderBy(r => r.Name)],
                    2 => [.. desc ? records.OrderByDescending(r => r.World).ThenByDescending(r => r.Slot == 0 ? int.MaxValue : r.Slot)
                                  : records.OrderBy(r => r.World).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot)],
                    _ => records,
                };
                sortSpecs.SpecsDirty = false;
            }

            var filter  = search.Trim();
            var visible = string.IsNullOrEmpty(filter)
                ? records
                : records.Where(r =>
                    r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    r.World.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var rec in visible)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);

                // SpanAllColumns covers the full row for hover highlight and click
                ImGui.PushStyleColor(ImGuiCol.Header,        ColGoldSub);
                ImGui.PushStyleColor(ImGuiCol.HeaderHovered, ColGoldSub);
                ImGui.PushStyleColor(ImGuiCol.HeaderActive,  ColGoldMid);
                ImGui.PushStyleColor(ImGuiCol.Text,          ColWhiteDim);
                bool clicked = ImGui.Selectable(
                    $"{rec.LastSeen.ToLocalTime():yyyy-MM-dd HH:mm}##pick{rec.Key}",
                    false,
                    ImGuiSelectableFlags.SpanAllColumns);
                ImGui.PopStyleColor(4);

                if (clicked) { nextName = rec.Name; nextWorld = rec.World; }

                ImGui.TableSetColumnIndex(1);
                ImGui.PushStyleColor(ImGuiCol.Text, ColGold);
                ImGui.TextUnformatted(rec.Name);
                ImGui.PopStyleColor();

                ImGui.TableSetColumnIndex(2);
                ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
                ImGui.TextUnformatted(rec.Slot > 0 ? $"{rec.World}/{rec.Slot}" : rec.World);
                ImGui.PopStyleColor();
            }

            ImGui.EndTable();
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.PushStyleColor(ImGuiCol.Border, ColGoldMid);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.InputTextWithHint("##pickersearch", "Search...", ref search, 128);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();

        if (nextName != null && nextWorld != null)
        {
            IsOpen = false;
            onLogin(nextName, nextWorld);
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
                    + style.WindowPadding.X * 2
                    + 4f; // outer borders

        Size          = new Vector2(Math.Max(280f, total), Size?.Y ?? 310f);
        SizeCondition = ImGuiCond.Always;
    }
}
