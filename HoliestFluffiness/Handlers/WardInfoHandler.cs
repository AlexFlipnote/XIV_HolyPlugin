using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
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
// handler triggers on its own). StartSweep() (walks the ward buttons currently shown) and
// StartAutoSweepAll() (teleports through every housing district on this world, then sweeps each
// one) are both automation, but both only ever run after an explicit button press, and both can be
// cancelled mid-run - neither one chains into anything beyond what's described here on its own
// initiative. Auto-sweep-all is entirely self-contained (no third-party plugin): it calls the
// game's own Telepo::Teleport to reach each district's gateway city aetheryte, then drives the
// same "Residential District Aethernet" -> "Go to specified ward" native menu chain a player would
// click through by hand, stopping the instant HousingSelectBlock opens - it never confirms travel
// into a ward. Clicking a row in the standalone viewer (see WardInfoWindow) is unrelated automation
// that separately hands the plot's address to Lifestream's own /li command for actual travel.
public sealed unsafe class WardInfoHandler : IDisposable
{
    public const string AddonName = "HousingSelectBlock";
    private static readonly TimeSpan SweepStepTimeout = TimeSpan.FromSeconds(4);
    private const int MaxWardsPerSweep = 30;
    private const int ConsecutiveMissLimit = 2;

    // Fixed order the "Sweep all districts" automation walks through - the same five keys
    // HousingDistricts.TerritoryIds tracks, spelled out here so the order is stable and explicit
    // rather than depending on dictionary enumeration order.
    private static readonly string[] AutoSweepDistricts =
        ["Mist", "The Lavender Beds", "The Goblet", "Shirogane", "Empyreum"];

    // Each residential district is entered from one specific main-city aetheryte's own interaction
    // menu ("Residential District Aethernet"), not from a district-specific aetheryte - these IDs
    // are the same gateway-city Aetheryte IDs Lifestream itself uses for this exact purpose
    // (Lifestream.Enums.ResidentialAetheryteKind: Limsa=8, Gridania=2, Uldah=9, Kugane=111,
    // Foundation=70), cross-referenced against its ResidentialTerritoryForResidentialAetheryte map.
    private static readonly Dictionary<string, uint> AutoSweepGatewayAetheryteId = new()
    {
        ["Mist"]              = 8,   // Limsa Lominsa
        ["The Lavender Beds"] = 2,   // New Gridania
        ["The Goblet"]        = 9,   // Ul'dah - Steps of Nald
        ["Shirogane"]         = 111, // Kugane
        ["Empyreum"]          = 70,  // Foundation
    };

    private const string ResidentialDistrictEntryText = "Residential District Aethernet";
    private const string GoToSpecifiedWardEntryText    = "Go to specified ward. (Review Tabs)";

    // A city Aetheryte Plaza is a lot bigger than a housing ward's platform - teleporting in
    // usually lands well outside actual interact range, so the aetheryte first needs to be found
    // (search range) and then approached (interact range) rather than interacted with immediately.
    private const float AetheryteSearchRange   = 30f;
    private const float AetheryteInteractRange = 10f;

    private static readonly TimeSpan AutoSweepTeleportTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AutoSweepApproachTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AutoSweepStepTimeout     = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AutoSweepMenuTimeout     = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AutoSweepRetryInterval   = TimeSpan.FromSeconds(1);
    // Settle time after arriving in interact range (residual movement from /automove) and again
    // after closing a district's leftover menus (close animation/state) before the next teleport.
    private static readonly TimeSpan AutoSweepSettleDelay     = TimeSpan.FromSeconds(2);

    private enum AutoSweepStage { None, WaitForZoneChange, ApproachAetheryte, InteractWithAetheryte, SelectResidentialDistrict, SelectGoToWard, WaitForHousingMenu, SettleBeforeTeleport }

    private delegate void HandleHousingWardInfoDelegate(nint agentBase, nint housingWardInfoPtr);

