using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace HoliestFluffiness.Windows;

public partial class ConfigWindow
{
    private List<CharacterRecord>? cachedRecords;
    private string charFilter = "";
    private string? csvExportMessage;

    private static readonly string[] DbColNames = ["Last Seen", "Character", "World", "DC", "FC", "Search Info", "Private House", "FC House", "Gil", "MGP"];

    private void LoadCharacters() =>
        cachedRecords = [.. characterDb.GetAll().OrderBy(r => r.World).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot)];

    private void DrawCharacterNameCell(CharacterRecord rec, bool lifestreamOn, string? currentKey)
    {
        bool isCurrent = currentKey != null && rec.Key == currentKey;

        if (isCurrent)
        {
            Common.GreenText(rec.Name);
        }
        else if (lifestreamOn)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColGold);
            if (ImGui.Selectable($"{rec.Name}##sel{rec.Key}", false, ImGuiSelectableFlags.None))
                onSwitchCharacter(rec.Name, rec.World);
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Click to switch to {rec.Name} on {rec.World}");
        }
        else
        {
            ImGui.TextUnformatted(rec.Name);
        }
    }

    private void DrawCharactersSection()
    {
        if (cachedRecords == null) LoadCharacters();

        bool lifestreamOn = Common.IsPluginLoaded(pluginInterface, "Lifestream");
        string? currentKey = Common.GetCurrentPlayerKey(objectTable);

        BeginSection("Characters");

        var cols = configuration.CharactersColumns;

        SectionRow();
        ImGui.SetNextItemWidth(180f);
        PushInput();
        ImGui.InputText("##charfilter", ref charFilter, 128);
        PopInput();
        ImGui.SameLine();
        Common.DimmedText("Filter");
        ImGui.SameLine();
        PushButton();
        if (ImGui.Button("Columns##charcolsbtn")) ImGui.OpenPopup("##charcolspopup");
        ImGui.SameLine(0, 4);
        if (ImGui.Button("Refresh##charrefresh")) LoadCharacters();
        PopButton();

        if (ImGui.BeginPopup("##charcolspopup"))
        {
            PushCheckbox();
            for (int i = 0; i < DbColNames.Length; i++)
            {
                bool vis = configuration.CharactersColumns[i];
                if (ImGui.Checkbox(DbColNames[i] + "##colchk" + i, ref vis))
                {
                    configuration.CharactersColumns[i] = vis;
                    configuration.Save();
                }
            }
            PopCheckbox();
            ImGui.EndPopup();
        }

        ImGui.Dummy(new Vector2(0, 2));

        int colCount = cols.Count(v => v) + 1; // +1 for Actions
        if (colCount == 1)
        {
            Common.DimmedText("No columns selected.");
        }
        else
        {
            var tableH = Math.Max(50f, ImGui.GetContentRegionAvail().Y - 4f);
            var tableFlags = ImGuiTableFlags.Sortable
                | ImGuiTableFlags.ScrollY
                | ImGuiTableFlags.BordersOuter
                | ImGuiTableFlags.BordersInnerV
                | ImGuiTableFlags.RowBg
                | ImGuiTableFlags.SizingStretchProp;

            if (ImGui.BeginTable("##chardb", colCount, tableFlags, new Vector2(0, tableH)))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                if (cols[0]) ImGui.TableSetupColumn("Last Seen",     ImGuiTableColumnFlags.PreferSortDescending, 0, 0);
                if (cols[1]) ImGui.TableSetupColumn("Character",     ImGuiTableColumnFlags.None, 0, 1);
                if (cols[2]) ImGui.TableSetupColumn("World",         ImGuiTableColumnFlags.DefaultSort, 0, 2);
                if (cols[3]) ImGui.TableSetupColumn("DC",            ImGuiTableColumnFlags.None, 0, 3);
                if (cols[4]) ImGui.TableSetupColumn("FC",            ImGuiTableColumnFlags.None, 0, 4);
                if (cols[5]) ImGui.TableSetupColumn("Search Info",   ImGuiTableColumnFlags.None, 0, 5);
                if (cols[6]) ImGui.TableSetupColumn("Private House", ImGuiTableColumnFlags.None, 0, 6);
                if (cols[7]) ImGui.TableSetupColumn("FC House",      ImGuiTableColumnFlags.None, 0, 7);
                if (cols[8]) ImGui.TableSetupColumn("Gil",           ImGuiTableColumnFlags.None, 0, 8);
                if (cols[9]) ImGui.TableSetupColumn("MGP",           ImGuiTableColumnFlags.None, 0, 9);
                ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.WidthFixed, 60f, 10);
                ImGui.TableHeadersRow();

                var sortSpecs = ImGui.TableGetSortSpecs();
                if (sortSpecs.SpecsDirty && sortSpecs.SpecsCount > 0 && cachedRecords != null)
                {
                    var spec = sortSpecs.Specs;
                    bool desc = spec.SortDirection == ImGuiSortDirection.Descending;
                    cachedRecords = (int)spec.ColumnUserID switch
                    {
                        0 => [.. (desc ? cachedRecords.OrderByDescending(r => r.LastSeen).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot)     : cachedRecords.OrderBy(r => r.LastSeen).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot))],
                        1 => [.. (desc ? cachedRecords.OrderByDescending(r => r.Name).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot)         : cachedRecords.OrderBy(r => r.Name).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot))],
                        2 => [.. (desc ? cachedRecords.OrderByDescending(r => r.World).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot)        : cachedRecords.OrderBy(r => r.World).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot))],
                        3 => [.. (desc ? cachedRecords.OrderByDescending(r => r.DataCenter).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot)   : cachedRecords.OrderBy(r => r.DataCenter).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot))],
                        4 => [.. (desc ? cachedRecords.OrderByDescending(r => r.FreeCompany).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot)  : cachedRecords.OrderBy(r => r.FreeCompany).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot))],
                        5 => [.. (desc ? cachedRecords.OrderByDescending(r => r.SearchInfo).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot)   : cachedRecords.OrderBy(r => r.SearchInfo).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot))],
                        6 => [.. (desc ? cachedRecords.OrderByDescending(r => r.PrivateHouse).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot) : cachedRecords.OrderBy(r => r.PrivateHouse).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot))],
                        7 => [.. (desc ? cachedRecords.OrderByDescending(r => r.FcHouse).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot)      : cachedRecords.OrderBy(r => r.FcHouse).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot))],
                        8 => [.. (desc ? cachedRecords.OrderByDescending(r => r.Gil).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot)          : cachedRecords.OrderBy(r => r.Gil).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot))],
                        9 => [.. (desc ? cachedRecords.OrderByDescending(r => r.Mgp).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot)          : cachedRecords.OrderBy(r => r.Mgp).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot))],
                        _ => cachedRecords,
                    };
                    sortSpecs.SpecsDirty = false;
                }

                var filter = charFilter.Trim();
                var worldFilter = WorldResolver.Resolve(filter, cachedRecords!.Select(r => r.World)) ?? filter;
                string? pendingReset  = null;
                string? pendingDelete = null;
                foreach (var rec in cachedRecords ?? [])
                {
                    if (filter.Length > 0
                        && !rec.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        && !rec.World.Contains(worldFilter, StringComparison.OrdinalIgnoreCase)
                        && !rec.DataCenter.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        && !(rec.FreeCompany ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    ImGui.TableNextRow();
                    int c = 0;
                    if (cols[0]) { ImGui.TableSetColumnIndex(c++); ImGui.TextUnformatted(rec.LastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm")); }
                    if (cols[1])
                    {
                        ImGui.TableSetColumnIndex(c++);
                        DrawCharacterNameCell(rec, lifestreamOn, currentKey);
                    }
                    if (cols[2]) { ImGui.TableSetColumnIndex(c++); ImGui.TextUnformatted(rec.Slot > 0 ? $"{rec.World}/{rec.Slot}" : rec.World); }
                    if (cols[3]) { ImGui.TableSetColumnIndex(c++); ImGui.TextUnformatted(rec.DataCenter); }
                    if (cols[4]) { ImGui.TableSetColumnIndex(c++); ImGui.TextUnformatted(rec.FreeCompany ?? ""); }
                    if (cols[5]) { ImGui.TableSetColumnIndex(c++); ImGui.TextUnformatted(rec.SearchInfo ?? ""); }
                    if (cols[6]) { ImGui.TableSetColumnIndex(c++); ImGui.TextUnformatted(rec.PrivateHouse ?? ""); }
                    if (cols[7]) { ImGui.TableSetColumnIndex(c++); ImGui.TextUnformatted(rec.FcHouse ?? ""); }
                    if (cols[8]) { ImGui.TableSetColumnIndex(c++); ImGui.TextUnformatted(rec.Gil < 0 ? "" : rec.Gil.ToString("N0", CultureInfo.InvariantCulture)); }
                    if (cols[9]) { ImGui.TableSetColumnIndex(c++); ImGui.TextUnformatted(rec.Mgp < 0 ? "" : rec.Mgp.ToString("N0", CultureInfo.InvariantCulture)); }

                    ImGui.TableSetColumnIndex(c);
                    PushButton();
                    if (ImGui.SmallButton($"~##{rec.Key}")) pendingReset = rec.Key;
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Reset cached data for this character");
                    ImGui.SameLine(0, 2);
                    ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColRed);
                    if (ImGui.SmallButton($"X##{rec.Key}")) pendingDelete = rec.Key;
                    ImGui.PopStyleColor();
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Delete this character from the database");
                    PopButton();
                }

                ImGui.EndTable();

                if (pendingReset  != null) { characterDb.Reset(pendingReset);   LoadCharacters(); }
                if (pendingDelete != null) { characterDb.Delete(pendingDelete); LoadCharacters(); }
            }
        }

        EndSection();
    }
}
