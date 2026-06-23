using System;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace HoliestFluffiness.Handlers;

public sealed class AntiAfkHandler : IDisposable
{
    private readonly Configuration config;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly IntPtr windowHandle;
    private CancellationTokenSource? cts;

    private const uint WM_KEYDOWN = 0x100;
    private const uint WM_KEYUP   = 0x101;
    private const int  LControl   = 162;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    public AntiAfkHandler(Configuration config, IFramework framework, IPluginLog log, IntPtr windowHandle)
    {
        this.config       = config;
        this.framework    = framework;
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
        while (!token.IsCancellationRequested)
        {
            try
            {
                float maxTimer = 0f;
                framework.RunOnFrameworkThread(() =>
                {
                    var m = UIModule.Instance()->GetInputTimerModule();
                    maxTimer = Math.Max(m->AfkTimer, Math.Max(m->ContentInputTimer, m->InputTimer));
                }).GetAwaiter().GetResult();

                log.Verbose($"[HF] AntiAfk timer: {maxTimer:F1}s");

                if (maxTimer > config.AntiAfkTimerLimit)
                {
                    log.Debug($"[HF] AntiAfk: keypress at {maxTimer:F1}s");
                    SendMessage(windowHandle, WM_KEYDOWN, (IntPtr)LControl, IntPtr.Zero);
                    Thread.Sleep(50);
                    SendMessage(windowHandle, WM_KEYUP, (IntPtr)LControl, IntPtr.Zero);
                }
            }
            catch (Exception ex)
            {
                log.Error(ex, "[HF] AntiAfk error");
            }

            token.WaitHandle.WaitOne(10_000);
        }
    }

    public void Dispose() => Stop();
}
