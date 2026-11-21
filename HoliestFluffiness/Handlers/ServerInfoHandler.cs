using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;
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

    // Native string for re-shown tooltips; the game may read it after ShowTooltip returns
    private const int TooltipBufferSize = 256;
    private readonly nint tooltipBuffer = Marshal.AllocHGlobal(TooltipBufferSize);

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
    // Set on Framework.Update so the loop never touches game state from its own thread
    private volatile bool pingWanted;
    private bool pingPlaceholderSet;

    // Updated on Framework.Update, safe to read from Draw
    public float[] PingChartData { get; private set; } = [];

    // FPS history for the FPS chart, sampled on a fixed interval on Framework.Update.
    private readonly Queue<float> fpsHistory = new();
    private const int  FpsHistorySize      = 60;
    private const long FpsSampleIntervalMs = 1000;
    private long lastFpsSampleTick;
    public float[] FpsChartData { get; private set; } = [];

    private int lastFps;
    private bool lastFpsGreen;
    private FpsLimiterHandler? fpsLimiter;

    // Live mode: our own frame count over a short window, so the entry reacts faster than the
    // game's once a second FrameRate. The chart keeps sampling the game's value either way.
    private static readonly long FpsLiveWindowTicks = Stopwatch.Frequency / 4; // 250ms
    private long fpsLiveWindowStart;
    // Game text, not ImGui, so a fixed colour rather than Theme (same #50C850 as the custom theme green)
    private static readonly Vector4 FpsLimitGreen = new(80 / 255f, 200 / 255f, 80 / 255f, 1f);
    private int  fpsLiveFrames;
    private int  fpsLiveValue;
    private uint lastDcId;
    private int lastNearbyCount = -1;

    // Approximate lobby IP for the current DC, used only when the real address can't be detected
    private IPAddress dcFallbackAddress = IPAddress.Loopback;
    private bool tcpDetectDidError;

    // Written from the PingLoop thread; IPAddress is immutable so a bare volatile read/write is safe
    private volatile IPAddress connectedAddress = IPAddress.Loopback;
    public IPAddress ConnectedAddress => connectedAddress;

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

    public void SetFpsLimiter(FpsLimiterHandler limiter) => fpsLimiter = limiter;

    public void SetNearbyClickAction(Action action) => dtrNearby.OnClick = _ => action();

    public void SetPingClickAction(Action action) => dtrPing.OnClick = _ => action();

    public void SetFpsClickAction(Action action) => dtrFps.OnClick = _ => action();

    private unsafe void OnUpdate(IFramework fw)
    {
        pingWanted = clientState.IsLoggedIn && config.ServerInfoPingEnabled;
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
        var shown = config.NearbyDtrEnabled;
        if (dtrNearby.Shown != shown) dtrNearby.Shown = shown;
        if (!shown) return;

        var count = nearbyHandler?.NearbyPlayers.Count ?? 0;
        if (count == lastNearbyCount) return;
        lastNearbyCount = count;
        dtrNearby.Text    = $"\U0000e033 {count}";
        SetTooltip(dtrNearby, $"{count} nearby");
    }

    private void UpdateDcAddress()
    {
        var player = objectTable[0] as IPlayerCharacter;
        var dcId   = player?.CurrentWorld.ValueNullable?.DataCenter.RowId;
        if (dcId == null || dcId == lastDcId) return;
        lastDcId = (uint)dcId;
        dcFallbackAddress = dcId.Value switch
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

        var gameFw  = GameFramework.Instance();
        var gameFps = (int)(gameFw->FrameRate + 0.5f);

        // Sample the FPS chart history on a fixed interval, independent of the DTR text update below.
        var now = Environment.TickCount64;
        if (now - lastFpsSampleTick >= FpsSampleIntervalMs)
        {
            lastFpsSampleTick = now;
            fpsHistory.Enqueue(gameFps);
            if (fpsHistory.Count > FpsHistorySize) fpsHistory.Dequeue();
            FpsChartData = [.. fpsHistory];
        }

        // Alt-tabbed: nobody is watching the number, so fall back to the game's own value
        var fps = gameFps;
        if (config.ServerInfoFpsLive && !gameFw->WindowInactive)
            fps = SampleLiveFps(gameFps);
        else
            fpsLiveWindowStart = 0;

        var green = fpsLimiter?.IsLimiting == true;
        if (fps == lastFps && green == lastFpsGreen) return;
        lastFps      = fps;
        lastFpsGreen = green;

        var text = $"{fps} fps";
        dtrFps.Text = green
            ? new Lumina.Text.SeStringBuilder().PushColorRgba(FpsLimitGreen).Append(text).PopColor()
                .ToReadOnlySeString().ToDalamudString()
            : text;
        SetTooltip(dtrFps, green ? $"{text} (limited to {fpsLimiter!.ActiveTarget})" : text);
    }

    private int SampleLiveFps(int fallback)
    {
        var now = Stopwatch.GetTimestamp();
        if (fpsLiveWindowStart == 0)
        {
            fpsLiveWindowStart = now;
            fpsLiveFrames      = 0;
            fpsLiveValue       = fallback;
            return fpsLiveValue;
        }

        fpsLiveFrames++;
        var elapsed = now - fpsLiveWindowStart;
        if (elapsed >= FpsLiveWindowTicks)
        {
            fpsLiveValue       = (int)(fpsLiveFrames * (double)Stopwatch.Frequency / elapsed + 0.5);
            fpsLiveWindowStart = now;
            fpsLiveFrames      = 0;
        }
        return fpsLiveValue;
    }

    private void UpdatePingEntry()
    {
        var shown = config.ServerInfoPingEnabled;
        if (dtrPing.Shown != shown) dtrPing.Shown = shown;
        if (!shown) return;

        if (!pingDirty)
        {
            if (lastRtt == 0 && !pingPlaceholderSet)
            {
                dtrPing.Text = "0ms"; SetTooltip(dtrPing, "0ms");
                pingPlaceholderSet = true;
            }
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
        SetTooltip(dtrPing, pingText);
    }

    // Dalamud only pushes a DTR tooltip to the game on mouse over, so a tooltip that changes while
    // hovered stays stale until the cursor leaves and comes back. When the cursor is over the entry,
    // re-show the tooltip ourselves on the entry's text node so the open tooltip follows the text.
    private unsafe void SetTooltip(IDtrBarEntry entry, string text)
    {
        if (entry.Tooltip?.TextValue == text) return;
        entry.Tooltip = text;

        var (min, max) = entry.ScreenBounds;
        var mouse = ImGui.GetIO().MousePos;
        if (mouse.X < min.X || mouse.X > max.X || mouse.Y < min.Y || mouse.Y > max.Y) return;

        var stage = AtkStage.Instance();
        if (stage == null) return;
        var addon = stage->RaptureAtkUnitManager->GetAddonByName("_DTR");
        if (addon == null || !addon->IsVisible) return;

        // The entry's text node is the one sitting at the left edge of its bounds
        AtkResNode* target = null;
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || node->Type != NodeType.Text || !node->IsVisible()) continue;
            if (Math.Abs(node->ScreenX - min.X) > 1f || node->ScreenY < min.Y - 1f || node->ScreenY > max.Y) continue;
            target = node;
            break;
        }
        if (target == null) return;

        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var len   = Math.Min(bytes.Length, TooltipBufferSize - 1);
        Marshal.Copy(bytes, 0, tooltipBuffer, len);
        Marshal.WriteByte(tooltipBuffer + len, 0);
        stage->TooltipManager.ShowTooltip(addon->Id, target, (byte*)tooltipBuffer);
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
            // Nothing shows the result, so skip the TCP table read and the ping entirely
            if (!pingWanted)
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
                continue;
            }

            var address = DetectRealServerAddress() ?? dcFallbackAddress;
            connectedAddress = address;

            if (!IPAddress.IsLoopback(address))
            {
                try
                {
                    var reply = await ping.SendPingAsync(address).ConfigureAwait(false);
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

    // Reads the process' own TCP connection table to find the actual FFXIV server we're talking
    // to, rather than guessing from the DC's lobby IP. Falls back to null (and dcFallbackAddress)
    // if the table can't be read, or no matching connection is found (e.g. at the character select
    // screen, or briefly during a zone change).
    private const int AfInet = 2;
    private const int TcpTableOwnerPidConnections = 4;
    private const int TcpStateListen = 2;

    private static readonly (ushort Min, ushort Max)[] XivPortRanges =
    [
        (54992, 54994),
        (55006, 55007),
        (55021, 55040),
        (55296, 55551),
    ];

    private IPAddress? DetectRealServerAddress()
    {
        try
        {
            var size = 0;
            _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidConnections);
            if (size <= 0) return null;

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidConnections) != 0)
                    return null;

                var pid      = Environment.ProcessId;
                var rowSize  = Marshal.SizeOf<TcpRow>();
                var rowCount = Marshal.ReadInt32(buffer);
                var rowsPtr  = buffer + 4;

                for (var i = 0; i < rowCount && size - (4 + i * rowSize) >= rowSize; i++)
                {
                    var row = Marshal.PtrToStructure<TcpRow>(rowsPtr + i * rowSize);
                    if (row.State == TcpStateListen || (int)row.OwningPid != pid) continue;

                    var port = (ushort)IPAddress.NetworkToHostOrder((short)(ushort)row.RemotePort);
                    if (!XivPortRanges.Any(r => port >= r.Min && port <= r.Max)) continue;

                    var remote = new IPAddress(row.RemoteAddr);
                    if (IPAddress.IsLoopback(remote)) continue;

                    return remote;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            if (!tcpDetectDidError)
            {
                tcpDetectDidError = true;
                log.Warning(ex, "[HF] Reading TCP table for server address failed, falling back to DC address.");
            }
        }

        return null;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr tcpTable, ref int size, bool sort, int ipVersion,
        int tableClass, uint reserved = 0);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TcpRow
    {
        public readonly uint State;
        public readonly uint LocalAddr;
        public readonly uint LocalPort;
        public readonly uint RemoteAddr;
        public readonly uint RemotePort;
        public readonly uint OwningPid;
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
        Marshal.FreeHGlobal(tooltipBuffer);
    }
}
