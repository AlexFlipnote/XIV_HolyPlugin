using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace HoliestFluffiness.Windows;

public partial class ConfigWindow
{
    // Previews a doorbell event (0=enter, 1=already-here, 2=leave); wired to Plugin.TestDoorbell.
    private Action<int>? onTestDoorbell;
    internal void SetDoorbellTest(Action<int> test) => onTestDoorbell = test;

    private void DrawSocialSection()
    {
        BeginSection("Social", "Nearby players, targeting tracker, house doorbell, commendation sounds, and nameplate tweaks.");

        ConfigCheckbox(
            "Replace Wanderer / Traveller with home world##dynamictraveler",
            configuration.DynamicTravelerEnabled,
            v => configuration.DynamicTravelerEnabled = v,
            "Replaces the Wanderer / Traveller FC tags on cross-world nameplates with the player's home world.");

        // ── Nearby players ────────────────────────────────────────────────────
        SubsectionLabel("Nearby players",
            "A window listing every player around you. Open or close it with the /nearby command.");

        ConfigCheckbox(
            "Hide while in combat##nearbyhidecombat",
            configuration.NearbyHideInCombat,
            v => configuration.NearbyHideInCombat = v,
            "Hides the window automatically whenever you are in combat, and shows it again once combat ends.");

        ConfigCheckbox(
            "Hide while in duty##nearbyhideduty",
            configuration.NearbyHideInDuty,
            v => configuration.NearbyHideInDuty = v,
            "Hides the window while you are inside an instanced duty (dungeon, trial, raid, etc.).");

        ConfigCheckbox(
            "Filter AFK players##nearbyafk",
            configuration.NearbyFilterAfk,
            v => configuration.NearbyFilterAfk = v,
            "Leaves players flagged as Away from Keyboard out of the list.");

        ConfigCheckbox(
            "Filter low-level players (≤ 3)##nearbylowlevel",
            configuration.NearbyFilterLowLevel,
            v => configuration.NearbyFilterLowLevel = v,
            "Hides level 3 and below characters, usually brand new alts or throwaway characters.");

        ConfigCheckbox(
            "Colour job names by role##nearbycolorjobs",
            configuration.NearbyColorJobs,
            v => configuration.NearbyColorJobs = v,
            "Colours the job column by role: tanks blue, healers green, DPS red, everything else grey.");

        ConfigCheckbox(
            "Debug: add yourself to nearby list##nearbydebugself",
            configuration.NearbyDebugSelf,
            v => configuration.NearbyDebugSelf = v);
        ImGui.SameLine(0, 12);
        ImGui.BeginDisabled(!configuration.NearbyDebugSelf);
        var debugSelfAsModes = new[] { "as Normal", "as Friend", "as FC member", "as Party member", "as Targeting you" };
        ConfigCombo("##nearbydebugselfa", configuration.NearbyDebugSelfAs, debugSelfAsModes,
            v => configuration.NearbyDebugSelfAs = v, width: 140, padding: false,
            title: "Nearby: debug self display mode");
        ImGui.EndDisabled();

        // ── Colours ───────────────────────────────────────────────────────────
        RowGap(2);
        Common.DimmedTextWrapped("Colours of names inside if the character is...");
        SectionRow();
        ConfigColorEdit4("Party##nearbycolparty", configuration.NearbyColParty, v => configuration.NearbyColParty = v);
        ImGui.SameLine(0, 20);
        ConfigColorEdit4("Friend##nearbycolorfriend", configuration.NearbyColFriend, v => configuration.NearbyColFriend = v);
        ImGui.SameLine(0, 20);
        ConfigColorEdit4("Same FC##nearbycolorfc", configuration.NearbyColLocalFc, v => configuration.NearbyColLocalFc = v);

        RowGap();
        PushButton();
        if (ImGui.Button("Set to default##nearbycoldefault"))
        {
            configuration.NearbyColParty   = Configuration.DefaultNearbyColParty;
            configuration.NearbyColFriend  = Configuration.DefaultNearbyColFriend;
            configuration.NearbyColLocalFc = Configuration.DefaultNearbyColLocalFc;
            configuration.Save();
        }
        PopButton();

        // ── Targeting you ─────────────────────────────────────────────────────
        SubsectionLabel("Targeting you");
        ConfigCheckbox(
            "Track who's targeting you##nearbytargeters",
            configuration.NearbyShowTargeters,
            v => configuration.NearbyShowTargeters = v,
            "Adds a Target History panel to the window listing players who currently have you targeted, plus a log of who did recently.");

        ImGui.BeginDisabled(!configuration.NearbyShowTargeters);

        ConfigCheckbox(
            "Debug: Track yourself##nearbytracksself",
            configuration.NearbyTargeterTrackSelf,
            v => configuration.NearbyTargeterTrackSelf = v,
            "Counts targeting your own character (via focus target on yourself) as a targeter, for previewing the panel.");

        ConfigCheckbox(
            "Mark targeting you in-world##nearbymarktargeting",
            configuration.NearbyMarkTargeting,
            v => configuration.NearbyMarkTargeting = v,
            "Draws a coloured dot over anyone currently targeting you, so you can spot them in the world.");

        ImGui.BeginDisabled(!configuration.NearbyMarkTargeting);
        SectionRow();
        ConfigColorEdit4("##nearbymarkcol", configuration.NearbyMarkTargetingColour, v => configuration.NearbyMarkTargetingColour = v,
            title: "Targeting mark colour");
        ImGui.SameLine();
        ConfigSliderInt("Mark size##nearbymarksize", configuration.NearbyMarkTargetingSize, 1, 20,
            v => configuration.NearbyMarkTargetingSize = v, width: 200, padding: false);
        ImGui.EndDisabled();

        // ── Sound ─────────────────────────────────────────────────────────────

        ConfigCheckbox(
            "Play sound when someone targets you##nearbysound",
            configuration.NearbyTargeterSound,
            v => configuration.NearbyTargeterSound = v,
            "Plays the sound below each time a new player starts targeting you.");

        ImGui.Dummy(new Vector2(0, 2));
        ImGui.BeginDisabled(!configuration.NearbyTargeterSound);
        DrawSoundPicker(
            "nearbytargeter", "Targeting sound",
            Path.Combine(pluginInterface.AssemblyLocation.DirectoryName!, "Sounds", "Targeting", "looking.mp3"),
            configuration.NearbyTargeterSoundPath,
            configuration.NearbyTargeterSoundVolume,
            p => { configuration.NearbyTargeterSoundPath   = p; configuration.Save(); },
            v => configuration.NearbyTargeterSoundVolume = v);
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        // ── House doorbell ────────────────────────────────────────────────────

        SubsectionLabel("House doorbell",
            "Alerts when players enter or leave a house, or are already present when you arrive.");

        var doorbellDir = Path.Combine(pluginInterface.AssemblyLocation.DirectoryName!, "Sounds", "Doorbell");

        DrawDoorbellBlock(
            "doorbellenter", "Someone entered", Path.Combine(doorbellDir, "doorbell.mp3"), () => onTestDoorbell?.Invoke(0),
            configuration.DoorbellEnterChat,    v => configuration.DoorbellEnterChat = v,
            configuration.DoorbellEnterText,    v => configuration.DoorbellEnterText = v, Configuration.DefaultDoorbellEnterText,
            configuration.DoorbellEnterSound,   v => configuration.DoorbellEnterSound = v,
            configuration.DoorbellEnterSoundPath,   p => { configuration.DoorbellEnterSoundPath   = p; configuration.Save(); },
            configuration.DoorbellEnterSoundVolume, v => configuration.DoorbellEnterSoundVolume = v,
            firstSet: true);

        DrawDoorbellBlock(
            "doorbellalready", "Already inside when you arrive", Path.Combine(doorbellDir, "doorbell.mp3"), () => onTestDoorbell?.Invoke(1),
            configuration.DoorbellAlreadyHereChat,    v => configuration.DoorbellAlreadyHereChat = v,
            configuration.DoorbellAlreadyHereText,    v => configuration.DoorbellAlreadyHereText = v, Configuration.DefaultDoorbellAlreadyHereText,
            configuration.DoorbellAlreadyHereSound,   v => configuration.DoorbellAlreadyHereSound = v,
            configuration.DoorbellAlreadyHereSoundPath,   p => { configuration.DoorbellAlreadyHereSoundPath   = p; configuration.Save(); },
            configuration.DoorbellAlreadyHereSoundVolume, v => configuration.DoorbellAlreadyHereSoundVolume = v);

        DrawDoorbellBlock(
            "doorbellleave", "Someone left", Path.Combine(doorbellDir, "leave.mp3"), () => onTestDoorbell?.Invoke(2),
            configuration.DoorbellLeaveChat,    v => configuration.DoorbellLeaveChat = v,
            configuration.DoorbellLeaveText,    v => configuration.DoorbellLeaveText = v, Configuration.DefaultDoorbellLeaveText,
            configuration.DoorbellLeaveSound,   v => configuration.DoorbellLeaveSound = v,
            configuration.DoorbellLeaveSoundPath,   p => { configuration.DoorbellLeaveSoundPath   = p; configuration.Save(); },
            configuration.DoorbellLeaveSoundVolume, v => configuration.DoorbellLeaveSoundVolume = v);

        // ── Commendations ─────────────────────────────────────────────────────

        SubsectionLabel("Commendations");

        ConfigCheckbox(
            "Enable commendation sounds##commendation",
            configuration.CommendationEnabled,
            v => configuration.CommendationEnabled = v,
            "Plays a sound when you receive commendations after a duty, based on how many you received.");

        ImGui.Dummy(new Vector2(0, 8));
        ImGui.BeginDisabled(!configuration.CommendationEnabled);

        var cDir = Path.Combine(pluginInterface.AssemblyLocation.DirectoryName!, "Sounds", "Congratulations");

        SectionRow();
        Common.DimmedText("1/3 commends:");
        ImGui.Dummy(new Vector2(0, 2));
        DrawSoundPicker(
            "commendot", "Commendation sound: 1/3",
            Path.Combine(cDir, "one-third.mp3"),
            configuration.CommendationOneThirdPath,
            configuration.CommendationOneThirdVolume,
            p => { configuration.CommendationOneThirdPath   = p; configuration.Save(); },
            v => configuration.CommendationOneThirdVolume = v);

        RowGap(8);
        Common.DimmedText("2/3 commends:");
        ImGui.Dummy(new Vector2(0, 2));
        DrawSoundPicker(
            "commendtt", "Commendation sound: 2/3",
            Path.Combine(cDir, "two-thirds.mp3"),
            configuration.CommendationTwoThirdsPath,
            configuration.CommendationTwoThirdsVolume,
            p => { configuration.CommendationTwoThirdsPath   = p; configuration.Save(); },
            v => configuration.CommendationTwoThirdsVolume = v);

        RowGap(8);
        Common.DimmedText("3/3 commends:");
        ImGui.Dummy(new Vector2(0, 2));
        DrawSoundPicker(
            "commendth", "Commendation sound: 3/3",
            Path.Combine(cDir, "three-thirds.mp3"),
            configuration.CommendationThreeThirdsPath,
            configuration.CommendationThreeThirdsVolume,
            p => { configuration.CommendationThreeThirdsPath   = p; configuration.Save(); },
            v => configuration.CommendationThreeThirdsVolume = v);

        RowGap(8);
        Common.DimmedText("All 7 (full party):");
        ImGui.Dummy(new Vector2(0, 2));
        DrawSoundPicker(
            "commendas", "Commendation sound: all 7 (full party)",
            Path.Combine(cDir, "all-seven.mp3"),
            configuration.CommendationAllSevenPath,
            configuration.CommendationAllSevenVolume,
            p => { configuration.CommendationAllSevenPath   = p; configuration.Save(); },
            v => configuration.CommendationAllSevenVolume = v);

        ImGui.EndDisabled();

        EndSection(10);
    }

