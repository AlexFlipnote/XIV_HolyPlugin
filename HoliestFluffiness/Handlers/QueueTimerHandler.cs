using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace HoliestFluffiness.Handlers;

// Adds a countdown to the "server is currently congested" queue dialog. The server cadence is not
// exposed anywhere, so it is measured: every time the game rewrites the message, that counts as a
// refresh, and the shortest gap seen so far is used as the estimate for the next one.
// Shows "started" until the first refresh, "updated" until a full gap is known, then "next".
public sealed unsafe class QueueTimerHandler : IDisposable
{
    private const string AddonName     = "SelectOk";
    private const long   MinIntervalMs = 5000;

    private static readonly Regex QueueRx = new(@"queue:\s*\d+", RegexOptions.IgnoreCase);

    private readonly Configuration   config;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IPluginLog      log;

    private readonly Stopwatch sinceRefresh = new();
    private byte[]? baseText;
    private byte[]? lastWritten;
    private string? lastSuffix;
    private long    shortestIntervalMs;
    private int     refreshCount;

    public QueueTimerHandler(Configuration config, IAddonLifecycle addonLifecycle, IPluginLog log)
    {
        this.config         = config;
        this.addonLifecycle = addonLifecycle;
        this.log            = log;

        addonLifecycle.RegisterListener(AddonEvent.PostUpdate,  AddonName, OnUpdate);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnClose);
    }

    private void OnUpdate(AddonEvent type, AddonArgs args)
    {
        if (!config.QueueTimerEnabled) return;
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || !addon->IsReady) return;

        var node = FindQueueTextNode(addon);
        if (node == null) return;

        // Anything other than our own last write means the game put a fresh message there
        var current   = node->NodeText.AsSpan();
        var refreshed = lastWritten == null || !current.SequenceEqual(lastWritten);
        if (refreshed)
        {
            if (sinceRefresh.IsRunning)
            {
                refreshCount++;
                var ms = sinceRefresh.ElapsedMilliseconds;
                // The dialog opening is not lined up with the server cadence, so only gaps between two refreshes count
                if (refreshCount >= 2 && ms >= MinIntervalMs && (shortestIntervalMs == 0 || ms < shortestIntervalMs))
                    shortestIntervalMs = ms;
                log.Debug("[HF] Queue refresh #{Count} after {Ms} ms: {Text}", refreshCount, ms, node->NodeText.ToString());
            }
            baseText = current.ToArray();
            sinceRefresh.Restart();
        }

        var elapsed = (int)(sinceRefresh.ElapsedMilliseconds / 1000);
        var next    = shortestIntervalMs > 0
            ? (int)Math.Ceiling((shortestIntervalMs - sinceRefresh.ElapsedMilliseconds) / 1000.0)
            : 0;
        var suffix  = refreshCount == 0 ? $" (started {elapsed}s ago)"
                    : next > 0          ? $" (next in ~{next}s)"
                    :                     $" (updated {elapsed}s ago)";
        if (!refreshed && suffix == lastSuffix) return;

        var suffixLen = Encoding.UTF8.GetByteCount(suffix);
        var buf       = new byte[baseText!.Length + suffixLen + 1];
        baseText.CopyTo(buf, 0);
        Encoding.UTF8.GetBytes(suffix, 0, suffix.Length, buf, baseText.Length);

        node->SetText(buf);
        lastWritten = buf[..^1];
        lastSuffix  = suffix;
    }

    private void OnClose(AddonEvent type, AddonArgs args)
    {
        sinceRefresh.Reset();
        baseText           = null;
        lastWritten        = null;
        lastSuffix         = null;
        shortestIntervalMs = 0;
        refreshCount       = 0;
    }

    // SelectOk is shared by plenty of other dialogs, so only the one carrying a queue count is touched
    private static AtkTextNode* FindQueueTextNode(AtkUnitBase* addon)
    {
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || node->Type != NodeType.Text) continue;
            var text = node->GetAsAtkTextNode();
            if (QueueRx.IsMatch(text->NodeText.ToString())) return text;
        }
        return null;
    }

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(AddonEvent.PostUpdate,  AddonName, OnUpdate);
        addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, AddonName, OnClose);
    }
}
