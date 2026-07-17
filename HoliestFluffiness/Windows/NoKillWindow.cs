using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace HoliestFluffiness.Windows;

public class NoKillWindow : Window
{

    private int interceptCount;
    private string? autoLoginName;
    private string? autoLoginWorld;
    private IReadOnlyList<DateTime> interceptLog = [];

    public NoKillWindow()
        : base("No-kill##NoKillPopup",
               ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar |
               ImGuiWindowFlags.NoSavedSettings  | ImGuiWindowFlags.NoCollapse)
    {
        RespectCloseHotkey = true;
    }

    public void Show(int count, string? name, string? world, IReadOnlyList<DateTime> log)
    {
        interceptCount  = count;
        autoLoginName   = name;
        autoLoginWorld  = world;
        interceptLog    = log;
        IsOpen = true;
    }

    public override void PreDraw()
    {
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (Theme.UseCustom)
        {
            ImGui.PushStyleColor(ImGuiCol.Text,          Theme.ColWhite);
            ImGui.PushStyleColor(ImGuiCol.WindowBg,      Theme.Fade(Theme.ColSecondary));
            ImGui.PushStyleColor(ImGuiCol.TitleBg,       Theme.ColHighlight);
            ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Theme.ColHighlight);
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.Fade(ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg]));
        }
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 12));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(Theme.UseCustom ? 4 : 1);
        ImGui.PopStyleVar(2);
    }

    public override void Draw()
    {
        Common.GoldText("A lobby error was intercepted.");

        Common.DimmedText("The error was converted to a connection lost.");
        Common.DimmedText("Auto-dismiss + reconnect will be attempted.");

        ImGui.Dummy(new Vector2(0, 4));

        Common.DimmedText($"Intercepted this session: {interceptCount}");

        if (interceptLog.Count > 0)
        {
            var start = Math.Max(0, interceptLog.Count - 5);
            for (int i = interceptLog.Count - 1; i >= start; i--)
                Common.DimmedText($"- {interceptLog[i]:HH:mm:ss}");
        }

        var charLabel = (autoLoginName != null && autoLoginWorld != null)
            ? $"{autoLoginName} @ {autoLoginWorld}"
            : "none";
        Common.DimmedText($"Reconnect target: {charLabel}");

        ImGui.Dummy(new Vector2(0, 4));

        const float btnW = 80f;
        Common.CenterCursorForWidth(btnW);
        Common.PushGoldButton();
        if (ImGui.Button("OK", new Vector2(btnW, 0)))
            IsOpen = false;
        Common.PopGoldButton();
    }
}
