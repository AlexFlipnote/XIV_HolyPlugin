using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace HoliestFluffiness.Handlers;

public class ReadyCheckHandler : IDisposable
{
    private readonly Configuration config;
    private readonly IClientState clientState;
    private readonly IChatGui chatGui;
    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;

    private unsafe delegate void EndReadyCheckDelegate(AgentReadyCheck* self);
    private readonly Hook<EndReadyCheckDelegate> endHook;

    private List<ReadyCheckEntry> data = [];
    private bool active;
    private CancellationTokenSource? clearCts;
    private HashSet<ulong> savedPartyIds = [];

    public bool IsValid { get; private set; }

    public ReadyCheckHandler(Configuration config, IGameInteropProvider gameInterop, IClientState clientState, IChatGui chatGui, IFramework framework, IObjectTable objectTable, IPluginLog log)
    {
        this.config = config; this.clientState = clientState; this.chatGui = chatGui;
        this.framework = framework; this.objectTable = objectTable; this.log = log;

        unsafe { endHook = gameInterop.HookFromAddress<EndReadyCheckDelegate>(AgentReadyCheck.MemberFunctionPointers.EndReadyCheck, OnEnd); }
        endHook.Enable();
        framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        endHook.Dispose();
        clearCts?.Cancel();
        clearCts?.Dispose();
    }

    // Called by Plugin.cs from its existing InitiateReadyCheck hook
    public void OnBegin()
    {
        if (!clientState.IsLoggedIn) return;
        active = true;
        IsValid = true;
        clearCts?.Cancel();
    }

    public List<ReadyCheckEntry> GetData() => data;

    public void Invalidate() { IsValid = false; data.Clear(); savedPartyIds.Clear(); }

    private unsafe void CapturePartyIds()
    {
        savedPartyIds.Clear();
        var gm = GroupManager.Instance();
        if ((nint)gm == nint.Zero) return;
        for (var i = 0; i < gm->MainGroup.MemberCount; i++)
        {
            var m = gm->MainGroup.GetPartyMemberByIndex(i);
            if ((nint)m != nint.Zero)
                savedPartyIds.Add((ulong)m->ContentId);
        }
    }

    private unsafe void CheckPartyChanged()
    {
        if (savedPartyIds.Count == 0) return;
        var gm = GroupManager.Instance();
        if ((nint)gm == nint.Zero) return;
        var count = gm->MainGroup.MemberCount;
        if (count != savedPartyIds.Count) { Invalidate(); return; }
        for (var i = 0; i < count; i++)
        {
            var m = gm->MainGroup.GetPartyMemberByIndex(i);
            if ((nint)m == nint.Zero || !savedPartyIds.Contains((ulong)m->ContentId))
            {
                Invalidate();
                return;
            }
        }
    }

    private unsafe void OnEnd(AgentReadyCheck* ptr)
    {
        endHook.Original(ptr);
        if (!clientState.IsLoggedIn) return;
        active = false;
        ProcessData();
        CapturePartyIds();
        AfterReadyCheck();
    }

    private void AfterReadyCheck()
    {
        if (config.ReadyCheckShowNames)
        {
            var notReady = new List<string>();
            foreach (var e in data)
                if (e.ReadyState is ReadyCheckStatus.NotReady or ReadyCheckStatus.MemberNotPresent)
                    notReady.Add(e.Name);
            if (notReady.Count > 0)
                Task.Run(async () =>
                {
                    await Task.Delay(500);
                    await framework.RunOnFrameworkThread(() =>
                        chatGui.Print($"[HF] Not ready: {string.Join(", ", notReady)}"));
                });
        }

        if (config.ReadyCheckDrawOverlay)
            ScheduleClear(Math.Max(1, config.ReadyCheckClearAfterSeconds) * 1000);
    }

    private void OnUpdate(IFramework fw)
    {
        if (clientState.IsLoggedIn && active) ProcessData();
        else if (IsValid && !active) CheckPartyChanged();
    }

    private unsafe void ProcessData()
    {
        var proxy = InfoProxyCrossRealm.Instance();
        var gm = GroupManager.Instance();
        if ((nint)proxy == nint.Zero || (nint)gm == nint.Zero) return;

        if (proxy->IsCrossRealm && !proxy->IsInAllianceRaid && gm->MainGroup.MemberCount < 1)
            ProcessCrossWorld();
        else
            ProcessRegular();
    }

