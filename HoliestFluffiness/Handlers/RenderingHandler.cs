using System;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace HoliestFluffiness.Handlers;

// Ported from Henchman (Knightmore). Two rendering tweaks:
//  - Skip fade: swallows the draw call of the black fade addon shown on zone changes.
//  - Disable render: turns off 3D rendering (UI stays) either while the window is unfocused or always.
//    "Force" rewrites the flag every frame so other plugins cannot flip it back, and publishes the
//    ECommons frame counter so ECommons based plugins can see someone else owns the flag this frame.
public sealed unsafe class RenderingHandler : IDisposable
{
    private const string ECommonsRenderDataTag = "ECommons.RenderDisableProcessingFramecount";

    private delegate void FadeDrawDelegate(nint addon);

    private readonly Configuration           config;
    private readonly IFramework              framework;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly Hook<FadeDrawDelegate>? fadeHook;

    private uint[]? ecommonsFrameData;

    // Last value we wrote to the render flag; null means we are not touching it. Without force we only
    // write on a change, so another plugin's own toggle is left alone between our transitions.
    private bool? appliedDisabled;

    public bool IsSkipFadeAvailable => fadeHook != null;

    public RenderingHandler(Configuration config, IFramework framework, IGameInteropProvider gameInterop,
        IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.config          = config;
        this.framework       = framework;
        this.pluginInterface = pluginInterface;

        fadeHook = Common.TryCreateHookFromSignature<FadeDrawDelegate>(
            Sigs.FadeMiddleBackDraw, FadeDetour, gameInterop, log,
            "[HF] Rendering: skip fade sig scan failed.", enable: config.SkipFadeEnabled);

        framework.Update += OnFrameworkUpdate;
    }

    public void SetSkipFade(bool enabled)
    {
        if (enabled) fadeHook?.Enable();
        else         fadeHook?.Disable();
    }

    // Not calling the original is the whole tweak: the fade never draws
    private static void FadeDetour(nint addon) { }

    private void OnFrameworkUpdate(IFramework fw)
    {
        var manager = Manager.Instance();
        if (manager == null) return;

        if (!config.DisableRenderEnabled)
        {
            if (appliedDisabled == true) manager->Is3DRenderingDisabled = false;
            appliedDisabled = null;
            ReleaseECommonsData();
            return;
        }

        var fwInstance = Framework.Instance();
        if (fwInstance == null) return;

        var disable = config.DisableRenderMode == DisableRenderMode.Always || fwInstance->WindowInactive;

        if (config.DisableRenderForce)
        {
            manager->Is3DRenderingDisabled = disable;
            appliedDisabled = disable;
            ecommonsFrameData ??= pluginInterface.GetOrCreateData<uint[]>(ECommonsRenderDataTag, () => [0]);
            ecommonsFrameData[0] = fwInstance->FrameCounter;
            return;
        }

        ReleaseECommonsData();
        if (appliedDisabled == disable) return;
        manager->Is3DRenderingDisabled = disable;
        appliedDisabled = disable;
    }

    private void ReleaseECommonsData()
    {
        if (ecommonsFrameData == null) return;
        pluginInterface.RelinquishData(ECommonsRenderDataTag);
        ecommonsFrameData = null;
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        fadeHook?.Dispose();

        if (appliedDisabled == true)
        {
            var manager = Manager.Instance();
            if (manager != null) manager->Is3DRenderingDisabled = false;
        }
        ReleaseECommonsData();
    }
}
