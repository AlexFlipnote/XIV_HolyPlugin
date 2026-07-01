using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

using HoliestFluffiness.Handlers;

namespace HoliestFluffiness.Windows;

public partial class ConfigWindow
{
    private ClientTweaksHandler clientTweaksHandler = null!;
    internal void SetClientTweaksHandler(ClientTweaksHandler handler) => clientTweaksHandler = handler;

    private void DrawClientSection()
    {
        BeginSection("Client", "Settings that change client/application behaviour.");

        ConfigCheckbox(
            "Disable idle movie##titlemovie",
            configuration.TitleMovieDisabled,
            v => configuration.TitleMovieDisabled = v,
            "Prevents the intro video from playing on the title screen");

        ImGui.BeginDisabled(!fastMouseClickFixHandler.IsAvailable);
        ConfigCheckbox(
            "Fast mouse click fix##fastmouseclickfix",
            fastMouseClickFixHandler.IsEnabled,
            v => { if (v) fastMouseClickFixHandler.Enable(); else fastMouseClickFixHandler.Disable(); },
            fastMouseClickFixHandler.IsAvailable
                ? "Removes an artificial delay the client imposes between mouse clicks."
                : "Unavailable: the memory signature for this fix could not be found on your current game version.");
        ImGui.EndDisabled();

        SubsectionLabel(
            "Window title",
            "When the game launches, change the window title to something else.");

        ConfigInputText("Title prefix##clientprefix", configuration.ClientTitlePrefix,
            v => configuration.ClientTitlePrefix = v, width: 240,
            hint: "(empty = FINAL FANTASY XIV)", onChange: onClientSettingsChanged);

        ConfigCheckbox(
            "Append name on login##clientappend",
            configuration.ClientAppendNameOnLogin,
            v =>
            {
                configuration.ClientAppendNameOnLogin = v;
                onClientSettingsChanged();
            },
            "Shows \"PREFIX / NAME @ WORLD\" while logged in");

        SubsectionLabel("Flash taskbar on...",
            "Flashes the FFXIV taskbar icon when you are alt-tabbed. Useful if you have " +
            "game sounds disabled while in the background and don't want to miss events.");

        ConfigCheckbox(
            "Incoming tell##clientflashtell",
            configuration.ClientFlashOnTell,
            v => configuration.ClientFlashOnTell = v);

        ConfigCheckbox(
            "Ready check##clientflashready",
            configuration.ClientFlashOnReadyCheck,
            v => configuration.ClientFlashOnReadyCheck = v);

        ConfigCheckbox(
            "Alarm##clientflashalarm",
            configuration.ClientFlashOnAlarm,
            v => configuration.ClientFlashOnAlarm = v);

        ConfigCheckbox(
            "In combat##clientflashcombat",
            configuration.ClientFlashOnCombat,
            v => configuration.ClientFlashOnCombat = v);

        ConfigCheckbox(
            "Synthesis complete##clientflashsynthesis",
            configuration.ClientFlashOnSynthesis,
            v => configuration.ClientFlashOnSynthesis = v);

        SubsectionLabel("No-kill",
            "Intercepts lobby errors and converts them to a reconnect attempt instead of closing the game.");

        ConfigCheckbox(
            "Enable no-kill##nokill",
            configuration.NoKillEnabled,
            v =>
            {
                configuration.NoKillEnabled = v;
                noKillHandler.SetEnabled(v);
            });

        ImGui.BeginDisabled(!configuration.NoKillEnabled);
        ConfigCheckbox(
            "Disable popup on lobby error##nokillpopup",
            configuration.NoKillDisablePopup,
            v => configuration.NoKillDisablePopup = v);
        ImGui.EndDisabled();

        SubsectionLabel("High FPS physics fix");
        ConfigCheckbox(
            "Enable physics fix##physicsenable",
            physicsHandler.IsEnabled,
            v => { if (v) physicsHandler.Enable(); else physicsHandler.Disable(); },
            "Limits physics updates to a target FPS so hair/cloth physics behave correctly at high frame rates.");

        ConfigSliderInt("Physics FPS##physicsfps", (int)configuration.PhysicsTargetFps, 1, 120,
            v => { configuration.PhysicsTargetFps = Math.Clamp(v, 1, 240); physicsHandler.Recalculate(); },
            hint: "(default 60)");

        SubsectionLabel("Anti-AFK");
        ConfigCheckbox(
            "Enable anti-AFK##antiafk",
            configuration.AntiAfkEnabled,
            v =>
            {
                configuration.AntiAfkEnabled = v;
                antiAfkHandler.SetEnabled(v);
            },
            "Periodically presses LCtrl when the AFK timer exceeds the threshold to prevent being kicked.");

        ConfigSliderInt("AFK timer threshold (s)##antiafklimit", configuration.AntiAfkTimerLimit, 5, 60,
            v => configuration.AntiAfkTimerLimit = v,
            hint: "(default 30)");

        EndSection(10);
    }
}
