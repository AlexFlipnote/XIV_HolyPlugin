using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace HoliestFluffiness.Windows;

public partial class ConfigWindow
{
    private void DrawServerInfoSection()
    {
        BeginSection("Server info");

        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Adds entries to the server info bar (the row of icons at the top right).");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 8));

        SubsectionLabel("Ping");
        ImGui.Dummy(new Vector2(0, 2));
        SectionRow();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Pings the FFXIV lobby server for your data centre every 3 seconds.");
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

        if (pingEnabled)
        {
            ImGui.Dummy(new Vector2(0, 4));
            SectionRow();
            ImGui.SetNextItemWidth(200);
            var displayModes = new[] { "Last ping", "Average ping", "Both" };
            var displayIndex = (int)configuration.ServerInfoPingDisplay;
            PushInput();
            if (ImGui.Combo("Display##serverinfopingdisplay", ref displayIndex, displayModes, displayModes.Length))
            {
                configuration.ServerInfoPingDisplay = (PingDisplay)displayIndex;
                configuration.Save();
            }
            PopInput();
        }

        ImGui.Dummy(new Vector2(0, 8));
        SubsectionLabel("FPS");
        ImGui.Dummy(new Vector2(0, 2));
        SectionRow();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Shows your current in-game FPS in the server info bar.");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 4));
        SectionRow();

        PushCheckbox();
        var fpsEnabled = configuration.ServerInfoFpsEnabled;
        if (ImGui.Checkbox("Show FPS##serverinfofps", ref fpsEnabled))
        {
            configuration.ServerInfoFpsEnabled = fpsEnabled;
            configuration.Save();
        }
        PopCheckbox();

        EndSection();
    }
}
