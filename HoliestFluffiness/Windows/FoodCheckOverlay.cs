using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using HoliestFluffiness.Handlers;

namespace HoliestFluffiness.Windows;

public class FoodCheckOverlay : HfOverlayWindow, IDisposable
{
    private readonly Configuration config;
    private readonly FoodCheckHandler handler;
    private readonly IGameGui gameGui;

    private static readonly Vector2 RowInset   = new(5f, 5f);
    private static readonly Vector2 RowDeflate = new(8f, 10f);
    private const           float   RowRounding = 10f;

    public FoodCheckOverlay(Configuration config, FoodCheckHandler handler, IGameGui gameGui)
        : base("##HFFoodCheckOverlay")
    {
        this.config  = config;
        this.handler = handler;
        this.gameGui = gameGui;
    }

    public void Dispose() { }

    public override void PreOpenCheck()
    {
        IsOpen = config.FoodCheckHighlight && handler.IsValid;
    }

    public override unsafe void Draw()
    {
        var pParty   = (AddonPartyList*)gameGui.GetAddonByName("_PartyList").Address;
        var drawList = ImGui.GetWindowDrawList();

        foreach (var entry in handler.GetEntries())
        {
            if (entry.HudSlot is < 0 or > 7) continue;
            var color = entry.RemainingSeconds == 0 ? Theme.ColRed : Theme.ColGold;
            var label = entry.RemainingSeconds == 0 ? "No food!" : $"Food: {entry.RemainingSeconds / 60}m left";
            DrawMember(entry, pParty, drawList, color, label);
        }
    }

    private static unsafe void DrawMember(FoodCheckEntry entry, AddonPartyList* pList,
        ImDrawListPtr drawList, Vector4 color, string label)
    {
        if ((nint)pList == nint.Zero || !Common.IsAddonVisible(&pList->AtkUnitBase)) return;

        var colNode = (AtkResNode*)pList->PartyMembers[entry.HudSlot].TargetGlow;
        if ((nint)colNode == nint.Zero) return;

        var viewport = ImGui.GetMainViewport().Pos;
        var scale    = pList->Scale;
        var rectMin  = Common.GetNodePosition(colNode) + RowInset   * scale + viewport;
        var rectSize = (new Vector2(colNode->Width, colNode->Height) - RowDeflate) * scale;
        var rectMax  = rectMin + rectSize;

        Common.DrawHighlightRect(drawList, rectMin, rectMax, RowRounding * scale, color, label, pulse: true, scale);
    }
}