    private readonly Hook<HandleHousingWardInfoDelegate>? wardInfoHook;
    private readonly Configuration config;
    private readonly IGameGui gameGui;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly ICondition condition;

    private readonly Dictionary<(short WorldId, short TerritoryTypeId, short WardNumber), HousingWardInfo> cache = new();

    private int sweepNextIndex;
    private int sweepMissStreak;
    private int sweepExpectedWard = -1;
    private DateTime sweepStepSentAt;

    public bool IsSweeping { get; private set; }
    public int SweptCount { get; private set; }
    public int SweepTotal { get; private set; }

    // ── Auto-sweep-all state ────────────────────────────────────────────────
    private int autoSweepDistrictIndex = -1;
    private AutoSweepStage autoSweepStage = AutoSweepStage.None;
    private DateTime autoSweepStageStartedAt;
    private DateTime autoSweepLastActionAt;
    private bool autoSweepSawBetweenAreas;

    public bool IsAutoSweeping { get; private set; }
    public string? AutoSweepCurrentDistrict =>
        IsAutoSweeping && autoSweepDistrictIndex >= 0 && autoSweepDistrictIndex < AutoSweepDistricts.Length
            ? AutoSweepDistricts[autoSweepDistrictIndex]
            : null;

    // The most recently received ward's identity - used by the UI to know which district/world
    // is "currently being browsed" without needing its own addon-reading logic.
    public LandIdent? LastLandIdent { get; private set; }
    private DateTime lastLandIdentAt = DateTime.MinValue;

    // Bumped whenever new ward data is captured, so a UI rebuilding its row list from GetPlots()
    // can tell "nothing changed" from "data changed" and avoid discarding its current sort order
    // every frame for no reason.
    public int Version { get; private set; }

    public WardInfoHandler(Configuration config, ISigScanner sigScanner, IGameInteropProvider gameInterop, IGameGui gameGui,
        IFramework framework, IPluginLog log, IObjectTable objectTable, ITargetManager targetManager,
        ICondition condition)
    {
        this.config        = config;
        this.gameGui       = gameGui;
        this.framework     = framework;
        this.log           = log;
        this.objectTable   = objectTable;
        this.targetManager = targetManager;
        this.condition     = condition;

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
    // viewer (see /wardinfo) pick which one to look at instead of only ever showing the last-browsed.
    public IEnumerable<(short WorldId, short TerritoryTypeId)> GetCachedDistricts() =>
        cache.Values.Select(i => (i.LandIdent.WorldId, i.LandIdent.TerritoryTypeId)).Distinct();

    // Returns one row per plot for every ward captured so far for this district/world.
    public IEnumerable<(LandIdent Ward, PurchaseType Purchase, TenantType Tenant, HouseInfoEntry Plot, int PlotIndex)> GetPlots(short worldId, short territoryTypeId) =>
        EnumeratePlots(i => i.LandIdent.WorldId == worldId && i.LandIdent.TerritoryTypeId == territoryTypeId);

    // Returns one row per plot across every district/world captured so far this session - backs
    // the standalone viewer's "All indexes" option.
    public IEnumerable<(LandIdent Ward, PurchaseType Purchase, TenantType Tenant, HouseInfoEntry Plot, int PlotIndex)> GetAllPlots() =>
        EnumeratePlots(null);

    private IEnumerable<(LandIdent Ward, PurchaseType Purchase, TenantType Tenant, HouseInfoEntry Plot, int PlotIndex)> EnumeratePlots(Func<HousingWardInfo, bool>? predicate)
    {
        foreach (var info in cache.Values)
        {
            if (predicate != null && !predicate(info)) continue;
            for (var i = 0; i < info.HouseInfoEntries.Length; i++)
                yield return (info.LandIdent, info.PurchaseType, info.TenantType, info.HouseInfoEntries[i], i);
        }
    }

    public int CapturedWardCount(short worldId, short territoryTypeId) =>
        cache.Values.Count(i => i.LandIdent.WorldId == worldId && i.LandIdent.TerritoryTypeId == territoryTypeId);

    // ── Manual-trigger sweep ────────────────────────────────────────────────
    // Simulates clicking through the ward list currently shown by HousingSelectBlock. Only ever
    // runs when explicitly started (e.g. a button press) - never on its own.

    public bool CanSweep => !IsSweeping && !IsAutoSweeping && GetAddon() != null;

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
        if (IsAutoSweeping && autoSweepStage != AutoSweepStage.None)
        {
            HandleAutoSweepStage();
            return;
        }

        if (!IsSweeping) return;

        var addon = GetAddon();
        if (addon == null)
        {
            log.Debug("[HF] WardInfo: sweep aborted, addon closed.");
            IsSweeping = false;
            // The menu we were sweeping just disappeared out from under us - something other than
            // the automation itself closed it, so stop the whole chain rather than guess at
            // whether it's safe to keep going.
            if (IsAutoSweeping)
            {
                log.Debug("[HF] WardInfo: auto-sweep-all aborted, menu closed unexpectedly.");
                IsAutoSweeping = false;
            }
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
            if (IsAutoSweeping) AdvanceAutoSweepDistrict();
            return;
        }

        FireNextWard(addon);
    }

