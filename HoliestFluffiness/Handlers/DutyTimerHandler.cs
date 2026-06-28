using System;
using System.Diagnostics;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace HoliestFluffiness.Handlers;

public sealed unsafe class DutyTimerHandler : IDisposable
{
    private readonly Configuration  config;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IDataManager   dataManager;
    private readonly IPluginLog     log;

    private int      capturedSeconds;
    private readonly Stopwatch stopwatch = new();

    public DutyTimerHandler(Configuration config, IAddonLifecycle addonLifecycle, IDataManager dataManager, IPluginLog log)
    {
        this.config         = config;
        this.addonLifecycle = addonLifecycle;
        this.dataManager    = dataManager;
        this.log            = log;

        addonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "ContentsFinderConfirm", OnConfirmUpdate);
        addonLifecycle.RegisterListener(AddonEvent.PostUpdate,          "ContentsFinderReady",   OnReadyUpdate);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize,         "ContentsFinderReady",   OnReadyClose);
    }

    // Capture estimated wait time from the queue confirm dialog
    private void OnConfirmUpdate(AddonEvent type, AddonArgs args)
    {
        if (!config.DutyTimerEnabled) return;
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || !addon->IsReady) return;

        var node = addon->GetTextNodeById(60);
        if (node == null) return;

        var text = node->NodeText.ToString();
        if (TryParseMinSec(text, out var seconds))
        {
            capturedSeconds = seconds;
            stopwatch.Restart();
        }
    }

    // Display remaining time in the duty ready dialog
    private void OnReadyUpdate(AddonEvent type, AddonArgs args)
    {
        if (!config.DutyTimerEnabled || capturedSeconds <= 0) return;
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || !addon->IsReady) return;

        var remaining = capturedSeconds - (int)stopwatch.Elapsed.TotalSeconds;
        var node      = addon->GetTextNodeById(3);
        if (node == null) return;

        if (remaining > 0)
        {
            // "Time remaining" label from Addon sheet row 2780
            var label = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>()?.GetRowOrDefault(2780)?.Text.ExtractText() ?? "Time remaining";
            var text  = $"{label}: {remaining}s";
            Span<byte> buf = stackalloc byte[Encoding.UTF8.GetMaxByteCount(text.Length) + 1];
            var len = Encoding.UTF8.GetBytes(text, buf);
            buf[len] = 0;
            node->SetText(buf[..(len + 1)]);
        }
        else
        {
            node->SetText("00:00:00"u8);
        }
    }

    private void OnReadyClose(AddonEvent type, AddonArgs args)
    {
        capturedSeconds = 0;
        stopwatch.Reset();
    }

    private static bool TryParseMinSec(string text, out int totalSeconds)
    {
        totalSeconds = 0;
        if (string.IsNullOrEmpty(text)) return false;
        var parts = text.Split(':');
        if (parts.Length == 2
            && int.TryParse(parts[0], out var min)
            && int.TryParse(parts[1], out var sec))
        {
            totalSeconds = min * 60 + sec;
            return totalSeconds > 0;
        }
        return false;
    }

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, "ContentsFinderConfirm", OnConfirmUpdate);
        addonLifecycle.UnregisterListener(AddonEvent.PostUpdate,          "ContentsFinderReady",   OnReadyUpdate);
        addonLifecycle.UnregisterListener(AddonEvent.PreFinalize,         "ContentsFinderReady",   OnReadyClose);
    }
}
