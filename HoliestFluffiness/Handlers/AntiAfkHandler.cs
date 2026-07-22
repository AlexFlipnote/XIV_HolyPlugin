using System;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace HoliestFluffiness.Handlers;

public sealed class AntiAfkHandler : IDisposable
{
    private readonly Configuration config;
    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly IntPtr windowHandle;
    private CancellationTokenSource? cts;

    private const uint WM_KEYDOWN = 0x100;
    private const uint WM_KEYUP   = 0x101;
    private const int  LControl   = 162;
    private const uint AfkOnlineStatus = 17;
    private readonly Random rng = new Random();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    public AntiAfkHandler(Configuration config, IFramework framework, IObjectTable objectTable, IPluginLog log, IntPtr windowHandle)
    {
        this.config       = config;
        this.framework    = framework;
        this.objectTable  = objectTable;
        this.log          = log;
        this.windowHandle = windowHandle;
        if (config.AntiAfkEnabled)
            Start();
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled) Start();
        else Stop();
    }

    private void Start()
    {
        if (cts != null) return;
        cts = new CancellationTokenSource();
        var token = cts.Token;
        new Thread(() => Work(token)) { IsBackground = true, Name = "HF-AntiAfk" }.Start();
    }

    private void Stop()
    {
        var old = cts;
        cts = null;
        old?.Cancel();
        old?.Dispose();
    }

    private unsafe void Work(CancellationToken token)
    {
        while (true)
        {
            try
            {
                if (token.IsCancellationRequested) return;

                float maxTimer = 0f;
                bool  manualAfk = false;
                float jitter = (float)rng.NextDouble() * 5f;
                float effectiveLimit = Math.Max(0, config.AntiAfkTimerLimit - jitter);

                framework.RunOnFrameworkThread(() =>
                {
                    var m = UIModule.Instance()->GetInputTimerModule();
                    maxTimer = Math.Max(m->AfkTimer, Math.Max(m->ContentInputTimer, m->InputTimer));
                    if (config.AntiAfkRespectManualAfk)
                        manualAfk = objectTable[0] is IPlayerCharacter pc && pc.OnlineStatus.RowId == AfkOnlineStatus;
                }).GetAwaiter().GetResult();

                if (manualAfk)
                {
                    log.Debug("[HF] AntiAfk paused: player manually AFK");
                    token.WaitHandle.WaitOne(5000);
                    continue;
                }

                if (maxTimer > effectiveLimit)
                {
                    log.Debug($"[HF] AntiAfk: keypress at {maxTimer:F1}s (Limit: {effectiveLimit:F1}s)");
                    SendMessage(windowHandle, WM_KEYDOWN, (IntPtr)LControl, IntPtr.Zero);
                    Thread.Sleep(50);
                    SendMessage(windowHandle, WM_KEYUP, (IntPtr)LControl, IntPtr.Zero);

                    token.WaitHandle.WaitOne(5000);
                }
                else
                {
                    float timeUntilLimit = effectiveLimit - maxTimer;
                    int sleepMs = (int)Math.Clamp(timeUntilLimit * 500, 1000, 10000);

                    log.Verbose($"[HF] AntiAfk sleeping for {sleepMs}ms");
                    token.WaitHandle.WaitOne(sleepMs);
                }
            }
            catch (ObjectDisposedException) { return; }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                log.Error(ex, "[HF] AntiAfk error");
                try { token.WaitHandle.WaitOne(5000); } // Safety wait on error
                catch (ObjectDisposedException) { return; }
            }
        }
    }

    public void Dispose() => Stop();
}
