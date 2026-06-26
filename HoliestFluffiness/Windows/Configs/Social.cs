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
        BeginSection("Social");

        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Nearby players, targeting tracker, house doorbell, and commendation sounds.");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 8));

        // ── Nearby players ────────────────────────────────────────────────────
        SubsectionLabel("Nearby players");
        ImGui.Dummy(new Vector2(0, 4));

        SectionRow();
        PushCheckbox();
        var enabled = configuration.NearbyEnabled;
        if (ImGui.Checkbox("Enable nearby players window##nearbyenabled", ref enabled))
        {
            configuration.NearbyEnabled = enabled;
            configuration.Save();
        }
        PopCheckbox();

        ImGui.Dummy(new Vector2(0, 4));
        ImGui.BeginDisabled(!configuration.NearbyEnabled);

        SectionRow();
        PushCheckbox();
        var hideInCombat = configuration.NearbyHideInCombat;
        if (ImGui.Checkbox("Hide while in combat##nearbyhidecombat", ref hideInCombat))
        {
            configuration.NearbyHideInCombat = hideInCombat;
            configuration.Save();
        }
        PopCheckbox();

        ImGui.Dummy(new Vector2(0, 2));
        SectionRow();
        PushCheckbox();
        var hideInDuty = configuration.NearbyHideInDuty;
        if (ImGui.Checkbox("Hide while in duty##nearbyhideduty", ref hideInDuty))
        {
            configuration.NearbyHideInDuty = hideInDuty;
            configuration.Save();
        }
        PopCheckbox();

        ImGui.Dummy(new Vector2(0, 2));
        SectionRow();
        PushCheckbox();
        var filterAfk = configuration.NearbyFilterAfk;
        if (ImGui.Checkbox("Filter AFK players##nearbyafk", ref filterAfk))
        {
            configuration.NearbyFilterAfk = filterAfk;
            configuration.Save();
        }
        PopCheckbox();

        ImGui.Dummy(new Vector2(0, 2));
        SectionRow();
        PushCheckbox();
        var filterLow = configuration.NearbyFilterLowLevel;
        if (ImGui.Checkbox("Filter low-level players (≤ 3)##nearbylowlevel", ref filterLow))
        {
            configuration.NearbyFilterLowLevel = filterLow;
            configuration.Save();
        }
        PopCheckbox();

        ImGui.Dummy(new Vector2(0, 2));
        SectionRow();
        PushCheckbox();
        var debugSelf = configuration.NearbyDebugSelf;
        if (ImGui.Checkbox("Debug: add yourself to nearby list##nearbydebugself", ref debugSelf))
        {
            configuration.NearbyDebugSelf = debugSelf;
            configuration.Save();
        }
        PopCheckbox();
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

        ImGui.Dummy(new Vector2(0, 12));

        // ── Colours ───────────────────────────────────────────────────────────
        SubsectionLabel("Colours");
        ImGui.Dummy(new Vector2(0, 4));

        SectionRow();
        var colParty = configuration.NearbyColParty;
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

        ImGui.Dummy(new Vector2(0, 4));
        SectionRow();
        PushButton();
        if (ImGui.Button("Set to default##nearbycoldefault"))
        {
            configuration.NearbyColParty   = new Vector4(100/255f, 180/255f, 255/255f, 1f);
            configuration.NearbyColFriend  = new Vector4(1f, 127/255f, 0f, 1f);
            configuration.NearbyColLocalFc = new Vector4(220/255f, 200/255f, 80/255f, 1f);
            configuration.Save();
        }
        PopButton();

        ImGui.Dummy(new Vector2(0, 12));

        // ── Targeting you ─────────────────────────────────────────────────────
        SubsectionLabel("Targeting you");
        ImGui.Dummy(new Vector2(0, 4));

        SectionRow();
        PushCheckbox();
        var showTargeters = configuration.NearbyShowTargeters;
        if (ImGui.Checkbox("Track who's targeting you##nearbytargeters", ref showTargeters))
        {
            configuration.NearbyShowTargeters = showTargeters;
            configuration.Save();
        }
        PopCheckbox();

        ImGui.Dummy(new Vector2(0, 2));
        ImGui.BeginDisabled(!configuration.NearbyShowTargeters);
        SectionRow();
        PushCheckbox();
        var trackSelf = configuration.NearbyTargeterTrackSelf;
        if (ImGui.Checkbox("Debug: Track yourself##nearbytracksself", ref trackSelf))
        {
            configuration.NearbyTargeterTrackSelf = trackSelf;
            configuration.Save();
        }
        PopCheckbox();
        ImGui.EndDisabled();

        ImGui.Dummy(new Vector2(0, 4));
        ImGui.BeginDisabled(!configuration.NearbyShowTargeters);

        SectionRow();
        PushCheckbox();
        var markTargeting = configuration.NearbyMarkTargeting;
        if (ImGui.Checkbox("Mark targeting you in-world##nearbymarktargeting", ref markTargeting))
        {
            configuration.NearbyMarkTargeting = markTargeting;
            configuration.Save();
        }
        PopCheckbox();

        ImGui.Dummy(new Vector2(0, 2));
        ImGui.BeginDisabled(!configuration.NearbyMarkTargeting);
        SectionRow();
        var markCol = configuration.NearbyMarkTargetingColour;
        if (ImGui.ColorEdit4("Mark colour##nearbymarkcol", ref markCol, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar))
        {
            configuration.NearbyMarkTargetingColour = markCol;
            configuration.Save();
        }
        ImGui.SameLine(0, 20);
        ImGui.SetNextItemWidth(200);
        var markSize = configuration.NearbyMarkTargetingSize;
        PushInput();
        if (ImGui.SliderInt("Mark size##nearbymarksize", ref markSize, 1, 20))
        {
            configuration.NearbyMarkTargetingSize = markSize;
            configuration.Save();
        }
        PopInput();
        ImGui.EndDisabled();

        ImGui.Dummy(new Vector2(0, 4));

        // ── Sound ─────────────────────────────────────────────────────────────

        SectionRow();
        PushCheckbox();
        var soundEnabled = configuration.NearbyTargeterSound;
        if (ImGui.Checkbox("Play sound when someone targets you##nearbysound", ref soundEnabled))
        {
            configuration.NearbyTargeterSound = soundEnabled;
            configuration.Save();
        }
        PopCheckbox();

        ImGui.Dummy(new Vector2(0, 4));
        ImGui.BeginDisabled(!configuration.NearbyTargeterSound);
        DrawSoundPicker("nearbytargeter",
            Path.Combine(pluginInterface.AssemblyLocation.DirectoryName!, "Sounds", "Targeting", "looking.mp3"),
            configuration.NearbyTargeterSoundPath, configuration.NearbyTargeterSoundVolume,
            p => { configuration.NearbyTargeterSoundPath  = p; configuration.Save(); },
            v => { configuration.NearbyTargeterSoundVolume = v; configuration.Save(); });
        ImGui.EndDisabled();

        ImGui.EndDisabled();

        ImGui.Dummy(new Vector2(0, 4));

        // ── House doorbell ────────────────────────────────────────────────────

        ImGui.Dummy(new Vector2(0, 12));
        SubsectionLabel("House doorbell");
        ImGui.Dummy(new Vector2(0, 2));
        SectionRow();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextWrapped("Alerts when players enter or leave a house, or are already present when you arrive.");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 8));

        var doorbellDefault = Path.Combine(pluginInterface.AssemblyLocation.DirectoryName!, "Sounds", "Doorbell", "doorbell.wav");

        // Entered
        SectionRow();
        PushCheckbox();
        var enterEnabled = configuration.DoorbellEnterEnabled;
        if (ImGui.Checkbox("Someone entered##doorbellenterenabled", ref enterEnabled))
        {
            configuration.DoorbellEnterEnabled = enterEnabled;
            configuration.Save();
        }
        PopCheckbox();
        ImGui.BeginDisabled(!configuration.DoorbellEnterEnabled);
        ImGui.SameLine();
        PushCheckbox();
        var enterChat = configuration.DoorbellEnterChat;
        if (ImGui.Checkbox("Print in chat##doorbellenterchat", ref enterChat))
        {
            configuration.DoorbellEnterChat = enterChat;
            configuration.Save();
        }
        PopCheckbox();
        ImGui.SameLine();
        PushCheckbox();
        var enterSound = configuration.DoorbellEnterSound;
        if (ImGui.Checkbox("Play a sound##doorbellentersonud", ref enterSound))
        {
            configuration.DoorbellEnterSound = enterSound;
            configuration.Save();
        }
        PopCheckbox();
        ImGui.Dummy(new Vector2(0, 2));
        ImGui.BeginDisabled(!configuration.DoorbellEnterSound);
        DrawSoundPicker("doorbellenter", doorbellDefault,
            configuration.DoorbellEnterSoundPath, configuration.DoorbellEnterSoundVolume,
            p => { configuration.DoorbellEnterSoundPath  = p; configuration.Save(); },
            v => { configuration.DoorbellEnterSoundVolume = v; configuration.Save(); });
        ImGui.EndDisabled();
        ImGui.EndDisabled();

        ImGui.Dummy(new Vector2(0, 8));

        // Already here
        SectionRow();
        PushCheckbox();
        var alreadyEnabled = configuration.DoorbellAlreadyHereEnabled;
        if (ImGui.Checkbox("Already inside when you arrive##doorbellalreadyenabled", ref alreadyEnabled))
        {
            configuration.DoorbellAlreadyHereEnabled = alreadyEnabled;
            configuration.Save();
        }
        PopCheckbox();
        ImGui.BeginDisabled(!configuration.DoorbellAlreadyHereEnabled);
        ImGui.SameLine();
        PushCheckbox();
        var alreadyChat = configuration.DoorbellAlreadyHereChat;
        if (ImGui.Checkbox("Print in chat##doorbellalreadychat", ref alreadyChat))
        {
            configuration.DoorbellAlreadyHereChat = alreadyChat;
            configuration.Save();
        }
        PopCheckbox();
        ImGui.SameLine();
        PushCheckbox();
        var alreadySound = configuration.DoorbellAlreadyHereSound;
        if (ImGui.Checkbox("Play a sound##doorbellalreadysound", ref alreadySound))
        {
            configuration.DoorbellAlreadyHereSound = alreadySound;
            configuration.Save();
        }
        PopCheckbox();
        ImGui.Dummy(new Vector2(0, 2));
        ImGui.BeginDisabled(!configuration.DoorbellAlreadyHereSound);
        DrawSoundPicker("doorbellalready", doorbellDefault,
            configuration.DoorbellAlreadyHereSoundPath, configuration.DoorbellAlreadyHereSoundVolume,
            p => { configuration.DoorbellAlreadyHereSoundPath   = p; configuration.Save(); },
            v => { configuration.DoorbellAlreadyHereSoundVolume = v; configuration.Save(); });
        ImGui.EndDisabled();
        ImGui.EndDisabled();

        ImGui.Dummy(new Vector2(0, 8));

        // Left
        SectionRow();
        PushCheckbox();
        var leaveEnabled = configuration.DoorbellLeaveEnabled;
        if (ImGui.Checkbox("Someone left##doorbelllaveenabled", ref leaveEnabled))
        {
            configuration.DoorbellLeaveEnabled = leaveEnabled;
            configuration.Save();
        }
        PopCheckbox();
        ImGui.BeginDisabled(!configuration.DoorbellLeaveEnabled);
        ImGui.SameLine();
        PushCheckbox();
        var leaveChat = configuration.DoorbellLeaveChat;
        if (ImGui.Checkbox("Print in chat##doorbellleavechat", ref leaveChat))
        {
            configuration.DoorbellLeaveChat = leaveChat;
            configuration.Save();
        }
        PopCheckbox();
        ImGui.SameLine();
        PushCheckbox();
        var leaveSound = configuration.DoorbellLeaveSound;
        if (ImGui.Checkbox("Play a sound##doorbellleavesound", ref leaveSound))
        {
            configuration.DoorbellLeaveSound = leaveSound;
            configuration.Save();
        }
        PopCheckbox();
        ImGui.Dummy(new Vector2(0, 2));
        ImGui.BeginDisabled(!configuration.DoorbellLeaveSound);
        DrawSoundPicker("doorbellleave", doorbellDefault,
            configuration.DoorbellLeaveSoundPath, configuration.DoorbellLeaveSoundVolume,
            p => { configuration.DoorbellLeaveSoundPath  = p; configuration.Save(); },
            v => { configuration.DoorbellLeaveSoundVolume = v; configuration.Save(); });
        ImGui.EndDisabled();
        ImGui.EndDisabled();

        ImGui.Dummy(new Vector2(0, 4));

        // ── Commendations ─────────────────────────────────────────────────────

        ImGui.Dummy(new Vector2(0, 12));
        SubsectionLabel("Commendations");
        ImGui.Dummy(new Vector2(0, 2));
        SectionRow();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextWrapped("Plays a sound when you receive commendations after a duty. Each tier plays a different sound based on how many you received.");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 4));
        SectionRow();

        PushCheckbox();
        var commendEnabled = configuration.CommendationEnabled;
        if (ImGui.Checkbox("Enable##commendation", ref commendEnabled))
        {
            configuration.CommendationEnabled = commendEnabled;
            configuration.Save();
        }
        PopCheckbox();

        ImGui.Dummy(new Vector2(0, 8));
        ImGui.BeginDisabled(!configuration.CommendationEnabled);

        var cDir = Path.Combine(pluginInterface.AssemblyLocation.DirectoryName!, "Sounds", "Congratulations");

        SectionRow();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("1/3 commends:");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 2));
        DrawSoundPicker("commendot", Path.Combine(cDir, "one-third.mp3"),
            configuration.CommendationOneThirdPath, configuration.CommendationOneThirdVolume,
            p => { configuration.CommendationOneThirdPath   = p; configuration.Save(); },
            v => { configuration.CommendationOneThirdVolume = v; configuration.Save(); });

        ImGui.Dummy(new Vector2(0, 8));
        SectionRow();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("2/3 commends:");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 2));
        DrawSoundPicker("commendtt", Path.Combine(cDir, "two-thirds.mp3"),
            configuration.CommendationTwoThirdsPath, configuration.CommendationTwoThirdsVolume,
            p => { configuration.CommendationTwoThirdsPath   = p; configuration.Save(); },
            v => { configuration.CommendationTwoThirdsVolume = v; configuration.Save(); });

        ImGui.Dummy(new Vector2(0, 8));
        SectionRow();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("3/3 commends:");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 2));
        DrawSoundPicker("commendth", Path.Combine(cDir, "three-thirds.mp3"),
            configuration.CommendationThreeThirdsPath, configuration.CommendationThreeThirdsVolume,
            p => { configuration.CommendationThreeThirdsPath   = p; configuration.Save(); },
            v => { configuration.CommendationThreeThirdsVolume = v; configuration.Save(); });

        ImGui.Dummy(new Vector2(0, 8));
        SectionRow();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("All 7 (full party):");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 2));
        DrawSoundPicker("commendas", Path.Combine(cDir, "all-seven.mp3"),
            configuration.CommendationAllSevenPath, configuration.CommendationAllSevenVolume,
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
            fileDialogManager.OpenFileDialog("Select sound file", ".wav,.mp3,.ogg,.aif,.aiff,.wma",
                (ok, p) => { if (ok) setPath(p); });
        PopButton();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted(string.IsNullOrEmpty(configPath)
            ? (string.IsNullOrEmpty(defaultPath) ? "No sound set" : "Default sound")
            : $"Current: {Path.GetFileName(configPath)}");
        ImGui.PopStyleColor();

        // Row 2: [Test sound] [slider]
        ImGui.Dummy(new Vector2(0, 2));
        SectionRow();
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
