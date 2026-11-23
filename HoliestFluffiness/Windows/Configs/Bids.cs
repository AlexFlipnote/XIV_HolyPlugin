using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;

namespace HoliestFluffiness.Windows;

public partial class ConfigWindow
{
    private List<(HousingBidRecord Bid, CharacterRecord? Char)>? cachedBids;
    private string bidFilter = "";

    private void LoadBids()
    {
        var allBids  = characterDb.GetAllBids();
        var allChars = characterDb.GetAll();
        cachedBids   = [.. allBids.Select(b => (b, allChars.FirstOrDefault(c => c.Key == b.CharacterKey))).OrderBy(x => x.Item1.BidDate)];
    }

    private List<TableColumn<(HousingBidRecord Bid, CharacterRecord? Char)>> BuildBidColumns(bool lifestreamOn, string? currentKey, Action<int> onDelete)
    {
        return
        [
            new("Bid placed", 0, ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortAscending | ImGuiTableColumnFlags.WidthFixed, 90f,
                t => t.Bid.BidDate,
                t => ImGui.TextUnformatted(t.Bid.BidDate.ToLocalTime().ToString("yyyy-MM-dd"))),
            new("Character", 1, ImGuiTableColumnFlags.NoHide | ImGuiTableColumnFlags.WidthStretch, 0,
                t => t.Char?.Name ?? t.Bid.CharacterKey,
                t =>
                {
                    var (bid, rec) = t;
                    if (rec != null)
                    {
                        bool isCurrent = currentKey != null && rec.Key == currentKey;
                        if (isCurrent)
                        {
                            Common.GreenText(rec.Name);
                        }
                        else if (lifestreamOn)
                        {
                            ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColGold);
                            if (ImGui.Selectable($"{rec.Name}##sel{bid.Id}", false, ImGuiSelectableFlags.None))
                                onGoToBid(rec, bid);
                            ImGui.PopStyleColor();
                            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Switch to {rec.Name} and teleport to {bid.District} W{bid.Ward} P{bid.Plot}");
                        }
                        else
                        {
                            ImGui.TextUnformatted(rec.Name);
                        }
                    }
                    else
                    {
                        Common.DimmedText(bid.CharacterKey);
                    }
                }),
            new("World", 2, ImGuiTableColumnFlags.WidthStretch, 0,
                t => t.Char?.World ?? "",
                t =>
                {
                    if (t.Char == null) return;
                    ImGui.TextUnformatted(t.Char.Slot > 0 ? $"{t.Char.World}/{t.Char.Slot}" : t.Char.World);
                }),
            new("Location", 3, ImGuiTableColumnFlags.WidthStretch, 0,
                t => t.Bid.District,
                t => ImGui.TextUnformatted($"{t.Bid.District} W{t.Bid.Ward} P{t.Bid.Plot}")),
            new("Bid#", 4, ImGuiTableColumnFlags.WidthFixed, 40f,
                t => t.Bid.BidNumber,
                t => ImGui.TextUnformatted(t.Bid.BidNumber.ToString())),
            new("Type", 5, ImGuiTableColumnFlags.WidthFixed, 60f,
                t => t.Bid.BidType,
                t => ImGui.TextUnformatted(t.Bid.BidType == BidType.Private ? "Private" : "FC")),
            new("Actions", 6,
                ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoHide | ImGuiTableColumnFlags.NoHeaderLabel, 26f,
                null,
                t =>
                {
                    PushButton();
                    ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColRed);
                    if (ImGui.SmallButton($"X##{t.Bid.Id}")) onDelete(t.Bid.Id);
                    ImGui.PopStyleColor();
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Delete this bid");
                    PopButton();
                }),
        ];
    }

    private void DrawBidsSection()
    {
        if (cachedBids == null) LoadBids();

        bool lifestreamOn  = Common.IsPluginLoaded(pluginInterface, "Lifestream");
        string? currentKey = Common.GetCurrentPlayerKey(objectTable);

        BeginSection(
            "Bids",
            "Housing lottery bids are tracked automatically when you place or confirm a bid. " +
            "Once the lottery concludes, whether you win and claim the plot, get refunded, or simply lose, the entry is removed automatically, anything still listed here is still an active, unresolved bid.");

        DrawTableToolbar(ref bidFilter, "##bidfilter", LoadBids, "Refresh##bidrefresh");

        int? pendingDelete = null;
        var columns = BuildBidColumns(lifestreamOn, currentKey, id => pendingDelete = id);

        var filter = bidFilter.Trim();
        var rows = cachedBids ?? [];
        ConfigTable.DrawDataTable(
            "##bidtable",
            columns,
            ref rows,
            t => t.Bid.Id,
            t => filter.Length == 0
                || (t.Char?.Name ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (t.Char?.World ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase)
                || t.Bid.District.Contains(filter, StringComparison.OrdinalIgnoreCase));
        cachedBids = rows;

        if (pendingDelete != null) { characterDb.DeleteBid(pendingDelete.Value); LoadBids(); }

        EndSection();
    }
}
