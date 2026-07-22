using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace HoliestFluffiness.Handlers;

public sealed unsafe class LoginEnhancementHandler : IDisposable
{
    private delegate bool UpdateCharaSelectDelegate(AgentLobby* self, sbyte index, bool a2);
    private delegate void OpenLoginWaitDelegate    (AgentLobby* self, int position);

    private readonly Hook<UpdateCharaSelectDelegate>? charaSelectHook;
    private readonly Hook<OpenLoginWaitDelegate>?     loginWaitHook;

    private readonly Configuration   config;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IDataManager    dataManager;
    private readonly IPluginLog      log;

    private ushort _currentTerritoryType;

    public LoginEnhancementHandler(Configuration config, IGameInteropProvider gameInterop,
        IAddonLifecycle addonLifecycle, IDataManager dataManager, IPluginLog log)
    {
        this.config         = config;
        this.addonLifecycle = addonLifecycle;
        this.dataManager    = dataManager;
        this.log            = log;

        addonLifecycle.RegisterListener(AddonEvent.PostSetup, "Logo", OnLogoSetup);

        charaSelectHook = Common.TryCreateHook<UpdateCharaSelectDelegate>(
            (nint)AgentLobby.MemberFunctionPointers.UpdateCharaSelectDisplay, CharaSelectDetour, gameInterop, log,
            "[HF] LoginEnhancement: UpdateCharaSelectDisplay hook failed.");

        loginWaitHook = Common.TryCreateHook<OpenLoginWaitDelegate>(
            (nint)AgentLobby.MemberFunctionPointers.OpenLoginWaitDialog, LoginWaitDetour, gameInterop, log,
            "[HF] LoginEnhancement: OpenLoginWaitDialog hook failed.");
    }

    // ── Skip logo ─────────────────────────────────────────────────────────────

    private void OnLogoSetup(AddonEvent type, AddonArgs args)
    {
        if (!config.LoginSkipLogo) return;
        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null) return;
            var val = new AtkValue { Type = AtkValueType.Int, Int = 0 };
            addon->FireCallback(1, &val, true);
            addon->Hide(false, false, 1);
        }
        catch (Exception ex) { log.Warning(ex, "[HF] LoginEnhancement: skip logo failed."); }
    }

    // ── Track selected character territory (for preload) ──────────────────────

    private bool CharaSelectDetour(AgentLobby* self, sbyte index, bool a2)
    {
        var retVal = charaSelectHook!.Original(self, index, a2);
        try
        {
            if (index < 0) { _currentTerritoryType = 0; return retVal; }

            var adjustedIndex = index >= 100 ? (sbyte)(index - 100) : index;
            var entry         = self->LobbyData.GetCharacterEntryByIndex(0, self->WorldIndex, adjustedIndex);
            if (entry != null)
                _currentTerritoryType = entry->ClientSelectData.TerritoryType;
        }
        catch (Exception ex) { log.Debug(ex, "[HF] LoginEnhancement: chara select detour failed."); }
        return retVal;
    }

    // ── Preload territory ─────────────────────────────────────────────────────

    private void LoginWaitDetour(AgentLobby* self, int position)
    {
        loginWaitHook!.Original(self, position);
        if (!config.PreloadTerritory) return;
        try { PreloadCurrentTerritory(); }
        catch (Exception ex) { log.Debug(ex, "[HF] LoginEnhancement: preload territory failed."); }
    }

    private void PreloadCurrentTerritory()
    {
        if (_currentTerritoryType == 0) return;

        var territoryId = (uint)_currentTerritoryType;
        if (HousingDistricts.InteriorToOutdoor.TryGetValue(territoryId, out var outdoor))
            territoryId = outdoor;

        var ttRow = dataManager.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(territoryId);
        if (ttRow == null) return;

        var bg = ttRow.Value.Bg.ToString();
        if (string.IsNullOrEmpty(bg)) return;

        LayoutWorld.UnloadPrefetchLayout();
        LayoutWorld.Instance()->LoadPrefetchLayout(2, bg, 40, 0, (ushort)territoryId, null, 0);
    }

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "Logo", OnLogoSetup);
        charaSelectHook?.Dispose();
        loginWaitHook?.Dispose();
    }
}
