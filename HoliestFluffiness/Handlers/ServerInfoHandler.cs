using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.SubKinds;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace HoliestFluffiness.Handlers;

public sealed class ServerInfoHandler : IDisposable
{
    private readonly Configuration config;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;

    private readonly IDtrBarEntry dtrPing;
    private readonly IDtrBarEntry dtrFps;
    private readonly IDtrBarEntry dtrNearby;

    private NearbyHandler? nearbyHandler;

    private readonly Ping ping = new();
    private readonly CancellationTokenSource cts = new();
    // null entry = timed out, ulong = RTT in ms
    private readonly Queue<ulong?> pingHistory = new();
    private const int HistorySize = 30;

    // Written from background thread, read on Framework.Update (guarded by pingDirty)
    private ulong lastRtt;
    private ulong avgRtt;
    private int recentTimeouts;
    private float[] pendingChartData = [];
    private volatile bool pingDirty;

    // Updated on Framework.Update, safe to read from Draw
    public float[] PingChartData { get; private set; } = [];

    private int lastFps;
    private uint lastDcId;
    private IPAddress serverAddress = IPAddress.Loopback;

    public ServerInfoHandler(Configuration config, IDtrBar dtrBar, IFramework framework,
        IClientState clientState, IObjectTable objectTable, IPluginLog log)
    {
        this.config      = config;
        this.framework   = framework;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.log         = log;

        dtrPing   = dtrBar.Get("[HF] Ping");
        dtrPing.Shown = false;

        dtrFps   = dtrBar.Get("[HF] FPS");
        dtrFps.Shown = false;

        dtrNearby = dtrBar.Get("[HF] Nearby");
        dtrNearby.Shown = false;

        framework.Update += OnUpdate;
        Task.Run(() => PingLoop(cts.Token));
    }

    public void SetNearbyHandler(NearbyHandler handler) => nearbyHandler = handler;

    public void SetNearbyClickAction(Action action) => dtrNearby.OnClick = _ => action();

    public void SetPingClickAction(Action action) => dtrPing.OnClick = _ => action();

    private unsafe void OnUpdate(IFramework fw)
    {
        if (!clientState.IsLoggedIn)
        {
            if (dtrPing.Shown)   dtrPing.Shown   = false;
            if (dtrFps.Shown)    dtrFps.Shown    = false;
            if (dtrNearby.Shown) dtrNearby.Shown = false;
            return;
        }

        UpdateDcAddress();
        UpdateFps();
        UpdatePingEntry();
        UpdateNearbyEntry();
    }

    private void UpdateNearbyEntry()
    {
        var shown = config.NearbyDtrEnabled && config.NearbyEnabled;
        if (dtrNearby.Shown != shown) dtrNearby.Shown = shown;
        if (!shown) return;

        var count = nearbyHandler?.NearbyPlayers.Count ?? 0;
        dtrNearby.Text    = $"\U0000e033 {count}";
        dtrNearby.Tooltip = $"{count} nearby";
    }

    private void UpdateDcAddress()
    {
        var player = objectTable[0] as IPlayerCharacter;
        var dcId   = player?.CurrentWorld.ValueNullable?.DataCenter.RowId;
        if (dcId == null || dcId == lastDcId) return;
        lastDcId = (uint)dcId;
        serverAddress = dcId.Value switch
        {
            1  => IPAddress.Parse("119.252.36.6"),  // Elemental
            2  => IPAddress.Parse("119.252.36.7"),  // Gaia
            3  => IPAddress.Parse("119.252.36.8"),  // Mana
            4  => IPAddress.Parse("204.2.29.6"),    // Aether
            5  => IPAddress.Parse("204.2.29.7"),    // Primal
            6  => IPAddress.Parse("80.239.145.6"),  // Chaos
            7  => IPAddress.Parse("80.239.145.7"),  // Light
            8  => IPAddress.Parse("204.2.29.8"),    // Crystal
            9  => IPAddress.Parse("153.254.80.103"),// Materia
            10 => IPAddress.Parse("119.252.36.9"),  // Meteor
            11 => IPAddress.Parse("204.2.29.9"),    // Dynamis
            12 => IPAddress.Parse("80.239.145.8"),  // Shadow
            _  => IPAddress.Loopback,
        };
    }

    private unsafe void UpdateFps()
    {
        var shown = config.ServerInfoFpsEnabled;
        if (dtrFps.Shown != shown) dtrFps.Shown = shown;
        if (!shown) return;

        var fps = (int)(GameFramework.Instance()->FrameRate + 0.5f);
        if (fps == lastFps) return;
        lastFps        = fps;
        dtrFps.Text    = $"{fps} fps";
        dtrFps.Tooltip = $"{fps} fps";
    }

    private void UpdatePingEntry()
    {
        var shown = config.ServerInfoPingEnabled;
        if (dtrPing.Shown != shown) dtrPing.Shown = shown;
        if (!shown) return;

        if (!pingDirty)
        {
            if (lastRtt == 0) { dtrPing.Text = "0ms"; dtrPing.Tooltip = "0ms"; }
            return;
        }
        pingDirty = false;

        PingChartData = pendingChartData;

        var pingText = config.ServerInfoPingDisplay switch
        {
            PingDisplay.Average => $"{avgRtt}ms",
            PingDisplay.Both    => $"{lastRtt}/{avgRtt}ms",
            _                   => $"{lastRtt}ms",
        };
        if (recentTimeouts > 0)
            pingText += $" / {recentTimeouts}TO";

        dtrPing.Text    = pingText;
        dtrPing.Tooltip = pingText;
    }

    // Called only from the background PingLoop thread
    private void RecordPing(ulong? rtt)
    {
        if (rtt.HasValue) lastRtt = rtt.Value;
        pingHistory.Enqueue(rtt);
        if (pingHistory.Count > HistorySize) pingHistory.Dequeue();

        var successes = pingHistory.Where(v => v.HasValue).ToList();
        avgRtt         = successes.Count > 0 ? (ulong)successes.Average(v => (double)v!.Value) : 0;
        recentTimeouts = pingHistory.Count - successes.Count;
        pendingChartData = [.. pingHistory.Select(v => v.HasValue ? (float)v.Value : 0f)];
        pingDirty      = true;
    }

    private async Task PingLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!IPAddress.IsLoopback(serverAddress))
            {
                try
                {
                    var reply = await ping.SendPingAsync(serverAddress).ConfigureAwait(false);
                    RecordPing(reply.Status == IPStatus.Success ? (ulong)reply.RoundtripTime : null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.Warning(ex, "[HF] Ping failed.");
                    RecordPing(null);
                }
            }

            await Task.Delay(3000, token).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        cts.Cancel();
        cts.Dispose();
        ping.Dispose();
        dtrPing.Remove();
        dtrFps.Remove();
        dtrNearby.Remove();
    }
}
