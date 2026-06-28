using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace HoliestFluffiness.Handlers;

public sealed unsafe class CastBarHandler : IDisposable
{
    // Matches ActionManager.Delegates.OpenCastBar
    private delegate void OpenCastBarDelegate(ActionManager* self, BattleChara* character, ActionType actionType,
        uint actionId, uint spellId, uint extraParam, float castTimeElapsed, float castTimeTotal);

    private delegate bool TeleportDelegate(Telepo* self, uint aetheryteId, byte subIndex);

    private readonly Hook<OpenCastBarDelegate>? castBarOpenHook;
    private readonly Hook<TeleportDelegate>?    teleportHook;

    private readonly Configuration   config;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IDataManager    dataManager;
    private readonly IClientState    clientState;
    private readonly IPluginLog      log;

    private bool         _isTeleportCast;
    private TeleportInfo _teleportInfo;
    private bool         _hasTeleportInfo;

    public CastBarHandler(Configuration config, ISigScanner sigScanner, IGameInteropProvider gameInterop,
        IAddonLifecycle addonLifecycle, IDataManager dataManager, IClientState clientState, IPluginLog log)
    {
        this.config         = config;
        this.addonLifecycle = addonLifecycle;
        this.dataManager    = dataManager;
        this.clientState    = clientState;
        this.log            = log;

        try
        {
            teleportHook = gameInterop.HookFromAddress<TeleportDelegate>(
                (nint)Telepo.MemberFunctionPointers.Teleport, TeleportDetour);
            teleportHook.Enable();
        }
        catch (Exception ex) { log.Warning(ex, "[HF] CastBar: Teleport hook failed."); }

        try
        {
            castBarOpenHook = gameInterop.HookFromAddress<OpenCastBarDelegate>(
                (nint)ActionManager.MemberFunctionPointers.OpenCastBar, OpenCastBarDetour);
            castBarOpenHook.Enable();
        }
        catch (Exception ex) { log.Warning(ex, "[HF] CastBar: OpenCastBar hook failed."); }

        addonLifecycle.RegisterListener(AddonEvent.PreRefresh, "_CastBar", OnCastBarPreRefresh);
        clientState.TerritoryChanged += OnTerritoryChanged;
    }

    private bool TeleportDetour(Telepo* self, uint aetheryteId, byte subIndex)
    {
        _hasTeleportInfo = false;
        foreach (var info in self->TeleportList)
        {
            if (info.AetheryteId == aetheryteId && info.SubIndex == subIndex)
            {
                _teleportInfo    = info;
                _hasTeleportInfo = true;
                break;
            }
        }
        return teleportHook!.Original(self, aetheryteId, subIndex);
    }

    private void OpenCastBarDetour(ActionManager* self, BattleChara* character, ActionType actionType,
        uint actionId, uint spellId, uint extraParam, float castTimeElapsed, float castTimeTotal)
    {
        _isTeleportCast = actionType == ActionType.Action && actionId == 5;
        castBarOpenHook!.Original(self, character, actionType, actionId, spellId, extraParam, castTimeElapsed, castTimeTotal);
    }

    private void OnCastBarPreRefresh(AddonEvent type, AddonArgs args)
    {
        if (!config.CastBarAetheryteEnabled || !_isTeleportCast || !_hasTeleportInfo)
        {
            ClearState();
            return;
        }

        var name = ResolveAetheryteName(_teleportInfo);
        if (!string.IsNullOrEmpty(name))
            AtkStage.Instance()->GetStringArrayData(StringArrayType.CastBar)->SetValue(0, name, false, true, false);

        ClearState();
    }

    private void ClearState()
    {
        _isTeleportCast  = false;
        _hasTeleportInfo = false;
    }

    private string? ResolveAetheryteName(TeleportInfo info)
    {
        try
        {
            var row = dataManager.GetExcelSheet<Aetheryte>()?.GetRowOrDefault(info.AetheryteId);
            if (row == null) return null;

            if (info.IsApartment)
                return dataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>()
                    ?.GetRowOrDefault(8518)?.Text.ExtractText();

            if (info.IsSharedHouse)
            {
                var template = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>()
                    ?.GetRowOrDefault(8519)?.Text.ExtractText() ?? "";
                return template
                    .Replace("%1", (info.Ward + 1).ToString())
                    .Replace("%2", (info.Plot + 1).ToString());
            }

            return row.Value.PlaceName.Value.Name.ExtractText();
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[HF] CastBar: failed to resolve aetheryte name.");
            return null;
        }
    }

    private void OnTerritoryChanged(uint id) => ClearState();

    public void Dispose()
    {
        clientState.TerritoryChanged -= OnTerritoryChanged;
        addonLifecycle.UnregisterListener(AddonEvent.PreRefresh, "_CastBar", OnCastBarPreRefresh);
        teleportHook?.Disable();
        teleportHook?.Dispose();
        castBarOpenHook?.Disable();
        castBarOpenHook?.Dispose();
    }
}
