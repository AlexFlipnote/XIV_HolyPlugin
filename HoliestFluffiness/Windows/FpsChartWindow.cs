using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using HoliestFluffiness.Handlers;

namespace HoliestFluffiness.Windows;

public sealed class FpsChartWindow : Window, IDisposable
{
    private readonly ServerInfoHandler handler;

    // ServerInfoHandler replaces FpsChartData's reference whenever new data lands, so
    // reference equality tells us whether these stats need recomputing this frame.
    private float[]? cachedData;
    private int      cachedAvg, cachedMin, cachedMax;

    public FpsChartWindow(ServerInfoHandler handler) : base("FPS History##HFFpsChart")
    {
        this.handler  = handler;
        Size          = new Vector2(340, 180);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags         = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        if (Theme.UseCustom)
        {
            ImGui.PushStyleColor(ImGuiCol.WindowBg,          Theme.Fade(Theme.ColSecondary));
            ImGui.PushStyleColor(ImGuiCol.Text,              Theme.ColWhite);
            ImGui.PushStyleColor(ImGuiCol.TitleBg,           Theme.ColHighlight);
            ImGui.PushStyleColor(ImGuiCol.TitleBgActive,     Theme.ColHighlight);
            ImGui.PushStyleColor(ImGuiCol.FrameBg,           Theme.ColPrimary);
            ImGui.PushStyleColor(ImGuiCol.ResizeGrip,        Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, Theme.ColGoldMid);
            ImGui.PushStyleColor(ImGuiCol.ResizeGripActive,  Theme.ColGold);
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.Fade(ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg]));
        }
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(Theme.UseCustom ? 8 : 1);
    }

    public override void Draw()
    {
        var data = handler.FpsChartData;
        if (data.Length == 0)
        {
            Common.DimmedText("Waiting for FPS data...");
            return;
        }

        if (!ReferenceEquals(cachedData, data))
        {
            cachedData = data;
            var successCount = 0;
            var sum = 0f;
            var sampleMin = float.MaxValue;
            var sampleMax = float.MinValue;
            foreach (var v in data)
            {
                if (v <= 0) continue;
                successCount++;
                sum += v;
                if (v < sampleMin) sampleMin = v;
                if (v > sampleMax) sampleMax = v;
            }
            cachedAvg = successCount > 0 ? (int)(sum / successCount) : 0;
            cachedMin = successCount > 0 ? (int)sampleMin : 0;
            cachedMax = successCount > 0 ? (int)sampleMax : 0;
        }

        Common.GoldText($"avg {cachedAvg} fps");

        ImGui.SameLine();
        Common.DimmedText($"  min {cachedMin}  max {cachedMax}");

        var plotSize = ImGui.GetContentRegionAvail();
        var scaleMax = cachedMax > 0 ? cachedMax * 1.15f : 60f;

        ImGui.PushStyleColor(ImGuiCol.PlotLines, Theme.ColGold);
        ImGui.PlotLines("##fps", data, data.Length, "", 0f, scaleMax, plotSize);
        ImGui.PopStyleColor(1);
    }
}
