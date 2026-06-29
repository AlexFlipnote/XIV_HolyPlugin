using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using HoliestFluffiness.Handlers;
using Lumina.Data.Files;

namespace HoliestFluffiness.Windows;

public class ReadyCheckOverlay : HfOverlayWindow, IDisposable
{
    private readonly Configuration config;
    private readonly ReadyCheckHandler handler;
    private readonly IGameGui gameGui;
    private readonly IDalamudTextureWrap readyCheckTex;
    private readonly IDalamudTextureWrap notPresentTex;

    public ReadyCheckOverlay(Configuration config, ReadyCheckHandler handler, IGameGui gameGui, ITextureProvider textureProvider, IDataManager dataManager)
        : base("##HFReadyCheckOverlay")
    {
        this.config = config; this.handler = handler; this.gameGui = gameGui;
        readyCheckTex = textureProvider.CreateFromTexFile(dataManager.GetFile<TexFile>("ui/uld/ReadyCheck_hr1.tex")!);
        notPresentTex = textureProvider.GetFromGameIcon(61504).RentAsync().Result;
    }

    public void Dispose()
    {
        readyCheckTex.Dispose();
        notPresentTex.Dispose(); // rented via RentAsync, must be returned
    }

    public override void PreOpenCheck()
    {
        IsOpen = config.ReadyCheckDrawOverlay && handler.IsValid;
    }

    public override unsafe void Draw()
    {
        var pParty     = (AddonPartyList*)gameGui.GetAddonByName("_PartyList").Address;
        var pAlliance1 = (AddonAllianceListX*)gameGui.GetAddonByName("_AllianceList1").Address;
        var pAlliance2 = (AddonAllianceListX*)gameGui.GetAddonByName("_AllianceList2").Address;
        var pCrossWorld = (AddonAlliance48*)gameGui.GetAddonByName("Alliance48").Address;
        var drawList = ImGui.GetWindowDrawList();

        foreach (var e in handler.GetData())
        {
            var idx = handler.GetHUDIndex(e.ContentId, e.EntityId);
            if (idx == null) continue;
            var i = idx.Value;
            switch (i.GroupNumber)
            {
                case 0: DrawPartyList(i.PartyMemberIndex, e.ReadyState, pParty, drawList); break;
                case 1:
                    if (i.CrossWorld) DrawCrossWorld(i.GroupNumber, i.PartyMemberIndex, e.ReadyState, pCrossWorld, drawList);
                    else              DrawAlliance(i.PartyMemberIndex, e.ReadyState, pAlliance1, drawList);
                    break;
                case 2:
                    if (i.CrossWorld) DrawCrossWorld(i.GroupNumber, i.PartyMemberIndex, e.ReadyState, pCrossWorld, drawList);
                    else              DrawAlliance(i.PartyMemberIndex, e.ReadyState, pAlliance2, drawList);
                    break;
                default:
                    if (i.CrossWorld) DrawCrossWorld(i.GroupNumber, i.PartyMemberIndex, e.ReadyState, pCrossWorld, drawList);
                    break;
            }
        }
    }

    private unsafe void DrawPartyList(int idx, ReadyCheckStatus state, AddonPartyList* pList, ImDrawListPtr drawList)
    {
        if (idx is < 0 or > 7 || (nint)pList == nint.Zero || !Common.IsAddonVisible(&pList->AtkUnitBase)) return;
        var m    = pList->PartyMembers[idx];
        var node = m.PartyMemberComponent->OwnerNode;
        var icon = m.ClassJobIcon;
        var size = new Vector2(icon->Width / 1.5f, icon->Height / 1.5f) * pList->Scale;
        var pos  = new Vector2(
            pList->X + node->AtkResNode.X * pList->Scale + icon->X * pList->Scale + icon->Width  * pList->Scale / 2,
            pList->Y + pList->PartyListAtkResNode->Y + node->AtkResNode.Y * pList->Scale + icon->Y * pList->Scale + icon->Height * pList->Scale / 2)
            + new Vector2(-7, -5) * pList->Scale;
        DrawIcon(state, pos, size, drawList);
    }

    private unsafe void DrawAlliance(int idx, ReadyCheckStatus state, AddonAllianceListX* pList, ImDrawListPtr drawList)
    {
        if (idx is < 0 or > 7 || (nint)pList == nint.Zero || !Common.IsAddonVisible(&pList->AtkUnitBase)) return;
        var m    = pList->AllianceMembers[idx];
        var node = m.ComponentBase->OwnerNode;
        var icon = m.ComponentBase->GetImageNodeById(9)->GetAsAtkImageNode();
        var size = new Vector2(icon->Width / 3.0f, icon->Height / 3.0f) * pList->Scale;
        var pos  = new Vector2(
            pList->X + node->AtkResNode.X * pList->Scale + icon->X * pList->Scale + icon->Width  * pList->Scale / 2,
            pList->Y + node->AtkResNode.Y * pList->Scale + icon->Y * pList->Scale + icon->Height * pList->Scale / 2);
        DrawIcon(state, pos, size, drawList);
    }

    private unsafe void DrawCrossWorld(int groupIdx, int memberIdx, ReadyCheckStatus state, AddonAlliance48* pList, ImDrawListPtr drawList)
    {
        if (groupIdx is < 1 or > 5 || memberIdx is < 0 or > 7 || (nint)pList == nint.Zero || !Common.IsAddonVisible(&pList->AtkUnitBase)) return;
        var alliance = pList->Alliances[groupIdx - 1];
        var aNode    = alliance.ComponentBase->OwnerNode;
        var member   = alliance.Members[memberIdx];
        var mNode    = member.AtkComponentBase->OwnerNode;
        var icon     = member.AtkComponentBase->GetImageNodeById(2)->GetAsAtkImageNode();
        var size = new Vector2(icon->Width / 2.0f, icon->Height / 2.0f) * pList->Scale;
        var pos  = new Vector2(
            pList->X + aNode->AtkResNode.X * pList->Scale + mNode->AtkResNode.X * pList->Scale + icon->X * pList->Scale + icon->Width  * pList->Scale / 2,
            pList->Y + aNode->AtkResNode.Y * pList->Scale + mNode->AtkResNode.Y * pList->Scale + icon->Y * pList->Scale + icon->Height * pList->Scale / 2);
        DrawIcon(state, pos, size, drawList);
    }

    private void DrawIcon(ReadyCheckStatus state, Vector2 pos, Vector2 size, ImDrawListPtr drawList)
    {
        if (state == ReadyCheckStatus.NotReady)
            drawList.AddImage(readyCheckTex.Handle, pos, pos + size, new Vector2(0.5f, 0f), new Vector2(1f));
        else if (state == ReadyCheckStatus.Ready)
            drawList.AddImage(readyCheckTex.Handle, pos, pos + size, Vector2.Zero, new Vector2(0.5f, 1f));
        else if (state == ReadyCheckStatus.MemberNotPresent)
            drawList.AddImage(notPresentTex.Handle, pos, pos + size);
    }

}
