using System;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace HoliestFluffiness.Handlers;

public sealed unsafe class RepairHandler : IDisposable
{
    private delegate nint LoadIconByIdDelegate(void* comp, int iconId);
    private delegate void ReceiveEventDelegate(nint a1, short a2, nint a3, nint a4, nint a5);

    private readonly LoadIconByIdDelegate?       loadIcon;
    private readonly Hook<ReceiveEventDelegate>? receiveEventHook;

    private readonly Configuration   config;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IClientState    clientState;
    private readonly IPluginLog      log;

    public float? TestPct { get; set; }

    private int    cachedGameCount = -1;
    private int    lastSlotUsed    = -1; // hide this before counting so it isn't mistaken for a game node
    private nint   ourCompAddr;
    private nint   hoveredComp;
    private ushort cachedAddonId;
    private bool   tooltipShown;
    private readonly nint tooltipMemory = Marshal.AllocHGlobal(1024);

    private const int    IconLow      = 215289;
    private const int    IconCritical = 215290;
    private const string AddonName    = "_StatusCustom2";
    private const int    NodeMax      = 24;
    private const int    NodeMin      = 5;

    public RepairHandler(Configuration config, ISigScanner sigScanner, IGameInteropProvider gameInterop,
                         IAddonLifecycle addonLifecycle, IClientState clientState, IPluginLog log)
    {
        this.config         = config;
        this.addonLifecycle = addonLifecycle;
        this.clientState    = clientState;
        this.log            = log;

        try
        {
            var fnAddr = sigScanner.ScanText("E8 ?? ?? ?? ?? 41 8D 45 3E");
            loadIcon = Marshal.GetDelegateForFunctionPointer<LoadIconByIdDelegate>(fnAddr);
            log.Debug($"[HF] Repair: LoadIconByID at 0x{fnAddr:X16}");
        }
        catch (Exception ex) { log.Warning(ex, "[HF] Repair: LoadIconByID sig failed."); }

        try
        {
            var hookAddr = sigScanner.ScanText("44 0F B7 C2 4D 8B D1");
            receiveEventHook = gameInterop.HookFromAddress<ReceiveEventDelegate>(hookAddr, OnReceiveEvent);
            receiveEventHook.Enable();
        }
        catch (Exception ex) { log.Warning(ex, "[HF] Repair: ReceiveEvent hook failed; hover tooltip disabled."); }

        addonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, AddonName, OnRequestedUpdate);
        addonLifecycle.RegisterListener(AddonEvent.PostUpdate,          AddonName, OnPostUpdate);
    }

    // Fires every frame before PostUpdate. Our injected node from the previous PostUpdate
    // is still visible. We hide it before counting — but only if it still has our sentinel
    // byte (0x01) in the timer text. If the game called loadIcon on that slot to show a
    // real status, it will have overwritten the sentinel, meaning the slot is now the game's.
    private void OnRequestedUpdate(AddonEvent _, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || !addon->IsReady) return;

        if (lastSlotUsed >= NodeMin && lastSlotUsed < addon->UldManager.NodeListCount)
        {
            var prev = addon->UldManager.NodeList[lastSlotUsed];
            if (prev != null && prev->IsVisible() && SlotIsOurs(prev))
                prev->NodeFlags ^= NodeFlags.Visible;
            // If sentinel is gone the game took this slot — leave it visible and count it.
        }
        lastSlotUsed = -1;
        ourCompAddr  = 0;

        cachedGameCount = CountGameNodes(addon);
    }

    // Returns true if the timer text ends with '%', which is how we write our percentage.
    // Game timer text uses s/m/h/d suffixes — never '%' — so this reliably identifies our slot.
    private static bool SlotIsOurs(AtkResNode* slotNode)
    {
        var compNode = slotNode->GetAsAtkComponentNode();
        if (compNode == null || compNode->Component == null) return false;
        var comp = compNode->Component;
        if (comp->UldManager.NodeListCount <= 2) return false;
        var timerNode = comp->UldManager.NodeList[2]->GetAsAtkTextNode();
        if (timerNode == null) return false;
        var ptr = (byte*)timerNode->NodeText.StringPtr;
        if ((nint)ptr == 0 || *ptr == 0) return false;
        var i = 0;
        while (ptr[i] != 0 && i < 16) i++;
        return ptr[i - 1] == (byte)'%';
    }

    private void OnReceiveEvent(nint a1, short a2, nint a3, nint a4, nint a5)
    {
        if (a2 == 6) hoveredComp = a1;
        if (a2 == 7) hoveredComp = 0;
        receiveEventHook!.Original(a1, a2, a3, a4, a5);
    }

    private void OnPostUpdate(AddonEvent _, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || !addon->IsReady) return;

        if (loadIcon == null || !clientState.IsLoggedIn || (!config.RepairLowEnabled && !config.RepairCriticalEnabled))
        {
            HideTooltip(addon->Id);
            return;
        }

        var lowestPct  = TestPct ?? GetLowestConditionPct();
        var isCritical = config.RepairCriticalEnabled && lowestPct <= config.RepairCriticalThreshold;
        var isLow      = !isCritical && config.RepairLowEnabled && lowestPct <= config.RepairLowThreshold;

        if (!isCritical && !isLow) { HideTooltip(addon->Id); return; }

        // Use count from the most recent PostRequestedUpdate; fall back to live on first frame.
        var gameCount = cachedGameCount >= 0 ? cachedGameCount : CountGameNodes(addon);
        var slotIdx   = NodeMax - gameCount;

        if (slotIdx < NodeMin || slotIdx >= addon->UldManager.NodeListCount)
        {
            HideTooltip(addon->Id);
            return;
        }

        var container = addon->UldManager.NodeList[slotIdx];
        if (container == null) return;

        var compNode = container->GetAsAtkComponentNode();
        if (compNode == null || compNode->Component == null) return;
        var comp = compNode->Component;

        // Game cleared this slot in OnRequestedUpdate; re-show it.
        if (!container->IsVisible())
            container->NodeFlags ^= NodeFlags.Visible;

        try { loadIcon(comp, isCritical ? IconCritical : IconLow); }
        catch (Exception ex) { log.Warning(ex, "[HF] Repair: loadIcon failed."); return; }

        SetTimerText(comp, 2, lowestPct, isCritical);
        HideSubNode(comp, 0);

        lastSlotUsed  = slotIdx;
        ourCompAddr   = (nint)comp;
        cachedAddonId = (ushort)addon->Id;

        if (hoveredComp == ourCompAddr)
        {
            var msg = isCritical
                ? $"Your gear is really damaged ({lowestPct:F0}%), repair now!!"
                : $"Gear at {lowestPct:F0}%, consider repairing";
            WriteTooltipText(msg);
            AtkStage.Instance()->TooltipManager.ShowTooltip(cachedAddonId, container, (byte*)tooltipMemory);
            tooltipShown = true;
        }
        else
        {
            HideTooltip(addon->Id);
        }
    }

    private void HideTooltip(int addonId)
    {
        if (!tooltipShown) return;
        AtkStage.Instance()->TooltipManager.HideTooltip((ushort)addonId);
        tooltipShown = false;
    }

    private void WriteTooltipText(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var len   = Math.Min(bytes.Length, 1022);
        Marshal.Copy(bytes, 0, tooltipMemory, len);
        Marshal.WriteByte(tooltipMemory + len, 0);
    }

    private static int CountGameNodes(AtkUnitBase* addon)
    {
        var count = 0;
        for (var i = NodeMax; i >= NodeMin; i--)
        {
            if (i < addon->UldManager.NodeListCount
                && addon->UldManager.NodeList[i] != null
                && addon->UldManager.NodeList[i]->IsVisible())
                count++;
        }
        return count;
    }

    // Shows "45%" on the icon timer text in yellow (low) or red (critical).
    // The trailing '%' also serves as our ownership marker — game timers use s/m/h/d.
    private static void SetTimerText(AtkComponentBase* comp, int idx, float pct, bool critical)
    {
        if (comp->UldManager.NodeListCount <= idx) return;
        var node = comp->UldManager.NodeList[idx];
        if (node == null) return;
        if (!node->IsVisible()) node->NodeFlags ^= NodeFlags.Visible;

        Span<byte> buf = stackalloc byte[8];
        var len = Encoding.UTF8.GetBytes($"{pct:F0}%", buf);
        buf[len] = 0;
        var textNode = node->GetAsAtkTextNode();
        textNode->SetText(buf[..(len + 1)]);

        textNode->TextColor = new ByteColor { R = 0xFF, G = 0xFF, B = 0xFF, A = 0xFF };
        textNode->EdgeColor = critical
            ? new ByteColor { R = 0xE7, G = 0x4C, B = 0x3C, A = 0xFF }
            : new ByteColor { R = 0xF3, G = 0x9C, B = 0x12, A = 0xFF };
    }

    private static void HideSubNode(AtkComponentBase* comp, int idx)
    {
        if (comp->UldManager.NodeListCount <= idx) return;
        var node = comp->UldManager.NodeList[idx];
        if (node != null && node->IsVisible())
            node->NodeFlags ^= NodeFlags.Visible;
    }

    private static float GetLowestConditionPct()
    {
        var mgr = InventoryManager.Instance();
        if (mgr == null) return 100f;
        var container = mgr->GetInventoryContainer(InventoryType.EquippedItems);
        if (container == null) return 100f;
        var lowest = ushort.MaxValue;
        for (var i = 0; i < 13; i++)
        {
            var slot = container->GetInventorySlot(i);
            if (slot == null || slot->ItemId == 0) continue;
            if (slot->Condition < lowest) lowest = slot->Condition;
        }
        return lowest == ushort.MaxValue ? 100f : lowest / 300f;
    }

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, AddonName, OnRequestedUpdate);
        addonLifecycle.UnregisterListener(AddonEvent.PostUpdate,          AddonName, OnPostUpdate);
        if (tooltipShown) AtkStage.Instance()->TooltipManager.HideTooltip(cachedAddonId);
        receiveEventHook?.Disable();
        receiveEventHook?.Dispose();
        Marshal.FreeHGlobal(tooltipMemory);
    }
}
