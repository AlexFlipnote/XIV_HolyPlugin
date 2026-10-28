using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using HoliestFluffiness.Handlers;

namespace HoliestFluffiness.Windows;

public partial class ConfigWindow
{
    private ClientTweaksHandler clientTweaksHandler = null!;
    internal void SetClientTweaksHandler(ClientTweaksHandler handler) => clientTweaksHandler = handler;

    private MediaHandler? mediaHandler;
    internal void SetMediaHandler(MediaHandler handler) => mediaHandler = handler;

    private void DrawClientSection()
    {
        BeginSection("Client", "Settings that change client/application behaviour.");

        ConfigCheckbox(
            "Disable idle movie##titlemovie",
            configuration.TitleMovieDisabled,
            v => configuration.TitleMovieDisabled = v,
            "Prevents the intro video from playing on the title screen");

        ConfigCheckbox(
            "Always yes##alwaysyes",
            configuration.AlwaysYesEnabled,
            v => configuration.AlwaysYesEnabled = v,
            "Defaults confirmation dialogs to the yes button, so pressing confirm button, accepts right away.");

        ConfigCheckbox(
            "Alt + F4 exit game##altf4exit",
            configuration.AltF4ExitEnabled,
            v => configuration.AltF4ExitEnabled = v,
            "Pressing Alt + F4 closes the game safely through its normal shutdown.");

        ImGui.BeginDisabled(!fastMouseClickFixHandler.IsAvailable);
        ConfigCheckbox(
            "Fast mouse click fix##fastmouseclickfix",
            fastMouseClickFixHandler.IsEnabled,
            v => { if (v) fastMouseClickFixHandler.Enable(); else fastMouseClickFixHandler.Disable(); },
            fastMouseClickFixHandler.IsAvailable
                ? "Removes an artificial delay the client imposes between mouse clicks."
                : "Unavailable: the memory signature for this fix could not be found on your current game version.");
        ImGui.EndDisabled();

        ConfigCheckbox(
            "Better draw/sheathe##drawsheatheemote",
            configuration.DrawSheatheEmoteEnabled,
            v => configuration.DrawSheatheEmoteEnabled = v,
            "Replaces your draw/sheathe weapon keybind with the /draw and /sheathe emotes when possible.");

        ConfigCheckbox(
            "Show congestion queue timer##queuetimer",
            configuration.QueueTimerEnabled,
            v => configuration.QueueTimerEnabled = v,
            "Adds a countdown to the \"server is currently congested\" dialog. The refresh rate is measured " +
            "while you wait, so it shows time since the queue started or last updated until it is known.");

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
            "Countdown started##clientflashcountdown",
            configuration.ClientFlashOnCountdown,
            v => configuration.ClientFlashOnCountdown = v);

        ConfigCheckbox(
            "Alarm##clientflashalarm",
            configuration.ClientFlashOnAlarm,
            v => configuration.ClientFlashOnAlarm = v);

        ConfigCheckbox(
            "In combat##clientflashcombat",
            configuration.ClientFlashOnCombat,
            v => configuration.ClientFlashOnCombat = v);

        SubsectionLabel("Media controls", "Control what is playing on your PC, like Spotify or a browser.");

        ConfigCheckbox(
            "Enable media controls##mediaenabled",
            configuration.MediaEnabled,
            v => configuration.MediaEnabled = v,
            "Adds /media for a small player window. /media next, prev, toggle and now also work in macros.");

        if (configuration.MediaEnabled && mediaHandler is { IsAvailable: false })
            Common.DimmedTextWrapped("Windows media sessions are not available here (for example under Wine).");

        ImGui.BeginDisabled(!configuration.MediaEnabled);
        ConfigCheckbox(
            "Show song on the experience bar##mediaexpbar",
            configuration.MediaExpBarEnabled,
            v => configuration.MediaExpBarEnabled = v,
            "The bar shows song progress and title while something plays.");

        ImGui.BeginDisabled(!configuration.MediaExpBarEnabled);
        var textModes = new[] { "Replace chain EXP", "Replace job and EXP" };
        ConfigCombo("Song text##mediaexpbartext", (int)configuration.MediaExpBarText, textModes,
            v => configuration.MediaExpBarText = (MediaBarText)v, width: 180,
            desc: "Where the song text goes on the experience bar.");
        ImGui.EndDisabled();

        ImGui.Dummy(new Vector2(0, 4));

        ConfigCheckbox(
            "Show song in the server info bar##mediadtr",
            configuration.MediaDtrEnabled,
            v => configuration.MediaDtrEnabled = v,
            "Next to ping and FPS. Click it to open the player.");

        ImGui.BeginDisabled(!configuration.MediaDtrEnabled);
        ConfigInputText("Server info text##mediadtrformat", configuration.MediaDtrFormat,
            v => configuration.MediaDtrFormat = v, width: 240,
            hint: $"(empty = {Configuration.DefaultMediaDtrFormat})",
            desc: "Format of the server info entry, using <song>, <artist>, <app> and <time>.");
        RowGap(0);

        var tagsHelper = "Tags: <song> | <artist> | <app> | <time>";
        if (mediaHandler?.Current is { Title.Length: > 0 } song)
        {
            Common.DimmedText($"{tagsHelper}\nPreview: {MediaDtrHandler.Format(configuration.MediaDtrFormat, song)}");
        }
        else {
            Common.DimmedText($"{tagsHelper}");
        }

        ImGui.EndDisabled();
        ImGui.EndDisabled();

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

        ConfigCheckbox(
            "Respect manual /afk##antiafkrespect",
            configuration.AntiAfkRespectManualAfk,
            v => configuration.AntiAfkRespectManualAfk = v,
            "Pauses anti-AFK while you are marked away with the /afk command, so you can go AFK on purpose.");

        ConfigSliderInt("AFK timer threshold (s)##antiafklimit", configuration.AntiAfkTimerLimit, 5, 60,
            v => configuration.AntiAfkTimerLimit = v,
            hint: "(default 30)");

        EndSection(10);
    }
}
