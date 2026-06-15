using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace HoliestFluffiness.Handlers;

public sealed class CharaSelectHandler : IDisposable
{
    private readonly Configuration configuration;
    private readonly CharacterDb characterDb;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IDataManager dataManager;
    private readonly IFramework framework;

    public CharaSelectHandler(Configuration configuration, CharacterDb characterDb, IAddonLifecycle addonLifecycle, IDataManager dataManager, IFramework framework)
    {
        this.configuration  = configuration;
        this.characterDb    = characterDb;
        this.addonLifecycle = addonLifecycle;
        this.dataManager    = dataManager;
        this.framework      = framework;

        addonLifecycle.RegisterListener(AddonEvent.PostSetup,   "_CharaSelectListMenu", OnCharaSelectList);
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, "_CharaSelectListMenu", OnCharaSelectList);
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
            int slot = ++worldCounters[world]; // 1-based position within this world

            characterDb.UpsertSlot($"{name}@{world}", name, world, dc, slot);
        }
    }

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup,   "_CharaSelectListMenu", OnCharaSelectList);
        addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, "_CharaSelectListMenu", OnCharaSelectList);
    }
}
