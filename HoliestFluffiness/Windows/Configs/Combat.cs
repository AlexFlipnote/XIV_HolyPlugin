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

    private void DrawCombatSection()
    {
        BeginSection("Combat");

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColWhiteDim);
        ImGui.TextWrapped(
            "Adds audio and visual feedback to combat events like critical hits and direct hits, built to be expanded over time. " +
            "The default sounds are heavily inspired by TF2, a game near and dear to my heart."
        );
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 12));

        var combatDir = Path.Combine(pluginInterface.AssemblyLocation.DirectoryName!, "Sounds", "Combat");

        DrawCombatBlock("dc", "Direct Critical Hit", FlyTextKind.DamageCritDh, Path.Combine(combatDir, "direct_critical.wav"),
            configuration.CombatDcEnabled,   v => { configuration.CombatDcEnabled  = v; configuration.Save(); },
            configuration.CombatDcShowText,  v => { configuration.CombatDcShowText = v; configuration.Save(); },
            configuration.CombatDcText,      v => { configuration.CombatDcText     = v; configuration.Save(); },
            configuration.CombatDcSound,     v => { configuration.CombatDcSound    = v; configuration.Save(); },
            configuration.CombatDcVol,       v => { configuration.CombatDcVol      = v; configuration.Save(); });

        ImGui.Dummy(new Vector2(0, 10));

        DrawCombatBlock("c", "Critical Hit", FlyTextKind.DamageCrit, Path.Combine(combatDir, "critical.wav"),
            configuration.CombatCEnabled,   v => { configuration.CombatCEnabled  = v; configuration.Save(); },
            configuration.CombatCShowText,  v => { configuration.CombatCShowText = v; configuration.Save(); },
            configuration.CombatCText,      v => { configuration.CombatCText     = v; configuration.Save(); },
            configuration.CombatCSound,     v => { configuration.CombatCSound    = v; configuration.Save(); },
            configuration.CombatCVol,       v => { configuration.CombatCVol      = v; configuration.Save(); });

        ImGui.Dummy(new Vector2(0, 10));

        DrawCombatBlock("d", "Direct Hit", FlyTextKind.DamageDh, Path.Combine(combatDir, "critical.wav"),
            configuration.CombatDEnabled,   v => { configuration.CombatDEnabled  = v; configuration.Save(); },
            configuration.CombatDShowText,  v => { configuration.CombatDShowText = v; configuration.Save(); },
            configuration.CombatDText,      v => { configuration.CombatDText     = v; configuration.Save(); },
            configuration.CombatDSound,     v => { configuration.CombatDSound    = v; configuration.Save(); },
            configuration.CombatDVol,       v => { configuration.CombatDVol      = v; configuration.Save(); });

        ImGui.Dummy(new Vector2(0, 10));

        DrawCombatBlock("cho", "Critical Heal (own)", FlyTextKind.HealingCrit, Path.Combine(combatDir, "critical.wav"),
            configuration.CombatChoEnabled,   v => { configuration.CombatChoEnabled  = v; configuration.Save(); },
            configuration.CombatChoShowText,  v => { configuration.CombatChoShowText = v; configuration.Save(); },
            configuration.CombatChoText,      v => { configuration.CombatChoText     = v; configuration.Save(); },
            configuration.CombatChoSound,     v => { configuration.CombatChoSound    = v; configuration.Save(); },
            configuration.CombatChoVol,       v => { configuration.CombatChoVol      = v; configuration.Save(); });

        ImGui.Dummy(new Vector2(0, 10));

        DrawCombatBlock("cht", "Critical Heal (others + fairies)", FlyTextKind.HealingCrit, Path.Combine(combatDir, "heal.mp3"),
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
        float vol,     Action<float>  setVol)
    {
        SubsectionLabel(label);
        ImGui.Dummy(new Vector2(0, 3));

        // Row 1: [x] Enable custom text  [textbox]
        SectionRow();
        PushCheckbox();
        var st = showText;
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
        ImGui.Dummy(new Vector2(0, 2));
        SectionRow();
        PushCheckbox();
        var en = enabled;
        if (ImGui.Checkbox($"Enable sound##{id}en", ref en)) setEnabled(en);
        PopCheckbox();

        // Rows 3-4: sound settings (dimmed when disabled)
        ImGui.BeginDisabled(!enabled);

        ImGui.Dummy(new Vector2(0, 2));
        SectionRow();
        PushButton();
        if (ImGui.Button($"Reset to default##{id}rst")) setSound("");
        PopButton();
        ImGui.SameLine();
        PushButton();
        if (ImGui.Button($"Browse...##{id}brw"))
            fileDialogManager.OpenFileDialog("Select sound file", ".wav,.mp3,.ogg,.aif,.aiff,.wma",
                (ok, p) => { if (ok) setSound(p); });
        PopButton();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColWhiteDim);
        ImGui.TextUnformatted(string.IsNullOrEmpty(sound) ? "Default sound" : $"Current: {Path.GetFileName(sound)}");
        ImGui.PopStyleColor();

        ImGui.Dummy(new Vector2(0, 2));
        SectionRow();
        ImGui.SetNextItemWidth(160);
        var v = vol * 100f;
        PushInput();
        if (ImGui.SliderFloat($"##{id}vol", ref v, 0f, 100f, "%.0f%%")) setVol(v / 100f);
        PopInput();
        ImGui.SameLine();
        PushButton();
        if (ImGui.Button($"Test##{id}test"))
            combatHitHandler.TestHit(testKind, showText, text, sound, defaultSound, vol);
        PopButton();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColWhiteDim);
        ImGui.TextUnformatted("Test the sound + text");
        ImGui.PopStyleColor();

        ImGui.EndDisabled();
    }
}
