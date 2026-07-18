using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace HoliestFluffiness.Handlers;

// Hides the MP bar for jobs that don't use MP, in the party list and/or the player's parameter
// widget. Ported from VanillaPlus (MidoriKami). A job counts as "uses MP" if any of its actions
// costs MP; that set is built once from the Action sheet.
public sealed unsafe class HideMpBarsHandler : IDisposable
{
    private const uint GathererCategory = 32;
    private const uint CrafterCategory  = 33;
    private const uint ParamMpNodeId    = 4;

    private readonly Configuration   config;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IClientState    clientState;
    private readonly IObjectTable    objectTable;
    private readonly HashSet<uint>   manaJobs;

    public HideMpBarsHandler(Configuration config, IAddonLifecycle addonLifecycle,
        IClientState clientState, IObjectTable objectTable, IDataManager dataManager)
    {
        this.config         = config;
        this.addonLifecycle = addonLifecycle;
        this.clientState    = clientState;
        this.objectTable    = objectTable;
        manaJobs            = BuildManaUsingJobs(dataManager);

        addonLifecycle.RegisterListener(AddonEvent.PreUpdate,   "_PartyList",       OnPartyListUpdate);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "_PartyList",       OnPartyListFinalize);
        addonLifecycle.RegisterListener(AddonEvent.PreUpdate,   "_ParameterWidget", OnParamWidgetUpdate);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "_ParameterWidget", OnParamWidgetFinalize);
    }

    private static HashSet<uint> BuildManaUsingJobs(IDataManager dataManager)
    {
        var set = new HashSet<uint>();
        foreach (var action in dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>())
        {
            var jobId = action.ClassJob.RowId;
            if (jobId is 0 or uint.MaxValue) continue;
            // PrimaryCostType 3 = MP, 96 = MP (all elements), which together cover the MP users.
            if (action.PrimaryCostType is 3 or 96) set.Add(jobId);
        }
        return set;
    }

    // ── Party list ───────────────────────────────────────────────────────────

    private void OnPartyListUpdate(AddonEvent type, AddonArgs args)
    {
        if (!config.HideMpBarsPartyList || clientState.IsPvP) return;

        var addon = (AddonPartyList*)args.Addon.Address;
        if (addon == null) return;

        var player = objectTable.LocalPlayer;
        if (player == null) return;
        var localNonCombatant = LocalIsNonCombatant();

        var group = GroupManager.Instance();
        if (group != null && group->MainGroup.MemberCount == 0)
        {
            // Solo: the party list shows only the local player in slot 0.
            var keep = manaJobs.Contains(player.ClassJob.RowId) || localNonCombatant;
            SetPartyMpVisible(addon, 0, keep);
            return;
        }

        var hud = AgentHUD.Instance();
        if (hud == null) return;

        var playerId = player.EntityId;
        foreach (ref readonly var member in hud->PartyMembers)
        {
            if (member.EntityId == 0 || member.Object == null) continue;
            // Leave the local player's bar alone while on a crafter/gatherer.
            if (member.EntityId == playerId && localNonCombatant) continue;
            SetPartyMpVisible(addon, member.Index, manaJobs.Contains(member.Object->ClassJob));
        }
    }

    private void OnPartyListFinalize(AddonEvent type, AddonArgs args)
        => ShowAllPartyMp((AddonPartyList*)args.Addon.Address);

    public void ResetPartyList()
        => ShowAllPartyMp((AddonPartyList*)AtkStage.Instance()->RaptureAtkUnitManager->GetAddonByName("_PartyList"));

    private static void SetPartyMpVisible(AddonPartyList* addon, int index, bool visible)
    {
        if (index < 0 || index >= addon->PartyMembers.Length) return;
        var node = addon->PartyMembers[index].MPGaugeBar;
        if (node == null || node->OwnerNode == null) return;
        node->OwnerNode->ToggleVisibility(visible);
    }

    private static void ShowAllPartyMp(AddonPartyList* addon)
    {
        if (addon == null) return;
        for (var i = 0; i < addon->PartyMembers.Length; i++)
            SetPartyMpVisible(addon, i, true);
    }

    // ── Parameter widget (player HP/MP near the hotbars) ─────────────────────

    private void OnParamWidgetUpdate(AddonEvent type, AddonArgs args)
    {
        if (!config.HideMpBarsParamWidget || clientState.IsPvP) return;

        var player = objectTable.LocalPlayer;
        if (player == null) return;

        var keep = manaJobs.Contains(player.ClassJob.RowId) || LocalIsNonCombatant();
        SetParamMpVisible((AtkUnitBase*)args.Addon.Address, keep);
    }

    private void OnParamWidgetFinalize(AddonEvent type, AddonArgs args)
        => SetParamMpVisible((AtkUnitBase*)args.Addon.Address, true);

    public void ResetParamWidget()
        => SetParamMpVisible((AtkUnitBase*)AtkStage.Instance()->RaptureAtkUnitManager->GetAddonByName("_ParameterWidget"), true);

    private static void SetParamMpVisible(AtkUnitBase* addon, bool visible)
    {
        if (addon == null) return;
        var node = addon->GetNodeById(ParamMpNodeId);
        if (node != null) node->ToggleVisibility(visible);
    }

    // Crafters and gatherers legitimately show an MP bar in the UI, so we never touch theirs.
    private bool LocalIsNonCombatant()
    {
        if (objectTable.LocalPlayer?.ClassJob.ValueNullable is not { } job) return false;
        return job.ClassJobCategory.RowId is GathererCategory or CrafterCategory;
    }

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(AddonEvent.PreUpdate,   "_PartyList",       OnPartyListUpdate);
        addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "_PartyList",       OnPartyListFinalize);
        addonLifecycle.UnregisterListener(AddonEvent.PreUpdate,   "_ParameterWidget", OnParamWidgetUpdate);
        addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "_ParameterWidget", OnParamWidgetFinalize);

        ResetPartyList();
        ResetParamWidget();
    }
}
