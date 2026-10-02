using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using LifestreamAddress = (string Name, int World, int City, int Ward, int PropertyType, int Plot, int Apartment, bool ApartmentSubdivision, bool AliasEnabled, string Alias);

namespace HoliestFluffiness.Handlers;

// Walks every apartment building in one housing district (ward 1-30, main then subdivision) and
// pauses at each one with a vacant room, offering to continue. Only ever started from the "Find
// apartment" button on the docked ward menu panel (see WardInfoWindow), and cancellable at any
// point from the progress popup (see ApartmentSweepWindow). It never buys or enters anything.
//
// Route per ward:
//   Lifestream trip to the main apartment (city aetheryte -> ward -> walk to the entrance)
//   -> read room list -> shard hop to the subdivision apartment -> read room list
// Ward 1 instead travels straight from the ward menu the button was clicked on, since it's
// already open. Lifestream is used for the per-ward trip because moving between wards from
// inside a district means going back to a city aetheryte anyway, which it already handles; the
// main -> subdivision hop goes through its HousingAethernetTeleportById IPC instead, avoiding a
// second full trip for a building a single shard away.
//
// If a ward-menu or shard step fails, the building is retried with that same Lifestream trip,
// which can reach it from anywhere. Lifestream would finish every such trip by picking a room and offering to enter it, so the moment the room list
// (MansionSelectRoom) is set up, Lifestream is aborted and the list is read here. Its room pick
// waits for the list data to arrive from the server, which never happens within the setup frame,
// so the abort always wins that race.
public sealed unsafe class ApartmentSweepHandler : IDisposable
{
    public const string RoomListAddonName = "MansionSelectRoom";
    private const string WardMenuAddonName = "HousingSelectBlock";
    private const string YesnoAddonName    = "SelectYesno";

    private const int WardCount = 30;
    public const int BuildingCount = WardCount * 2;
    private const int MaxConsecutiveFailures = 3;

    // MansionSelectRoom AtkValues layout, matching Lifestream's own ReaderMansionSelectRoom: a
    // header (load state, shown section, section count, rooms in this section) followed by one
    // 12-value block per room, 15 rooms per section (one floor per section).
    private const int IdxLoadStatus = 0, IdxSection = 1, IdxSectionCount = 5, IdxSectionRoomCount = 41, IdxFirstRoom = 42;
    private const int RoomStride = 12, RoomsPerSection = 15;
    private const int RoomAccessState = 0, RoomOwner = 4;
    private const long RoomListLoaded = 4;
    private const long AccessStateVacant = 1;

    // HousingAethernet rows per district (one entry each, matched to the district at runtime by
    // the row's TerritoryType): the shards next to the main and subdivision apartment buildings.
    // Same IDs as Lifestream's ResidentialAethernet.
    private static readonly uint[] ApartmentAethernetIds    = [1966132, 1966088, 1966120, 1966104, 1966149];
    private static readonly uint[] ApartmentSubAethernetIds = [1966142, 1966096, 1966128, 1966112, 1966157];

    // EObjName rows whose name is one of the aethernet shard variants; every EObj sharing one of
    // these names is a shard (same lookup Lifestream's Utils.AethernetShards does).
    private static readonly uint[] ShardNameRowIds = [2000151, 2014665, 2014664, 2003395, 2011160];
    private const uint ApartmentEntranceDataId = 2007402;
    private const uint HousingSelectBlockTravelButtonId = 34;

    private const string GoToSpecifiedApartmentText    = "Go to specified apartment";
    private const string TravelToPromptText            = "Travel to";

    private const int LifestreamPropertyApartment = 1;

    private const float ObjectSearchRange       = 60f;
    private const float EntranceInteractRange   = 3.5f;

    private static readonly TimeSpan SettleDelay        = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan RetryInterval      = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ClickInterval      = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan JumpAfter          = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan StepTimeout        = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ApproachTimeout    = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ZoneStartTimeout   = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ZoneTimeout        = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TravelTimeout      = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan TravelStartTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TravelIdleGrace    = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RoomListTimeout    = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SectionSwitchDelay = TimeSpan.FromMilliseconds(100);
    // The list can report its data loaded before the addon accepts input; a floor switch sent in
    // that window is silently dropped. So reading only starts a moment after the addon itself is
    // ready (same readiness check Lifestream makes before clicking it), and a switch that still
    // doesn't take is re-sent.
    private static readonly TimeSpan RoomListSettleDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan SectionRetryDelay   = TimeSpan.FromSeconds(2);

