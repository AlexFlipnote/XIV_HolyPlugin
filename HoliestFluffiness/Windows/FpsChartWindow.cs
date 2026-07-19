using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using HoliestFluffiness.Handlers;

namespace HoliestFluffiness.Windows;

public sealed class FpsChartWindow : Window, IDisposable
{
    private readonly ServerInfoHandler handler;

    // ServerInfoHandler swaps the FpsChartData reference on new data, so reference equality
    // tells us whether these stats need recomputing.
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

    public override void PreDraw() => Common.PushChartWindowTheme();

    public override void PostDraw() => Common.PopChartWindowTheme();

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
            var (_, sAvg, sMin, sMax) = Common.ComputeSampleStats(data);
            cachedAvg = sAvg;
            cachedMin = sMin;
            cachedMax = sMax;
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
