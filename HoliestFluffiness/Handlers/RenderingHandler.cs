using System;
using Dalamud.Game.ClientState.Conditions;
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

    // Flipping Is3DRenderingDisabled while the render thread is loading a zone or cutscene crashes the
    // game (SetPixelShader on the render thread), and rapid alt-tabbing makes hitting that window likely.
    // So: only disable after the window has stayed unfocused for a moment, and never flip the flag during
    // a transition or shortly after one ends.
    private const long UnfocusedDelayMs     = 1000;
    private const long TransitionSettleMs   = 3000;

    private delegate void FadeDrawDelegate(nint addon);

    private readonly Configuration           config;
    private readonly IFramework              framework;
    private readonly ICondition              condition;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly Hook<FadeDrawDelegate>? fadeHook;

    private uint[]? ecommonsFrameData;

    // Last value we wrote to the render flag; null means we are not touching it. Without force we only
    // write on a change, so another plugin's own toggle is left alone between our transitions.
    private bool? appliedDisabled;

    private long unfocusedSince;   // 0 while focused
    private long transitionEndedAt;

    public bool IsSkipFadeAvailable => fadeHook != null;

    public RenderingHandler(Configuration config, IFramework framework, ICondition condition,
        IGameInteropProvider gameInterop, IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.config          = config;
        this.framework       = framework;
        this.condition       = condition;
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

    private bool IsInTransition() =>
        condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] ||
        condition[ConditionFlag.OccupiedInCutSceneEvent] || condition[ConditionFlag.WatchingCutscene] ||
        condition[ConditionFlag.WatchingCutscene78];

    // True when it is unsafe to change the flag this frame
    private bool IsFlipBlocked(long now)
    {
        if (IsInTransition())
        {
            transitionEndedAt = now;
            return true;
        }
        return transitionEndedAt != 0 && now - transitionEndedAt < TransitionSettleMs;
    }

    private void OnFrameworkUpdate(IFramework fw)
    {
        var manager = Manager.Instance();
        if (manager == null) return;

        var now     = Environment.TickCount64;
        var blocked = IsFlipBlocked(now);

        if (!config.DisableRenderEnabled)
        {
            ReleaseECommonsData();
            unfocusedSince = 0;
            if (appliedDisabled == null || blocked) return;
            if (appliedDisabled == true) manager->Is3DRenderingDisabled = false;
            appliedDisabled = null;
            return;
        }

        var fwInstance = Framework.Instance();
        if (fwInstance == null) return;

        if (fwInstance->WindowInactive)
        {
            if (unfocusedSince == 0) unfocusedSince = now;
        }
        else unfocusedSince = 0;

        var disable = config.DisableRenderMode == DisableRenderMode.Always ||
                      (unfocusedSince != 0 && now - unfocusedSince >= UnfocusedDelayMs);

        if (config.DisableRenderForce)
        {
            // Keep owning the flag, but hold its current value until the transition has settled
            if (blocked) disable = appliedDisabled ?? manager->Is3DRenderingDisabled;
            manager->Is3DRenderingDisabled = disable;
            appliedDisabled = disable;
            ecommonsFrameData ??= pluginInterface.GetOrCreateData<uint[]>(ECommonsRenderDataTag, () => [0]);
            ecommonsFrameData[0] = fwInstance->FrameCounter;
            return;
        }

        ReleaseECommonsData();
        if (blocked || appliedDisabled == disable) return;
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