    private enum Stage
    {
        None,
        // Ward travel from an open ward menu
        SelectWard, ClickTravel, ConfirmTravel,
        // Shard hop inside the district
        ApproachShard, AethernetTeleport,
        WaitArrival,
        // Apartment building
        ApproachEntrance, InteractEntrance, SelectGoToApartment, ReadRooms,
        // Lifestream trip to the building from wherever we are
        LifestreamSettle, LifestreamTravel,
    }

    public const string DefaultFoundSound = "Sounds/WardInfo/tada.mp3";

    private readonly Configuration config;
    private readonly string assemblyDir;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IGameGui gameGui;
    private readonly IFramework framework;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly ICondition condition;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    private Stage stage = Stage.None;
    private DateTime stageStartedAt;
    private DateTime lastActionAt;
    private DateTime lastBusyAt;
    private DateTime arrivedAt;
    private bool sawLifestreamBusy;
    private bool sawBetweenAreas;
    private int expectedSection;
    private DateTime loadedAt;
    private int buildingVacant;
    private int buildingFirstVacantRoom;
    private bool walkStarted;
    private int consecutiveFailures;
    private int worldId;
    private int lifestreamCity;
    private uint apartmentAethernetId, apartmentSubAethernetId;
    private uint aethernetTarget;
    private Stage afterAethernet;
    private Stage afterArrival;
    private HashSet<uint>? shardDataIds;
    private readonly List<ApartmentVacancy> found = [];

    public bool IsRunning => stage != Stage.None;
    public string District { get; private set; } = "";
    public int BuildingIndex { get; private set; }
    public int CurrentWard => BuildingIndex / 2 + 1;
    public bool CurrentIsSubdivision => BuildingIndex % 2 == 1;
    public int FoundCount => found.Count;
    private string Division => CurrentIsSubdivision ? "sub" : "main";
    public string Status { get; private set; } = "";
    public ApartmentSweepResult? Result { get; private set; }

    public bool CanContinue => Result is { Outcome: ApartmentSweepOutcome.Found } && BuildingIndex + 1 < BuildingCount;