    // ── Auto-sweep-all (standalone only) ────────────────────────────────────
    // Chains the same manual Sweep above across every housing district on this world. Entirely
    // self-contained - no third-party plugin involved - by replicating, in order, exactly the steps
    // a player takes by hand: Telepo::Teleport to the district's gateway city aetheryte, target and
    // interact with the aetheryte there, pick "Residential District Aethernet" then "Go to
    // specified ward" from the native SelectString menus that appear, then stop the moment
    // HousingSelectBlock opens - it never clicks "Select" or confirms "Travel to Ward X?", so it
    // never actually enters a ward. Only ever runs after StartAutoSweepAll() is called from an
    // explicit button press, and CancelAutoSweepAll() stops it at any point - it does not restart
    // itself or run outside of this one explicit invocation.

    public bool CanAutoSweepAll => !IsSweeping && !IsAutoSweeping && AutoSweepDistricts.Any(IsAutoSweepDistrictEnabled);

    // Config toggles under Settings > Ward Info > "Sweep all districts" - unticked districts are
    // skipped entirely rather than merely deprioritized.
    private bool IsAutoSweepDistrictEnabled(string district) => district switch
    {
        "Mist"              => config.WardInfoAutoSweepMist,
        "The Lavender Beds" => config.WardInfoAutoSweepLavenderBeds,
        "The Goblet"        => config.WardInfoAutoSweepGoblet,
        "Shirogane"         => config.WardInfoAutoSweepShirogane,
        "Empyreum"          => config.WardInfoAutoSweepEmpyreum,
        _                   => true,
    };

    public void StartAutoSweepAll()
    {
        if (!CanAutoSweepAll) return;

        IsAutoSweeping        = true;
        autoSweepDistrictIndex = -1;
        AdvanceAutoSweepDistrict();
    }

    public void CancelAutoSweepAll()
    {
        if (!IsAutoSweeping) return;

        IsAutoSweeping  = false;
        autoSweepStage  = AutoSweepStage.None;
        StopAutoMove();
        if (IsSweeping) CancelSweep();
    }

    private void AdvanceAutoSweepDistrict()
    {
        if (!IsAutoSweeping) return;

        // A prior step may have left a native menu open (a timeout mid-selection) or automove
        // running (a timeout mid-approach) - both would interfere with the next teleport, so clear
        // them defensively before firing it. Harmless no-ops when there's nothing to clear.
        StopAutoMove();
        CloseLingeringAutoSweepMenus();

        do
        {
            autoSweepDistrictIndex++;
            if (autoSweepDistrictIndex >= AutoSweepDistricts.Length)
            {
                log.Debug("[HF] WardInfo: auto-sweep-all finished.");
                IsAutoSweeping = false;
                autoSweepStage = AutoSweepStage.None;
                return;
            }
        } while (!IsAutoSweepDistrictEnabled(AutoSweepDistricts[autoSweepDistrictIndex]));

        // Give the close (and, on the very first district, nothing) a moment to actually settle
        // before teleporting - firing Telepo::Teleport in the same tick a menu was told to close
        // is unreliable, since the close hasn't necessarily taken effect client-side yet.
        EnterAutoSweepStage(AutoSweepStage.SettleBeforeTeleport);
    }

