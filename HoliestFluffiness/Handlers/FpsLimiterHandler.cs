using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Plugin.Services;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace HoliestFluffiness.Handlers;

// Custom frame rate cap, since the game only offers 30, 60 or none. Holding the main thread on
// Framework.Update paces the whole frame. Waits on a high resolution waitable timer for the bulk of
// the gap and spins the last stretch, so the cap lands on target instead of drifting with the 1ms
// scheduler tick. While the window is inactive a separate background cap applies, or none, leaving
// the game's own background limit in charge.
public sealed class FpsLimiterHandler : IDisposable
{
    public const int MinFps = 30;
    public const int MaxFps = 360;
    public const int MinBackgroundFps = 1;
    public const int MaxBackgroundFps = 60;

    // Final stretch spun instead of slept; covers timer wake jitter
    private static readonly long SpinTicks = Stopwatch.Frequency / 2000; // 0.5ms
    // How long after the last wait we still call the cap "holding", so the colour does not flicker
    private static readonly long HoldTicks = Stopwatch.Frequency / 2;    // 500ms

    private readonly Configuration config;
    private readonly IFramework framework;
    private readonly nint timer;

    private long nextFrame;
    private long lastWaitAt;
    private bool wasInactive;

    // True while the cap is the thing deciding the frame rate: enabled, focused and actually waiting
    public bool IsLimiting { get; private set; }

    public FpsLimiterHandler(Configuration config, IFramework framework)
    {
        this.config    = config;
        this.framework = framework;

        timer = CreateWaitableTimerExW(nint.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        framework.Update += OnUpdate;
    }

    private unsafe void OnUpdate(IFramework fw)
    {
        var gameFw = GameFramework.Instance();
        int? target = null;
        if (gameFw != null)
        {
            var inactive = gameFw->WindowInactive;
            if (inactive && config.FpsLimitBackgroundEnabled)
                target = Math.Clamp(config.FpsLimitBackgroundTarget, MinBackgroundFps, MaxBackgroundFps);
            else if (!inactive && config.FpsLimitEnabled)
                target = Math.Clamp(config.FpsLimitTarget, MinFps, MaxFps);

            // Switching between the two caps starts a fresh schedule
            if (inactive != wasInactive) nextFrame = 0;
            wasInactive = inactive;
        }

        if (target == null)
        {
            nextFrame  = 0;
            IsLimiting = false;
            return;
        }

        var interval = Stopwatch.Frequency / target.Value;
        var now      = Stopwatch.GetTimestamp();

        // First frame, or we fell more than a frame behind (load screen, hitch): start fresh rather
        // than racing to catch up
        if (nextFrame == 0 || now - nextFrame > interval)
        {
            nextFrame  = now + interval;
            IsLimiting = now - lastWaitAt < HoldTicks;
            return;
        }

        if (now < nextFrame)
        {
            WaitUntil(nextFrame);
            lastWaitAt = Stopwatch.GetTimestamp();
        }

        // Advance from the deadline, not from now, so the average lands on target
        nextFrame += interval;
        IsLimiting = Stopwatch.GetTimestamp() - lastWaitAt < HoldTicks;
    }

    private void WaitUntil(long deadline)
    {
        var remaining = deadline - Stopwatch.GetTimestamp();
        if (timer != nint.Zero && remaining > SpinTicks)
        {
            // Relative due time in 100ns units, negative means relative
            var sleepTicks = remaining - SpinTicks;
            var dueTime    = -(sleepTicks * 10_000_000 / Stopwatch.Frequency);
            if (dueTime < 0 && SetWaitableTimerEx(timer, ref dueTime, 0, nint.Zero, nint.Zero, nint.Zero, 0))
                _ = WaitForSingleObject(timer, Infinite);
        }
        else if (timer == nint.Zero)
        {
            // No high resolution timer (older Windows, Wine): coarse sleeps, leaving a wider spin margin
            var margin = Stopwatch.Frequency / 500; // 2ms
            while (deadline - Stopwatch.GetTimestamp() > margin)
                Thread.Sleep(1);
        }

        while (Stopwatch.GetTimestamp() < deadline)
            Thread.SpinWait(20);
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        if (timer != nint.Zero) CloseHandle(timer);
    }

    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess                     = 0x001F0003;
    private const uint Infinite                           = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(nint timer, ref long dueTime, int period, nint completion,
        nint completionArg, nint wakeContext, uint tolerableDelay);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
