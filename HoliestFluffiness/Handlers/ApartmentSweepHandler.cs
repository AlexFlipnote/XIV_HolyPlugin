using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
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
// already open. The main -> subdivision hop goes through Lifestream's HousingAethernetTeleportById
// IPC instead, avoiding a second full trip for a building a single shard away.
//
// Districts with a known ward exit (see WardExitRoutes) skip the city trip between wards too,
// which costs a teleport each time: shard hop to the ward entrance aetheryte, walk to the NPC
// there that offers "Go to specified ward", and travel to the next ward from its ward menu.
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
    // The aetheryte next to where ward travel drops you (Lifestream's StartingAetherytes).
    private static readonly uint[] WardEntranceAethernetIds = [1966103, 1966081, 1966118, 1966145, 1966129];

    // Walk from the ward entrance aetheryte to whatever offers "Go to specified ward". With TalkToNpc
    // that's whichever event NPC stands nearest the last point; without, it's the district exit
    // itself, whose menu pops up on walking into it, so the last point sits a few yalms past the
    // exit line to make sure the walk actually crosses it. Points taken in-game. TargetDelay is how
    // long into the last leg the NPC is reliably loaded and can be targeted.
    private sealed record WardExitRoute(string Name, Vector3[] Path, bool TalkToNpc, double TargetDelay = 0);
    private static readonly Dictionary<string, WardExitRoute> WardExitRoutes = new()
    {
        ["Mist"]      = new("the district exit", [new(-10.490199f, 48.996967f, -126.59106f), new(-10.059337f, 48.346664f, -169.46365f),
                                                  new(-10.0f, 48.346664f, -174.5f)], TalkToNpc: false),
        ["The Goblet"] = new("the district exit", [new(-10.556103f, -11.076661f, -198.26257f), new(-14.58f, -11.076661f, -201.23f)], TalkToNpc: false),
        ["The Lavender Beds"] = new("the ferry", [new(4.1270623f, 2.6108832f, 193.42778f), new(10.623131f, 2.6109006f, 205.35211f)], TalkToNpc: true, TargetDelay: 1),
        ["Shirogane"] = new("the ferry", [new(-101.64072f, 2.0699985f, 126.58039f), new(-118.73602f, 2.02f, 153.73157f)], TalkToNpc: true, TargetDelay: 3),
        ["Empyreum"]  = new("Odilie", [new(40.248085f, -15.400002f, 174.06548f), new(17.930742f, -15.200001f, 181.06882f)], TalkToNpc: true, TargetDelay: 1),
    };
    private const float WardExitNpcRange = 6f;
    // Close enough to talk to the NPC; the walk hands over the moment it gets here instead of
    // finishing the last leg.
    private const float WardExitTalkRange = 4f;

    // EObjName rows whose name is one of the aethernet shard variants; every EObj sharing one of
    // these names is a shard (same lookup Lifestream's Utils.AethernetShards does).
    private static readonly uint[] ShardNameRowIds = [2000151, 2014665, 2014664, 2003395, 2011160];
    private const uint ApartmentEntranceDataId = 2007402;
    private const uint HousingSelectBlockTravelButtonId = 34;

    private const string GoToSpecifiedApartmentText    = "Go to specified apartment";
    private const string GoToSpecifiedWardText         = "Go to specified ward";
    private const string TalkAddonName                 = "Talk";
    private const string TravelToPromptText            = "Travel to";

    private const int LifestreamPropertyApartment = 1;

    private const float ObjectSearchRange       = 60f;
    private const float EntranceInteractRange   = 3.5f;
    private const float LeadInPointReached      = 1.5f;
    private const float WalkStallDistance       = 0.5f;
    private const float ShardSnugRange          = 2f;
    private const float ShardPressDistance      = 0.15f;

    // Sprint, plus the statuses Lifestream also treats as "already sprinting".
    private const uint SprintActionId = 3;
    private static readonly uint[] SprintStatusIds = [50, 1199, 4209];

    // Ward travel drops you at the district entrance, not at a shard. Shirogane and the Lavender
    // Beds use Lifestream's TaskApproachHousingAetheryte steps for getting clear of it: Shirogane
    // runs straight ahead until it's out of the entrance corridor (Z < 128), the Lavender Beds
    // runs ahead until a shard is in sight. Empyreum walks to a point clear of the entrance (taken
    // in-game; Lifestream's own waypoint there kept getting caught on the scenery), from where the
    // shard step's straight lock-on run has a clear line to the shard.
    private static readonly Vector3[] EmpyreumPlazaPath = [new(23.849737f, -15.200001f, 179.62442f)];
    private const float ShiroganePlazaExitZ  = 128f;
    private const float LavenderShardInSight = 9.35f;

    // Shirogane's apartment shards drop you where a straight run at the entrance can wedge against
    // the side of the staircase, so walk to the middle of the stairs first. Lifestream has no
    // equivalent; this is the main-area point, mirrored from one taken in-game in the subdivision.
    private static readonly Vector3 ShiroganeStairsPoint = SubdivisionToMain(new(-690.1085f, 25.05f, -719.8623f));

    // Every district's subdivision is the main area turned 90 degrees and shifted by -704 on X and
    // Z, at the same height (fits all 60 plot fronts in Lifestream's HousingData to within 0.7y).
    private const float SubdivisionOffset = -704f;

    private static readonly TimeSpan SettleDelay        = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan RetryInterval      = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ClickInterval      = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TalkClickInterval  = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan WalkStallCheck     = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan ShardPressCheck    = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StepTimeout        = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ApproachTimeout    = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LeadInTimeout      = TimeSpan.FromSeconds(15);
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
        // District-specific walk before heading for the shard or entrance
        LeadIn,
        // Ward exit: walk to the ward-travel NPC and open its ward menu
        WalkWardExit, TalkWardExit,
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
    private readonly PointWalker pointWalker;
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
    private bool lockedOn;
    private DateTime lastLockOnAt;
    private DateTime walkCheckAt;
    private DateTime shardCheckAt;
    private Vector3 shardCheckPos;
    private Vector3 walkCheckPos;
    private int consecutiveFailures;
    private int worldId;
    private int lifestreamCity;
    private uint apartmentAethernetId, apartmentSubAethernetId, wardEntranceAethernetId;
    private WardExitRoute? wardExitRoute;
    private int wardExitPathIndex;
    private DateTime wardExitLegStartedAt;
    private uint aethernetTarget;
    private Stage afterAethernet;
    private Stage afterArrival;
    private LeadInStep? leadIn;
    private int leadInPathIndex;
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
        ITargetManager targetManager, ICondition condition, IDataManager dataManager, PointWalker pointWalker, IPluginLog log)
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
        this.pointWalker     = pointWalker;
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
        wardEntranceAethernetId = ResolveAethernet(WardEntranceAethernetIds, territory);
        wardExitRoute           = WardExitRoutes.GetValueOrDefault(District);

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
    private bool HasWardExitRoute => HasFastRoute && wardEntranceAethernetId != 0 && wardExitRoute != null && pointWalker.IsAvailable;

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
        lockedOn       = false;
        pointWalker.Destination = null;
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
            case Stage.LeadIn:                    HandleLeadIn(); break;
            case Stage.WalkWardExit:              HandleWalkWardExit(); break;
            case Stage.TalkWardExit:              HandleTalkWardExit(); break;
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
        if (!Common.TryGetLocalPlayer(objectTable, out var player)) return;

        var target = FindNearest(player.Position, IsShardOrAetheryte);
        var now    = DateTime.UtcNow;

        // Lifestream counts a shard as in reach from 4.6y, but the game refuses the interaction
        // ("Too far away") from the edge of that. So once Lifestream says it's in reach, keep
        // running at the shard until up against it (barely moving any more) or right next to it,
        // then hop straight away. Aetherytes are big enough that Lifestream's range is fine.
        if (GetActiveResidentialAetheryte() != 0)
        {
            var snug = target == null || target.ObjectKind == ObjectKind.Aetheryte ||
                       Vector3.Distance(player.Position, target.Position) < ShardSnugRange;
            var pressed = walkStarted && now - shardCheckAt >= ShardPressCheck &&
                          Vector3.Distance(player.Position, shardCheckPos) < ShardPressDistance;
            if (snug || pressed)
            {
                StopAutoMove();
                EnterStage(Stage.AethernetTeleport);
                return;
            }
        }

        if (now - shardCheckAt >= ShardPressCheck)
        {
            shardCheckAt  = now;
            shardCheckPos = player.Position;
        }

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
        if (!Common.TryGetLocalPlayer(objectTable, out var player)) return;

        if (arrivedAt == DateTime.MinValue) arrivedAt = DateTime.UtcNow;
        if (DateTime.UtcNow - arrivedAt < SettleDelay || IsLifestreamBusy()) return;

        var status = afterArrival switch
        {
            Stage.ApproachShard     => "Heading to the apartment shard",
            Stage.ApproachEntrance  => "Walking to the apartment entrance",
            _                       => null,
        };

        leadIn = afterArrival switch
        {
            Stage.ApproachShard    => PlazaLeadIn(),
            Stage.ApproachEntrance => ApartmentLeadIn(player.Position),
            _                      => null,
        };
        if (leadIn is { Path: not null } && !pointWalker.IsAvailable) leadIn = null;

        leadInPathIndex = 0;
        EnterStage(leadIn != null ? Stage.LeadIn : afterArrival, status);
    }

    // ── District lead-ins ───────────────────────────────────────────────────

    // Either walk Path point by point until the last one is reached, or (Path null) run straight
    // ahead with /automove until Done.
    private sealed record LeadInStep(string Name, Vector3[]? Path, Func<Vector3, bool>? Done = null);

    // Ward travel arrival -> first shard.
    private LeadInStep? PlazaLeadIn() => District switch
    {
        "Empyreum"          => new("Empyreum plaza", EmpyreumPlazaPath),
        "Shirogane"         => new("Shirogane plaza", null, p => p.Z < ShiroganePlazaExitZ),
        "The Lavender Beds" => new("Lavender Beds plaza", null, p => FindNearest(p, IsShard, LavenderShardInSight) != null),
        _                   => null,
    };

    // Shard arrival -> apartment entrance. Only used when the point is actually nearby, so a drop
    // somewhere unexpected still goes straight for the entrance.
    private LeadInStep? ApartmentLeadIn(Vector3 from) =>
        StairsPointNear(from) is { } stairs ? new("Shirogane stairs", [stairs]) : null;

    // The Shirogane stairs point (main or subdivision) within reach of a spot, if any.
    private Vector3? StairsPointNear(Vector3 from)
    {
        if (District != "Shirogane") return null;
        foreach (var point in (Vector3[])[ShiroganeStairsPoint, MainToSubdivision(ShiroganeStairsPoint)])
            if (HorizontalDistance(from, point) <= ObjectSearchRange) return point;
        return null;
    }

    private static Vector3 MainToSubdivision(Vector3 p) => new(SubdivisionOffset - p.Z, p.Y, p.X + SubdivisionOffset);
    private static Vector3 SubdivisionToMain(Vector3 p) => new(p.Z - SubdivisionOffset, p.Y, SubdivisionOffset - p.X);

    // A lead-in that times out hands over to the normal approach instead of failing the building;
    // it only exists to give that approach a better starting spot.
    private void HandleLeadIn()
    {
        if (leadIn == null) { EnterStage(afterArrival); return; }
        if (!Common.TryGetLocalPlayer(objectTable, out var player)) return;

        var path = leadIn.Path;
        while (path != null && leadInPathIndex < path.Length &&
               HorizontalDistance(player.Position, path[leadInPathIndex]) < LeadInPointReached)
        {
            leadInPathIndex++;
            if (leadInPathIndex < path.Length) pointWalker.Destination = path[leadInPathIndex];
        }

        // A shard on the way is already within Lifestream's reach: hand over to the shard step's
        // straight lock-on and automove run.
        if (afterArrival == Stage.ApproachShard && GetActiveResidentialAetheryte() != 0)
        {
            if (path == null) StopAutoMove();
            leadIn = null;
            EnterStage(afterArrival);
            return;
        }

        var done = path != null ? leadInPathIndex >= path.Length : leadIn.Done!(player.Position);
        if (done || StageTimedOut(LeadInTimeout))
        {
            if (!done) log.Debug("[HF] ApartmentSweep: {LeadIn:l} lead-in timed out; carrying on.", leadIn.Name);
            if (path == null) StopAutoMove();
            leadIn = null;
            EnterStage(afterArrival);
            return;
        }

        if (walkStarted || IsOccupied()) return;
        walkStarted = true;
        UseSprint(player);
        if (path != null) pointWalker.Destination = path[leadInPathIndex];
        else Common.ExecuteCommand("/automove on");
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));

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
        else if (HasWardExitRoute)
        {
            wardExitPathIndex = 0;
            GoAethernet(wardEntranceAethernetId, Stage.WalkWardExit, $"Heading to {wardExitRoute!.Name} for ward {CurrentWard}");
        }
        else
            EnterLifestreamTravel();
    }

    // ── Ward exit ───────────────────────────────────────────────────────────

    private void HandleWalkWardExit()
    {
        if (StageTimedOut(ApproachTimeout)) { FailRoute($"could not reach {wardExitRoute!.Name}"); return; }
        if (!Common.TryGetLocalPlayer(objectTable, out var player)) return;

        // A walk-in exit has opened its menu: stop here and answer it.
        if (Common.TryGetOpenSelectMenu(gameGui, out _, out _)) { EnterStage(Stage.TalkWardExit); return; }

        // On the last leg, lock on to the NPC so it's clear where the walk is headed (earlier legs
        // can pass other NPCs standing near it), and start talking as soon as it's in reach.
        var path = wardExitRoute!.Path;
        if (walkStarted && wardExitPathIndex == path.Length - 1 &&
            DateTime.UtcNow - wardExitLegStartedAt >= TimeSpan.FromSeconds(wardExitRoute.TargetDelay))
        {
            var npc = FindWardExitNpc();
            if (npc != null)
            {
                // From here it's a straight run: lock on (the camera follows) and automove at it.
                if (!lockedOn)
                {
                    pointWalker.Destination = null;
                    LockOnWhileWalking(npc);
                    Common.ExecuteCommand("/automove on");
                }
                if (HorizontalDistance(player.Position, npc.Position) < WardExitTalkRange)
                {
                    StopAutoMove();
                    EnterStage(Stage.TalkWardExit);
                    return;
                }
            }
        }

        if (HorizontalDistance(player.Position, path[wardExitPathIndex]) < LeadInPointReached)
        {
            if (++wardExitPathIndex >= path.Length)
            {
                if (lockedOn) StopAutoMove();
                EnterStage(Stage.TalkWardExit);
                return;
            }
            wardExitLegStartedAt    = DateTime.UtcNow;
            pointWalker.Destination = path[wardExitPathIndex];
            return;
        }

        if (walkStarted || IsOccupied()) return;
        walkStarted             = true;
        wardExitLegStartedAt    = DateTime.UtcNow;
        UseSprint(player);
        pointWalker.Destination = path[wardExitPathIndex];
    }

    // Interact with the NPC, click through its line of dialogue, pick "Go to specified ward", then
    // carry on from its ward menu exactly as from the one the sweep was started on.
    private void HandleTalkWardExit()
    {
        if (StageTimedOut(StepTimeout)) { FailRoute($"{wardExitRoute!.Name} never offered the ward menu"); return; }

        if (GetAddon(WardMenuAddonName) != null)
        {
            EnterStage(Stage.SelectWard, $"Selecting ward {CurrentWard}");
            return;
        }

        if (Common.TryGetOpenSelectMenu(gameGui, out var menu, out var entries))
        {
            var index = entries.FindIndex(e => e.Contains(GoToSpecifiedWardText, StringComparison.OrdinalIgnoreCase));
            if (index < 0 || !ReadyForAction(RetryInterval)) return;
            var value = new AtkValue { Type = AtkValueType.Int, Int = index };
            menu->FireCallback(1, &value, true);
            return;
        }

        var talk = GetAddon(TalkAddonName);
        if (talk != null)
        {
            if (IsAddonReady(talk) && ReadyForAction(TalkClickInterval)) ClickTalk(talk);
            return;
        }

        if (IsOccupied() || !ReadyForAction(ClickInterval)) return;

        var npc = FindWardExitNpc();
        if (npc != null) Interact(npc);
    }

    private IGameObject? FindWardExitNpc() => wardExitRoute!.TalkToNpc
        ? FindNearest(wardExitRoute.Path[^1], o => o.ObjectKind == ObjectKind.EventNpc, WardExitNpcRange)
        : null;

    // Advances a Talk dialog the way a click on it does (the same mouse down, click, mouse up
    // events ECommons' AddonMaster.Talk sends).
    private static void ClickTalk(AtkUnitBase* talk)
    {
        var evt = stackalloc AtkEvent[1];
        evt->Listener = (AtkEventListener*)talk;
        evt->Target   = &AtkStage.Instance()->AtkEventTarget;
        evt->State    = new AtkEventState { StateFlags = (AtkEventStateFlags)132 };
        var data = stackalloc AtkEventData[1];
        *data = default;
        talk->ReceiveEvent(AtkEventType.MouseDown, 0, evt, data);
        talk->ReceiveEvent(AtkEventType.MouseClick, 0, evt, data);
        talk->ReceiveEvent(AtkEventType.MouseUp, 0, evt, data);
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
        sawBetweenAreas   = false;
        arrivedAt         = DateTime.MinValue;
        EnterStage(Stage.LifestreamTravel);
    }

    private void HandleLifestreamTravel()
    {
        if (GetAddon(RoomListAddonName) != null)
        {
            BeginReadingRooms();
            return;
        }

        if (TryTakeOverFromLifestream()) return;

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

    // Lifestream's walk from the apartment shard to the entrance is a straight run, which is what
    // wedges on Shirogane's stairs. So once its trip lands within reach of a building that has a
    // lead-in, it's stopped there and the rest of the walk is done here instead.
    private bool TryTakeOverFromLifestream()
    {
        if (IsBetweenAreas()) { sawBetweenAreas = true; arrivedAt = DateTime.MinValue; return false; }
        if (!sawBetweenAreas || !Common.TryGetLocalPlayer(objectTable, out var player)) return false;
        if (HousingDistricts.FromTerritoryId((ushort)clientState.TerritoryType) != District) return false;

        if (arrivedAt == DateTime.MinValue) arrivedAt = DateTime.UtcNow;
        if (DateTime.UtcNow - arrivedAt < SettleDelay) return false;

        var lead = ApartmentLeadIn(player.Position);
        if (lead == null || !pointWalker.IsAvailable) return false;
        if (FindNearest(player.Position, o => o.ObjectKind == ObjectKind.EventObj && o.BaseId == ApartmentEntranceDataId) == null) return false;

        log.Debug("[HF] ApartmentSweep: taking over from Lifestream for the {LeadIn:l} lead-in.", lead.Name);
        AbortLifestream();
        leadIn          = lead;
        leadInPathIndex = 0;
        afterArrival = Stage.ApproachEntrance;
        EnterStage(Stage.LeadIn, "Walking to the apartment entrance");
        return true;
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
        pointWalker.Destination = null;
        leadIn = null;
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

    private IGameObject? FindNearest(Vector3 from, Func<IGameObject, bool> predicate, float range = ObjectSearchRange)
    {
        IGameObject? best = null;
        var bestDistSq = range * range;
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

    // Target, lock on and start the game's own automove, the same way Lifestream does. The game
    // drops those commands while the character is still held by an event (the room list closing,
    // say), so this waits until it's free, and starts over if the character still isn't moving a
    // moment later. Never jumps: a jump on Shirogane's entrance bridge can land you in the river,
    // and the paths and lead-ins are what get around scenery.
    private void WalkTowards(IGameObject target)
    {
        if (!Common.TryGetLocalPlayer(objectTable, out var player)) return;
        var now = DateTime.UtcNow;

        if (!walkStarted)
        {
            if (IsOccupied()) return;
            walkStarted          = true;
            lastActionAt         = now;
            walkCheckAt          = now;
            walkCheckPos         = player.Position;
            UseSprint(player);
            targetManager.Target = target;
            Common.ExecuteCommand("/lockon");
            Common.ExecuteCommand("/automove on");
            return;
        }

        if (now - walkCheckAt > WalkStallCheck)
        {
            var moved = HorizontalDistance(player.Position, walkCheckPos);
            walkCheckAt  = now;
            walkCheckPos = player.Position;
            if (moved < WalkStallDistance && !condition[ConditionFlag.Jumping])
            {
                log.Debug("[HF] ApartmentSweep: not moving towards {Target:l}; restarting the walk.", target.Name.TextValue);
                walkStarted = false;
                return;
            }
        }
    }

    private bool IsOccupied() =>
        condition[ConditionFlag.Occupied] || condition[ConditionFlag.OccupiedInEvent] ||
        condition[ConditionFlag.OccupiedInQuestEvent] || condition[ConditionFlag.Occupied33] ||
        condition[ConditionFlag.OccupiedInCutSceneEvent];

    // Same as Lifestream's UseSprint: free in residential districts, skipped while a sprint-like
    // status is already up.
    private static void UseSprint(IPlayerCharacter player)
    {
        foreach (var status in player.StatusList)
            if (SprintStatusIds.Contains(status.StatusId)) return;

        var am = ActionManager.Instance();
        if (am->GetActionStatus(ActionType.Action, SprintActionId) == 0)
            am->UseAction(ActionType.Action, SprintActionId);
    }

    private bool IsShard(IGameObject obj) => obj.ObjectKind == ObjectKind.EventObj && ShardDataIds.Contains(obj.BaseId);
    private bool IsShardOrAetheryte(IGameObject obj) => obj.ObjectKind == ObjectKind.Aetheryte || IsShard(obj);

    // Target and lock on once per stage, so the camera follows where the walk is headed.
    // If the game drops the target anyway, it's re-taken at most once a second rather than
    // re-sending /lockon every frame.
    private void LockOnWhileWalking(IGameObject target)
    {
        if (IsOccupied()) return;

        var now = DateTime.UtcNow;
        if (targetManager.Target?.Address != target.Address)
        {
            if (now - lastLockOnAt < RetryInterval) return;
            targetManager.Target = target;
            lockedOn = false;
        }
        if (lockedOn) return;
        lockedOn     = true;
        lastLockOnAt = now;
        Common.ExecuteCommand("/lockon");
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