    private void DrawSoundPicker(string id, string title, string defaultPath, string configPath, float volume, Action<string> setPath, Action<float> setVolume, bool showTest = true)
    {
        ImGui.BeginGroup();

        // Row 1: [Reset to default] [Browse...] [Default sound / Current: filename]
        SectionRow();
        PushButton();
        if (ImGui.Button($"Reset to default##{id}reset")) setPath("");
        PopButton();
        ImGui.SameLine();
        PushButton();
        if (ImGui.Button($"Browse...##{id}browse"))
            fileDialogManager.OpenFileDialog(
                "Select sound file",
                SoundEngine.FileFilter,
                (ok, p) => { if (ok) setPath(p); });
        PopButton();
        ImGui.SameLine();
        Common.DimmedText(string.IsNullOrEmpty(configPath)
            ? (string.IsNullOrEmpty(defaultPath) ? "No sound set" : "Default sound")
            : $"Current: {Path.GetFileName(configPath)}");

        // Row 2: [Test sound] [slider]
        RowGap(1);
        if (showTest)
        {
            PushButton();
            if (ImGui.Button($"Test sound##{id}test"))
                HoliestFluffiness.SoundEngine.Play(string.IsNullOrEmpty(configPath) ? defaultPath : configPath, volume);
            PopButton();
            ImGui.SameLine();
        }
        ImGui.SetNextItemWidth(200);
        var vol = volume * 100f;
        PushInput();
        // Slider drags 0-100% (100% = normal full volume). No AlwaysClamp, so Ctrl+Click entry can
        // exceed the bar up to 300% to boost quiet files; the setter caps it there.
        if (ImGui.SliderFloat($"##{id}vol", ref vol, 0f, 100f, "%.0f%%"))
            setVolume(Math.Clamp(vol, 0f, 300f) / 100f);
        if (ImGui.IsItemDeactivatedAfterEdit()) configuration.Save();
        PopInput();

        ImGui.EndGroup();
        Anchor(id, title, "Sound file and volume settings");
    }

