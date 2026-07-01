using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Dalamud.Bindings.ImGui;

namespace HoliestFluffiness.Windows;

public partial class ConfigWindow
{
    private record CharacterRow(CharacterRecord Rec, Dictionary<uint, int> Items);

    private List<CharacterRow>? cachedRecords;
    private string charFilter = "";
    private string? csvExportMessage;

    private void LoadCharacters()
    {
        cachedRecords = [.. characterDb.GetAll()
            .OrderBy(r => r.World).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot)
            .Select(r =>
            {
                var items = r.Inventory != null
                    ? JsonSerializer.Deserialize<Dictionary<uint, int>>(r.Inventory) ?? []
                    : new Dictionary<uint, int>();
                return new CharacterRow(r, items);
            })];
    }

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

    private List<TableColumn<CharacterRow>> BuildCharacterColumns(bool lifestreamOn, string? currentKey, Action<string> onReset, Action<string> onDelete)
    {
        var itemCols = LoginInfoHandler.TrackedItems.ToList();
        uint uid = 0;

        var columns = new List<TableColumn<CharacterRow>>
        {
            new("Last Seen", uid++, ImGuiTableColumnFlags.PreferSortDescending, 0,
                r => r.Rec.LastSeen,
                r =>
                {
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);
                    ImGui.TextUnformatted(r.Rec.LastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                },
                HeaderPadLeft: 8f),
            new("Character", uid++, ImGuiTableColumnFlags.NoHide, 0,
                r => r.Rec.Name,
                r => DrawCharacterNameCell(r.Rec, lifestreamOn, currentKey)),
            new("World", uid++, ImGuiTableColumnFlags.DefaultSort, 0,
                r => r.Rec.World,
                r => ImGui.TextUnformatted(r.Rec.Slot > 0 ? $"{r.Rec.World}/{r.Rec.Slot}" : r.Rec.World)),
            new("DC", uid++, ImGuiTableColumnFlags.None, 0,
                r => r.Rec.DataCenter,
                r => ImGui.TextUnformatted(r.Rec.DataCenter)),
            new("FC", uid++, ImGuiTableColumnFlags.None, 0,
                r => r.Rec.FreeCompany ?? "",
                r => ImGui.TextUnformatted(r.Rec.FreeCompany ?? "")),
            new("Search Info", uid++, ImGuiTableColumnFlags.None, 0,
                r => r.Rec.SearchInfo ?? "",
                r => ImGui.TextUnformatted(r.Rec.SearchInfo ?? "")),
            new("Private House", uid++, ImGuiTableColumnFlags.None, 0,
                r => r.Rec.PrivateHouse ?? "",
                r => ImGui.TextUnformatted(r.Rec.PrivateHouse ?? "")),
            new("FC House", uid++, ImGuiTableColumnFlags.None, 0,
                r => r.Rec.FcHouse ?? "",
                r => ImGui.TextUnformatted(r.Rec.FcHouse ?? "")),
            new("Gil", uid++, ImGuiTableColumnFlags.None, 0,
                r => r.Rec.Gil,
                r => ImGui.TextUnformatted(r.Rec.Gil < 0 ? "" : r.Rec.Gil.ToString("N0", CultureInfo.InvariantCulture))),
            new("MGP", uid++, ImGuiTableColumnFlags.None, 0,
                r => r.Rec.Mgp,
                r => ImGui.TextUnformatted(r.Rec.Mgp < 0 ? "" : r.Rec.Mgp.ToString("N0", CultureInfo.InvariantCulture))),
        };

        foreach (var (itemId, itemName) in itemCols)
        {
            columns.Add(new TableColumn<CharacterRow>(itemName, uid++, ImGuiTableColumnFlags.None, 0,
                r => r.Items.GetValueOrDefault(itemId),
                r =>
                {
                    if (r.Items.TryGetValue(itemId, out var qty))
                        ImGui.TextUnformatted(qty.ToString("N0", CultureInfo.InvariantCulture));
                    else
                        Common.DimmedText("-");
                }));
        }

        columns.Add(new TableColumn<CharacterRow>("Actions", uid,
            ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoHide | ImGuiTableColumnFlags.NoHeaderLabel, 60f,
            null,
            r =>
            {
                PushButton();
                if (ImGui.SmallButton($"~##{r.Rec.Key}")) onReset(r.Rec.Key);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Reset cached data for this character");
                ImGui.SameLine(0, 2);
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColRed);
                if (ImGui.SmallButton($"X##{r.Rec.Key}")) onDelete(r.Rec.Key);
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Delete this character from the database");
                PopButton();
            }));

        return columns;
    }

    private void DrawCharactersSection()
    {
        if (cachedRecords == null) LoadCharacters();

        bool lifestreamOn = Common.IsPluginLoaded(pluginInterface, "Lifestream");
        string? currentKey = Common.GetCurrentPlayerKey(objectTable);

        BeginSection(
            "Characters",
            "Shows cached info for every character you've logged into, including gil, MGP, houses, and tracked items like FC submarine materials. " +
            "Right-click the header to show/hide columns, drag a header to reorder.");

        DrawTableToolbar(ref charFilter, "##charfilter", LoadCharacters, "Refresh##charrefresh");

        string? pendingReset = null;
        string? pendingDelete = null;

        var columns = BuildCharacterColumns(lifestreamOn, currentKey, key => pendingReset = key, key => pendingDelete = key);

        var filter = charFilter.Trim();
        var worldFilter = WorldResolver.Resolve(filter, (cachedRecords ?? []).Select(r => r.Rec.World)) ?? filter;

        var rows = cachedRecords ?? [];
        ConfigTable.DrawDataTable(
            "##chardb",
            columns,
            ref rows,
            r => r.Rec.Slot == 0 ? int.MaxValue : r.Rec.Slot,
            r => filter.Length == 0
                || r.Rec.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.Rec.World.Contains(worldFilter, StringComparison.OrdinalIgnoreCase)
                || r.Rec.DataCenter.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (r.Rec.FreeCompany ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase));
        cachedRecords = rows;

        if (pendingReset != null) { characterDb.Reset(pendingReset); LoadCharacters(); }
        if (pendingDelete != null) { characterDb.Delete(pendingDelete); LoadCharacters(); }

        EndSection();
    }
}
