using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace HoliestFluffiness.Handlers;

// Captures per-plot ownership/price/availability data for housing wards. Purely in-memory for the
// life of the game process by design (per project convention, see
// feedback-dalamud-plugin-constraints): nothing here is ever written to config or the database,
// and the whole cache is dropped only on Dispose (plugin unload / game close). Deliberately kept
// across character switches - everything is already keyed by (world, district, ward), so a
// different character on the same world just sees the same data, and a different world is simply
// another entry in the district dropdown; there is nothing to mix up.
//
// Data arrives passively whenever the game sends a HousingWardInfo blob (i.e. whenever the user
// manually opens a ward in the "HousingSelectBlock" addon - normal gameplay, not something this
// handler triggers on its own). The user-triggered automation is StartSweep() (walks the ward
// buttons currently shown) and EnterWard() (switches the open menu to a specific ward) - both only
// ever run after an explicit user action (e.g. a button/row click), never on their own, and
// neither involves any third-party plugin.
//
// A prior version of EnterWard tried to also drive the player all the way to a specific plot -
// click the native "Select" confirm button, accept the "Travel to Ward X?" prompt, wait for the
// zone to change, then hand off to Lifestream to walk the last stretch. That consistently stalled
// waiting for a zone-change event that may never fire in the first place (wards within one
// district likely share a single TerritoryType, so switching wards without changing district might
// not raise TerritoryChanged at all), so it was removed - EnterWard only ever switches the
// displayed ward now.
public sealed unsafe class WardInfoHandler : IDisposable
{
    public const string AddonName = "HousingSelectBlock";
    private static readonly TimeSpan SweepStepTimeout = TimeSpan.FromSeconds(4);
    private const int MaxWardsPerSweep = 30;
    private const int ConsecutiveMissLimit = 2;

    private delegate void HandleHousingWardInfoDelegate(nint agentBase, nint housingWardInfoPtr);

    private readonly Hook<HandleHousingWardInfoDelegate>? wardInfoHook;
    private readonly IGameGui gameGui;
    private readonly IFramework framework;
    private readonly IPluginLog log;

    private readonly Dictionary<(short WorldId, short TerritoryTypeId, short WardNumber), HousingWardInfo> cache = new();

    private int sweepNextIndex;
    private int sweepMissStreak;
    private int sweepExpectedWard = -1;
    private DateTime sweepStepSentAt;

    public bool IsSweeping { get; private set; }
    public int SweptCount { get; private set; }
    public int SweepTotal { get; private set; }

    // The most recently received ward's identity - used by the UI to know which district/world
    // is "currently being browsed" without needing its own addon-reading logic.
    public LandIdent? LastLandIdent { get; private set; }
    private DateTime lastLandIdentAt = DateTime.MinValue;

    // Bumped whenever new ward data is captured, so a UI rebuilding its row list from GetPlots()
    // can tell "nothing changed" from "data changed" and avoid discarding its current sort order
    // every frame for no reason.
    public int Version { get; private set; }

    public WardInfoHandler(ISigScanner sigScanner, IGameInteropProvider gameInterop, IGameGui gameGui,
        IFramework framework, IPluginLog log)
    {
        this.gameGui   = gameGui;
        this.framework = framework;
        this.log       = log;

        wardInfoHook = Common.TryCreateHookFromSignature<HandleHousingWardInfoDelegate>(
            Sigs.HousingWardInfoHandler, OnHousingWardInfo, gameInterop, log,
            "[HF] WardInfo: HousingWardInfoHandler sig failed; ward capture disabled.");

        framework.Update += OnUpdate;
    }

    private void OnHousingWardInfo(nint agentBase, nint dataPtr)
    {
        wardInfoHook!.Original(agentBase, dataPtr);

        try
        {
            var info = HousingWardInfo.Read(dataPtr);
            var key = (info.LandIdent.WorldId, info.LandIdent.TerritoryTypeId, info.LandIdent.WardNumber);
            cache[key] = info;
            LastLandIdent = info.LandIdent;
            lastLandIdentAt = DateTime.UtcNow;
            Version++;
            log.Debug("[HF] WardInfo: captured ward {Ward} territory {Territory} world {World}",
                info.LandIdent.WardNumber, info.LandIdent.TerritoryTypeId, info.LandIdent.WorldId);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[HF] WardInfo: failed to parse HousingWardInfo blob.");
        }
    }

    // Every distinct (world, district) captured so far this login session - lets a standalone
    // viewer (see /houses) pick which one to look at instead of only ever showing the last-browsed.
    public IEnumerable<(short WorldId, short TerritoryTypeId)> GetCachedDistricts() =>
        cache.Values.Select(i => (i.LandIdent.WorldId, i.LandIdent.TerritoryTypeId)).Distinct();