    private unsafe void ProcessRegular()
    {
        var gm = GroupManager.Instance();
        try
        {
            var entries = AgentReadyCheck.Instance()->ReadyCheckEntries;
            var result = new List<ReadyCheckEntry>();
            var foundSelf = false;

            var alliance = new Dictionary<uint, (ulong cid, string name, byte grp, byte idx)>();
            for (var j = 0; j < 2; j++)
                for (var i = 0; i < 8; i++)
                {
                    var m = gm->MainGroup.GetAllianceMemberByGroupAndIndex(j, i);
                    if ((nint)m != nint.Zero)
                        alliance.TryAdd(m->EntityId, ((ulong)m->ContentId, m->NameString, (byte)(j + 1), (byte)i));
                }

            for (var i = 0; i < entries.Length; i++)
            {
                if (i < gm->MainGroup.MemberCount)
                {
                    var m = gm->MainGroup.GetPartyMemberByIndex(i);
                    if ((nint)m == nint.Zero) continue;
                    var name = m->NameString;
                    if (m->EntityId == objectTable.LocalPlayer?.EntityId)
                    {
                        result.Insert(0, new(name, (ulong)m->ContentId, m->EntityId, entries[0].Status, 0, 0));
                        foundSelf = true;
                    }
                    else if (!foundSelf)
                        result.Add(new(name, (ulong)m->ContentId, m->EntityId, entries[i + 1].Status, 0, (byte)(i + 1)));
                    else
                        result.Add(new(name, (ulong)m->ContentId, m->EntityId, entries[i].Status, 0, (byte)i));
                }
                else if (entries[i].ContentId > 0 && (entries[i].ContentId & 0xFFFFFFFF) != 0xE0000000)
                {
                    if (alliance.TryGetValue((uint)entries[i].ContentId, out var t))
                        result.Add(new(t.name, t.cid, (uint)entries[i].ContentId, entries[i].Status, t.grp, t.idx));
                }
            }
            data = result;
        }
        catch (Exception e) { log.Debug($"ReadyCheck ProcessRegular: {e}"); }
    }

    private unsafe void ProcessCrossWorld()
    {
        try
        {
            var entries = AgentReadyCheck.Instance()->ReadyCheckEntries;
            var result = new List<ReadyCheckEntry>();
            foreach (var e in entries)
            {
                CrossRealmMember* m = e.ContentId > uint.MaxValue
                    ? InfoProxyCrossRealm.GetMemberByContentId(e.ContentId)
                    : InfoProxyCrossRealm.GetMemberByEntityId((uint)e.ContentId);
                if ((nint)m != nint.Zero)
                    result.Add(new(m->NameString, m->ContentId, m->EntityId, e.Status, m->GroupIndex, m->MemberIndex));
            }
            data = result;
        }
        catch (Exception e) { log.Debug($"ReadyCheck ProcessCrossWorld: {e}"); }
    }

    public unsafe void Simulate()
    {
        var hud = AgentHUD.Instance();
        if ((nint)hud == nint.Zero) return;

        var result = new List<ReadyCheckEntry>();
        var statuses = new[] { ReadyCheckStatus.Ready, ReadyCheckStatus.NotReady, ReadyCheckStatus.MemberNotPresent };
        for (var i = 0; i < 8; i++)
        {
            var m = hud->PartyMembers[i];
            if (m.ContentId == 0 && m.EntityId is 0 or 0xE0000000) continue;
            result.Add(new("", m.ContentId, m.EntityId, statuses[i % statuses.Length], 0, (byte)i));
        }

        if (result.Count == 0) return;
        data = result;
        IsValid = true;
        ScheduleClear(1000);
    }

    private void ScheduleClear(int delayMs)
    {
        clearCts?.Cancel();
        clearCts?.Dispose();
        var cts = new CancellationTokenSource();
        clearCts = cts;
        Task.Run(async () =>
        {
            try { await Task.Delay(delayMs, cts.Token); }
            catch (OperationCanceledException) { return; }
            Invalidate();
        });
    }

    public unsafe HUDIndex? GetHUDIndex(ulong contentId, uint entityId)
    {
        if (contentId == 0 && entityId is 0 or 0xE0000000) return null;
        var proxy = InfoProxyCrossRealm.Instance();
        var gm = GroupManager.Instance();
        var hud = AgentHUD.Instance();
        if (proxy == null || gm == null || hud == null) return null;

        for (var i = 0; i < 8; i++)
        {
            var c = hud->PartyMembers[i];
            if (contentId > 0 && contentId == c.ContentId) return new(false, 0, i);
            if (entityId > 0 && entityId != 0xE0000000 && entityId == c.EntityId) return new(false, 0, i);
        }

        if (gm->MainGroup.MemberCount > 0)
        {
            for (var i = 0; i < 40; i++)
                if (entityId > 0 && entityId != 0xE0000000 && entityId == hud->RaidMemberIds[i])
                    return new(false, i / 8 + 1, i % 8);
        }
        else if (proxy->IsCrossRealm)
        {
            var m = InfoProxyCrossRealm.GetMemberByContentId(contentId);
            if (m == null || contentId == 0) return null;
            return new(proxy->IsCrossRealm && !proxy->IsInAllianceRaid, m->GroupIndex, m->MemberIndex);
        }
        return null;
    }
}

public readonly record struct ReadyCheckEntry(string Name, ulong ContentId, uint EntityId, ReadyCheckStatus ReadyState, byte GroupIndex, byte MemberIndex);
public readonly record struct HUDIndex(bool CrossWorld, int GroupNumber, int PartyMemberIndex);
