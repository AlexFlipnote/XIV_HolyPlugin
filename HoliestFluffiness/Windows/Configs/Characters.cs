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

    private string FormatTableNum(long n) => configuration.CharactersDbShortenNumbers
        ? Common.ShortenNumber(n)
        : n.ToString("N0", CultureInfo.InvariantCulture);

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
        uint uid = 0;

        var columns = new List<TableColumn<CharacterRow>>
        {
            new("Last Seen", uid++, ImGuiTableColumnFlags.PreferSortDescending, 0,
                r => r.Rec.LastSeen,
                r => ImGui.TextUnformatted(r.Rec.LastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))),
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
                r =>
                {
                    var fc = r.Rec.FreeCompany ?? "";
                    if (fc.Length > 0 && r.Rec.FcLeader)
                    {
                        Common.GreenText(fc);
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Leader of this Free Company");
                    }
                    else
                    {
                        ImGui.TextUnformatted(fc);
                    }
                }),
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
                r => ImGui.TextUnformatted(r.Rec.Gil < 0 ? "" : FormatTableNum(r.Rec.Gil))),
            new("MGP", uid++, ImGuiTableColumnFlags.None, 0,
                r => r.Rec.Mgp,
                r => ImGui.TextUnformatted(r.Rec.Mgp < 0 ? "" : FormatTableNum(r.Rec.Mgp))),
            new("FC Points", uid++, ImGuiTableColumnFlags.None, 0,
                r => r.Rec.FcPoints,
                r => ImGui.TextUnformatted(r.Rec.FcPoints < 0 ? "" : FormatTableNum(r.Rec.FcPoints))),
        };

        foreach (var (itemId, itemName) in LoginInfoHandler.TrackedItems)
        {
            columns.Add(new TableColumn<CharacterRow>(itemName, uid++, ImGuiTableColumnFlags.None, 0,
                r => r.Items.GetValueOrDefault(itemId),
                r =>
                {
                    if (r.Items.TryGetValue(itemId, out var qty))
                        ImGui.TextUnformatted(FormatTableNum(qty));
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
            r =>
            {
                if (filter.Length == 0) return true;
                bool Has(string? s) => s != null && s.Contains(filter, StringComparison.OrdinalIgnoreCase);
                return Has(r.Rec.Name)
                    || r.Rec.World.Contains(worldFilter, StringComparison.OrdinalIgnoreCase)
                    || Has(r.Rec.DataCenter)
                    || Has(r.Rec.FreeCompany)
                    || Has(r.Rec.SearchInfo)
                    || Has(r.Rec.PrivateHouse)
                    || Has(r.Rec.FcHouse);
            });
        cachedRecords = rows;

        if (pendingReset != null) { characterDb.Reset(pendingReset); LoadCharacters(); }
        if (pendingDelete != null) { characterDb.Delete(pendingDelete); LoadCharacters(); }

        EndSection();
    }
}
