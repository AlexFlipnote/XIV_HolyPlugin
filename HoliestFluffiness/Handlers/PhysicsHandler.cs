using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Physics;

namespace HoliestFluffiness.Handlers;

public sealed unsafe class PhysicsHandler : IDisposable
{
    private delegate void PhysicsDelegate(nint a1, nint a2);
    private readonly Hook<PhysicsDelegate>? hook;
    private readonly IFramework framework;
    private readonly Configuration config;

    private volatile bool disposed;
    private bool executePhysics;
    private long expectedFrameTime;
    private long sliceStart, sliceEnd;
    private bool sliceRan;

    public bool IsEnabled => hook?.IsEnabled ?? false;

    public PhysicsHandler(Configuration config, IFramework framework, IGameInteropProvider gameInterop, IPluginLog log)
    {
        this.config    = config;
        this.framework = framework;
        try
        {
            // ClientStructs resolves this one for us (BoneSimulator::Update), so it needs no sig of
            // our own; its address tracks their updates instead of rotting in Sigs.cs every patch.
            // Unlike ScanText, an address ClientStructs fails to resolve is a silent null rather
            // than a throw, so the catch below would never see it. Check before handing it over.
            var addr = (nint)BoneSimulator.MemberFunctionPointers.Update;
            if (addr == 0)
            {
                log.Warning("[HF] Physics: ClientStructs did not resolve BoneSimulator::Update.");
                return;
            }

            hook = gameInterop.HookFromAddress<PhysicsDelegate>(addr, Detour);
            Recalculate();
            if (config.PhysicsEnabled)
                EnableInternal();
        }
        catch (Exception ex) { log.Warning(ex, "[HF] Physics: hook init failed."); }
    }

    public void Enable()
    {
        config.PhysicsEnabled = true;
        config.Save();
        EnableInternal();
    }

    public void Disable()
    {
        config.PhysicsEnabled = false;
        config.Save();
        DisableInternal();
    }

    private void EnableInternal()
    {
        hook?.Enable();
        framework.Update += OnUpdate;
    }

    private void DisableInternal()
    {
        hook?.Disable();
        framework.Update -= OnUpdate;
    }

    public void Recalculate()
    {
        expectedFrameTime = (long)(TimeSpan.TicksPerSecond / config.PhysicsTargetFps);
        sliceStart = DateTime.UtcNow.Ticks;
        sliceEnd   = sliceStart + expectedFrameTime;
        sliceRan   = false;
    }

    private void OnUpdate(IFramework fw)
    {
        var now = DateTime.UtcNow.Ticks;
        while (now > sliceEnd)
        {
            sliceStart = sliceEnd + 1;
            sliceEnd   = sliceStart + expectedFrameTime;
            sliceRan   = false;
        }
        if (!sliceRan) { executePhysics = true;  sliceRan = true; }
        else           { executePhysics = false; }
    }

    private void Detour(nint a1, nint a2)
    {
        if (!disposed && executePhysics)
            hook!.Original(a1, a2);
    }

    public void Dispose()
    {
        disposed = true;
        framework.Update -= OnUpdate;
        hook?.Dispose();
    }
}
