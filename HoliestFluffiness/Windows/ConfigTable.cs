using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace HoliestFluffiness.Windows;

// A column in a DrawDataTable. SortKey == null marks a non-sortable column (pair with
// ImGuiTableColumnFlags.NoSort, e.g. a trailing Actions column). HeaderPadLeft nudges the header
// label right by that many pixels - only needed by a column sitting flush against a window that
// has zero WindowPadding of its own (e.g. CharacterPickerWindow), where the table has no natural
// left margin to inherit.
public readonly record struct TableColumn<T>(
    string Label,
    uint UserId,
    ImGuiTableColumnFlags Flags,
    float WidthOrWeight,
    Func<T, IComparable>? SortKey,
    Action<T> DrawCell,
    float HeaderPadLeft = 0f);

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
        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            ImGui.TableSetColumnIndex(i);
            if (col.HeaderPadLeft > 0f)
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + col.HeaderPadLeft);
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
            foreach (var col in columns)
            {
                ImGui.TableNextColumn();
                col.DrawCell(row);
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
