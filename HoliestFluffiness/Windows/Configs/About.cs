using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace HoliestFluffiness.Windows;

public partial class ConfigWindow
{
    private void DrawAboutSection()
    {
        BeginSection("About");

        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("A custom plugin made mostly for our FC, but shared to others too.");
        ImGui.PopStyleColor();

        ImGui.Dummy(new Vector2(0, 8));
        SectionRow();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.PushTextWrapPos(ImGui.GetContentRegionMax().X - 8f);
        ImGui.TextUnformatted(
            "The plugin is named after our Free Company, when no existing plugin did exactly what we needed, " +
            "building our own felt like the natural next step, so we named it after home. " +
            "The gold-and-dark palette is pulled straight from our FC colours, because the plugin is part of the " +
            "experience and should look the part. As for why it exists: we have too many alts and other plugins " +
            "couldn't keep up with how we play, so we took matters into our own hands.");
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();

        ImGui.Dummy(new Vector2(0, 10));
        SubsectionLabel("Optional 3rd party plugins");
        SectionRow();
        bool lifestreamOn = pluginInterface.InstalledPlugins.Any(p => p.InternalName == "Lifestream" && p.IsLoaded);
        ImGui.PushStyleColor(ImGuiCol.Text, lifestreamOn ? ColGreen : ColRed);
        ImGui.TextUnformatted("Lifestream");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted(lifestreamOn ? "Enables switching to characters and travelling to housing plots directly from this plugin." : "Install Lifestream to enable character switching and housing plot travel.");
        ImGui.PopStyleColor();

        ImGui.Dummy(new Vector2(0, 10));
        SubsectionLabel("Developer");
        SectionRow();
        ImGui.TextUnformatted("AlexFlipnote");
        ImGui.SameLine();
        PushButton();
        if (ImGui.Button("GitHub##about"))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/AlexFlipnote/XIV_HolyPlugin") { UseShellExecute = true });
        PopButton();

        EndSection();
    }
}
