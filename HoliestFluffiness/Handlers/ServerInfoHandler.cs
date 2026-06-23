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

    private readonly Ping ping = new();
    private readonly CancellationTokenSource cts = new();
    private readonly Queue<float> rttHistory = new();
    private const int HistorySize = 20;

    // Written from background thread, read on Framework.Update (guarded by pingDirty)
    private ulong lastRtt;
    private ulong avgRtt;
    private volatile bool pingDirty;

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

        dtrPing = dtrBar.Get("[HF] Ping");
        dtrPing.Tooltip = "Holy Plugin";
        dtrPing.Shown   = false;

        dtrFps = dtrBar.Get("[HF] FPS");
        dtrFps.Tooltip = "Holy Plugin";
        dtrFps.Shown   = false;

        framework.Update += OnUpdate;
        Task.Run(() => PingLoop(cts.Token));
    }

    private unsafe void OnUpdate(IFramework fw)
    {
        if (!clientState.IsLoggedIn)
        {
            if (dtrPing.Shown) dtrPing.Shown = false;
            if (dtrFps.Shown)  dtrFps.Shown  = false;
            return;
        }

        UpdateDcAddress();
        UpdateFps();
        UpdatePingEntry();
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
        lastFps  = fps;
        dtrFps.Text = $"{fps} fps";
    }

    private void UpdatePingEntry()
    {
        var shown = config.ServerInfoPingEnabled;
        if (dtrPing.Shown != shown) dtrPing.Shown = shown;
        if (!shown) return;

        if (!pingDirty) return;
        pingDirty = false;

        dtrPing.Text = config.ServerInfoPingDisplay switch
        {
            PingDisplay.Average => $"{avgRtt}ms",
            PingDisplay.Both    => $"{lastRtt}/{avgRtt}ms",
            _                   => $"{lastRtt}ms",
        };
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
                    if (reply.Status == IPStatus.Success)
                    {
                        lastRtt = (ulong)reply.RoundtripTime;
                        rttHistory.Enqueue(lastRtt);
                        if (rttHistory.Count > HistorySize) rttHistory.Dequeue();
                        avgRtt    = (ulong)rttHistory.Average();
                        pingDirty = true;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.Warning(ex, "[HF] Ping failed.");
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
        dtrPing.Dispose();
        dtrFps.Dispose();
    }
}
