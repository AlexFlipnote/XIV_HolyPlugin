using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;

namespace HoliestFluffiness.Handlers;

public sealed class DoorbellHandler : IDisposable
{
    private readonly Configuration configuration;
    private readonly IClientState  clientState;
    private readonly IObjectTable  objectTable;
    private readonly IFramework    framework;

    private sealed class KnownPlayer { public string Name = ""; public string World = ""; public int Unseen; }
    private readonly Dictionary<uint, KnownPlayer> knownPlayers = new();
    private readonly Stopwatch timeInHouse = new();

    public event Action<string, string>? OnEntered;
    public event Action<string, string>? OnLeft;

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

    public DoorbellHandler(Configuration configuration, IClientState clientState, IObjectTable objectTable, IFramework framework)
    {
        this.configuration = configuration;
        this.clientState   = clientState;
        this.objectTable   = objectTable;
        this.framework     = framework;

        clientState.TerritoryChanged += OnTerritoryChanged;
        OnTerritoryChanged((uint)clientState.TerritoryType);
    }

    private void OnTerritoryChanged(uint territory)
    {
        knownPlayers.Clear();
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
        if (!configuration.DoorbellEnabled) return;

        var seen = new HashSet<uint>();

        foreach (var obj in objectTable)
        {
            if (obj is not IPlayerCharacter pc) continue;
            if (obj.ObjectIndex is >= 200 or 0) continue;

            var id = obj.EntityId;
            seen.Add(id);

            if (!knownPlayers.ContainsKey(id))
            {
                var world = pc.HomeWorld.ValueNullable?.Name.ExtractText() ?? "";
                knownPlayers[id] = new KnownPlayer { Name = pc.Name.TextValue, World = world };

                if (timeInHouse.ElapsedMilliseconds > 2000)
                    OnEntered?.Invoke(pc.Name.TextValue, world);
            }
            else
            {
                knownPlayers[id].Unseen = 0;
            }
        }

        foreach (var id in knownPlayers.Keys.ToList())
        {
            if (seen.Contains(id)) continue;
            var player = knownPlayers[id];
            player.Unseen++;
            if (player.Unseen <= 60) continue;
            OnLeft?.Invoke(player.Name, player.World);
            knownPlayers.Remove(id);
        }
    }

    public void Dispose()
    {
        clientState.TerritoryChanged -= OnTerritoryChanged;
        framework.Update             -= OnUpdate;
    }
}
