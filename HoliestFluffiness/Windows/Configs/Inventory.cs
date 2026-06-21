using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;

namespace HoliestFluffiness.Windows;

public partial class ConfigWindow
{
    private record InventoryRow(CharacterRecord Rec, Dictionary<uint, int> Items);
    private List<InventoryRow>? cachedInventory;

    private void LoadInventory()
    {
        cachedInventory = [.. characterDb.GetAll()
            .Select(r =>
            {
                var items = r.Inventory != null
                    ? JsonSerializer.Deserialize<Dictionary<uint, int>>(r.Inventory) ?? []
                    : new Dictionary<uint, int>();
                return new InventoryRow(r, items);
            })
            .OrderBy(row => row.Rec.World)];
    }

    private void DrawInventorySection()
    {
        if (cachedInventory == null) LoadInventory();

        bool lifestreamOn  = pluginInterface.InstalledPlugins.Any(p => p.InternalName == "Lifestream" && p.IsLoaded);
        var  localPlayer   = objectTable[0] as IPlayerCharacter;
        string? currentKey = localPlayer != null
            ? $"{localPlayer.Name.TextValue}@{localPlayer.HomeWorld.ValueNullable?.Name.ExtractText()}"
            : null;

        BeginSection("Inventory");

        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextWrapped("Tracks special items in the inventory across all your cached caracters, mainly right now, FC submarine items.");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 4));
        SectionRow();
        PushButton();
        if (ImGui.Button("Refresh##invrefresh")) LoadInventory();
        PopButton();

        ImGui.Dummy(new Vector2(0, 2));

        var itemCols  = LoginInfoHandler.TrackedItems.ToList(); // ordered dict entries
        int colCount  = 2 + itemCols.Count; // Last Updated + Character + World + items
        var tableH    = Math.Max(50f, ImGui.GetContentRegionAvail().Y - 4f);
        var tableFlags = ImGuiTableFlags.Sortable
            | ImGuiTableFlags.ScrollY
            | ImGuiTableFlags.BordersOuter
            | ImGuiTableFlags.BordersInnerV
            | ImGuiTableFlags.RowBg
            | ImGuiTableFlags.SizingStretchProp;

        if (ImGui.BeginTable("##invtable", colCount + 1, tableFlags, new Vector2(0, tableH)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Last Updated", ImGuiTableColumnFlags.PreferSortDescending, 0, 0);
            ImGui.TableSetupColumn("Character",    ImGuiTableColumnFlags.None,                 0, 1);
            ImGui.TableSetupColumn("World",        ImGuiTableColumnFlags.DefaultSort,          0, 2);
            for (int i = 0; i < itemCols.Count; i++)
                ImGui.TableSetupColumn(itemCols[i].Value, ImGuiTableColumnFlags.None, 0, (uint)(3 + i));
            ImGui.TableHeadersRow();

            var sortSpecs = ImGui.TableGetSortSpecs();
            if (sortSpecs.SpecsDirty && sortSpecs.SpecsCount > 0 && cachedInventory != null)
            {
                var spec = sortSpecs.Specs;
                bool desc = spec.SortDirection == ImGuiSortDirection.Descending;
                int uid = (int)spec.ColumnUserID;
                static int SlotKey(InventoryRow r) => r.Rec.Slot == 0 ? int.MaxValue : r.Rec.Slot;
                cachedInventory = uid switch
                {
                    0 => [.. (desc ? cachedInventory.OrderByDescending(r => r.Rec.LastSeen).ThenBy(SlotKey) : cachedInventory.OrderBy(r => r.Rec.LastSeen).ThenBy(SlotKey))],
                    1 => [.. (desc ? cachedInventory.OrderByDescending(r => r.Rec.Name)    .ThenBy(SlotKey) : cachedInventory.OrderBy(r => r.Rec.Name)    .ThenBy(SlotKey))],
                    2 => [.. (desc ? cachedInventory.OrderByDescending(r => r.Rec.World)   .ThenBy(SlotKey) : cachedInventory.OrderBy(r => r.Rec.World)   .ThenBy(SlotKey))],
                    _ when uid >= 3 && uid - 3 < itemCols.Count =>
                    [.. (desc
                        ? cachedInventory.OrderByDescending(r => r.Items.GetValueOrDefault(itemCols[uid - 3].Key)).ThenBy(SlotKey)
                        : cachedInventory.OrderBy(r => r.Items.GetValueOrDefault(itemCols[uid - 3].Key)).ThenBy(SlotKey))],
                    _ => cachedInventory,
                };
                sortSpecs.SpecsDirty = false;
            }

            foreach (var row in cachedInventory ?? [])
            {
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(row.Rec.LastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));

                ImGui.TableSetColumnIndex(1);
                bool isCurrent = currentKey != null && row.Rec.Key == currentKey;
                if (isCurrent)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, ColGreen);
                    ImGui.TextUnformatted(row.Rec.Name);
                    ImGui.PopStyleColor();
                }
                else if (lifestreamOn)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, ColGold);
                    if (ImGui.Selectable($"{row.Rec.Name}##invsel{row.Rec.Key}", false, ImGuiSelectableFlags.None))
                        onSwitchCharacter(row.Rec.Name, row.Rec.World);
                    ImGui.PopStyleColor();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"Click to switch to {row.Rec.Name} on {row.Rec.World}");
                }
                else
                {
                    ImGui.TextUnformatted(row.Rec.Name);
                }

                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(row.Rec.Slot > 0 ? $"{row.Rec.World}/{row.Rec.Slot}" : row.Rec.World);

                for (int i = 0; i < itemCols.Count; i++)
                {
                    ImGui.TableSetColumnIndex(3 + i);
                    if (row.Items.TryGetValue(itemCols[i].Key, out var qty))
                        ImGui.TextUnformatted(qty.ToString("N0", CultureInfo.InvariantCulture));
                    else
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
                        ImGui.TextUnformatted("-");
                        ImGui.PopStyleColor();
                    }
                }
            }

            ImGui.EndTable();
        }

        EndSection();
    }
}
