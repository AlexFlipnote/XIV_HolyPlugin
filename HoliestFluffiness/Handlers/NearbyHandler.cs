using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace HoliestFluffiness.Handlers;

public sealed record NearbyPlayer(
    string Name,
    string HomeWorld,
    string CompanyTag,
    string JobAbbr,
    byte   Level,
    bool   IsParty,
    bool   IsFriend,
    bool   IsLocalFc,
    ulong  GameObjectId
);

public sealed record Targeter(
    string   Name,
    string   HomeWorld,
    ulong    GameObjectId,
    DateTime When
);

public sealed class NearbyHandler : IDisposable
{
    private readonly Configuration  config;
    private readonly IObjectTable   objectTable;
    private readonly IFramework     framework;
    private readonly IPartyList     partyList;
    private readonly ITargetManager targetManager;

    private DateTime lastUpdate = DateTime.MinValue;

    public event Action<Targeter>? NewTargeter;

    public List<NearbyPlayer> NearbyPlayers     { get; private set; } = [];
    public List<Targeter>     CurrentTargeters  { get; private set; } = [];
    public List<Targeter>     PreviousTargeters { get; private set; } = [];

    public NearbyHandler(Configuration config, IObjectTable objectTable, IFramework framework, IPartyList partyList, ITargetManager targetManager)
    {
        this.config        = config;
        this.objectTable   = objectTable;
        this.framework     = framework;
        this.partyList     = partyList;
        this.targetManager = targetManager;
        framework.Update += OnUpdate;
    }

    public void Dispose() => framework.Update -= OnUpdate;

    public void ClearTargeterHistory() => PreviousTargeters.Clear();

    private void OnUpdate(IFramework fw)
    {
        if (!config.NearbyEnabled) return;
        if (DateTime.Now - lastUpdate < TimeSpan.FromMilliseconds(500)) return;
        lastUpdate = DateTime.Now;

        var local = objectTable.LocalPlayer;
        if (local == null) return;

        UpdateNearby(local);
        if (config.NearbyShowTargeters)
            UpdateTargeters(local);
        else
            CurrentTargeters = [];
    }

    private unsafe void UpdateNearby(IPlayerCharacter local)
    {
        var partyIds  = partyList.Select(m => m.EntityId).ToHashSet();
        var localFcTag = local.CompanyTag.TextValue;
        var players   = new List<NearbyPlayer>();

        foreach (var obj in objectTable)
        {
            if (obj is not IPlayerCharacter pc) continue;
            if (pc.GameObjectId == local.GameObjectId) continue;
            if (pc.ObjectIndex >= 240) continue;
            if (pc.Level == 0) continue;
            if (config.NearbyFilterLowLevel && pc.Level <= 3) continue;
            if (config.NearbyFilterAfk && pc.OnlineStatus.RowId == 17) continue;

            var chara    = (Character*)pc.Address;
            var isFriend = chara->IsFriend;
            var fcTag    = pc.CompanyTag.TextValue;
            var isLocalFc = !string.IsNullOrEmpty(localFcTag) && fcTag == localFcTag;

            players.Add(new NearbyPlayer(
                pc.Name.TextValue,
                pc.HomeWorld.ValueNullable?.Name.ExtractText() ?? "",
                fcTag,
                pc.ClassJob.ValueNullable?.Abbreviation.ExtractText() ?? "?",
                pc.Level,
                partyIds.Contains(pc.EntityId),
                isFriend,
                isLocalFc,
                pc.GameObjectId
            ));
        }

        if (config.NearbyDebugSelf)
        {
            var isFriend  = config.NearbyDebugSelfAs == 1;
            var isLocalFc = config.NearbyDebugSelfAs == 2;
            var isParty   = config.NearbyDebugSelfAs == 3;
            players.Add(new NearbyPlayer(
                local.Name.TextValue,
                local.HomeWorld.ValueNullable?.Name.ExtractText() ?? "",
                local.CompanyTag.TextValue,
                local.ClassJob.ValueNullable?.Abbreviation.ExtractText() ?? "?",
                local.Level,
                isParty, isFriend, isLocalFc,
                local.GameObjectId
            ));
        }

        // Party first, then friends, then local FC, then rest — alphabetical within each group
        NearbyPlayers = [.. players
            .OrderByDescending(p => p.IsParty ? 3 : p.IsFriend ? 2 : p.IsLocalFc ? 1 : 0)
            .ThenBy(p => p.Name)];
    }

    private void UpdateTargeters(IPlayerCharacter local)
    {
        var newCurrent = new List<Targeter>();

        if (config.NearbyDebugSelf && config.NearbyDebugSelfAs == 4)
        {
            var existing = CurrentTargeters.FirstOrDefault(c => c.GameObjectId == local.GameObjectId);
            newCurrent.Add(existing ?? new Targeter(
                local.Name.TextValue,
                local.HomeWorld.ValueNullable?.Name.ExtractText() ?? "",
                local.GameObjectId,
                DateTime.Now
            ));
        }

        if (config.NearbyTargeterTrackSelf && targetManager.Target?.GameObjectId == local.GameObjectId)
        {
            var existing = CurrentTargeters.FirstOrDefault(c => c.GameObjectId == local.GameObjectId);
            newCurrent.Add(existing ?? new Targeter(
                local.Name.TextValue,
                local.HomeWorld.ValueNullable?.Name.ExtractText() ?? "",
                local.GameObjectId,
                DateTime.Now
            ));
        }

        foreach (var obj in objectTable)
        {
            if (obj is not IPlayerCharacter pc) continue;
            if (pc.GameObjectId == local.GameObjectId) continue;
            if (pc.TargetObjectId != local.GameObjectId) continue;

            var existing = CurrentTargeters.FirstOrDefault(c => c.GameObjectId == pc.GameObjectId);
            newCurrent.Add(existing ?? new Targeter(
                pc.Name.TextValue,
                pc.HomeWorld.ValueNullable?.Name.ExtractText() ?? "",
                pc.GameObjectId,
                DateTime.Now
            ));
        }

        foreach (var t in newCurrent.Where(n => CurrentTargeters.All(c => c.GameObjectId != n.GameObjectId)))
            NewTargeter?.Invoke(t);

        foreach (var stopped in CurrentTargeters.Where(c => newCurrent.All(n => n.GameObjectId != c.GameObjectId)))
        {
            PreviousTargeters.RemoveAll(p => p.GameObjectId == stopped.GameObjectId);
            PreviousTargeters.Insert(0, stopped);
            if (PreviousTargeters.Count > 50)
                PreviousTargeters.RemoveAt(PreviousTargeters.Count - 1);
        }

        CurrentTargeters = newCurrent;
    }
}
