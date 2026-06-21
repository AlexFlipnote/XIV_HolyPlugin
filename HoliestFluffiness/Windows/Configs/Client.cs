using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace HoliestFluffiness.Windows;

public partial class ConfigWindow
{
    private void DrawClientSection()
    {
        BeginSection("Client");

        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Settings that change client/application behaviour.");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 8));

        SubsectionLabel("Window title");
        ImGui.Dummy(new Vector2(0, 4));
        SectionRow();

        var prefix = configuration.ClientTitlePrefix;
        ImGui.SetNextItemWidth(240);
        PushInput();
        if (ImGui.InputText("Title prefix##clientprefix", ref prefix, 128))
        {
            configuration.ClientTitlePrefix = prefix;
            configuration.Save();
            onClientSettingsChanged();
        }
        PopInput();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("(empty = FINAL FANTASY XIV)");
        ImGui.PopStyleColor();

        ImGui.Dummy(new Vector2(0, 4));
        SectionRow();

        PushCheckbox();
        var appendName = configuration.ClientAppendNameOnLogin;
        if (ImGui.Checkbox("Append name on login##clientappend", ref appendName))
        {
            configuration.ClientAppendNameOnLogin = appendName;
            configuration.Save();
            onClientSettingsChanged();
        }
        PopCheckbox();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Shows \"PREFIX / NAME @ WORLD\" while logged in");
        ImGui.PopStyleColor();

        ImGui.Dummy(new Vector2(0, 8));
        SubsectionLabel("Flash taskbar on...");
        ImGui.Dummy(new Vector2(0, 2));
        SectionRow();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextWrapped(
            "Flashes the FFXIV taskbar icon when you are alt-tabbed. Useful if you have " +
            "game sounds disabled while in the background and don't want to miss events."
        );
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 4));
        SectionRow();

        PushCheckbox();
        var flashTell = configuration.ClientFlashOnTell;
        if (ImGui.Checkbox("Incoming tell##clientflashtell", ref flashTell))
        {
            configuration.ClientFlashOnTell = flashTell;
            configuration.Save();
        }
        PopCheckbox();

        ImGui.SameLine();

        PushCheckbox();
        var flashReady = configuration.ClientFlashOnReadyCheck;
        if (ImGui.Checkbox("Ready check##clientflashready", ref flashReady))
        {
            configuration.ClientFlashOnReadyCheck = flashReady;
            configuration.Save();
        }
        PopCheckbox();

        ImGui.Dummy(new Vector2(0, 8));
        SubsectionLabel("No-kill");
        ImGui.Dummy(new Vector2(0, 2));
        SectionRow();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextWrapped("Intercepts lobby errors and converts them to a reconnect attempt instead of closing the game.");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 4));
        SectionRow();

        PushCheckbox();
        var noKillEnabled = configuration.NoKillEnabled;
        if (ImGui.Checkbox("Enable no-kill##nokill", ref noKillEnabled))
        {
            configuration.NoKillEnabled = noKillEnabled;
            configuration.Save();
            noKillHandler.SetEnabled(noKillEnabled);
        }
        PopCheckbox();

        ImGui.Dummy(new Vector2(0, 2));

        ImGui.BeginDisabled(!configuration.NoKillEnabled);
        SectionRow();
        PushCheckbox();
        var disablePopup = configuration.NoKillDisablePopup;
        if (ImGui.Checkbox("Disable popup on lobby error##nokillpopup", ref disablePopup))
        {
            configuration.NoKillDisablePopup = disablePopup;
            configuration.Save();
        }
        PopCheckbox();
        ImGui.EndDisabled();

        ImGui.Dummy(new Vector2(0, 8));
        SubsectionLabel("High FPS physics fix");
        ImGui.Dummy(new Vector2(0, 2));
        SectionRow();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextWrapped("Limits physics updates to a target FPS so hair/cloth physics behave correctly at high frame rates.");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 4));
        SectionRow();

        PushCheckbox();
        var physicsEnabled = physicsHandler.IsEnabled;
        if (ImGui.Checkbox("Enable physics fix##physicsenable", ref physicsEnabled))
        {
            if (physicsEnabled) physicsHandler.Enable();
            else                physicsHandler.Disable();
        }
        PopCheckbox();

        ImGui.Dummy(new Vector2(0, 4));
        SectionRow();

        var targetFps = (int)configuration.PhysicsTargetFps;
        ImGui.SetNextItemWidth(220);
        PushInput();
        if (ImGui.SliderInt("Physics FPS##physicsfps", ref targetFps, 1, 120))
        {
            configuration.PhysicsTargetFps = Math.Clamp(targetFps, 1, 240);
            configuration.Save();
            physicsHandler.Recalculate();
        }
        PopInput();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("(default 60)");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 4));

        EndSection();
    }
}
