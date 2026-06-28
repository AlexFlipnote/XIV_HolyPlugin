using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using HoliestFluffiness.Handlers;

namespace HoliestFluffiness.Windows;

public partial class ConfigWindow
{
    private NearbyHandler nearbyHandler = null!;

    internal void SetNearbyHandler(NearbyHandler handler) => nearbyHandler = handler;

    private void DrawSocialSection()
    {
        BeginSection("Social", "Nearby players, targeting tracker, house doorbell, commendation sounds, and nameplate tweaks.");

        // ── Dynamic Traveler ──────────────────────────────────────────────────
        SubsectionLabel("Dynamic Traveler");

        ConfigCheckbox(
            "Replace Wanderer / Traveller with home world##dynamictraveler",
            configuration.DynamicTravelerEnabled,
            v => configuration.DynamicTravelerEnabled = v,
            "Replaces the Wanderer / Traveller FC tags on cross-world nameplates with the player's home world.");

        // ── Echo Party Finder ─────────────────────────────────────────────────
        SubsectionLabel("Echo Party Finder");

        ConfigCheckbox(
            "Echo party finder description##echopartyfinder",
            configuration.EchoPartyFinderEnabled,
            v => configuration.EchoPartyFinderEnabled = v,
            "Prints the party finder listing description to chat when you join, and again when you enter the duty.");

        // ── Nearby players ────────────────────────────────────────────────────
        SubsectionLabel("Nearby players");
        ConfigCheckbox(
            "Enable nearby players window##nearbyenabled",
            configuration.NearbyEnabled,
            v => configuration.NearbyEnabled = v);

        ImGui.BeginDisabled(!configuration.NearbyEnabled);

        ConfigCheckbox(
            "Hide while in combat##nearbyhidecombat",
            configuration.NearbyHideInCombat,
            v => configuration.NearbyHideInCombat = v);

        ConfigCheckbox(
            "Hide while in duty##nearbyhideduty",
            configuration.NearbyHideInDuty,
            v => configuration.NearbyHideInDuty = v);

        ConfigCheckbox(
            "Filter AFK players##nearbyafk",
            configuration.NearbyFilterAfk,
            v => configuration.NearbyFilterAfk = v);

        ConfigCheckbox(
            "Filter low-level players (≤ 3)##nearbylowlevel",
            configuration.NearbyFilterLowLevel,
            v => configuration.NearbyFilterLowLevel = v);

        ConfigCheckbox(
            "Debug: add yourself to nearby list##nearbydebugself",
            configuration.NearbyDebugSelf,
            v => configuration.NearbyDebugSelf = v);
        ImGui.SameLine(0, 12);
        ImGui.BeginDisabled(!configuration.NearbyDebugSelf);
        ImGui.SetNextItemWidth(140);
        var debugSelfAs = configuration.NearbyDebugSelfAs;
        PushInput();
        if (ImGui.Combo("##nearbydebugselfa", ref debugSelfAs, "as Normal\0as Friend\0as FC member\0as Party member\0as Targeting you\0"))
        {
            configuration.NearbyDebugSelfAs = debugSelfAs;
            configuration.Save();
        }
        PopInput();
        ImGui.EndDisabled();

        ImGui.EndDisabled();

        // ── Colours ───────────────────────────────────────────────────────────
        var colParty = configuration.NearbyColParty;
        RowGap(2);
        Common.DimmedTextWrapped("Colours of names inside if the character is...");
        SectionRow();
        if (ImGui.ColorEdit4("Party##nearbycolparty", ref colParty, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar))
        {
            configuration.NearbyColParty = colParty;
            configuration.Save();
        }
        ImGui.SameLine(0, 20);
        var colFriend = configuration.NearbyColFriend;
        if (ImGui.ColorEdit4("Friend##nearbycolorfriend", ref colFriend, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar))
        {
            configuration.NearbyColFriend = colFriend;
            configuration.Save();
        }
        ImGui.SameLine(0, 20);
        var colFc = configuration.NearbyColLocalFc;
        if (ImGui.ColorEdit4("Same FC##nearbycolorfc", ref colFc, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar))
        {
            configuration.NearbyColLocalFc = colFc;
            configuration.Save();
        }

        RowGap();
        PushButton();
        if (ImGui.Button("Set to default##nearbycoldefault"))
        {
            configuration.NearbyColParty   = new Vector4(100/255f, 180/255f, 255/255f, 1f);
            configuration.NearbyColFriend  = new Vector4(1f, 127/255f, 0f, 1f);
            configuration.NearbyColLocalFc = new Vector4(220/255f, 200/255f, 80/255f, 1f);
            configuration.Save();
        }
        PopButton();

        // ── Targeting you ─────────────────────────────────────────────────────
        SubsectionLabel("Targeting you");
        ConfigCheckbox(
            "Track who's targeting you##nearbytargeters",
            configuration.NearbyShowTargeters,
            v => configuration.NearbyShowTargeters = v);

        ImGui.BeginDisabled(!configuration.NearbyShowTargeters);

        ConfigCheckbox(
            "Debug: Track yourself##nearbytracksself",
            configuration.NearbyTargeterTrackSelf,
            v => configuration.NearbyTargeterTrackSelf = v);
        ImGui.EndDisabled();

        ImGui.BeginDisabled(!configuration.NearbyShowTargeters);

        ConfigCheckbox(
            "Mark targeting you in-world##nearbymarktargeting",
            configuration.NearbyMarkTargeting,
            v => configuration.NearbyMarkTargeting = v);

        ImGui.BeginDisabled(!configuration.NearbyMarkTargeting);
        SectionRow();
        var markCol = configuration.NearbyMarkTargetingColour;
        if (ImGui.ColorEdit4("Mark colour##nearbymarkcol", ref markCol, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar))
        {
            configuration.NearbyMarkTargetingColour = markCol;
            configuration.Save();
        }
        ImGui.SameLine(0, 20);
        ConfigSliderInt("Mark size##nearbymarksize", configuration.NearbyMarkTargetingSize, 1, 20,
            v => configuration.NearbyMarkTargetingSize = v, width: 200);
        ImGui.EndDisabled();

        // ── Sound ─────────────────────────────────────────────────────────────

        ConfigCheckbox(
            "Play sound when someone targets you##nearbysound",
            configuration.NearbyTargeterSound,
            v => configuration.NearbyTargeterSound = v);

        ImGui.Dummy(new Vector2(0, 2));
        ImGui.BeginDisabled(!configuration.NearbyTargeterSound);
        DrawSoundPicker(
            "nearbytargeter",
            Path.Combine(pluginInterface.AssemblyLocation.DirectoryName!, "Sounds", "Targeting", "looking.mp3"),
            configuration.NearbyTargeterSoundPath,
            configuration.NearbyTargeterSoundVolume,
            p => { configuration.NearbyTargeterSoundPath   = p; configuration.Save(); },
            v => { configuration.NearbyTargeterSoundVolume = v; configuration.Save(); });
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        // ── House doorbell ────────────────────────────────────────────────────

        SubsectionLabel("House doorbell",
            "Alerts when players enter or leave a house, or are already present when you arrive.");

        var doorbellDefault = Path.Combine(
            pluginInterface.AssemblyLocation.DirectoryName!,
            "Sounds", "Doorbell", "doorbell.wav"
        );

        // Entered
        ConfigCheckbox(
            "Someone entered##doorbellenterenabled",
            configuration.DoorbellEnterEnabled,
            v => configuration.DoorbellEnterEnabled = v);
        ImGui.BeginDisabled(!configuration.DoorbellEnterEnabled);
        ImGui.SameLine();
        ConfigCheckbox(
            "Print in chat##doorbellenterchat",
            configuration.DoorbellEnterChat,
            v => configuration.DoorbellEnterChat = v);
        ImGui.SameLine();
        ConfigCheckbox(
            "Play a sound##doorbellentersonud",
            configuration.DoorbellEnterSound,
            v => configuration.DoorbellEnterSound = v);
        ImGui.Dummy(new Vector2(0, 2));
        ImGui.BeginDisabled(!configuration.DoorbellEnterSound);
        DrawSoundPicker(
            "doorbellenter",
            doorbellDefault,
            configuration.DoorbellEnterSoundPath,
            configuration.DoorbellEnterSoundVolume,
            p => { configuration.DoorbellEnterSoundPath   = p; configuration.Save(); },
            v => { configuration.DoorbellEnterSoundVolume = v; configuration.Save(); });
        ImGui.EndDisabled();
        ImGui.EndDisabled();

        ImGui.Dummy(new Vector2(0, 8));

        // Already here
        ConfigCheckbox(
            "Already inside when you arrive##doorbellalreadyenabled",
            configuration.DoorbellAlreadyHereEnabled,
            v => configuration.DoorbellAlreadyHereEnabled = v);
        ImGui.BeginDisabled(!configuration.DoorbellAlreadyHereEnabled);
        ImGui.SameLine();
        ConfigCheckbox(
            "Print in chat##doorbellalreadychat",
            configuration.DoorbellAlreadyHereChat,
            v => configuration.DoorbellAlreadyHereChat = v);
        ImGui.SameLine();
        ConfigCheckbox(
            "Play a sound##doorbellalreadysound",
            configuration.DoorbellAlreadyHereSound,
            v => configuration.DoorbellAlreadyHereSound = v);
        ImGui.Dummy(new Vector2(0, 2));
        ImGui.BeginDisabled(!configuration.DoorbellAlreadyHereSound);
        DrawSoundPicker(
            "doorbellalready",
            doorbellDefault,
            configuration.DoorbellAlreadyHereSoundPath,
            configuration.DoorbellAlreadyHereSoundVolume,
            p => { configuration.DoorbellAlreadyHereSoundPath   = p; configuration.Save(); },
            v => { configuration.DoorbellAlreadyHereSoundVolume = v; configuration.Save(); });
        ImGui.EndDisabled();
        ImGui.EndDisabled();

        ImGui.Dummy(new Vector2(0, 8));

        // Left
        ConfigCheckbox(
            "Someone left##doorbelllaveenabled",
            configuration.DoorbellLeaveEnabled,
            v => configuration.DoorbellLeaveEnabled = v);
        ImGui.BeginDisabled(!configuration.DoorbellLeaveEnabled);
        ImGui.SameLine();
        ConfigCheckbox(
            "Print in chat##doorbellleavechat",
            configuration.DoorbellLeaveChat,
            v => configuration.DoorbellLeaveChat = v);
        ImGui.SameLine();
        ConfigCheckbox(
            "Play a sound##doorbellleavesound",
            configuration.DoorbellLeaveSound,
            v => configuration.DoorbellLeaveSound = v);
        ImGui.Dummy(new Vector2(0, 2));
        ImGui.BeginDisabled(!configuration.DoorbellLeaveSound);
        DrawSoundPicker(
            "doorbellleave",
            doorbellDefault,
            configuration.DoorbellLeaveSoundPath,
            configuration.DoorbellLeaveSoundVolume,
            p => { configuration.DoorbellLeaveSoundPath   = p; configuration.Save(); },
            v => { configuration.DoorbellLeaveSoundVolume = v; configuration.Save(); });
        ImGui.EndDisabled();
        ImGui.EndDisabled();

        ImGui.Dummy(new Vector2(0, 4));

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
            "commendot",
            Path.Combine(cDir, "one-third.mp3"),
            configuration.CommendationOneThirdPath,
            configuration.CommendationOneThirdVolume,
            p => { configuration.CommendationOneThirdPath   = p; configuration.Save(); },
            v => { configuration.CommendationOneThirdVolume = v; configuration.Save(); });

