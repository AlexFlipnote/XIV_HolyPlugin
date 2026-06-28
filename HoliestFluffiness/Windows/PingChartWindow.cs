using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using HoliestFluffiness.Handlers;

namespace HoliestFluffiness.Windows;

public sealed class PingChartWindow : Window, IDisposable
{
    private readonly ServerInfoHandler handler;
    private readonly Configuration configuration;

    public PingChartWindow(ServerInfoHandler handler, Configuration configuration) : base("Ping History##HFPingChart")
    {
        this.handler       = handler;
        this.configuration = configuration;
        Size          = new Vector2(340, 180);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags         = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg,          Theme.ColSecondary);
        ImGui.PushStyleColor(ImGuiCol.Text,              Theme.ColWhite);
        ImGui.PushStyleColor(ImGuiCol.TitleBg,           Theme.ColHighlight);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive,     Theme.ColHighlight);
        ImGui.PushStyleColor(ImGuiCol.FrameBg,           Theme.ColPrimary);
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip,        Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, Theme.ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive,  Theme.ColGold);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(8);
    }

    public override void Draw()
    {
        var data = handler.PingChartData;
        if (data.Length == 0)
        {
            Common.DimmedText("Waiting for ping data...");
            return;
        }

        var successes = data.Where(v => v > 0).ToArray();
        var timeouts  = data.Length - successes.Length;
        var avg       = successes.Length > 0 ? (int)successes.Average() : 0;
        var min       = successes.Length > 0 ? (int)successes.Min()     : 0;
        var max       = successes.Length > 0 ? (int)successes.Max()     : 0;

        Common.GoldText($"avg {avg}ms");

        ImGui.SameLine();
        Common.DimmedText($"  min {min}ms  max {max}ms");

        if (timeouts > 0)
        {
            ImGui.SameLine();
            Common.RedText($"  {timeouts} TO");
        }

        var plotSize = ImGui.GetContentRegionAvail();
        var scaleMax = configuration.PingChartScaleMax > 0 ? (float)configuration.PingChartScaleMax : (max > 0 ? max * 1.3f : 200f);

        ImGui.PushStyleColor(ImGuiCol.PlotLines, Theme.ColGold);
        ImGui.PlotLines("##ping", data, data.Length, "", 0f, scaleMax, plotSize);
        ImGui.PopStyleColor(1);
    }
}
