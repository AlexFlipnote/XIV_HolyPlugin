using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace HoliestFluffiness.Windows;

// A column in a DrawDataTable. SortKey == null marks a non-sortable column (pair with
// ImGuiTableColumnFlags.NoSort, e.g. a trailing Actions column).
public readonly record struct TableColumn<T>(
    string Label,
    uint UserId,
    ImGuiTableColumnFlags Flags,
    float WidthOrWeight,
    Func<T, IComparable>? SortKey,
    Action<T> DrawCell);

// Hideable/Reorderable columns get show/hide, drag-reorder and reset from ImGui's right-click header
// menu, persisted via its table-settings ini keyed on tableId.
internal static class ConfigTable
{
    private const ImGuiTableFlags DefaultFlags = ImGuiTableFlags.Sortable
        | ImGuiTableFlags.Hideable
        | ImGuiTableFlags.Reorderable
        | ImGuiTableFlags.ScrollY
        | ImGuiTableFlags.BordersInnerV
        | ImGuiTableFlags.RowBg
        | ImGuiTableFlags.SizingStretchProp;

    // The leftmost visible column has no left divider for margin, so it needs a nudge.
    // Reordering can change which one that is, so find it fresh each frame by screen X.
    // Must be called inside a table, on the row whose columns are being measured.
    internal static int LeftmostVisibleColumn(int columnCount)
    {
        int leftmostIdx = 0;
        float leftmostX = float.MaxValue;
        for (int i = 0; i < columnCount; i++)
        {
            ImGui.TableSetColumnIndex(i);
            if (!ImGui.TableGetColumnFlags(i).HasFlag(ImGuiTableColumnFlags.IsVisible)) continue;
            var x = ImGui.GetCursorScreenPos().X;
            if (x < leftmostX) { leftmostX = x; leftmostIdx = i; }
        }
        return leftmostIdx;
    }

    public static bool DrawDataTable<T>(
        string tableId,
        IReadOnlyList<TableColumn<T>> columns,
        ref List<T> rows,
        Func<T, IComparable> stableTieBreak,
        Func<T, bool>? filter = null,
        ImGuiTableFlags tableFlags = DefaultFlags,
        float? heightOverride = null)
    {
        var tableH = Math.Max(50f, heightOverride ?? ImGui.GetContentRegionAvail().Y - 4f);

        if (!ImGui.BeginTable(tableId, columns.Count, tableFlags, new Vector2(0, tableH)))
            return false;

        ImGui.TableSetupScrollFreeze(0, 1);
        foreach (var col in columns)
        {
            // Header-less columns have no menu entry, so hiding one would be irreversible
            var flags = col.Label.StartsWith("##", StringComparison.Ordinal) ? col.Flags | ImGuiTableColumnFlags.NoHide : col.Flags;
            ImGui.TableSetupColumn(col.Label, flags, col.WidthOrWeight, col.UserId);
        }

        Common.PushTableHeader();
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

        var leftmostIdx = LeftmostVisibleColumn(columns.Count);

        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            ImGui.TableSetColumnIndex(i);
            if (i == leftmostIdx)
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);
            ImGui.TableHeader(col.Flags.HasFlag(ImGuiTableColumnFlags.NoHeaderLabel) ? "" : col.Label);
        }
        Common.PopTableHeader();

        var sortSpecs = ImGui.TableGetSortSpecs();
        if (sortSpecs.SpecsDirty && sortSpecs.SpecsCount > 0)
        {
            var spec = sortSpecs.Specs;
            var sortCol = columns.FirstOrDefault(c => c.UserId == spec.ColumnUserID);
            if (sortCol.SortKey != null)
            {
                bool desc = spec.SortDirection == ImGuiSortDirection.Descending;
                var key = sortCol.SortKey!;
                rows = [.. (desc
                    ? rows.OrderByDescending(key).ThenBy(stableTieBreak)
                    : rows.OrderBy(key).ThenBy(stableTieBreak))];
            }
            sortSpecs.SpecsDirty = false;
        }

        foreach (var row in rows)
        {
            if (filter != null && !filter(row)) continue;

            ImGui.TableNextRow();
            for (int i = 0; i < columns.Count; i++)
            {
                ImGui.TableNextColumn();
                if (i == leftmostIdx)
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);
                columns[i].DrawCell(row);
            }
        }

        ImGui.EndTable();
        return true;
    }
}

public partial class ConfigWindow
{
    // Filter + Refresh row, shared by every table section
    private void DrawTableToolbar(ref string filter, string filterId, Action onRefresh, string refreshId)
    {
        SectionRow();
        ImGui.SetNextItemWidth(180f);
        PushInput();
        ImGui.InputText(filterId, ref filter, 128);
        PopInput();
        ImGui.SameLine();
        PushButton();
        if (ImGui.Button(refreshId)) onRefresh();
        PopButton();

        ImGui.Dummy(new Vector2(0, 2));
    }
}
