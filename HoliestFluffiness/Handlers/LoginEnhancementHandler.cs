using System;
using System.Collections.Generic;
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

    private static readonly Dictionary<uint, uint> HousingInteriorToOutdoor = new()
    {
        // Mist
        [282] = 339, [283] = 339, [284] = 339, [384] = 339, [423] = 339, [573] = 339, [608] = 339,
        // Lavender Beds
        [342] = 340, [343] = 340, [344] = 340, [385] = 340, [425] = 340, [574] = 340, [609] = 340,
        // The Goblet
        [345] = 341, [346] = 341, [347] = 341, [386] = 341, [424] = 341, [575] = 341, [610] = 341,
        // Shirogane
        [649] = 641, [650] = 641, [651] = 641, [652] = 641, [653] = 641, [654] = 641, [655] = 641,
        // Empyreum
        [980] = 979, [981] = 979, [982] = 979, [983] = 979, [984] = 979, [985] = 979, [999] = 979,
    };

    public LoginEnhancementHandler(Configuration config, IGameInteropProvider gameInterop,
        IAddonLifecycle addonLifecycle, IDataManager dataManager, IPluginLog log)
    {
        this.config         = config;
        this.addonLifecycle = addonLifecycle;
        this.dataManager    = dataManager;
        this.log            = log;

        addonLifecycle.RegisterListener(AddonEvent.PostSetup, "Logo", OnLogoSetup);

        try
        {
            charaSelectHook = gameInterop.HookFromAddress<UpdateCharaSelectDelegate>(
                (nint)AgentLobby.MemberFunctionPointers.UpdateCharaSelectDisplay, CharaSelectDetour);
            charaSelectHook.Enable();
        }
        catch (Exception ex) { log.Warning(ex, "[HF] LoginEnhancement: UpdateCharaSelectDisplay hook failed."); }

        try
        {
            loginWaitHook = gameInterop.HookFromAddress<OpenLoginWaitDelegate>(
                (nint)AgentLobby.MemberFunctionPointers.OpenLoginWaitDialog, LoginWaitDetour);
            loginWaitHook.Enable();
        }
        catch (Exception ex) { log.Warning(ex, "[HF] LoginEnhancement: OpenLoginWaitDialog hook failed."); }
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
        if (HousingInteriorToOutdoor.TryGetValue(territoryId, out var outdoor))
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
        charaSelectHook?.Disable();
        charaSelectHook?.Dispose();
        loginWaitHook?.Disable();
        loginWaitHook?.Dispose();
    }
}
