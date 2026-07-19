using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.FlyText;
using HoliestFluffiness.Handlers;

namespace HoliestFluffiness.Windows;

public partial class ConfigWindow
{
    private CombatHitHandler combatHitHandler = null!;
    internal void SetCombatHitHandler(CombatHitHandler handler) => combatHitHandler = handler;

    private HideMpBarsHandler hideMpBarsHandler = null!;
    internal void SetHideMpBarsHandler(HideMpBarsHandler handler) => hideMpBarsHandler = handler;

    private void DrawIndicatorsSection()
    {
        BeginSection("Indicators", "Settings for in-game indicators and HUD additions.");

        ConfigCheckbox(
            "Enable cast bar aetheryte names##castbaraetheryte",
            configuration.CastBarAetheryteEnabled,
            v => configuration.CastBarAetheryteEnabled = v,
            "Replaces the generic cast bar text with the actual aetheryte name when using Teleport.");

        ConfigCheckbox(
            "Show duty queue timer##dutytimer",
            configuration.DutyTimerEnabled,
            v => configuration.DutyTimerEnabled = v,
            "Shows estimated remaining queue time in the duty ready check dialog.");

        ConfigCheckbox(
            "Hide hotbar lock##hotbarlock",
            configuration.HotbarLockHidden,
            v =>
            {
                configuration.HotbarLockHidden = v;
                if (!v) clientTweaksHandler?.RestoreHotbarLock();
            },
            "Hides the padlock icon on the action bar");

        // ── Loot ──────────────────────────────────────────────────────────────
        SubsectionLabel("Loot");

        ConfigCheckbox(
            "Fade loot button##lootfade",
            configuration.LootFadeEnabled,
            v => configuration.LootFadeEnabled = v,
            "Fades the loot roll window once you've rolled on everything available.");

        ImGui.BeginDisabled(!configuration.LootFadeEnabled);
        ConfigSliderFloat("Fade amount##lootfadeamount", configuration.LootFadePercent * 100f, 0f, 95f,
            v => configuration.LootFadePercent = v / 100f, width: 200, format: "%.0f%%",
            hint: "(0% = no fade)");
        ImGui.EndDisabled();

        // ── Hide MP bars ──────────────────────────────────────────────────────
        SubsectionLabel("Hide MP bars",
            "Hides the MP bar for jobs that don't use MP (melee, tanks, ranged physical, etc.).");

        ConfigCheckbox(
            "In party list##hidempparty",
            configuration.HideMpBarsPartyList,
            v => { configuration.HideMpBarsPartyList = v; if (!v) hideMpBarsHandler?.ResetPartyList(); },
            "The MP bar to right right of HP in party list.");

        ConfigCheckbox(
            "In parameter bar (own HP/MP)##hidempparam",
            configuration.HideMpBarsParamWidget,
            v => { configuration.HideMpBarsParamWidget = v; if (!v) hideMpBarsHandler?.ResetParamWidget(); },
            "The HP/MP block shown near your hotbars.");

        // ── Server info ───────────────────────────────────────────────────────
        SubsectionLabel("Server info", "Adds entries to the server info bar (the row of icons at the top right).");

        ConfigCheckbox(
            "Show FPS##serverinfofps",
            configuration.ServerInfoFpsEnabled,
            v => configuration.ServerInfoFpsEnabled = v);

        ConfigCheckbox(
            "Show nearby player count##nearbydtr",
            configuration.NearbyDtrEnabled,
            v => configuration.NearbyDtrEnabled = v);

        ConfigCheckbox(
            "Show ping##serverinfoping",
            configuration.ServerInfoPingEnabled,
            v => configuration.ServerInfoPingEnabled = v);
        ImGui.SameLine();
        ImGui.BeginDisabled(!configuration.ServerInfoPingEnabled);
        var displayModes = new[] { "Last ping", "Average ping", "Both" };
        ConfigCombo("##serverinfopingdisplay", (int)configuration.ServerInfoPingDisplay, displayModes,
            v => configuration.ServerInfoPingDisplay = (PingDisplay)v, width: 150, padding: false,
            title: "Ping display mode");
        ImGui.EndDisabled();

        // ── Repair ────────────────────────────────────────────────────────────
        SubsectionLabel("Repair",
            "Adds a debuff icon to Status (Other) when your gear durability drops below a threshold. " +
            "Critical takes priority over Low, only one icon appears at a time.");

        const string repairLowDesc = "Shows: \"Gear at X%, consider repairing\"";
        ImGui.BeginGroup();
        ConfigCheckbox(
            "##repairlowcheck",
            configuration.RepairLowEnabled,
            v => configuration.RepairLowEnabled = v);

        ImGui.SameLine();
        ConfigSliderFloat("Low threshold##repairlowthr", configuration.RepairLowThreshold, 1f, 100f,
            v => configuration.RepairLowThreshold = v, width: 200, format: "%.0f%%");

        SectionRow();
        Common.DimmedText(repairLowDesc);
        ImGui.EndGroup();
        Anchor("repairlow", "Low gear repair threshold", repairLowDesc);

        ImGui.Dummy(new Vector2(0, 4));

        const string repairCritDesc = "Shows: \"Gear really damaged (X%), repair now!!\"";
        ImGui.BeginGroup();
        ConfigCheckbox(
            "##repaircritcheck",
            configuration.RepairCriticalEnabled,
            v => configuration.RepairCriticalEnabled = v);

        ImGui.SameLine();
        ConfigSliderFloat("Critical threshold##repaircritthr", configuration.RepairCriticalThreshold, 1f, 100f,
            v => configuration.RepairCriticalThreshold = v, width: 200, format: "%.0f%%");

        SectionRow();
        Common.DimmedText(repairCritDesc);
        ImGui.EndGroup();
        Anchor("repaircrit", "Critical gear repair threshold", repairCritDesc);

        ImGui.Dummy(new Vector2(0, 8));

        SectionRow();
        var testing = repairHandler.TestPct.HasValue;
        PushButton();
        if (ImGui.Button(testing ? "Stop testing##repairtest" : "Test at 69%##repairtest"))
            repairHandler.TestPct = testing ? null : 69f;
        PopButton();
        ImGui.SameLine();
        Common.DimmedText("Simulates 69% gear condition to preview the debuff icon.");

        // ── Food check ────────────────────────────────────────────────────────
        SubsectionLabel("Food Check Helper",
            "Warns when party members are missing or running low on food. Triggers automatically on ready check and countdown start. " +
            "You can also use '/hf foodcheck' or '/foodcheck' to check on demand.");

        SectionRow();
        PushButton();
        if (ImGui.Button("Test food check##foodchecktest"))
            foodCheckHandler?.ForceCheck();
        PopButton();
        ImGui.SameLine();

        ConfigSliderInt("Notify when food below (minutes)##foodcheckthreshold",
            configuration.FoodCheckThreshold, 1, 60,
            v => configuration.FoodCheckThreshold = v, width: 150, hint: "(default 15)");

        ImGui.Dummy(new Vector2(0, 4));

        ConfigCheckbox(
            "Echo to chat##foodcheckecho",
            configuration.FoodCheckEcho,
            v => configuration.FoodCheckEcho = v,
            "Prints a /echo message listing party members with missing or low food");

        ConfigCheckbox(
            "Highlight on party frame##foodcheckhighlight",
            configuration.FoodCheckHighlight,
            v => { configuration.FoodCheckHighlight = v; if (!v) foodCheckHandler?.Invalidate(); },
            "Overlays a highlight on the party list of member(s) with missing or low food");

        ConfigCheckbox(
            "Play sound##foodchecksound",
            configuration.FoodCheckSound,
            v => configuration.FoodCheckSound = v,
            "Plays a sound notification when someone is missing or has low food");

        ImGui.Dummy(new Vector2(0, 2));
        ImGui.BeginDisabled(!configuration.FoodCheckSound);
        DrawSoundPicker(
            "foodcheck", "Food check sound",
            Path.Combine(pluginInterface.AssemblyLocation.DirectoryName!, "Sounds", "FoodCheck", "hungry.mp3"),
            configuration.FoodCheckSoundPath,
            configuration.FoodCheckSoundVolume,
            p => { configuration.FoodCheckSoundPath   = p; configuration.Save(); },
            v => { configuration.FoodCheckSoundVolume = v; configuration.Save(); });
        ImGui.EndDisabled();

        RowGap();
        Common.DimmedText("Duty scope (combine freely, at least one must be ticked):");
        ConfigCheckbox(
            "High-end duty##foodscopehighend",
            configuration.FoodCheckScopeHighEnd,
            v => configuration.FoodCheckScopeHighEnd = v,
            "Ultimate, current savage/unreal");

        ConfigCheckbox(
            "Any savage##foodscopesavage",
            configuration.FoodCheckScopeSavage,
            v => configuration.FoodCheckScopeSavage = v,
            "High-end, current and old savage, regardless");

        ConfigCheckbox(
            "Any extreme##foodscopeextreme",
            configuration.FoodCheckScopeExtreme,
            v => configuration.FoodCheckScopeExtreme = v,
            "High-end, current and old extreme, regardless");

        ConfigCheckbox(
            "Any content##foodscopeany",
            configuration.FoodCheckScopeAny,
            v => configuration.FoodCheckScopeAny = v,
            "You're crazy if you pick this...");

        // ── Ready check ───────────────────────────────────────────────────────
        SubsectionLabel("Ready Check Helper");

        ConfigCheckbox(
            "Show names in chat##rcnames",
            configuration.ReadyCheckShowNames,
            v => configuration.ReadyCheckShowNames = v,
            "Prints who is not ready after a ready check");

        ConfigCheckbox(
            "Draw ready check overlay##rcoverlay",
            configuration.ReadyCheckDrawOverlay,
            v => configuration.ReadyCheckDrawOverlay = v,
            "Shows ready/not-ready icons on the party list");

        ImGui.BeginDisabled(!configuration.ReadyCheckDrawOverlay);

        ConfigSliderInt("Clear after (s)##rcclear", configuration.ReadyCheckClearAfterSeconds, 1, 60,
            v => configuration.ReadyCheckClearAfterSeconds = v,
            hint: "(default 10)");

        RowGap();
        PushButton();
        if (ImGui.Button("Test overlay##rctest"))
            readyCheckHandler.Simulate();
        PopButton();
        ImGui.SameLine();
        Common.DimmedText("Simulates a ready check for 1 second");
        ImGui.EndDisabled();

        // ── Combat hits ───────────────────────────────────────────────────────
        SubsectionLabel("Combat Hits",
            "Adds audio and visual feedback to combat events like critical hits and direct hits, built to be expanded over time. " +
            "The default sounds are heavily inspired by TF2, a game near and dear to my heart.");

        var combatDir = Path.Combine(pluginInterface.AssemblyLocation.DirectoryName!, "Sounds", "Combat");

        DrawCombatBlock(
            "dc", "Direct Critical Hit", FlyTextKind.DamageCritDh, Path.Combine(combatDir, "direct_critical.wav"),
            configuration.CombatDcEnabled,   v => { configuration.CombatDcEnabled  = v; configuration.Save(); },
            configuration.CombatDcShowText,  v => { configuration.CombatDcShowText = v; configuration.Save(); },
            configuration.CombatDcText,      v => { configuration.CombatDcText     = v; configuration.Save(); },
            configuration.CombatDcSound,     v => { configuration.CombatDcSound    = v; configuration.Save(); },
            configuration.CombatDcVol,       v => { configuration.CombatDcVol      = v; configuration.Save(); },
            firstSet: true);

        DrawCombatBlock(
            "c", "Critical Hit", FlyTextKind.DamageCrit, Path.Combine(combatDir, "critical.wav"),
            configuration.CombatCEnabled,   v => { configuration.CombatCEnabled  = v; configuration.Save(); },
            configuration.CombatCShowText,  v => { configuration.CombatCShowText = v; configuration.Save(); },
            configuration.CombatCText,      v => { configuration.CombatCText     = v; configuration.Save(); },
            configuration.CombatCSound,     v => { configuration.CombatCSound    = v; configuration.Save(); },
            configuration.CombatCVol,       v => { configuration.CombatCVol      = v; configuration.Save(); });

        DrawCombatBlock(
            "d", "Direct Hit", FlyTextKind.DamageDh, Path.Combine(combatDir, "critical.wav"),
            configuration.CombatDEnabled,   v => { configuration.CombatDEnabled  = v; configuration.Save(); },
            configuration.CombatDShowText,  v => { configuration.CombatDShowText = v; configuration.Save(); },
            configuration.CombatDText,      v => { configuration.CombatDText     = v; configuration.Save(); },
            configuration.CombatDSound,     v => { configuration.CombatDSound    = v; configuration.Save(); },
            configuration.CombatDVol,       v => { configuration.CombatDVol      = v; configuration.Save(); });

        DrawCombatBlock(
            "cho", "Critical Heal (own)", FlyTextKind.HealingCrit, Path.Combine(combatDir, "critical.wav"),
            configuration.CombatChoEnabled,   v => { configuration.CombatChoEnabled  = v; configuration.Save(); },
            configuration.CombatChoShowText,  v => { configuration.CombatChoShowText = v; configuration.Save(); },
            configuration.CombatChoText,      v => { configuration.CombatChoText     = v; configuration.Save(); },
            configuration.CombatChoSound,     v => { configuration.CombatChoSound    = v; configuration.Save(); },
            configuration.CombatChoVol,       v => { configuration.CombatChoVol      = v; configuration.Save(); });

        DrawCombatBlock(
            "cht", "Critical Heal (others + fairies)", FlyTextKind.HealingCrit, Path.Combine(combatDir, "heal.mp3"),
            configuration.CombatChtEnabled,   v => { configuration.CombatChtEnabled  = v; configuration.Save(); },
            configuration.CombatChtShowText,  v => { configuration.CombatChtShowText = v; configuration.Save(); },
            configuration.CombatChtText,      v => { configuration.CombatChtText     = v; configuration.Save(); },
            configuration.CombatChtSound,     v => { configuration.CombatChtSound    = v; configuration.Save(); },
            configuration.CombatChtVol,       v => { configuration.CombatChtVol      = v; configuration.Save(); });

        EndSection(10);
    }

