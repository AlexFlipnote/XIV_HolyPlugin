using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace HoliestFluffiness.Windows;

public partial class ConfigWindow
{
    private void DrawRepairSection()
    {
        BeginSection("Debuff notifier");

        SectionRow();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextWrapped("Adds a debuff icon to Status (Other) when your gear durability drops below a threshold. " +
                          "Critical takes priority over Low, only one icon appears at a time. " +
                          "The main difference between them is the icon displayed and the message shown on hover.");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 8));

        // Low ─────────────────────────────────────────────────────────────────
        SectionRow();
        PushCheckbox();
        var lowEnabled = configuration.RepairLowEnabled;
        if (ImGui.Checkbox("##repairlowcheck", ref lowEnabled))
        {
            configuration.RepairLowEnabled = lowEnabled;
            configuration.Save();
        }
        PopCheckbox();

        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        var lowThreshold = configuration.RepairLowThreshold;
        PushInput();
        if (ImGui.SliderFloat("Low threshold##repairlowthr", ref lowThreshold, 1f, 100f, "%.0f%%"))
        {
            configuration.RepairLowThreshold = lowThreshold;
            configuration.Save();
        }
        PopInput();

        SectionRow();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Shows: \"Gear at X%, consider repairing\"");
        ImGui.PopStyleColor();

        ImGui.Dummy(new Vector2(0, 8));

        // Critical ────────────────────────────────────────────────────────────
        SectionRow();
        PushCheckbox();
        var critEnabled = configuration.RepairCriticalEnabled;
        if (ImGui.Checkbox("##repaircritcheck", ref critEnabled))
        {
            configuration.RepairCriticalEnabled = critEnabled;
            configuration.Save();
        }
        PopCheckbox();

        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        var critThreshold = configuration.RepairCriticalThreshold;
        PushInput();
        if (ImGui.SliderFloat("Critical threshold##repaircritthr", ref critThreshold, 1f, 100f, "%.0f%%"))
        {
            configuration.RepairCriticalThreshold = critThreshold;
            configuration.Save();
        }
        PopInput();

        SectionRow();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Shows: \"Gear really damaged (X%), repair now!!\"");
        ImGui.PopStyleColor();

        ImGui.Dummy(new Vector2(0, 12));

        // Test button ─────────────────────────────────────────────────────────
        SectionRow();
        var testing = repairHandler.TestPct.HasValue;
        PushButton();
        if (ImGui.Button(testing ? "Stop testing##repairtest" : "Test at 69%##repairtest"))
            repairHandler.TestPct = testing ? null : 69f;
        PopButton();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Simulates 69% gear condition to preview the debuff icon.");
        ImGui.PopStyleColor();

        ImGui.Dummy(new Vector2(0, 8));

        EndSection();
    }
}
