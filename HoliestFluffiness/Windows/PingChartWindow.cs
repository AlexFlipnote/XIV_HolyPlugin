using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using HoliestFluffiness.Handlers;

namespace HoliestFluffiness.Windows;

public sealed class PingChartWindow : Window, IDisposable
{
    private readonly ServerInfoHandler handler;

    // ServerInfoHandler swaps the PingChartData reference on new data, so reference equality
    // tells us whether these stats need recomputing.
    private float[]? cachedData;
    private int      cachedTimeouts;
    private int      cachedAvg, cachedMin, cachedMax;

    public PingChartWindow(ServerInfoHandler handler) : base("Ping History##HFPingChart")
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
        var data = handler.PingChartData;
        if (data.Length == 0)
        {
            Common.DimmedText("Waiting for ping data...");
            return;
        }

        if (!ReferenceEquals(cachedData, data))
        {
            cachedData = data;
            var (samples, sAvg, sMin, sMax) = Common.ComputeSampleStats(data);
            cachedTimeouts = data.Length - samples;
            cachedAvg      = sAvg;
            cachedMin      = sMin;
            cachedMax      = sMax;
        }
        var avg      = cachedAvg;
        var min      = cachedMin;
        var max      = cachedMax;
        var timeouts = cachedTimeouts;

        Common.GoldText($"avg {avg}ms");

        ImGui.SameLine();
        Common.DimmedText($"  min {min}ms  max {max}ms");

        if (timeouts > 0)
        {
            ImGui.SameLine();
            Common.RedText($"  {timeouts} TO");
        }

        var plotSize = ImGui.GetContentRegionAvail();
        var scaleMax = max > 0 ? max * 1.3f : 200f;

        ImGui.PushStyleColor(ImGuiCol.PlotLines, Theme.ColGold);
        ImGui.PlotLines("##ping", data, data.Length, "", 0f, scaleMax, plotSize);
        ImGui.PopStyleColor(1);
    }
}
