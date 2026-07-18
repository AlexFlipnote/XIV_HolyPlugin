using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace HoliestFluffiness.Handlers;

// Fades the loot roll notification window (_NotificationLoot) once you have rolled on everything
// available, so a finished loot list stops pulling your eye. Ported from VanillaPlus (MidoriKami).
public sealed unsafe class LootFadeHandler : IDisposable
{
    private readonly Configuration   config;
    private readonly IAddonLifecycle addonLifecycle;

    public LootFadeHandler(Configuration config, IAddonLifecycle addonLifecycle)
    {
        this.config         = config;
        this.addonLifecycle = addonLifecycle;

        addonLifecycle.RegisterListener(AddonEvent.PostUpdate,  "_NotificationLoot", OnUpdate);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "_NotificationLoot", OnFinalize);
    }

    private void OnUpdate(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || addon->RootNode == null) return;

        // When the feature is off, hold the window fully opaque so toggling it restores immediately.
        addon->RootNode->Color.A = config.LootFadeEnabled && AllLootRolled()
            ? (byte)(255 * (1f - config.LootFadePercent))
            : (byte)255;
    }

    private static void OnFinalize(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || addon->RootNode == null) return;
        addon->RootNode->Color.A = 255;
    }

    private static bool AllLootRolled()
    {
        var loot = Loot.Instance();
        if (loot == null) return false;

        foreach (ref readonly var item in loot->Items)
        {
            if (item.ItemId != 0 && item.RollState != RollState.Rolled)
                return false;
        }
        return true;
    }

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(AddonEvent.PostUpdate,  "_NotificationLoot", OnUpdate);
        addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "_NotificationLoot", OnFinalize);
    }
}
