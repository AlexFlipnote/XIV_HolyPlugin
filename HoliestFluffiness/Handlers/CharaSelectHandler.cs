using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace HoliestFluffiness.Handlers;

public sealed class CharaSelectHandler : IDisposable
{
    private readonly Configuration configuration;
    private readonly CharacterDb characterDb;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IDataManager dataManager;
    private readonly IFramework framework;
    private readonly NoKillHandler? noKillHandler;
    private readonly Action<string, string>? onAutoLogin;
    private CancellationTokenSource? autoLoginCts;

    public CharaSelectHandler(Configuration configuration, CharacterDb characterDb, IAddonLifecycle addonLifecycle, IDataManager dataManager, IFramework framework, NoKillHandler? noKillHandler = null, Action<string, string>? onAutoLogin = null)
    {
        this.configuration  = configuration;
        this.characterDb    = characterDb;
        this.addonLifecycle = addonLifecycle;
        this.dataManager    = dataManager;
        this.framework      = framework;
        this.noKillHandler  = noKillHandler;
        this.onAutoLogin    = onAutoLogin;

        addonLifecycle.RegisterListener(AddonEvent.PostSetup,   "_CharaSelectListMenu", OnCharaSelectList);
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, "_CharaSelectListMenu", OnCharaSelectList);
        addonLifecycle.RegisterListener(AddonEvent.PostSetup,   "Dialogue",             OnDialogueSetup);
    }

    private void OnCharaSelectList(AddonEvent type, AddonArgs args)
    {
        if (!configuration.CharactersDbEnabled) return;
        framework.RunOnFrameworkThread(ImportCharactersUnsafe);
    }

    private unsafe void ImportCharactersUnsafe()
    {
        var agent = AgentLobby.Instance();
        if (agent == null) return;

        ref var entries   = ref agent->LobbyData.CharaSelectEntries;
        var worldSheet    = dataManager.GetExcelSheet<World>();
        var worldCounters = new System.Collections.Generic.Dictionary<string, int>();

        for (long i = 0; i < entries.LongCount; i++)
        {
            var entry = entries[i].Value;
            if (entry == null) continue;

            var name = entry->NameString;
            if (string.IsNullOrEmpty(name)) continue;

            var world = entry->HomeWorldNameString;
            if (string.IsNullOrEmpty(world)) continue;

            var dc = "";
            if (worldSheet?.GetRowOrDefault(entry->HomeWorldId) is { } worldRow)
                dc = worldRow.DataCenter.ValueNullable?.Name.ExtractText() ?? "";

            worldCounters.TryAdd(world, 0);
            int slot = ++worldCounters[world];

            characterDb.UpsertSlot($"{name}@{world}", name, world, dc, slot);
        }
    }

    private void OnDialogueSetup(AddonEvent type, AddonArgs args)
    {
        if (noKillHandler == null) return;

        var isPending      = noKillHandler.PendingAutoLogin;
        var isRetry        = !isPending && noKillHandler.IsReconnecting;
        if (!isPending && !isRetry) return;

        var name  = noKillHandler.AutoLoginName;
        var world = noKillHandler.AutoLoginWorld;
        if (name == null || world == null) return;

        noKillHandler.ClearAutoLogin();
        var addr  = args.Addon.Address;
        // retry dialogues (lobby 2002 etc) need a longer backoff before re-invoking Lifestream
        var delay = isRetry ? 5000 : 3000;

        autoLoginCts?.Cancel();
        autoLoginCts?.Dispose();
        var cts = new CancellationTokenSource();
        autoLoginCts = cts;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, cts.Token);
                await framework.RunOnFrameworkThread(() => DismissDialogueUnsafe(addr));
                await Task.Delay(delay, cts.Token);
            }
            catch (OperationCanceledException) { return; }
            onAutoLogin?.Invoke(name, world);
        });
    }

    private unsafe void DismissDialogueUnsafe(nint addr)
    {
        var addon = (AtkUnitBase*)addr;
        if (addon == null || !addon->IsReady) return;

        var btn = addon->GetComponentButtonById(4);
        if (btn == null) return;

        var btnRes = btn->AtkComponentBase.OwnerNode->AtkResNode;
        var evt    = (AtkEvent*)btnRes.AtkEventManager.Event;
        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, btnRes.AtkEventManager.Event);
    }

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup,   "_CharaSelectListMenu", OnCharaSelectList);
        addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, "_CharaSelectListMenu", OnCharaSelectList);
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup,   "Dialogue",             OnDialogueSetup);
        autoLoginCts?.Cancel();
        autoLoginCts?.Dispose();
    }
}
