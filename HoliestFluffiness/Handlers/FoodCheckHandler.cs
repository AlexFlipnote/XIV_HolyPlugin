using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Statuses;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace HoliestFluffiness.Handlers;

public class FoodCheckHandler : IDisposable
{
    private const uint WellFedStatusId = 48;

    private readonly Configuration config;
    private readonly IPartyList partyList;
    private readonly IObjectTable objectTable;
    private readonly IClientState clientState;
    private readonly IChatGui chatGui;
    private readonly IDataManager dataManager;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly string assemblyDir;

    private delegate nint CountdownTimerDelegate(ulong a1);
    private readonly Hook<CountdownTimerDelegate>? countdownHook;
    private ulong countdownParam;
    private bool wasCountingDown;

    // Fired once each time a countdown transitions from stopped to running.
    public event System.Action? CountdownStarted;

    private List<FoodCheckEntry> lowFoodEntries = [];
    private CancellationTokenSource? clearCts;

    public bool IsValid { get; private set; }

    public FoodCheckHandler(
        Configuration config, IPartyList partyList, IObjectTable objectTable,
        IClientState clientState, IChatGui chatGui, IDataManager dataManager,
        IFramework framework, IGameInteropProvider gameInterop, IPluginLog log, string assemblyDir)
    {
        this.config       = config;
        this.partyList    = partyList;
        this.objectTable  = objectTable;
        this.clientState  = clientState;
        this.chatGui      = chatGui;
        this.dataManager  = dataManager;
        this.framework    = framework;
        this.log          = log;
        this.assemblyDir  = assemblyDir;

        framework.Update += OnFrameworkUpdate;

        countdownHook = Common.TryCreateHookFromSignature<CountdownTimerDelegate>(
            Sigs.CountdownTimer, OnCountdownTimer, gameInterop, log,
            "[HF] FoodCheck: countdown hook failed, countdown trigger will not work.");
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        countdownHook?.Dispose();
        clearCts?.Cancel();
        clearCts?.Dispose();
    }

    public List<FoodCheckEntry> GetEntries() => lowFoodEntries;

    public void Invalidate()
    {
        IsValid = false;
        lowFoodEntries = [];
    }

    public void OnReadyCheck() => framework.RunOnTick(() => RunCheck(ignoreDutyFilter: false), delayTicks: 1);

    // Test button: skips the duty scope filter and clears after 5s
    public void ForceCheck() => RunCheck(ignoreDutyFilter: true, clearDelayMs: 5_000);

    private nint OnCountdownTimer(ulong value)
    {
        countdownParam = value;
        return countdownHook!.Original(value);
    }

    private unsafe void OnFrameworkUpdate(IFramework fw)
    {
        if (countdownParam == 0) return;
        try
        {
            var active  = *(byte*)(countdownParam + 0x38) == 1;
            var current = *(float*)(countdownParam + 0x2c);
            var counting = active && current > 0f;

            if (counting && !wasCountingDown)
            {
                wasCountingDown = true;
                CountdownStarted?.Invoke();
                RunCheck(ignoreDutyFilter: false);
            }
            else if (!counting && wasCountingDown)
            {
                wasCountingDown = false;
                Invalidate();
            }
        }
        catch (Exception ex) { log.Debug($"FoodCheck OnFrameworkUpdate: {ex}"); }
    }

    private void RunCheck(bool ignoreDutyFilter, int clearDelayMs = 30_000)
    {
        if (!clientState.IsLoggedIn) return;
        if (!ignoreDutyFilter && !PassesDutyFilter()) return;

        var all       = BuildEntries();
        var threshold = config.FoodCheckThreshold * 60;
        var toNotify  = all.Where(e => e.RemainingSeconds < threshold).ToList();

        lowFoodEntries = toNotify;

        if (toNotify.Count == 0)
        {
            IsValid = false;
            return;
        }

        if (config.FoodCheckEcho)      SendEcho(toNotify);
        if (config.FoodCheckSound)     PlaySound();
        if (config.FoodCheckHighlight) { IsValid = true; ScheduleClear(clearDelayMs); }
    }

    private unsafe List<FoodCheckEntry> BuildEntries()
    {
        var result = new List<FoodCheckEntry>();
        var hud    = AgentHUD.Instance();

        var characters = new Dictionary<uint, IPlayerCharacter>();
        foreach (var obj in objectTable)
            if (obj is IPlayerCharacter pc)
                characters.TryAdd(pc.EntityId, pc);

        for (var i = 0; i < partyList.Length; i++)
        {
            var member = partyList[i];
            if (member == null) continue;

            var remaining = characters.TryGetValue(member.EntityId, out var character)
                ? ReadWellFed(character.StatusList)
                : ReadWellFed(member.Statuses);

            result.Add(new FoodCheckEntry(
                member.Name.TextValue,
                member.EntityId,
                GetHudSlot(hud, member.EntityId),
                remaining));
        }

        // Solo or all party lookups empty, check local player directly
        if (result.Count == 0 && objectTable[0] is IPlayerCharacter local)
            result.Add(new FoodCheckEntry(local.Name.TextValue, local.EntityId, 0, ReadWellFed(local.StatusList)));

        return result;
    }

    private static int ReadWellFed(StatusList statuses)
    {
        foreach (var status in statuses)
            if (status.StatusId == WellFedStatusId)
                return (int)status.RemainingTime;
        return 0;
    }

    private static unsafe int GetHudSlot(AgentHUD* hud, uint entityId)
    {
        if ((nint)hud == nint.Zero) return -1;
        for (var i = 0; i < 8; i++)
            if (hud->PartyMembers[i].EntityId == entityId) return i;
        return -1;
    }

    private bool PassesDutyFilter()
    {
        if (config.FoodCheckScopeAny) return true;

        var territory = dataManager.GetExcelSheet<TerritoryType>()?.GetRow(clientState.TerritoryType);
        var cfc       = territory?.ContentFinderCondition.ValueNullable;
        if (cfc == null || cfc.Value.RowId == 0) return false;

        if (config.FoodCheckScopeHighEnd && cfc.Value.HighEndDuty) return true;

        // ContentType 5 (Raids) and 4 (Trials) include normal tiers too, so Savage and Extreme are
        // matched by name; that also covers old content whose HighEndDuty flag was cleared.
        var name = cfc.Value.Name.ExtractText();
        if (config.FoodCheckScopeSavage && name.EndsWith("(Savage)")) return true;
        // Extremes use either an "(Extreme)" suffix or an older "the Minstrel's Ballad:" prefix
        if (config.FoodCheckScopeExtreme
            && (name.EndsWith("(Extreme)")
                || name.StartsWith("the Minstrel's Ballad", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    private void SendEcho(List<FoodCheckEntry> entries)
    {
        var builder = new SeStringBuilder().AddText("Food check complete:");
        foreach (var e in entries)
        {
            var detail = e.RemainingSeconds == 0 ? "no food" : $"{e.RemainingSeconds / 60}m left";
            builder.AddText($"\n    》 {e.Name} ({detail})");
        }
        chatGui.Print(builder.Build());
    }

    private void PlaySound()
    {
        var path = SoundEngine.Resolve(config.FoodCheckSoundPath, "Sounds/FoodCheck/hungry.mp3", assemblyDir);
        SoundEngine.Play(path, config.FoodCheckSoundVolume);
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
            await framework.RunOnFrameworkThread(Invalidate);
        });
    }
}

public readonly record struct FoodCheckEntry(string Name, uint EntityId, int HudSlot, int RemainingSeconds);
