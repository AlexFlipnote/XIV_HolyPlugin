using Dalamud.Bindings.ImGui;

namespace HoliestFluffiness.Windows;

public partial class ConfigWindow
{
    private void DrawAboutSection()
    {
        BeginSection("About", "A custom plugin made mostly for our FC, but shared to others too.");
        SectionRow();
        Common.DimmedTextWrapped(
            "The plugin is named after our Free Company, when no existing plugin did exactly what we needed, " +
            "building our own felt like the natural next step, so we named it after home. " +
            "The gold-and-dark palette is pulled straight from our FC colours, because the plugin is part of the " +
            "experience and should look the part. As for why it exists: we have too many alts and other plugins " +
            "couldn't keep up with how we play, so we took matters into our own hands.");
        SectionRow();
        Common.DimmedTextWrapped(
            "A lot of what's bundled here resembles other standalone plugins you may already know. Rather than " +
            "installing a dozen small plugins that each do one simple job, we folded them into one, cutting down " +
            "on boilerplate, reducing RAM and CPU usage, and keeping the overall code quality higher for better " +
            "performance across the board.");

        SubsectionLabel("Optional 3rd party plugins");
        bool lifestreamOn = Common.IsPluginLoaded(pluginInterface, "Lifestream");
        SectionRow();
        ImGui.PushStyleColor(ImGuiCol.Text, lifestreamOn ? Theme.ColGreen : Theme.ColRed);
        ImGui.TextUnformatted("Lifestream");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        Common.DimmedTextWrapped(lifestreamOn ? "Enables switching to characters and travelling to housing plots directly from this plugin." : "Install Lifestream to enable character switching and housing plot travel.");

        SubsectionLabel("Developer");
        SectionRow();
        ImGui.TextUnformatted("AlexFlipnote");
        ImGui.SameLine();
        PushButton();
        if (ImGui.Button("GitHub##about"))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/AlexFlipnote/XIV_HolyPlugin") { UseShellExecute = true });
        PopButton();

        SubsectionLabel("Make it yours",
            "You read all the way down here, so here's your reward: a couple of knobs to make the plugin look how you like.");

        ConfigCheckbox(
            "Disable custom theme##themecustom",
            configuration.ThemeDisableCustom,
            v => configuration.ThemeDisableCustom = v,
            "Off keeps our gold-and-dark FC palette. Turn it on to fall back to your default Dalamud theme.");

        ConfigSliderFloat("Window opacity##themeopacity", configuration.ThemeOpacity, 0.30f, 1.00f,
            v => configuration.ThemeOpacity = v,
            format: "%.2f", hint: "(1.00 = solid)",
            desc: "Fades the window backgrounds so you can see through them. Text and highlights stay solid.");

        EndSection(10);
    }
}
