using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;

namespace HoliestFluffiness.Handlers;

public sealed class DoorbellHandler : IDisposable
{
    private readonly IClientState  clientState;
    private readonly IObjectTable  objectTable;
    private readonly IFramework    framework;

    private sealed class KnownPlayer { public string Name = ""; public string World = ""; public uint WorldId; public long LastSeenMs; }
    private readonly Dictionary<uint, KnownPlayer> knownPlayers = new();
    private readonly Stopwatch timeInHouse = new();
    private readonly List<(string Name, string World, uint WorldId)> alreadyHereQueue = new();
    private bool alreadyHereFired;

    // Reused per-scan scratch buffers so OnUpdate allocates nothing on the hot path.
    private readonly HashSet<uint> seen = new();
    private readonly List<uint> expired = new();
    private long nextScanMs;

    private const long ScanIntervalMs = 250;   // full object-table scan at ~4 Hz, not every frame
    private const long LeaveTimeoutMs = 1000;   // consider a player gone after this long unseen

    public event Action<string, string, uint>? OnEntered;
    public event Action<string, string, uint>? OnLeft;
    public event Action<List<(string Name, string World, uint WorldId)>>? OnAlreadyHere;

    private static readonly HashSet<uint> HouseTerritories =
    [
        282, 283, 284, 384, 608,  // Mist
        342, 343, 344, 385, 609,  // Lavender Beds
        345, 346, 347, 386, 610,  // Goblet
        649, 650, 651, 652, 655,  // Shirogane
        980, 981, 982, 983, 999,  // Empyreum
        1249, 1250, 1251,          // Minimalist
        1374, 1375, 1376,          // Minimalist Dark (7.5)
    ];

    public DoorbellHandler(IClientState clientState, IObjectTable objectTable, IFramework framework)
    {
        this.clientState   = clientState;
        this.objectTable   = objectTable;
        this.framework     = framework;

        clientState.TerritoryChanged += OnTerritoryChanged;
        OnTerritoryChanged((uint)clientState.TerritoryType);
    }

    private void OnTerritoryChanged(uint territory)
    {
        knownPlayers.Clear();
        alreadyHereQueue.Clear();
        alreadyHereFired = false;
        nextScanMs = 0;
        timeInHouse.Stop();
        framework.Update -= OnUpdate;

        if (HouseTerritories.Contains(territory))
        {
            timeInHouse.Restart();
            framework.Update += OnUpdate;
        }
    }

    private void OnUpdate(IFramework fw)
    {
        var nowMs = timeInHouse.ElapsedMilliseconds;
        if (nowMs < nextScanMs) return;
        nextScanMs = nowMs + ScanIntervalMs;

        seen.Clear();

        foreach (var obj in objectTable)
        {
            if (obj is not IPlayerCharacter pc) continue;
            if (obj.ObjectIndex is >= 200 or 0) continue;

            var id = obj.EntityId;
            seen.Add(id);

            if (!knownPlayers.ContainsKey(id))
            {
                var worldId = pc.HomeWorld.RowId;
                var world   = pc.HomeWorld.ValueNullable?.Name.ExtractText() ?? "";
                knownPlayers[id] = new KnownPlayer { Name = pc.Name.TextValue, World = world, WorldId = worldId, LastSeenMs = nowMs };

                if (nowMs > 2000)
                    OnEntered?.Invoke(pc.Name.TextValue, world, worldId);
                else
                    alreadyHereQueue.Add((pc.Name.TextValue, world, worldId));
            }
            else
            {
                knownPlayers[id].LastSeenMs = nowMs;
            }
        }

        expired.Clear();
        foreach (var kv in knownPlayers)
            if (!seen.Contains(kv.Key) && nowMs - kv.Value.LastSeenMs > LeaveTimeoutMs)
                expired.Add(kv.Key);
        foreach (var id in expired)
        {
            var player = knownPlayers[id];
            OnLeft?.Invoke(player.Name, player.World, player.WorldId);
            knownPlayers.Remove(id);
        }

        if (!alreadyHereFired && alreadyHereQueue.Count > 0 && nowMs > 2000)
        {
            alreadyHereFired = true;
            OnAlreadyHere?.Invoke(alreadyHereQueue.ToList());
            alreadyHereQueue.Clear();
        }
    }

    public void Dispose()
    {
        clientState.TerritoryChanged -= OnTerritoryChanged;
        framework.Update             -= OnUpdate;
    }
}
