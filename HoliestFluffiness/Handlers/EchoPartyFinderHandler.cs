using System;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace HoliestFluffiness.Handlers;

public sealed unsafe class EchoPartyFinderHandler : IDisposable
{
    private delegate AtkValue* ReceiveEventDelegate(AgentInterface* self, AtkValue* returnValue,
        AtkValue* values, uint valueCount, ulong eventKind);

    private readonly Hook<ReceiveEventDelegate>? receiveEventHook;
    private readonly Configuration   config;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IDataManager    dataManager;
    private readonly IClientState    clientState;
    private readonly IChatGui        chatGui;
    private readonly IPluginLog      log;

    private bool    _listenerActive;
    private string? _description;
    private string? _leader;
    private uint    _targetTerritoryId;

    public EchoPartyFinderHandler(Configuration config, IGameInteropProvider gameInterop,
        IAddonLifecycle addonLifecycle, IDataManager dataManager, IClientState clientState,
        IChatGui chatGui, IPluginLog log)
    {
        this.config         = config;
        this.addonLifecycle = addonLifecycle;
        this.dataManager    = dataManager;
        this.clientState    = clientState;
        this.chatGui        = chatGui;
        this.log            = log;

        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroupDetail", OnDetailFinalize);
        clientState.TerritoryChanged += OnTerritoryChanged;

        try
        {
            var agentPtr = AgentModule.Instance()->GetAgentByInternalId(AgentId.LookingForGroup);
            if (agentPtr != null)
            {
                receiveEventHook = gameInterop.HookFromAddress<ReceiveEventDelegate>(
                    (nint)agentPtr->VirtualTable->ReceiveEvent, ReceiveEventDetour);
                receiveEventHook.Enable();
            }
        }
        catch (Exception ex) { log.Warning(ex, "[HF] EchoPartyFinder: ReceiveEvent hook failed."); }
    }

    private AtkValue* ReceiveEventDetour(AgentInterface* self, AtkValue* returnValue,
        AtkValue* values, uint valueCount, ulong eventKind)
    {
        var result = receiveEventHook!.Original(self, returnValue, values, valueCount, eventKind);
        try
        {
            // sender 6, arg count 1, arg[0]=0 → player clicked Join
            if (eventKind == 6 && valueCount == 1 && values[0].Int == 0)
                _listenerActive = true;
        }
        catch (Exception ex) { log.Debug(ex, "[HF] EchoPartyFinder: ReceiveEvent detour error."); }
        return result;
    }

    private void OnDetailFinalize(AddonEvent type, AddonArgs args)
    {
        if (!config.EchoPartyFinderEnabled || !_listenerActive) return;
        _listenerActive = false;
        try
        {
            var addon = (AddonLookingForGroupDetail*)args.Addon.Address;
            if (addon == null) return;

            _description = addon->DescriptionString.ToString();
            _leader      = addon->PartyLeaderTextNode->NodeText.ToString();

            var dutyName = addon->DutyNameTextNode->NodeText.ToString();
            var cfc = dataManager.GetExcelSheet<ContentFinderCondition>()?
                .FirstOrDefault(e => string.Equals(e.Name.ExtractText(), dutyName, StringComparison.OrdinalIgnoreCase));
            _targetTerritoryId = cfc?.TerritoryType.RowId ?? 0;

            PrintListing();
        }
        catch (Exception ex) { log.Debug(ex, "[HF] EchoPartyFinder: detail finalize read failed."); }
    }

    private void OnTerritoryChanged(uint territory)
    {
        if (!config.EchoPartyFinderEnabled || _targetTerritoryId == 0) return;
        if (territory != _targetTerritoryId) return;
        _targetTerritoryId = 0;
        PrintListing();
    }

    private void PrintListing()
    {
        if (_description == null || _leader == null) return;
        chatGui.Print(new SeStringBuilder()
            .AddUiForeground("[Party Finder] ", 62)
            .AddUiForeground($"[{_leader}] ", 45)
            .AddText(_description)
            .Build());
    }

    public void Dispose()
    {
        clientState.TerritoryChanged -= OnTerritoryChanged;
        addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "LookingForGroupDetail", OnDetailFinalize);
        receiveEventHook?.Disable();
        receiveEventHook?.Dispose();
    }
}
