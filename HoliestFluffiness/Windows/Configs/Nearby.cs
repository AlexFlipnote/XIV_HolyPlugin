using System.Numerics;
using Dalamud.Bindings.ImGui;
using HoliestFluffiness.Handlers;

namespace HoliestFluffiness.Windows;

public partial class ConfigWindow
{
    private NearbyHandler nearbyHandler = null!;

    internal void SetNearbyHandler(NearbyHandler handler) => nearbyHandler = handler;

    private void DrawNearbySection()
    {
        BeginSection("Players");

        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Nearby players window and targeting tracker. Open with /nearby");
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

        SectionRow();
        var browseW = ImGui.CalcTextSize("Browse").X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - browseW - ImGui.GetStyle().ItemSpacing.X - 8f);
        var soundPath = configuration.NearbyTargeterSoundPath;
        PushInput();
        if (ImGui.InputText("##nearbysoundpath", ref soundPath, 512))
        {
            configuration.NearbyTargeterSoundPath = soundPath;
            configuration.Save();
        }
        PopInput();
        ImGui.SameLine();
        PushButton();
        if (ImGui.Button("Browse##nearbysoundbrowse"))
            fileDialogManager.OpenFileDialog("Select sound file", ".wav,.mp3,.ogg,.aif,.aiff,.wma",
                (ok, path) => { if (ok) { configuration.NearbyTargeterSoundPath = path; configuration.Save(); } });
        PopButton();

        ImGui.Dummy(new Vector2(0, 2));
        SectionRow();
        ImGui.SetNextItemWidth(200);
        var volPct = configuration.NearbyTargeterSoundVolume * 100f;
        PushInput();
        if (ImGui.SliderFloat("Volume##nearbysoundvol", ref volPct, 0f, 100f, "%.0f%%"))
        {
            configuration.NearbyTargeterSoundVolume = volPct / 100f;
            configuration.Save();
        }
        PopInput();

        ImGui.Dummy(new Vector2(0, 4));
        SectionRow();
        PushButton();
        if (ImGui.Button("Test sound##nearbysoundtest"))
            HoliestFluffiness.SoundEngine.Play(configuration.NearbyTargeterSoundPath, configuration.NearbyTargeterSoundVolume);
        PopButton();

        ImGui.EndDisabled();

        ImGui.EndDisabled();

        ImGui.Dummy(new Vector2(0, 4));
        EndSection();
    }
}
