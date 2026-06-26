using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace HoliestFluffiness.Windows;

public partial class ConfigWindow
{
    private void DrawIndicatorsSection()
    {
        BeginSection("Indicators");

        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Settings for in-game indicators and HUD additions.");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 8));

        // ── Server info ───────────────────────────────────────────────────────
        SubsectionLabel("Server info");
        ImGui.Dummy(new Vector2(0, 2));
        SectionRow();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Adds entries to the server info bar (the row of icons at the top right).");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 4));
        SectionRow();

        PushCheckbox();
        var pingEnabled = configuration.ServerInfoPingEnabled;
        if (ImGui.Checkbox("Show ping##serverinfoping", ref pingEnabled))
        {
            configuration.ServerInfoPingEnabled = pingEnabled;
            configuration.Save();
        }
        PopCheckbox();
        ImGui.SameLine();
        ImGui.BeginDisabled(!pingEnabled);
        ImGui.SetNextItemWidth(150);
        var displayModes = new[] { "Last ping", "Average ping", "Both" };
        var displayIndex = (int)configuration.ServerInfoPingDisplay;
        PushInput();
        if (ImGui.Combo("##serverinfopingdisplay", ref displayIndex, displayModes, displayModes.Length))
        {
            configuration.ServerInfoPingDisplay = (PingDisplay)displayIndex;
            configuration.Save();
        }
        PopInput();
        ImGui.EndDisabled();

        ImGui.Dummy(new Vector2(0, 8));
        SectionRow();

        PushCheckbox();
        var fpsEnabled = configuration.ServerInfoFpsEnabled;
        if (ImGui.Checkbox("Show FPS##serverinfofps", ref fpsEnabled))
        {
            configuration.ServerInfoFpsEnabled = fpsEnabled;
            configuration.Save();
        }
        PopCheckbox();

        ImGui.Dummy(new Vector2(0, 2));
        SectionRow();

        PushCheckbox();
        var dtrEnabled = configuration.NearbyDtrEnabled;
        if (ImGui.Checkbox("Show nearby player count##nearbydtr", ref dtrEnabled))
        {
            configuration.NearbyDtrEnabled = dtrEnabled;
            configuration.Save();
        }
        PopCheckbox();

        ImGui.Dummy(new Vector2(0, 12));

        // ── Repair ────────────────────────────────────────────────────────────
        SubsectionLabel("Repair");
        ImGui.Dummy(new Vector2(0, 2));
        SectionRow();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextWrapped("Adds a debuff icon to Status (Other) when your gear durability drops below a threshold. " +
                          "Critical takes priority over Low, only one icon appears at a time. " +
                          "The main difference between them is the icon displayed and the message shown on hover.");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 8));

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

        ImGui.Dummy(new Vector2(0, 8));

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

        ImGui.Dummy(new Vector2(0, 12));

        // ── Ready check ───────────────────────────────────────────────────────
        SubsectionLabel("Ready Check Helper");
        ImGui.Dummy(new Vector2(0, 4));
        SectionRow();

        PushCheckbox();
        var showNames = configuration.ReadyCheckShowNames;
        if (ImGui.Checkbox("Show names in chat##rcnames", ref showNames))
        {
            configuration.ReadyCheckShowNames = showNames;
            configuration.Save();
        }
        PopCheckbox();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Prints who is not ready after a ready check");
        ImGui.PopStyleColor();

        ImGui.Dummy(new Vector2(0, 4));
        SectionRow();

        PushCheckbox();
        var drawOverlay = configuration.ReadyCheckDrawOverlay;
        if (ImGui.Checkbox("Draw ready check overlay##rcoverlay", ref drawOverlay))
        {
            configuration.ReadyCheckDrawOverlay = drawOverlay;
            configuration.Save();
        }
        PopCheckbox();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Shows ready/not-ready icons on the party list");
        ImGui.PopStyleColor();

        ImGui.Dummy(new Vector2(0, 4));
        ImGui.BeginDisabled(!configuration.ReadyCheckDrawOverlay);
        SectionRow();

        var seconds = configuration.ReadyCheckClearAfterSeconds;
        ImGui.SetNextItemWidth(220);
        PushInput();
        if (ImGui.SliderInt("Clear after (s)##rcclear", ref seconds, 1, 60))
        {
            configuration.ReadyCheckClearAfterSeconds = Math.Clamp(seconds, 1, 60);
            configuration.Save();
        }
        PopInput();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("(default 10)");
        ImGui.PopStyleColor();

        ImGui.Dummy(new Vector2(0, 4));
        SectionRow();
        PushButton();
        if (ImGui.Button("Test overlay##rctest"))
            readyCheckHandler.Simulate();
        PopButton();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Simulates a ready check for 1 second (requires a party)");
        ImGui.PopStyleColor();
        ImGui.EndDisabled();

        EndSection(10);
    }
}