        RowGap(8);
        Common.DimmedText("2/3 commends:");
        ImGui.Dummy(new Vector2(0, 2));
        DrawSoundPicker(
            "commendtt",
            Path.Combine(cDir, "two-thirds.mp3"),
            configuration.CommendationTwoThirdsPath,
            configuration.CommendationTwoThirdsVolume,
            p => { configuration.CommendationTwoThirdsPath   = p; configuration.Save(); },
            v => { configuration.CommendationTwoThirdsVolume = v; configuration.Save(); });

        RowGap(8);
        Common.DimmedText("3/3 commends:");
        ImGui.Dummy(new Vector2(0, 2));
        DrawSoundPicker(
            "commendth",
            Path.Combine(cDir, "three-thirds.mp3"),
            configuration.CommendationThreeThirdsPath,
            configuration.CommendationThreeThirdsVolume,
            p => { configuration.CommendationThreeThirdsPath   = p; configuration.Save(); },
            v => { configuration.CommendationThreeThirdsVolume = v; configuration.Save(); });

        RowGap(8);
        Common.DimmedText("All 7 (full party):");
        ImGui.Dummy(new Vector2(0, 2));
        DrawSoundPicker(
            "commendas",
            Path.Combine(cDir, "all-seven.mp3"),
            configuration.CommendationAllSevenPath,
            configuration.CommendationAllSevenVolume,
            p => { configuration.CommendationAllSevenPath   = p; configuration.Save(); },
            v => { configuration.CommendationAllSevenVolume = v; configuration.Save(); });

        ImGui.EndDisabled();

        EndSection(10);
    }

    private void DrawSoundPicker(string id, string defaultPath, string configPath, float volume, Action<string> setPath, Action<float> setVolume, bool showTest = true)
    {
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
                ".wav,.mp3,.ogg,.aif,.aiff,.wma",
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
        if (ImGui.SliderFloat($"##{id}vol", ref vol, 0f, 100f, "%.0f%%"))
            setVolume(vol / 100f);
        PopInput();
    }
}
