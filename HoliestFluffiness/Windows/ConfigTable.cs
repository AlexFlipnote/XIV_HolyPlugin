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

// Standalone (not tied to ConfigWindow) so any window can draw a table with the same DNA:
// native Hideable/Reorderable columns get show/hide, drag-reorder, and reset for free from Dear
// ImGui's own right-click header menu, persisted via its table-settings ini keyed on tableId - no
// plugin-side column state. Used by Characters/Bids (inside ConfigWindow) and CharacterPickerWindow.
internal static class ConfigTable
{
    private const ImGuiTableFlags DefaultFlags = ImGuiTableFlags.Sortable
        | ImGuiTableFlags.Hideable
        | ImGuiTableFlags.Reorderable
        | ImGuiTableFlags.ScrollY
        | ImGuiTableFlags.BordersInnerV
        | ImGuiTableFlags.RowBg
        | ImGuiTableFlags.SizingStretchProp;

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
            // Columns with no visible header text carry no identifying label in the right-click
            // menu either, so let the user hide them by accident with nothing to click to bring
            // them back - keep them permanently visible.
            var flags = col.Label.StartsWith("##", StringComparison.Ordinal) ? col.Flags | ImGuiTableColumnFlags.NoHide : col.Flags;
            ImGui.TableSetupColumn(col.Label, flags, col.WidthOrWeight, col.UserId);
        }

        Common.PushTableHeader();
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

        // Whichever column currently renders flush against the table's true left edge needs an
        // extra nudge - unlike every other column, it has no divider on its left to lean on for
        // margin. Dragging headers can move a different column into that slot, so this is
        // measured fresh every frame (via each visible column's actual screen X) rather than
        // hardcoded to whichever column happens to be declared first.
        int leftmostIdx = 0;
        float leftmostX = float.MaxValue;
        for (int i = 0; i < columns.Count; i++)
        {
            ImGui.TableSetColumnIndex(i);
            if (!ImGui.TableGetColumnFlags(i).HasFlag(ImGuiTableColumnFlags.IsVisible)) continue;
            var x = ImGui.GetCursorScreenPos().X;
            if (x < leftmostX) { leftmostX = x; leftmostIdx = i; }
        }

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
    // Filter InputText + Refresh button row shared by every table section.
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