    private void HandleAutoSweepSettleBeforeTeleport()
    {
        if (DateTime.UtcNow - autoSweepStageStartedAt < AutoSweepSettleDelay) return;

        var district = AutoSweepDistricts[autoSweepDistrictIndex];
        log.Debug("[HF] WardInfo: auto-sweep-all teleporting to {District}'s gateway.", district);
        Telepo.Instance()->Teleport(AutoSweepGatewayAetheryteId[district], 0);
        autoSweepSawBetweenAreas = false;
        EnterAutoSweepStage(AutoSweepStage.WaitForZoneChange);
    }

    private void EnterAutoSweepStage(AutoSweepStage stage)
    {
        autoSweepStage           = stage;
        autoSweepStageStartedAt  = DateTime.UtcNow;
        autoSweepLastActionAt    = DateTime.MinValue;
    }

    private void AbortAutoSweepDistrict(string reason)
    {
        log.Warning("[HF] WardInfo: auto-sweep-all - {District}: {Reason}; skipping.",
            AutoSweepDistricts[autoSweepDistrictIndex], reason);
        AdvanceAutoSweepDistrict();
    }

    private void HandleAutoSweepStage()
    {
        switch (autoSweepStage)
        {
            case AutoSweepStage.WaitForZoneChange:         HandleAutoSweepWaitForZoneChange(); break;
            case AutoSweepStage.ApproachAetheryte:          HandleAutoSweepApproachAetheryte(); break;
            case AutoSweepStage.InteractWithAetheryte:      HandleAutoSweepInteractWithAetheryte(); break;
            case AutoSweepStage.SelectResidentialDistrict:  HandleAutoSweepSelectEntry(ResidentialDistrictEntryText, AutoSweepStage.SelectGoToWard); break;
            case AutoSweepStage.SelectGoToWard:             HandleAutoSweepSelectEntry(GoToSpecifiedWardEntryText, AutoSweepStage.WaitForHousingMenu); break;
            case AutoSweepStage.WaitForHousingMenu:         HandleAutoSweepWaitForHousingMenu(); break;
            case AutoSweepStage.SettleBeforeTeleport:       HandleAutoSweepSettleBeforeTeleport(); break;
        }
    }

    // Teleport is a channelled cast, so BetweenAreas won't go true immediately - wait until we've
    // actually seen it turn on (the cast completed and the zone change started) before treating a
    // false reading as "arrived", or a slow cast start would look like an instant, wrong arrival.
    private void HandleAutoSweepWaitForZoneChange()
    {
        if (DateTime.UtcNow - autoSweepStageStartedAt > AutoSweepTeleportTimeout)
        {
            AbortAutoSweepDistrict("timed out waiting for the teleport to complete");
            return;
        }

        var betweenAreas = condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51];
        if (betweenAreas) { autoSweepSawBetweenAreas = true; return; }
        if (!autoSweepSawBetweenAreas) return;
        if (!Common.TryGetLocalPlayer(objectTable, out _)) return;