    // One doorbell event (enter / already-here / leave). Section-text label with a Test button, an
    // optional chat line (with a "<player>" token and reset button) and an optional sound. Mirrors
    // DrawCombatBlock so both share the same clean layout.
    private void DrawDoorbellBlock(
        string id, string label, string defaultSoundPath, Action onTest,
        bool chat,        Action<bool>   setChat,
        string text,      Action<string> setText, string defaultText,
        bool sound,       Action<bool>   setSound,
        string soundPath, Action<string> setSoundPath,
        float volume,     Action<float>  setVolume,
        bool firstSet = false)
    {
        ImGui.BeginGroup();

        if (firstSet) SectionRow();
        else RowGap(6);

        Common.DimmedTextWrapped(label);

        ImGui.SameLine();
        PushButton();
        if (ImGui.Button($"Test##{id}test")) onTest();
        PopButton();

        // Row 1: [x] Print in chat  [message]  [Reset]  <player> = name
        PushCheckbox();
        var c = chat;
        SectionRow();
        if (ImGui.Checkbox($"Print in chat##{id}chat", ref c)) { setChat(c); configuration.Save(); }
        PopCheckbox();
        ImGui.SameLine(0, 8);
        ImGui.BeginDisabled(!chat);
        ImGui.SetNextItemWidth(220);
        var t = text;
        PushInput();
        if (ImGui.InputText($"##{id}txt", ref t, 128)) setText(t);
        if (ImGui.IsItemDeactivatedAfterEdit()) configuration.Save();
        PopInput();
        ImGui.SameLine();
        PushButton();
        if (ImGui.Button($"Reset##{id}txtrst")) { setText(defaultText); configuration.Save(); }
        PopButton();
        ImGui.SameLine();
        Common.DimmedText("<player> = name");
        ImGui.EndDisabled();

        // Row 2: [x] Play a sound
        SectionRow();
        PushCheckbox();
        var s = sound;
        if (ImGui.Checkbox($"Play a sound##{id}en", ref s)) { setSound(s); configuration.Save(); }
        PopCheckbox();

        // Sound file + volume (dimmed when disabled). No per-picker test; the Test button above covers it.
        ImGui.BeginDisabled(!sound);
        ImGui.Dummy(new Vector2(0, 2));
        DrawSoundPicker(id, $"Doorbell: {label}", defaultSoundPath, soundPath, volume, setSoundPath, setVolume, showTest: false);
        ImGui.EndDisabled();

        ImGui.EndGroup();
        Anchor(id, $"Doorbell: {label}", "Doorbell chat text and sound settings");
    }
}