    private void DrawCombatBlock(
        string id, string label, FlyTextKind testKind, string defaultSound,
        bool enabled,  Action<bool>   setEnabled,
        bool showText, Action<bool>   setShowText,
        string text,   Action<string> setText,
        string sound,  Action<string> setSound,
        float vol,     Action<float>  setVol,
        bool firstSet = false)
    {
        ImGui.BeginGroup();

        if (firstSet) SectionRow();
        else RowGap(6);

        Common.DimmedTextWrapped(label);

        ImGui.SameLine();
        PushButton();
        if (ImGui.Button($"Test##{id}test"))
            combatHitHandler.TestHit(testKind, showText, text, enabled, sound, defaultSound, vol);
        PopButton();

        // Row 1: [x] Enable custom text  [textbox]
        PushCheckbox();
        var st = showText;
        SectionRow();
        if (ImGui.Checkbox($"Enable custom text##{id}st", ref st)) setShowText(st);
        PopCheckbox();
        ImGui.SameLine(0, 8);
        ImGui.BeginDisabled(!showText);
        ImGui.SetNextItemWidth(220);
        var t = text;
        PushInput();
        if (ImGui.InputText($"##{id}txt", ref t, 64)) setText(t);
        PopInput();

        ImGui.EndDisabled();

        // Row 2: [x] Enable sound
        SectionRow();
        PushCheckbox();
        var en = enabled;
        if (ImGui.Checkbox($"Enable sound##{id}en", ref en)) setEnabled(en);
        PopCheckbox();

        // Rows 3-4: sound settings (dimmed when disabled)
        ImGui.BeginDisabled(!enabled);

        RowGap(2);
        PushButton();
        if (ImGui.Button($"Reset to default##{id}rst")) setSound("");
        PopButton();
        ImGui.SameLine();
        PushButton();
        if (ImGui.Button($"Browse...##{id}brw"))
            fileDialogManager.OpenFileDialog(
                "Select sound file",
                SoundEngine.FileFilter,
                (ok, p) => { if (ok) setSound(p); });
        PopButton();
        ImGui.SameLine();
        Common.DimmedText(string.IsNullOrEmpty(sound) ? "Default sound" : $"Current: {Path.GetFileName(sound)}");

        SectionRow();
        ImGui.SetNextItemWidth(160);
        var v = vol * 100f;
        PushInput();
        // Slider drags 0-100%; no AlwaysClamp so Ctrl+Click can type up to 300%, capped by the setter.
        if (ImGui.SliderFloat($"##{id}vol", ref v, 0f, 100f, "%.0f%%")) setVol(Math.Clamp(v, 0f, 300f) / 100f);
        PopInput();

        ImGui.EndDisabled();

        ImGui.EndGroup();
        Anchor(id, $"Combat hit: {label}", "Audio/visual feedback settings for this hit type");
    }
}
