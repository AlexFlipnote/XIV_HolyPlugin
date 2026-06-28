using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace HoliestFluffiness.Windows;

public partial class ConfigWindow
{
    private List<(HousingBidRecord Bid, CharacterRecord? Char)>? cachedBids;

    private static readonly string[] Districts = ["Mist", "Lavender Beds", "The Goblet", "Shirogane", "Empyreum"];

    private void LoadBids()
    {
        var allBids  = characterDb.GetAllBids();
        var allChars = characterDb.GetAll();
        cachedBids   = [.. allBids.Select(b => (b, allChars.FirstOrDefault(c => c.Key == b.CharacterKey))).OrderBy(x => x.Item1.BidDate)];
    }

    private void DrawBidsSection()
    {
        if (cachedBids == null) LoadBids();

        bool lifestreamOn  = Common.IsPluginLoaded(pluginInterface, "Lifestream");
        string? currentKey = Common.GetCurrentPlayerKey(objectTable);

        BeginSection("Bids", "Housing lottery bids are tracked automatically when you place or confirm a bid.");

        SectionRow();
        PushButton();
        if (ImGui.Button("Refresh##bidrefresh")) LoadBids();
        PopButton();

        ImGui.Dummy(new Vector2(0, 2));

        var tableH     = Math.Max(50f, ImGui.GetContentRegionAvail().Y - 4f);
        var tableFlags = ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerV
                       | ImGuiTableFlags.RowBg   | ImGuiTableFlags.SizingStretchProp;

        if (ImGui.BeginTable("##bidtable", 6, tableFlags, new Vector2(0, tableH)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Location",  ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Bid#",      ImGuiTableColumnFlags.WidthFixed, 40f);
            ImGui.TableSetupColumn("Type",      ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn("Date",      ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("##bidacts", ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.WidthFixed, 26f);
            ImGui.TableHeadersRow();

            int? pendingDelete = null;
            foreach (var (bid, rec) in cachedBids ?? [])
            {
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                if (rec != null)
                {
                    bool isCurrent = currentKey != null && rec.Key == currentKey;
                    if (isCurrent)
                    {
                        Common.GreenText($"{rec.Name} @ {rec.World}");
                    }
                    else if (lifestreamOn)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColGold);
                        if (ImGui.Selectable($"{rec.Name} @ {rec.World}##sel{bid.Id}", false, ImGuiSelectableFlags.None))
                            onGoToBid(rec, bid);
                        ImGui.PopStyleColor();
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Switch to {rec.Name} and teleport to {bid.District} W{bid.Ward} P{bid.Plot}");
                    }
                    else
                    {
                        ImGui.TextUnformatted($"{rec.Name} @ {rec.World}");
                    }
                }
                else
                {
                    Common.DimmedText(bid.CharacterKey);
                }

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted($"{bid.District} W{bid.Ward} P{bid.Plot}");

                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(bid.BidNumber.ToString());

                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(bid.BidType == BidType.Private ? "Private" : "FC");

                ImGui.TableSetColumnIndex(4);
                ImGui.TextUnformatted(bid.BidDate.ToLocalTime().ToString("yyyy-MM-dd"));

                ImGui.TableSetColumnIndex(5);
                PushButton();
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColRed);
                if (ImGui.SmallButton($"X##{bid.Id}")) pendingDelete = bid.Id;
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Delete this bid");
                PopButton();
            }

            ImGui.EndTable();

            if (pendingDelete != null) { characterDb.DeleteBid(pendingDelete.Value); LoadBids(); }
        }

        EndSection();
    }
}