        EnterAutoSweepStage(AutoSweepStage.ApproachAetheryte);
    }

    // A city Aetheryte Plaza is a lot bigger than the tight range InteractWithObject needs, so a
    // fresh teleport usually lands out of interact range - target the nearest aetheryte, lock onto
    // it and use the game's own "move automatically" toggle to walk over, exactly the mechanism
    // Lifestream itself uses for this same approach step (WorldChange.LockOn/EnableAutomove).
    private void HandleAutoSweepApproachAetheryte()
    {
        if (DateTime.UtcNow - autoSweepStageStartedAt > AutoSweepApproachTimeout)
        {
            AbortAutoSweepDistrict("could not reach a nearby aetheryte");
            return;
        }

        if (!Common.TryGetLocalPlayer(objectTable, out var player)) return;
        if (!TryFindNearbyAetheryte(player.Position, AetheryteSearchRange, out var aetheryte)) return;

        if (Vector3.Distance(player.Position, aetheryte!.Position) < AetheryteInteractRange)
        {
            StopAutoMove();
            EnterAutoSweepStage(AutoSweepStage.InteractWithAetheryte);
            return;
        }

        if (DateTime.UtcNow - autoSweepLastActionAt < AutoSweepRetryInterval) return;
        autoSweepLastActionAt = DateTime.UtcNow;

        targetManager.Target = aetheryte;
        Common.ExecuteCommand("/lockon");
        Common.ExecuteCommand("/automove on");
    }

    private void StopAutoMove() => Common.ExecuteCommand("/automove off");

    // Mirrors targeting+interacting with a nearby aetheryte without needing to walk any closer -
    // TargetSystem::InteractWithObject works at range, same as it does for Lifestream's own
    // residential-district automation.
    private void HandleAutoSweepInteractWithAetheryte()
    {
        if (DateTime.UtcNow - autoSweepStageStartedAt > AutoSweepStepTimeout)
        {
            AbortAutoSweepDistrict("lost the nearby aetheryte before interacting");
            return;
        }

        // Let residual movement from /automove actually stop before the first interact attempt.
        if (DateTime.UtcNow - autoSweepStageStartedAt < AutoSweepSettleDelay) return;

        if (TryGetOpenSelectMenu(out _, out _))
        {
            EnterAutoSweepStage(AutoSweepStage.SelectResidentialDistrict);
            return;
        }

        if (DateTime.UtcNow - autoSweepLastActionAt < AutoSweepRetryInterval) return;

        if (!Common.TryGetLocalPlayer(objectTable, out var player)) return;
        if (!TryFindNearbyAetheryte(player.Position, AetheryteInteractRange, out var aetheryte)) return;

        autoSweepLastActionAt = DateTime.UtcNow;
        targetManager.Target  = aetheryte;
        TargetSystem.Instance()->InteractWithObject((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)aetheryte!.Address, false);
    }

    private bool TryFindNearbyAetheryte(Vector3 playerPos, float maxDistance, out IGameObject? aetheryte)
    {
        aetheryte = null;
        var nearestDistSq = maxDistance * maxDistance;
        foreach (var obj in objectTable)
        {
            if (obj.ObjectKind != ObjectKind.Aetheryte || !obj.IsTargetable) continue;
            var distSq = Vector3.DistanceSquared(playerPos, obj.Position);
            if (distSq >= nearestDistSq) continue;
            nearestDistSq = distSq;
            aetheryte = obj;
        }
        return aetheryte != null;
    }

    // Closes whatever native menu might still be open from a timed-out or skipped step - firing a
    // teleport while a SelectString or HousingSelectBlock addon is still open is unreliable, so
    // this always runs before AdvanceAutoSweepDistrict actually teleports anywhere.
    private void CloseLingeringAutoSweepMenus()
    {
        if (TryGetOpenSelectMenu(out var selectMenu, out _))
            selectMenu->Close(true);

        var housing = GetAddon();
        if (housing != null)
            housing->Close(true);
    }

    // Shared by both native SelectString menus in the chain ("Welcome to <City>." then "What would
    // you like to do?") - waits for whichever SelectString is currently open to contain the
    // expected entry text, then fires the same single-int callback the game uses when a player
    // clicks that entry, and advances to whatever comes next.
    private void HandleAutoSweepSelectEntry(string entryText, AutoSweepStage nextStage)
    {
        if (DateTime.UtcNow - autoSweepStageStartedAt > AutoSweepStepTimeout)
        {
            AbortAutoSweepDistrict($"the '{entryText}' menu entry never appeared");
            return;
        }

        if (DateTime.UtcNow - autoSweepLastActionAt < AutoSweepRetryInterval) return;
        autoSweepLastActionAt = DateTime.UtcNow;

        if (!TryGetOpenSelectMenu(out var addon, out var entries))
        {
            log.Debug("[HF] WardInfo: auto-sweep-all - waiting for a menu with '{Entry}'; none open right now.", entryText);
            return;
        }

        // Contains, not StartsWith/Equals - each raw entry carries a leading icon-payload glyph
        // before the label (plain UTF8 decoding doesn't strip SeString payload bytes, so it shows
        // up as a few mojibake characters, e.g. "◆◆F♦ Residential District Aethernet."),
        // and every entry also ends with a trailing period neither is worth hardcoding around.
        var index = entries.FindIndex(e => e.Contains(entryText, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            log.Debug("[HF] WardInfo: auto-sweep-all - menu open but '{Entry}' not among: {Entries}",
                entryText, string.Join(" | ", entries));
            return; // stale popup mid-transition, or not open yet - keep waiting
        }

        var value = new AtkValue { Type = AtkValueType.Int, Int = index };
        addon->FireCallback(1, &value, true);
        log.Debug("[HF] WardInfo: auto-sweep-all - clicked '{Entry}' (index {Index}).", entryText, index);

        EnterAutoSweepStage(nextStage);
    }

    private void HandleAutoSweepWaitForHousingMenu()
    {
        var targetTerritory = (short)HousingDistricts.TerritoryIds[AutoSweepDistricts[autoSweepDistrictIndex]];
        var addon = GetAddon();

        if (addon != null && LastLandIdent?.TerritoryTypeId == targetTerritory && lastLandIdentAt >= autoSweepStageStartedAt)
        {
            autoSweepStage = AutoSweepStage.None;
            StartSweep();
            return;
        }

        if (DateTime.UtcNow - autoSweepStageStartedAt > AutoSweepMenuTimeout)
            AbortAutoSweepDistrict("timed out waiting for the ward-select menu to open");
    }

    // The game renders these choice popups as either of two addons depending on whether any entry
    // carries an icon (e.g. a star marking a registered Favored Destination) - a city aetheryte's
    // "Welcome to <City>." menu is "SelectIconString", while plainer NPC dialogue choices use
    // "SelectString". Both expose the same PopupMenu shape, just at different field offsets, so
    // both are checked and whichever is actually open (has entries) wins. EntryCount > 0 is used
    // as the "genuinely open" signal instead of Common.IsAddonVisible, which is tuned for
    // HousingSelectBlock and isn't a reliable indicator for these two.
    private bool TryGetOpenSelectMenu(out AtkUnitBase* addon, out List<string> entries)
    {
        var selectString = (AddonSelectString*)gameGui.GetAddonByName("SelectString").Address;
        if (selectString != null && selectString->PopupMenu.PopupMenu.EntryCount > 0)
        {
            addon   = (AtkUnitBase*)selectString;
            entries = ReadPopupEntries(&selectString->PopupMenu.PopupMenu);
            return true;
        }

        var selectIconString = (AddonSelectIconString*)gameGui.GetAddonByName("SelectIconString").Address;
        if (selectIconString != null && selectIconString->PopupMenu.PopupMenu.EntryCount > 0)
        {
            addon   = (AtkUnitBase*)selectIconString;
            entries = ReadPopupEntries(&selectIconString->PopupMenu.PopupMenu);
            return true;
        }

        addon   = null;
        entries = [];
        return false;
    }

    private static List<string> ReadPopupEntries(PopupMenu* popup)
    {
        var list = new List<string>();
        for (var i = 0; i < popup->EntryCount; i++)
        {
            var ptr = popup->EntryNames[i];
            if (!ptr.HasValue) continue;
            list.Add(ptr.ToString());
        }
        return list;
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
        IsAutoSweeping = false;
        autoSweepStage = AutoSweepStage.None;
        cache.Clear();
        LastLandIdent = null;
        wardInfoHook?.Dispose();
    }
}