    // Returns one row per plot for every ward captured so far for this district/world.
    public IEnumerable<(LandIdent Ward, PurchaseType Purchase, TenantType Tenant, HouseInfoEntry Plot, int PlotIndex)> GetPlots(short worldId, short territoryTypeId)
    {
        foreach (var info in cache.Values)
        {
            if (info.LandIdent.WorldId != worldId || info.LandIdent.TerritoryTypeId != territoryTypeId)
                continue;
            for (var i = 0; i < info.HouseInfoEntries.Length; i++)
                yield return (info.LandIdent, info.PurchaseType, info.TenantType, info.HouseInfoEntries[i], i);
        }
    }

    public int CapturedWardCount(short worldId, short territoryTypeId) =>
        cache.Values.Count(i => i.LandIdent.WorldId == worldId && i.LandIdent.TerritoryTypeId == territoryTypeId);

    // Switches the currently-open ward menu straight to this ward - no travel, no third-party
    // integration, just the same synthetic addon interaction Sweep already uses. The menu is
    // already open (that's the precondition for this handler having any data to show in the first
    // place), so there is nothing to travel to; only the displayed tab needs to change.
    public void EnterWard(short wardNumber)
    {
        var addon = GetAddon();
        if (addon == null) return;
        SelectWard(addon, wardNumber);
    }

    // ── Manual-trigger sweep ────────────────────────────────────────────────
    // Simulates clicking through the ward list currently shown by HousingSelectBlock. Only ever
    // runs when explicitly started (e.g. a button press) - never on its own.

    public bool CanSweep => !IsSweeping && GetAddon() != null;

    public void StartSweep()
    {
        var addon = GetAddon();
        if (addon == null || IsSweeping) return;

        sweepNextIndex        = 0;
        sweepMissStreak       = 0;
        sweepExpectedWard     = -1;
        SweptCount            = 0;
        SweepTotal            = MaxWardsPerSweep;
        IsSweeping            = true;

        FireNextWard(addon);
    }

    public void CancelSweep()
    {
        IsSweeping = false;
    }

    private void OnUpdate(IFramework _)
    {
        if (!IsSweeping) return;

        var addon = GetAddon();
        if (addon == null)
        {
            log.Debug("[HF] WardInfo: sweep aborted, addon closed.");
            IsSweeping = false;
            return;
        }

        // Hit: the ward we just asked for came back fresh (captured after we fired). Reset the
        // miss streak and move on immediately instead of burning the full timeout every step.
        if (LastLandIdent?.WardNumber == sweepExpectedWard && lastLandIdentAt >= sweepStepSentAt)
        {
            sweepMissStreak = 0;
            SweptCount++;
            AdvanceSweep(addon);
            return;
        }

        if (DateTime.UtcNow - sweepStepSentAt < SweepStepTimeout) return;

        // Timed out waiting for this ward's data - count a miss, advance anyway.
        sweepMissStreak++;
        SweptCount++;
        AdvanceSweep(addon);
    }

    private void AdvanceSweep(AtkUnitBase* addon)
    {
        if (!IsSweeping) return;

        if (sweepMissStreak >= ConsecutiveMissLimit || sweepNextIndex >= MaxWardsPerSweep)
        {
            log.Debug("[HF] WardInfo: sweep finished after {Count} wards.", SweptCount);
            IsSweeping = false;
            return;
        }

        FireNextWard(addon);
    }

    private void FireNextWard(AtkUnitBase* addon)
    {
        var index = sweepNextIndex++;
        sweepExpectedWard = index;
        sweepStepSentAt   = DateTime.UtcNow;

        if (!SelectWard(addon, index)) IsSweeping = false;
    }

    // Selects a ward tab within an already-open HousingSelectBlock, the same synthetic addon
    // interaction Lifestream's own TaskGoToResidentialDistrict.SelectWard uses. Values are
    // [action type 1 = select ward, 0-based ward index], confirmed against that prior art.
    private bool SelectWard(AtkUnitBase* addon, int wardNumber)
    {
        Span<AtkValue> values = stackalloc AtkValue[2];
        values[0] = new AtkValue { Type = AtkValueType.Int, Int = 1 };
        values[1] = new AtkValue { Type = AtkValueType.Int, Int = wardNumber };

        fixed (AtkValue* ptr = values)
        {
            try
            {
                addon->FireCallback(2, ptr, true);
                return true;
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[HF] WardInfo: FireCallback failed for ward index {Index}.", wardNumber);
                return false;
            }
        }
    }

    private AtkUnitBase* GetAddon()
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(AddonName).Address;
        return addon != null && Common.IsAddonVisible(addon) ? addon : null;
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        IsSweeping = false;
        cache.Clear();
        LastLandIdent = null;
        wardInfoHook?.Dispose();
    }
}
