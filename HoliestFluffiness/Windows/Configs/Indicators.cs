using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace HoliestFluffiness.Windows;

public partial class ConfigWindow
{
    private void DrawIndicatorsSection()
    {
        BeginSection("Indicators", "Settings for in-game indicators and HUD additions.");

        // ── Server info ───────────────────────────────────────────────────────
        SubsectionLabel("Server info", "Adds entries to the server info bar (the row of icons at the top right).");

        ConfigCheckbox(
            "Show FPS##serverinfofps",
            configuration.ServerInfoFpsEnabled,
            v => configuration.ServerInfoFpsEnabled = v);

        ConfigCheckbox(
            "Show nearby player count##nearbydtr",
            configuration.NearbyDtrEnabled,
            v => configuration.NearbyDtrEnabled = v);

        ConfigCheckbox(
            "Show ping##serverinfoping",
            configuration.ServerInfoPingEnabled,
            v => configuration.ServerInfoPingEnabled = v);
        ImGui.SameLine();
        ImGui.BeginDisabled(!configuration.ServerInfoPingEnabled);
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

        ImGui.SetNextItemWidth(150);
        var pingScaleMax = configuration.PingChartScaleMax;
        SectionRow();
        PushInput();
        if (ImGui.InputInt("Ping chart Y-axis max (ms, 0 = auto)##pingscale", ref pingScaleMax, 10, 50))
        {
            configuration.PingChartScaleMax = Math.Max(0, pingScaleMax);
            configuration.Save();
        }
        PopInput();

        // ── Repair ────────────────────────────────────────────────────────────
        SubsectionLabel("Repair",
            "Adds a debuff icon to Status (Other) when your gear durability drops below a threshold. " +
            "Critical takes priority over Low, only one icon appears at a time.");

        ConfigCheckbox(
            "##repairlowcheck",
            configuration.RepairLowEnabled,
            v => configuration.RepairLowEnabled = v);

        ImGui.SameLine();
        ConfigSliderFloat("Low threshold##repairlowthr", configuration.RepairLowThreshold, 1f, 100f,
            v => configuration.RepairLowThreshold = v, width: 200, format: "%.0f%%");

        SectionRow();
        Common.DimmedText("Shows: \"Gear at X%, consider repairing\"");

        ImGui.Dummy(new Vector2(0, 4));

        ConfigCheckbox(
            "##repaircritcheck",
            configuration.RepairCriticalEnabled,
            v => configuration.RepairCriticalEnabled = v);

        ImGui.SameLine();
        ConfigSliderFloat("Critical threshold##repaircritthr", configuration.RepairCriticalThreshold, 1f, 100f,
            v => configuration.RepairCriticalThreshold = v, width: 200, format: "%.0f%%");

        SectionRow();
        Common.DimmedText("Shows: \"Gear really damaged (X%), repair now!!\"");

        ImGui.Dummy(new Vector2(0, 8));

        SectionRow();
        var testing = repairHandler.TestPct.HasValue;
        PushButton();
        if (ImGui.Button(testing ? "Stop testing##repairtest" : "Test at 69%##repairtest"))
            repairHandler.TestPct = testing ? null : 69f;
        PopButton();
        ImGui.SameLine();
        Common.DimmedText("Simulates 69% gear condition to preview the debuff icon.");

        // ── Ready check ───────────────────────────────────────────────────────
        SubsectionLabel("Ready Check Helper");

        ConfigCheckbox(
            "Show names in chat##rcnames",
            configuration.ReadyCheckShowNames,
            v => configuration.ReadyCheckShowNames = v,
            "Prints who is not ready after a ready check");

        ConfigCheckbox(
            "Draw ready check overlay##rcoverlay",
            configuration.ReadyCheckDrawOverlay,
            v => configuration.ReadyCheckDrawOverlay = v,
            "Shows ready/not-ready icons on the party list");

        ImGui.Dummy(new Vector2(0, 4));
        ImGui.BeginDisabled(!configuration.ReadyCheckDrawOverlay);

        ConfigSliderInt("Clear after (s)##rcclear", configuration.ReadyCheckClearAfterSeconds, 1, 60,
            v => configuration.ReadyCheckClearAfterSeconds = v,
            hint: "(default 10)");

        RowGap();
        PushButton();
        if (ImGui.Button("Test overlay##rctest"))
            readyCheckHandler.Simulate();
        PopButton();
        ImGui.SameLine();
        Common.DimmedText("Simulates a ready check for 1 second (requires a party)");
        ImGui.EndDisabled();

        // ── Cast bar aetheryte names ──────────────────────────────────────────
        SubsectionLabel("Cast bar");

        ConfigCheckbox(
            "Enable cast bar aetheryte names##castbaraetheryte",
            configuration.CastBarAetheryteEnabled,
            v => configuration.CastBarAetheryteEnabled = v,
            "Replaces the generic cast bar text with the actual aetheryte name when using Teleport.");

        // ── Duty timer ────────────────────────────────────────────────────────
        SubsectionLabel("Duty queue timer");

        ConfigCheckbox(
            "Show duty queue timer##dutytimer",
            configuration.DutyTimerEnabled,
            v => configuration.DutyTimerEnabled = v,
            "Shows estimated remaining queue time in the duty ready check dialog, based on the wait time shown when your duty was found.");

        EndSection(10);
    }
}