    public ApartmentSweepHandler(Configuration config, IDalamudPluginInterface pluginInterface, IGameGui gameGui, IFramework framework,
        IAddonLifecycle addonLifecycle, IClientState clientState, IObjectTable objectTable,
        ITargetManager targetManager, ICondition condition, IDataManager dataManager, IPluginLog log)
    {
        this.config          = config;
        this.assemblyDir     = pluginInterface.AssemblyLocation.DirectoryName!;
        this.pluginInterface = pluginInterface;
        this.gameGui         = gameGui;
        this.framework       = framework;
        this.addonLifecycle  = addonLifecycle;
        this.clientState     = clientState;
        this.objectTable     = objectTable;
        this.targetManager   = targetManager;
        this.condition       = condition;
        this.dataManager     = dataManager;
        this.log             = log;

        framework.Update += OnUpdate;
        clientState.Logout += OnLogout;
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, RoomListAddonName, OnRoomListSetup);
    }

    public bool IsLifestreamAvailable
    {
        get
        {
            try { pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc(); return true; }
            catch { return false; }
        }
    }

    public bool CanStart(LandIdent? ward) =>
        !IsRunning && Result == null && ward != null &&
        HousingDistricts.FromTerritoryId((ushort)ward.TerritoryTypeId) != null && IsLifestreamAvailable;

    public void Start(LandIdent ward)
    {
        if (!CanStart(ward)) return;
        if (!Common.TryGetLocalPlayer(objectTable, out var player)) return;

        var territory = (ushort)ward.TerritoryTypeId;
        District       = HousingDistricts.FromTerritoryId(territory)!;
        lifestreamCity = (int)HousingDistricts.GatewayAetheryteIds[District];
        worldId        = (int)player.CurrentWorld.RowId;

        apartmentAethernetId    = ResolveAethernet(ApartmentAethernetIds, territory);
        apartmentSubAethernetId = ResolveAethernet(ApartmentSubAethernetIds, territory);

        BuildingIndex       = 0;
        consecutiveFailures = 0;
        Result              = null;
        found.Clear();

        log.Debug("[HF] ApartmentSweep: started for {District:l} on world {World} (aethernet {Apt}/{Sub}).",
            District, worldId, apartmentAethernetId, apartmentSubAethernetId);

        // The button lives on the docked ward menu panel, so the menu is normally open already.
        if (HasFastRoute && GetAddon(WardMenuAddonName) != null)
            EnterStage(Stage.SelectWard, "Selecting ward 1");
        else
            EnterLifestreamTravel();
    }

    // Resumes from the building after the one just reported, keeping everything found so far.
    public void Continue()
    {
        if (!CanContinue) return;
        Result = null;
        consecutiveFailures = 0;
        AdvanceBuilding();
    }

    public void Cancel()
    {
        if (!IsRunning) return;

        StopEverything();
        stage  = Stage.None;
        Status = "";
        log.Debug("[HF] ApartmentSweep: cancelled at building {Index}.", BuildingIndex);
    }

    public void ClearResult()
    {
        Result = null;
        found.Clear();
    }

    private bool HasFastRoute => apartmentAethernetId != 0 && apartmentSubAethernetId != 0;

    private uint ResolveAethernet(uint[] ids, ushort territory)
    {
        var sheet = dataManager.GetExcelSheet<HousingAethernet>();
        return ids.FirstOrDefault(id => sheet.GetRowOrDefault(id)?.TerritoryType.RowId == territory);
    }

    private void OnLogout(int type, int code)
    {
        Cancel();
        ClearResult();
    }

    private void EnterStage(Stage next, string? status = null)
    {
        stage          = next;
        stageStartedAt = DateTime.UtcNow;
        lastActionAt   = DateTime.MinValue;
        walkStarted    = false;
        if (status != null) Status = status;
    }

    private void OnUpdate(IFramework _)
    {
        switch (stage)
        {
            case Stage.SelectWard:                HandleSelectWard(); break;
            case Stage.ClickTravel:               HandleClickTravel(); break;
            case Stage.ConfirmTravel:             HandleConfirmTravel(); break;
            case Stage.ApproachShard:             HandleApproachShard(); break;
            case Stage.AethernetTeleport:         HandleAethernetTeleport(); break;
            case Stage.WaitArrival:               HandleWaitArrival(); break;
            case Stage.ApproachEntrance:          HandleApproachEntrance(); break;
            case Stage.InteractEntrance:          HandleInteractEntrance(); break;
            case Stage.SelectGoToApartment:       HandleSelectEntry(GoToSpecifiedApartmentText, BeginReadingRooms); break;
            case Stage.ReadRooms:                 HandleReadRooms(); break;
            case Stage.LifestreamSettle:          HandleLifestreamSettle(); break;
            case Stage.LifestreamTravel:          HandleLifestreamTravel(); break;
        }
    }

    private bool StageTimedOut(TimeSpan timeout) => DateTime.UtcNow - stageStartedAt > timeout;

    private bool ReadyForAction(TimeSpan interval)
    {
        if (DateTime.UtcNow - lastActionAt < interval) return false;
        lastActionAt = DateTime.UtcNow;
        return true;
    }

    // ── Ward travel ─────────────────────────────────────────────────────────

    private void HandleSelectWard()
    {
        if (StageTimedOut(StepTimeout)) { FailRoute("ward menu never became usable"); return; }

        var addon = GetAddon(WardMenuAddonName);
        if (addon == null) return;

        if (lastActionAt == DateTime.MinValue)
        {
            // Values are [1 = select ward, 0-based ward index], same as WardInfoHandler.SelectWard.
            FireIntCallback(addon, 1, CurrentWard - 1);
            lastActionAt = DateTime.UtcNow;
            return;
        }

        // Give the menu a moment to load the selected ward before pressing Travel.
        if (DateTime.UtcNow - lastActionAt < RetryInterval) return;
        EnterStage(Stage.ClickTravel, $"Travelling to ward {CurrentWard}");
    }

    private void HandleClickTravel()
    {
        if (StageTimedOut(StepTimeout)) { FailRoute("could not press Travel on the ward menu"); return; }
        if (GetAddon(YesnoAddonName) != null) { EnterStage(Stage.ConfirmTravel); return; }

        var addon = GetAddon(WardMenuAddonName);
        if (addon == null) return;

        var button = addon->GetComponentButtonById(HousingSelectBlockTravelButtonId);
        if (button == null || !button->IsEnabled || !ReadyForAction(ClickInterval)) return;
        Common.ClickButton(addon, button);
    }

    private void HandleConfirmTravel()
    {
        if (StageTimedOut(StepTimeout)) { FailRoute("the travel confirmation never appeared"); return; }

        if (IsBetweenAreas())
        {
            AwaitArrival(Stage.ApproachShard, apartmentAethernetId, Stage.ApproachEntrance);
            return;
        }

        var yesno = (AddonSelectYesno*)GetAddon(YesnoAddonName);
        if (yesno == null || yesno->YesButton == null || !yesno->YesButton->IsEnabled) return;
        if (!ReadString((AtkUnitBase*)yesno, 0).Contains(TravelToPromptText, StringComparison.OrdinalIgnoreCase)) return;
        if (!ReadyForAction(ClickInterval)) return;

        Common.ClickButton((AtkUnitBase*)yesno, yesno->YesButton);
    }

    // ── Shard hops ──────────────────────────────────────────────────────────

    private void GoAethernet(uint destination, Stage next, string status)
    {
        aethernetTarget = destination;
        afterAethernet  = next;
        EnterStage(Stage.ApproachShard, status);
    }

    // Walks to the nearest shard (or aetheryte) until Lifestream reports one in range,
    // since its aethernet teleport only works from there.
    private void HandleApproachShard()
    {
        if (StageTimedOut(ApproachTimeout)) { FailRoute("could not reach an aethernet shard"); return; }
        if (DateTime.UtcNow - stageStartedAt < TimeSpan.FromMilliseconds(500)) return;

        if (GetActiveResidentialAetheryte() != 0)
        {
            StopAutoMove();
            EnterStage(Stage.AethernetTeleport);
            return;
        }

        if (!Common.TryGetLocalPlayer(objectTable, out var player)) return;
        var shards = ShardDataIds;
        var target = FindNearest(player.Position, o =>
            o.ObjectKind == ObjectKind.Aetheryte ||
            (o.ObjectKind == ObjectKind.EventObj && shards.Contains(o.BaseId)));
        if (target != null) WalkTowards(target);
    }

    private void HandleAethernetTeleport()
    {
        if (StageTimedOut(StepTimeout)) { FailRoute("the aethernet teleport was refused"); return; }
        if (IsLifestreamBusy() || !ReadyForAction(ClickInterval)) return;

        bool accepted;
        try
        {
            accepted = pluginInterface.GetIpcSubscriber<uint, bool>("Lifestream.HousingAethernetTeleportById").InvokeFunc(aethernetTarget);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[HF] ApartmentSweep: HousingAethernetTeleportById failed.");
            FailRoute("Lifestream aethernet IPC failed");
            return;
        }

        if (accepted) AwaitArrival(afterAethernet);
    }

    private void AwaitArrival(Stage next, uint thenAethernet = 0, Stage thenAfterAethernet = Stage.None)
    {
        afterArrival = next;
        if (thenAethernet != 0)
        {
            aethernetTarget = thenAethernet;
            afterAethernet  = thenAfterAethernet;
        }
        sawBetweenAreas = false;
        arrivedAt       = DateTime.MinValue;
        EnterStage(Stage.WaitArrival);
    }

    private void HandleWaitArrival()
    {
        if (StageTimedOut(ZoneTimeout)) { FailRoute("timed out waiting for the zone change"); return; }

        if (IsBetweenAreas()) { sawBetweenAreas = true; return; }
        if (!sawBetweenAreas)
        {
            if (StageTimedOut(ZoneStartTimeout)) FailRoute("the zone change never started");
            return;
        }
        if (!Common.TryGetLocalPlayer(objectTable, out _)) return;

        if (arrivedAt == DateTime.MinValue) arrivedAt = DateTime.UtcNow;
        if (DateTime.UtcNow - arrivedAt < SettleDelay || IsLifestreamBusy()) return;

        EnterStage(afterArrival, afterArrival switch
        {
            Stage.ApproachShard     => "Heading to the apartment shard",
            Stage.ApproachEntrance  => "Walking to the apartment entrance",
            _                       => null,
        });
    }

    // ── Apartment building ──────────────────────────────────────────────────

    private void HandleApproachEntrance()
    {
        if (StageTimedOut(ApproachTimeout)) { FailRoute("could not reach the apartment entrance"); return; }
        if (!Common.TryGetLocalPlayer(objectTable, out var player)) return;

        var entrance = FindNearest(player.Position, o => o.ObjectKind == ObjectKind.EventObj && o.BaseId == ApartmentEntranceDataId);
        if (entrance == null) return;

        if (Vector3.Distance(player.Position, entrance.Position) < EntranceInteractRange)
        {
            StopAutoMove();
            EnterStage(Stage.InteractEntrance);
            return;
        }

        WalkTowards(entrance);
    }

    private void HandleInteractEntrance()
    {
        if (StageTimedOut(StepTimeout)) { FailRoute("the apartment entrance menu never opened"); return; }
        if (DateTime.UtcNow - stageStartedAt < TimeSpan.FromMilliseconds(500)) return;

        if (Common.TryGetOpenSelectMenu(gameGui, out _, out _)) { EnterStage(Stage.SelectGoToApartment); return; }
        if (!ReadyForAction(ClickInterval)) return;
        if (!Common.TryGetLocalPlayer(objectTable, out var player)) return;

        var entrance = FindNearest(player.Position, o => o.ObjectKind == ObjectKind.EventObj && o.BaseId == ApartmentEntranceDataId);
        if (entrance == null) return;
        Interact(entrance);
    }

    private void OnRoomListSetup(AddonEvent type, AddonArgs args)
    {
        if (stage == Stage.LifestreamTravel) BeginReadingRooms();
    }

    private void BeginReadingRooms()
    {
        if (stage == Stage.LifestreamTravel) AbortLifestream();
        expectedSection         = 0;
        loadedAt                = DateTime.MinValue;
        buildingVacant          = 0;
        buildingFirstVacantRoom = 0;
        EnterStage(Stage.ReadRooms, "Reading room list");
        lastActionAt = DateTime.UtcNow;
    }

    private void HandleReadRooms()
    {
        var now = DateTime.UtcNow;
        if (StageTimedOut(RoomListTimeout))
        {
            log.Warning("[HF] ApartmentSweep: {District:l} W{Ward} {Division:l}: room list never finished loading; skipping.",
                District, CurrentWard, Division);
            if (++consecutiveFailures >= MaxConsecutiveFailures) { FinishFailed("the room list never finished loading"); return; }
            AdvanceBuilding();
            return;
        }

        var addon = GetAddon(RoomListAddonName);
        if (addon == null || addon->AtkValuesCount <= IdxFirstRoom) return;
        if (!IsAddonReady(addon) || ReadNumber(addon, IdxLoadStatus) != RoomListLoaded) return;
        if (loadedAt == DateTime.MinValue) loadedAt = now;
        if (now - loadedAt < RoomListSettleDelay || now - lastActionAt < SectionSwitchDelay) return;

        var section = (int)(ReadNumber(addon, IdxSection) ?? -1);
        if (section != expectedSection)
        {
            if (expectedSection > 0 && now - lastActionAt > SectionRetryDelay)
            {
                log.Debug("[HF] ApartmentSweep: floor {Floor} request didn't take, re-sending.", expectedSection + 1);
                FireIntCallback(addon, 1, expectedSection);
                lastActionAt = now;
            }
            return;
        }

        var sectionCount = (int)(ReadNumber(addon, IdxSectionCount) ?? 0);
        var roomCount    = Math.Clamp((int)(ReadNumber(addon, IdxSectionRoomCount) ?? 0), 0, RoomsPerSection);

        var firstVacant = -1;
        var vacant = 0;
        for (var i = 0; i < roomCount; i++)
        {
            var baseIdx = IdxFirstRoom + i * RoomStride;
            if (baseIdx + RoomOwner >= addon->AtkValuesCount) break;

            // Same vacancy test Lifestream uses before refusing to enter a room.
            var isVacant = ReadString(addon, baseIdx + RoomOwner).Length == 0 ||
                           ReadNumber(addon, baseIdx + RoomAccessState) == AccessStateVacant;
            if (!isVacant) continue;
            vacant++;
            if (firstVacant < 0) firstVacant = i;
        }

        log.Debug("[HF] ApartmentSweep: {District:l} W{Ward} {Division:l} floor {Section}/{Sections}: {Rooms} rooms, {Vacant} vacant.",
            District, CurrentWard, Division, section + 1, sectionCount, roomCount, vacant);

        // Keep reading the remaining floors even after a hit, so the report covers the whole building.
        buildingVacant += vacant;
        if (vacant > 0 && buildingFirstVacantRoom == 0)
            buildingFirstVacantRoom = section * RoomsPerSection + firstVacant + 1;

        if (section + 1 < sectionCount)
        {
            expectedSection = section + 1;
            FireIntCallback(addon, 1, expectedSection);
            stageStartedAt = now;
            lastActionAt   = now;
            Status         = $"Reading room list (floor {expectedSection + 1})";
            return;
        }

        consecutiveFailures = 0;

        if (buildingVacant > 0)
        {
            found.Add(new ApartmentVacancy(CurrentWard, CurrentIsSubdivision, buildingFirstVacantRoom, buildingVacant));
            if (config.WardInfoApartmentSound)
                SoundEngine.Play(SoundEngine.Resolve(config.WardInfoApartmentSoundPath, DefaultFoundSound, assemblyDir),
                    config.WardInfoApartmentSoundVolume);
            // The room list is deliberately left open so the vacancy can be seen in-game.
            Finish(ApartmentSweepOutcome.Found, BuildingIndex + 1, null);
            return;
        }

        AdvanceBuilding();
    }

    // Main -> subdivision stays in the same ward, so it's a single shard hop; subdivision -> next
    // ward is a Lifestream trip via the city aetheryte.
    private void AdvanceBuilding()
    {
        CloseAddon(RoomListAddonName);

        if (BuildingIndex + 1 >= BuildingCount)
        {
            Finish(ApartmentSweepOutcome.Exhausted, BuildingCount, null);
            return;
        }

        BuildingIndex++;
        if (CurrentIsSubdivision && HasFastRoute)
            GoAethernet(apartmentSubAethernetId, Stage.ApproachEntrance, "Heading to the subdivision apartment");
        else
            EnterLifestreamTravel();
    }

    // Shared by every SelectString/SelectIconString step: waits for the open menu to contain the
    // expected entry, then fires the same single-int callback a click on that entry does.
    private void HandleSelectEntry(string entryText, System.Action next)
    {
        if (StageTimedOut(StepTimeout)) { FailRoute($"the '{entryText}' menu entry never appeared"); return; }
        if (!ReadyForAction(RetryInterval)) return;
        if (!Common.TryGetOpenSelectMenu(gameGui, out var addon, out var entries)) return;

        var index = entries.FindIndex(e => e.Contains(entryText, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;

        var value = new AtkValue { Type = AtkValueType.Int, Int = index };
        addon->FireCallback(1, &value, true);
        next();
    }

    // ── Lifestream trip ─────────────────────────────────────────────────

    private void EnterLifestreamTravel()
    {
        StopAutoMove();
        CloseLingeringMenus();
        EnterStage(Stage.LifestreamSettle, $"Travelling to ward {CurrentWard} via the city aetheryte");
    }

    private void HandleLifestreamSettle()
    {
        if (DateTime.UtcNow - stageStartedAt < SettleDelay) return;
        if (IsLifestreamBusy()) return;

        LifestreamAddress entry = ("", worldId, lifestreamCity, CurrentWard, LifestreamPropertyApartment,
            1, 1, CurrentIsSubdivision, false, "");
        try
        {
            pluginInterface.GetIpcSubscriber<LifestreamAddress, object>("Lifestream.GoToHousingAddress").InvokeAction(entry);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[HF] ApartmentSweep: GoToHousingAddress failed.");
            FinishFailed("Could not reach Lifestream. Is it installed and loaded?");
            return;
        }

        sawLifestreamBusy = false;
        EnterStage(Stage.LifestreamTravel);
    }

    private void HandleLifestreamTravel()
    {
        if (GetAddon(RoomListAddonName) != null)
        {
            BeginReadingRooms();
            return;
        }

        var now = DateTime.UtcNow;
        if (IsLifestreamBusy())
        {
            sawLifestreamBusy = true;
            lastBusyAt        = now;
        }
        else if (sawLifestreamBusy ? now - lastBusyAt > TravelIdleGrace : now - stageStartedAt > TravelStartTimeout)
        {
            FailLifestream("Lifestream stopped before the room list opened");
            return;
        }

        if (now - stageStartedAt > TravelTimeout)
        {
            AbortLifestream();
            FailLifestream("timed out travelling");
        }
    }

    // ── Failure handling ────────────────────────────────────────────────────

    // A step of the in-district route went wrong: retry this same building through Lifestream,
    // which can reach it from wherever the character ended up.
    private void FailRoute(string reason)
    {
        log.Warning("[HF] ApartmentSweep: {District:l} W{Ward} {Division:l}: {Reason} ({Stage}); retrying via Lifestream.",
            District, CurrentWard, Division, reason, stage);
        if (++consecutiveFailures >= MaxConsecutiveFailures) { FinishFailed(reason); return; }
        EnterLifestreamTravel();
    }

    // Lifestream itself couldn't reach this building either: skip it.
    private void FailLifestream(string reason)
    {
        log.Warning("[HF] ApartmentSweep: {District:l} W{Ward} {Division:l}: {Reason}; skipping.",
            District, CurrentWard, Division, reason);
        if (++consecutiveFailures >= MaxConsecutiveFailures) { FinishFailed(reason); return; }

        if (BuildingIndex + 1 >= BuildingCount) { Finish(ApartmentSweepOutcome.Exhausted, BuildingCount, null); return; }
        BuildingIndex++;
        EnterLifestreamTravel();
    }

    private void FinishFailed(string reason)
    {
        StopEverything();
        Finish(ApartmentSweepOutcome.Failed, BuildingIndex, $"Stopped after {consecutiveFailures} failed attempts in a row (last: {reason}).");
    }

    private void Finish(ApartmentSweepOutcome outcome, int checkedCount, string? error)
    {
        stage  = Stage.None;
        Status = "";
        Result = new ApartmentSweepResult(outcome, District, found.ToArray(), checkedCount, error);
        log.Debug("[HF] ApartmentSweep: finished: {Outcome} after {Checked} buildings, {Found} found.", outcome, checkedCount, found.Count);
    }

    private void StopEverything()
    {
        if (IsLifestreamBusy()) AbortLifestream();
        StopAutoMove();
    }

    private void CloseLingeringMenus()
    {
        if (Common.TryGetOpenSelectMenu(gameGui, out var menu, out _)) menu->Close(true);
        CloseAddon(WardMenuAddonName);
        CloseAddon(RoomListAddonName);
    }

    // ── Movement / interaction ──────────────────────────────────────────────

    private IGameObject? FindNearest(Vector3 from, Func<IGameObject, bool> predicate)
    {
        IGameObject? best = null;
        var bestDistSq = ObjectSearchRange * ObjectSearchRange;
        foreach (var obj in objectTable)
        {
            if (!obj.IsTargetable || !predicate(obj)) continue;
            var distSq = Vector3.DistanceSquared(from, obj.Position);
            if (distSq >= bestDistSq) continue;
            bestDistSq = distSq;
            best = obj;
        }
        return best;
    }

    // Target, lock on and start the game's own automove once per approach, the same way Lifestream
    // does; the target is already found at range, so there's nothing to re-select. If we still
    // haven't arrived after a few seconds, hop now and then in case scenery is in the way.
    private void WalkTowards(IGameObject target)
    {
        if (!walkStarted)
        {
            walkStarted          = true;
            lastActionAt         = DateTime.UtcNow;
            targetManager.Target = target;
            Common.ExecuteCommand("/lockon");
            Common.ExecuteCommand("/automove on");
            return;
        }

        if (DateTime.UtcNow - stageStartedAt > JumpAfter && ReadyForAction(RetryInterval))
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 2);
    }

    private void Interact(IGameObject target)
    {
        if (targetManager.Target?.Address != target.Address) targetManager.Target = target;
        TargetSystem.Instance()->InteractWithObject((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)target.Address, false);
    }

    private static void StopAutoMove() => Common.ExecuteCommand("/automove off");

    private bool IsBetweenAreas() => condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51];

    private HashSet<uint> ShardDataIds
    {
        get
        {
            if (shardDataIds != null) return shardDataIds;

            var sheet = dataManager.GetExcelSheet<EObjName>();
            var names = ShardNameRowIds
                .Select(id => sheet.GetRowOrDefault(id)?.Singular.ExtractText())
                .Where(n => !string.IsNullOrEmpty(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            shardDataIds = sheet.Where(r => names.Contains(r.Singular.ExtractText())).Select(r => r.RowId).ToHashSet();
            return shardDataIds;
        }
    }

    // ── Lifestream IPC ──────────────────────────────────────────────────────

    // Lifestream's own abort leaves /automove running if it was mid-approach.
    private void AbortLifestream()
    {
        try { pluginInterface.GetIpcSubscriber<object>("Lifestream.Abort").InvokeAction(); }
        catch (Exception ex) { log.Warning(ex, "[HF] ApartmentSweep: Lifestream.Abort failed."); }
        StopAutoMove();
    }

    private bool IsLifestreamBusy()
    {
        try { return pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc(); }
        catch { return false; }
    }

    private uint GetActiveResidentialAetheryte()
    {
        try { return pluginInterface.GetIpcSubscriber<uint>("Lifestream.GetActiveResidentialAetheryte").InvokeFunc(); }
        catch { return 0; }
    }

    // ── Addon helpers ───────────────────────────────────────────────────────

    private AtkUnitBase* GetAddon(string name)
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(name).Address;
        return addon != null && addon->IsVisible ? addon : null;
    }

    private static bool IsAddonReady(AtkUnitBase* addon) =>
        addon->IsReady && addon->UldManager.LoadedState == AtkLoadState.Loaded;

    private void CloseAddon(string name)
    {
        var addon = GetAddon(name);
        if (addon != null) addon->Close(true);
    }

    private static void FireIntCallback(AtkUnitBase* addon, int a, int b)
    {
        Span<AtkValue> values = stackalloc AtkValue[2];
        values[0] = new AtkValue { Type = AtkValueType.Int, Int = a };
        values[1] = new AtkValue { Type = AtkValueType.Int, Int = b };
        fixed (AtkValue* ptr = values)
            addon->FireCallback(2, ptr, true);
    }

    private static long? ReadNumber(AtkUnitBase* addon, int index)
    {
        if (index >= addon->AtkValuesCount) return null;
        var value = addon->AtkValues[index];
        return value.Type switch
        {
            AtkValueType.Int  => value.Int,
            AtkValueType.UInt => value.UInt,
            _                 => null,
        };
    }

    private static string ReadString(AtkUnitBase* addon, int index)
    {
        if (index >= addon->AtkValuesCount) return string.Empty;
        var value = addon->AtkValues[index];
        if (value.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString)) return string.Empty;
        return value.String.ToString();
    }

    public void Dispose()
    {
        if (IsRunning) StopEverything();
        stage = Stage.None;
        framework.Update -= OnUpdate;
        clientState.Logout -= OnLogout;
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup, RoomListAddonName, OnRoomListSetup);
    }
}

public enum ApartmentSweepOutcome { Found, Exhausted, Failed }

// Room is the first vacant room number; Vacant is the total across every floor of the building.
public sealed record ApartmentVacancy(int Ward, bool Subdivision, int Room, int Vacant);

// Found holds every vacancy collected so far this run (Continue keeps adding to it).
public sealed record ApartmentSweepResult(ApartmentSweepOutcome Outcome, string District, IReadOnlyList<ApartmentVacancy> Found, int Checked, string? Error);
