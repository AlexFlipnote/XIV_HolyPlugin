using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace HoliestFluffiness.Handlers;

public sealed unsafe class ClientTweaksHandler : IDisposable
{
    private readonly Configuration   config;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IFramework      framework;

    public ClientTweaksHandler(Configuration config, IAddonLifecycle addonLifecycle, IFramework framework)
    {
        this.config         = config;
        this.addonLifecycle = addonLifecycle;
        this.framework      = framework;

        addonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "_ActionBar", OnActionBarUpdate);
        framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework fw)
    {
        if (!config.TitleMovieDisabled) return;
        var agent = AgentLobby.Instance();
        if (agent != null) agent->IdleTime = 0;
    }

    private void OnActionBarUpdate(AddonEvent type, AddonArgs args)
    {
        if (!config.HotbarLockHidden) return;
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || !addon->IsReady) return;
        var node = addon->GetNodeById(21);
        if (node != null) node->ToggleVisibility(false);
    }

    public void RestoreHotbarLock()
    {
        var addon = (AtkUnitBase*)AtkStage.Instance()->RaptureAtkUnitManager->GetAddonByName("_ActionBar");
        if (addon == null || !addon->IsReady) return;
        var node = addon->GetNodeById(21);
        if (node != null) node->ToggleVisibility(true);
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        addonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, "_ActionBar", OnActionBarUpdate);

        if (config.HotbarLockHidden)
            RestoreHotbarLock();
    }
}
