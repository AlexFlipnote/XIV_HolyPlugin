using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Interface.Textures;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace HoliestFluffiness.Handlers;

internal sealed class PluginStatRow
{
    public required string Name;
    public required string InternalName;
    public bool IsDev;
    public bool IsDalamud;
    public bool IsSelf;   // this plugin; its draw time includes drawing the monitor window itself

    // Milliseconds; NaN when the plugin has no data for that category (no UI, no update handler)
    public double DrawLast = double.NaN, DrawAvg = double.NaN, DrawMax = double.NaN;
    public double UpdLast  = double.NaN, UpdAvg  = double.NaN, UpdMax  = double.NaN;
    public int    LeakedStacks;

    public int  Hooks;
    public int  TexCount;
    public long TexBytes;

    // Since the window opened: texture MB every 5 s, and growth against the first sample
    public float[] TexTrend = [];
    public long    TexGrowth;
    public int     HookGrowth;

    public readonly List<(string Handler, double Avg, double Max)> Handlers = [];

    private static double Z(double v) => double.IsNaN(v) ? 0 : v;
    public double TotalAvg => Z(DrawAvg) + Z(UpdAvg);
    public double Peak     => Math.Max(Z(DrawMax), Z(UpdMax));
}

internal sealed class ThreadStatRow
{
    public uint     Tid;
    public string   Name   = "";
    public string   Module = "";
    public double   CpuPct;   // htop style: 100% is one full core
    public TimeSpan CpuTotal;
    public bool     IsMain;
}

internal sealed class MemorySnapshot
{
    public long   PrivateBytes, WorkingSet;
    public long   SysTotal, SysAvail;
    public long   GcHeap, GcCommitted, GcFragmented;
    public double AllocPerSec;
    public double Gen0PerSec, Gen1PerSec, Gen2PerSec;
    public int    Gen2Total;
    public double GcPausePct;
    public int    Handles, ThreadCount;
    public double ProcCpuPct;  // normalised to all cores, 0..100
    public double IoReadPerSec, IoWritePerSec;  // file and disk I/O of the whole process
    public int    Cores;
    public int    TexCount;
    public long   TexBytes;

    public double MainThreadPct;            // % of one core
    public double GpuPct = double.NaN;      // game process, busiest engine
    public string GpuEngine = "";
    public double GpuTotalPct = double.NaN; // 3D engine, all processes
    public long   VramUsage = -1, VramBudget;
    public string GpuName = "";
}

internal sealed class SpikeRecord
{
    public DateTime Time;
    public float    FrameMs;
    public float    TypicalMs;
    public double   PluginsMs;
    public string?  TopHandler;
    public double   TopHandlerMs;
    public readonly List<(string Name, double Draw, double Upd)> Plugins = [];
    public double   OtherMs => Math.Max(0, FrameMs - PluginsMs);
}

// hdebug: an htop-style monitor for the game process. Per-plugin frame cost comes from Dalamud's own /xlstats counters
// (internal, read via reflection); threads and memory are sampled from the OS on a background task.
// Everything only runs while the window is open.
internal sealed class ProcessMonitorHandler : IDisposable
{
    private readonly IDalamudPluginInterface pi;
    private readonly ICondition condition;
    private readonly IPluginLog log;
    private readonly DalamudReflection? refl;

    public string? ReflectionError { get; }

    public bool Active { get; private set; }
    private bool prevDoStats, prevFrameworkStats;
    private uint mainThreadId;

    private CancellationTokenSource? cts;

    // Swapped whole by the sampler; readers take the reference once per frame
    public IReadOnlyList<ThreadStatRow> Threads { get; private set; } = [];
    public MemorySnapshot? Memory { get; private set; }
    public float[] CpuHistory     { get; private set; } = [];
    public float[] PrivateHistory { get; private set; } = [];
    public float[] HeapHistory    { get; private set; } = [];
    private const int HistoryLen = 120;
    private readonly Queue<float> cpuHist = new(), privHist = new(), heapHist = new();

    // Background-built per-plugin extras (hooks, textures), merged into the main-thread rows
    private volatile Dictionary<string, (int Hooks, int TexCount, long TexBytes)> extras = new();
    private volatile Dictionary<string, (int TexCount, long TexBytes)> dalamudTex = new();

    public List<PluginStatRow> Plugins { get; private set; } = [];
    private long lastPluginRefresh;
    private readonly Dictionary<string, string?> handlerOwnerCache = new();

    // Spike catcher (main thread only)
    public float SpikeThresholdMs = 25f;
    public bool  IgnoreLoading    = true;
    public readonly List<SpikeRecord> Spikes = [];
    private const int MaxSpikes = 100;
    public const int FrameRingLen = 3000; // ~25-50 s depending on FPS; lows need a long window to mean anything
    private readonly float[] frameRing = new float[FrameRingLen];
    private int  frameRingPos, frameRingCount;
    private long lastDrawTs, startTs, lastLoadingTs;
    private List<(string Name, object Ds)> spikeDrawSources = [];
    private Dictionary<string, string> ownerNames = new();

    public IReadOnlyList<ModuleRow> Modules { get; private set; } = [];
    public NetSnapshot? Network { get; private set; }
    public float[] NetDownHistory { get; private set; } = [];
    public float[] NetUpHistory   { get; private set; } = [];
    private readonly Queue<float> downHist = new(), upHist = new();
    public float[] IoReadHistory  { get; private set; } = [];
    public float[] IoWriteHistory { get; private set; } = [];
    private readonly Queue<float> ioReadHist = new(), ioWriteHist = new();

    // Per plugin, built on the sampler thread and swapped whole
    private volatile Dictionary<string, (float[] TexMb, long TexStart, int HookStart)> trends = new();
    private const int TrendLen = 240; // 20 minutes at one point per 5 s

    public ProcessMonitorHandler(IDalamudPluginInterface pi, ICondition condition, IPluginLog log)
    {
        this.pi        = pi;
        this.condition = condition;
        this.log       = log;
        try
        {
            refl = new DalamudReflection();
        }
        catch (Exception ex)
        {
            ReflectionError = ex.Message;
            log.Warning(ex, "[HF] hdebug: Dalamud internals changed, plugin stats unavailable.");
        }
    }

    // Must be called on the main thread (window OnOpen)
    public void Start()
    {
        if (Active) return;
        Active       = true;
        mainThreadId = GetCurrentThreadId();

        if (refl != null)
        {
            try
            {
                prevDoStats        = refl.DoStats;
                prevFrameworkStats = refl.FrameworkStatsEnabled;
                refl.DoStats               = true;
                refl.FrameworkStatsEnabled = true;
            }
            catch (Exception ex) { log.Warning(ex, "[HF] hdebug: could not enable Dalamud stats."); }
        }

        lastDrawTs = 0;
        startTs    = Stopwatch.GetTimestamp();
        pi.UiBuilder.Draw += OnDraw;

        cts = new CancellationTokenSource();
        var token = cts.Token;
        Task.Run(() => SampleLoop(token), token);
    }

    public void Stop()
    {
        if (!Active) return;
        Active = false;
        pi.UiBuilder.Draw -= OnDraw;
        cts?.Cancel();
        cts?.Dispose();
        cts = null;

        // Only switch Dalamud's counters back off if they were off before we opened, so /xlstats keeps its state
        if (refl != null)
        {
            try
            {
                if (!prevDoStats)        refl.DoStats = false;
                if (!prevFrameworkStats) refl.FrameworkStatsEnabled = false;
            }
            catch { /* Dalamud is unloading or changed; nothing to restore */ }
        }
    }

    public void Dispose() => Stop();

    // Main thread only: Dalamud writes these counters from the draw and framework loop
    public void ResetStats()
    {
        if (refl == null) return;
        try
        {
            foreach (var lp in refl.InstalledPlugins())
                refl.ResetDrawStats(lp);
            refl.FrameworkStats()?.Clear();
        }
        catch (Exception ex) { log.Warning(ex, "[HF] hdebug: reset failed."); }
        lastPluginRefresh = 0;
    }

    public void ClearSpikes() => Spikes.Clear();

    // FPS over the whole frame history: average, and the average of the slowest 1% / 0.1% of frames
    // converted back to FPS. Recomputed twice a second; sorting a few thousand floats is cheap at that rate.
    public (float Avg, float Low1, float Low01, int Frames) FpsStats { get; private set; }
    private long lastFpsStats;

    private void UpdateFpsStats()
    {
        var now = Environment.TickCount64;
        if (now - lastFpsStats < 500) return;
        lastFpsStats = now;

        var n = frameRingCount;
        if (n < 10) { FpsStats = default; return; }
        var buf = new float[n];
        // Order does not matter here, and the ring fills from index 0, so the first n slots are the live ones
        Array.Copy(frameRing, buf, n);
        Array.Sort(buf);

        double sum = 0;
        foreach (var f in buf) sum += f;
        static float WorstAvg(float[] sorted, int count)
        {
            double s = 0;
            for (var i = sorted.Length - count; i < sorted.Length; i++) s += sorted[i];
            return (float)(s / count);
        }

        var w1  = WorstAvg(buf, Math.Max(1, n / 100));
        var w01 = WorstAvg(buf, Math.Max(1, n / 1000));
        FpsStats = ((float)(1000.0 / (sum / n)), 1000f / w1, 1000f / w01, n);
    }

    // The most recent `max` frame times in order, oldest first
    public float[] FrameTimes(int max = 600)
    {
        var n     = Math.Min(frameRingCount, max);
        var arr   = new float[n];
        var start = (frameRingPos - n + FrameRingLen) % FrameRingLen;
        for (var i = 0; i < n; i++) arr[i] = frameRing[(start + i) % FrameRingLen];
        return arr;
    }

    // Median of the last ~60 frames; spikes are judged against this so a low background
    // framerate is not one long stream of "spikes"
    public float TypicalFrameMs()
    {
        var n = Math.Min(frameRingCount, 60);
        if (n == 0) return 0;
        var buf = new float[n];
        for (var i = 0; i < n; i++) buf[i] = frameRing[(frameRingPos - 1 - i + FrameRingLen) % FrameRingLen];
        Array.Sort(buf);
        return buf[n / 2];
    }

    // Runs inside our own UiBuilder.Draw, so it fires once per frame at the same point. The gap between
    // two calls covers exactly the work the "last" counters hold right now: the plugins that drew after
    // us last frame, this frame's game and Framework.Update work, and the plugins that drew before us.
    private void OnDraw()
    {
        RefreshPlugins();

        var now = Stopwatch.GetTimestamp();
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
            lastLoadingTs = now;

        if (lastDrawTs != 0)
        {
            var ms      = (float)Stopwatch.GetElapsedTime(lastDrawTs, now).TotalMilliseconds;
            var warm    = Stopwatch.GetElapsedTime(startTs, now).TotalSeconds > 2;
            var loading = IgnoreLoading && lastLoadingTs != 0 && Stopwatch.GetElapsedTime(lastLoadingTs, now).TotalSeconds < 3;
            if (warm && !loading && ms >= SpikeThresholdMs)
            {
                var typical = TypicalFrameMs();
                if (ms >= typical * 2f) CaptureSpike(ms, typical);
            }

            // Loading-screen frames would sink the lows, so they stay out of the history entirely
            if (!loading)
            {
                frameRing[frameRingPos] = ms;
                frameRingPos = (frameRingPos + 1) % FrameRingLen;
                if (frameRingCount < FrameRingLen) frameRingCount++;
            }
        }
        lastDrawTs = now;
        UpdateFpsStats();
    }

    private void CaptureSpike(float frameMs, float typical)
    {
        var rec = new SpikeRecord { Time = DateTime.Now, FrameMs = frameMs, TypicalMs = typical };
        if (refl != null)
        {
            try
            {
                var tickToMs = 1000.0 / Stopwatch.Frequency;
                var per = new Dictionary<string, (double Draw, double Upd)>();
                foreach (var (name, ds) in spikeDrawSources)
                {
                    var t = refl.LastDraw(ds);
                    if (t > 0) per[name] = (t * tickToMs, 0);
                }

                var fw = refl.FrameworkStats();
                if (fw != null)
                {
                    foreach (var (key, samples) in fw)
                    {
                        if (samples.Count == 0) continue;
                        var v = samples[^1];
                        var owner = handlerOwnerCache.TryGetValue(key, out var o) && o != null && ownerNames.TryGetValue(o, out var n) ? n : "Dalamud";
                        var e = per.GetValueOrDefault(owner);
                        per[owner] = (e.Draw, e.Upd + v);
                        if (v > rec.TopHandlerMs) { rec.TopHandlerMs = v; rec.TopHandler = key; }
                    }
                }

                foreach (var (name, (draw, upd)) in per)
                {
                    rec.PluginsMs += draw + upd;
                    if (draw + upd >= 0.05) rec.Plugins.Add((name, draw, upd));
                }
                rec.Plugins.Sort((a, b) => (b.Draw + b.Upd).CompareTo(a.Draw + a.Upd));
            }
            catch (Exception ex) { log.Debug(ex, "[HF] hdebug: spike capture failed."); }
        }

        Spikes.Insert(0, rec);
        if (Spikes.Count > MaxSpikes) Spikes.RemoveAt(Spikes.Count - 1);
    }

    // ---------------------------------------------------------------- plugins (main thread)

    // Throttled to 2 Hz: averaging every handler's history each frame would show up in our own draw time
    public void RefreshPlugins()
    {
        if (refl == null) return;
        var now = Environment.TickCount64;
        if (now - lastPluginRefresh < 500) return;
        lastPluginRefresh = now;

        try
        {
            var rows      = new List<PluginStatRow>();
            var byAsm     = new Dictionary<Assembly, PluginStatRow>();
            var byPrefix  = new List<(string Prefix, PluginStatRow Row)>();
            var dalamud   = new PluginStatRow { Name = "Dalamud", InternalName = "Dalamud", IsDalamud = true };
            var tickToMs  = 1000.0 / Stopwatch.Frequency;
            var drawSrc   = new List<(string Name, object Ds)>();
            var names     = new Dictionary<string, string>();

            foreach (var lp in refl.InstalledPlugins())
            {
                if (!refl.IsLoaded(lp)) continue;
                var row = new PluginStatRow
                {
                    Name         = refl.Name(lp),
                    InternalName = refl.InternalName(lp),
                    IsDev        = refl.IsDev(lp),
                    IsSelf       = refl.InternalName(lp) == pi.InternalName,
                };

                names[row.InternalName] = row.Name;
                if (refl.DrawStatsObject(lp) is { } dso) drawSrc.Add((row.Name, dso));

                var ds = refl.DrawStats(lp);
                if (ds.HasValue && ds.Value.Last >= 0)
                {
                    row.DrawLast     = ds.Value.Last * tickToMs;
                    row.DrawAvg      = ds.Value.Avg  * tickToMs;
                    row.DrawMax      = ds.Value.Max  * tickToMs;
                    row.LeakedStacks = ds.Value.Leaked;
                }

                var asm = refl.Assembly(lp);
                if (asm != null)
                {
                    byAsm[asm] = row;
                    var asmName = asm.GetName().Name;
                    if (!string.IsNullOrEmpty(asmName)) byPrefix.Add((asmName + ".", row));
                }
                rows.Add(row);
            }

            // Framework.Update handlers are keyed "Namespace.Type::Method"; attribute each to its plugin
            var fw = refl.FrameworkStats();
            if (fw != null)
            {
                foreach (var (key, samples) in fw)
                {
                    if (samples.Count == 0) continue;
                    var owner = ResolveHandlerOwner(key, byAsm, byPrefix) ?? dalamud;

                    double sum = 0, max = 0;
                    foreach (var s in samples) { sum += s; if (s > max) max = s; }
                    var avg = sum / samples.Count;

                    owner.UpdLast = Nz(owner.UpdLast) + samples[^1];
                    owner.UpdAvg  = Nz(owner.UpdAvg) + avg;
                    owner.UpdMax  = Math.Max(Nz(owner.UpdMax), max);
                    owner.Handlers.Add((key, avg, max));
                }
            }

            var ex = extras;
            var tr = trends;
            foreach (var r in rows)
            {
                if (ex.TryGetValue(r.InternalName, out var e))
                {
                    r.Hooks    = e.Hooks;
                    r.TexCount = e.TexCount;
                    r.TexBytes = e.TexBytes;
                }
                if (tr.TryGetValue(r.InternalName, out var t))
                {
                    r.TexTrend   = t.TexMb;
                    r.TexGrowth  = r.TexBytes - t.TexStart;
                    r.HookGrowth = r.Hooks - t.HookStart;
                }
                r.Handlers.Sort((a, b) => b.Avg.CompareTo(a.Avg));
            }

            if (dalamudTex.TryGetValue("", out var dt)) { dalamud.TexCount = dt.TexCount; dalamud.TexBytes = dt.TexBytes; }
            dalamud.Handlers.Sort((a, b) => b.Avg.CompareTo(a.Avg));
            rows.Add(dalamud);

            Plugins          = rows;
            spikeDrawSources = drawSrc;
            ownerNames       = names;
        }
        catch (Exception e)
        {
            log.Warning(e, "[HF] hdebug: plugin stat refresh failed.");
        }
    }

    private static double Nz(double v) => double.IsNaN(v) ? 0 : v;

    private PluginStatRow? ResolveHandlerOwner(string key, Dictionary<Assembly, PluginStatRow> byAsm, List<(string Prefix, PluginStatRow Row)> byPrefix)
    {
        if (!handlerOwnerCache.TryGetValue(key, out var ownerName))
        {
            ownerName = null;
            var sep      = key.LastIndexOf("::", StringComparison.Ordinal);
            var typeName = sep > 0 ? key[..sep] : key;

            foreach (var (asm, row) in byAsm)
            {
                Type? t = null;
                try { t = asm.GetType(typeName, false); } catch { /* malformed generic name */ }
                if (t != null) { ownerName = row.InternalName; break; }
            }
            if (ownerName == null)
            {
                foreach (var (prefix, row) in byPrefix)
                    if (typeName.StartsWith(prefix, StringComparison.Ordinal)) { ownerName = row.InternalName; break; }
            }
            handlerOwnerCache[key] = ownerName;
        }

        if (ownerName == null) return null;
        foreach (var row in byAsm.Values)
            if (row.InternalName == ownerName) return row;
        return null;
    }

    // ---------------------------------------------------------------- background sampler

    private async Task SampleLoop(CancellationToken token)
    {
        var state = new SamplerState();
        try { state.Gpu  = new GpuCounters(); } catch (Exception ex) { log.Debug(ex, "[HF] hdebug: GPU counters unavailable."); }
        try { state.Vram = new VramReader(); }  catch (Exception ex) { log.Debug(ex, "[HF] hdebug: DXGI unavailable."); }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                try { SampleOnce(state); }
                catch (Exception ex) { log.Debug(ex, "[HF] hdebug: sample failed."); }
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
        finally
        {
            state.Gpu?.Dispose();
            state.Vram?.Dispose();
        }
    }

    private sealed class SamplerState
    {
        public long LastWallTicks;
        public long LastProcCpu100ns;
        public readonly Dictionary<uint, long>   ThreadCpu    = new();
        public readonly Dictionary<uint, string> ThreadModule = new();
        public readonly Dictionary<nint, string> ModuleNames  = new();
        public long     LastAlloc;
        public int      LastGen0, LastGen1, LastGen2;
        public TimeSpan LastPause;
        public bool     HasPrev;
        public GpuCounters? Gpu;
        public VramReader?  Vram;
        public readonly ModuleScanner Scanner = new();
        public List<ModuleRow> ModuleBase = [];
        public int ModuleTick;
        public ulong LastIoRead, LastIoWrite;
        public int   TrendTick;
        public readonly Dictionary<string, (Queue<float> Tex, long TexStart, int HookStart)> TrendAcc = new();
        public readonly NetworkSampler Net = new();
    }

    private void SampleOnce(SamplerState s)
    {
        var wall    = Stopwatch.GetTimestamp();
        var elapsed = s.HasPrev ? Stopwatch.GetElapsedTime(s.LastWallTicks, wall) : TimeSpan.Zero;
        var dt100ns = Math.Max(1, elapsed.Ticks);
        var pid     = (uint)Environment.ProcessId;

        // Threads
        var threads = new List<ThreadStatRow>();
        var seen    = new HashSet<uint>();
        var snap    = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snap != INVALID_HANDLE)
        {
            try
            {
                var te = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };
                for (var ok = Thread32First(snap, ref te); ok; ok = Thread32Next(snap, ref te))
                {
                    if (te.th32OwnerProcessID != pid) continue;
                    var tid = te.th32ThreadID;
                    var h   = OpenThread(THREAD_QUERY_INFORMATION | THREAD_QUERY_LIMITED_INFORMATION, false, tid);
                    if (h == 0) continue;
                    try
                    {
                        if (!GetThreadTimes(h, out _, out _, out var k, out var u)) continue;
                        var cpu = k + u;
                        seen.Add(tid);

                        var row = new ThreadStatRow { Tid = tid, CpuTotal = TimeSpan.FromTicks(cpu), IsMain = tid == mainThreadId };
                        if (s.HasPrev && s.ThreadCpu.TryGetValue(tid, out var prev))
                            row.CpuPct = Math.Max(0, (cpu - prev) * 100.0 / dt100ns);
                        s.ThreadCpu[tid] = cpu;

                        if (!s.ThreadModule.TryGetValue(tid, out var mod))
                        {
                            mod = ResolveStartModule(h, s);
                            s.ThreadModule[tid] = mod;
                        }
                        row.Module = mod;

                        // .NET mirrors Thread.Name into the Windows thread description, so plugin threads usually name themselves
                        if (GetThreadDescription(h, out var desc) >= 0 && desc != 0)
                        {
                            row.Name = Marshal.PtrToStringUni(desc) ?? "";
                            LocalFree(desc);
                        }
                        if (row.IsMain && row.Name.Length == 0) row.Name = "Main thread";
                        threads.Add(row);
                    }
                    finally { CloseHandle(h); }
                }
            }
            finally { CloseHandle(snap); }
        }
        foreach (var tid in s.ThreadCpu.Keys.Where(t => !seen.Contains(t)).ToList())
        {
            s.ThreadCpu.Remove(tid);
            s.ThreadModule.Remove(tid);
        }

        // Process + GC
        var m = new MemorySnapshot { Cores = Environment.ProcessorCount };
        using (var p = Process.GetCurrentProcess())
        {
            m.PrivateBytes = p.PrivateMemorySize64;
            m.WorkingSet   = p.WorkingSet64;
            m.Handles      = p.HandleCount;
            m.ThreadCount  = threads.Count;
            var procCpu = p.TotalProcessorTime.Ticks;
            if (s.HasPrev) m.ProcCpuPct = Math.Clamp((procCpu - s.LastProcCpu100ns) * 100.0 / dt100ns / m.Cores, 0, 100);
            s.LastProcCpu100ns = procCpu;
        }

        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref ms))
        {
            m.SysTotal = (long)ms.ullTotalPhys;
            m.SysAvail = (long)ms.ullAvailPhys;
        }

        var gci = GC.GetGCMemoryInfo();
        m.GcHeap       = gci.HeapSizeBytes;
        m.GcCommitted  = gci.TotalCommittedBytes;
        m.GcFragmented = gci.FragmentedBytes;

        var alloc = GC.GetTotalAllocatedBytes(false);
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        var pause = GC.GetTotalPauseDuration();
        m.Gen2Total = g2;
        if (s.HasPrev)
        {
            var secs = elapsed.TotalSeconds;
            m.AllocPerSec = (alloc - s.LastAlloc) / secs;
            m.Gen0PerSec  = (g0 - s.LastGen0) / secs;
            m.Gen1PerSec  = (g1 - s.LastGen1) / secs;
            m.Gen2PerSec  = (g2 - s.LastGen2) / secs;
            m.GcPausePct  = (pause - s.LastPause).TotalMilliseconds / elapsed.TotalMilliseconds * 100.0;
        }
        s.LastAlloc = alloc; s.LastGen0 = g0; s.LastGen1 = g1; s.LastGen2 = g2; s.LastPause = pause;

        if (GetProcessIoCounters(GetCurrentProcess(), out var io))
        {
            if (s.HasPrev)
            {
                var secs = Math.Max(0.001, elapsed.TotalSeconds);
                m.IoReadPerSec  = (io.ReadTransferCount  - s.LastIoRead)  / secs;
                m.IoWritePerSec = (io.WriteTransferCount - s.LastIoWrite) / secs;
            }
            s.LastIoRead  = io.ReadTransferCount;
            s.LastIoWrite = io.WriteTransferCount;
        }

        m.MainThreadPct = threads.FirstOrDefault(t => t.IsMain)?.CpuPct ?? 0;

        if (s.Gpu != null && s.Gpu.Sample(pid, out var gp, out var ge, out var gt))
        {
            m.GpuPct = gp; m.GpuEngine = ge; m.GpuTotalPct = gt;
        }
        if (s.Vram != null && s.Vram.Query(out var vu, out var vb, out var vn))
        {
            m.VramUsage = vu; m.VramBudget = vb; m.GpuName = vn;
        }

        // The module list changes rarely: rescan every 10 s, but refresh CPU per module every sample
        if (s.ModuleTick++ % 10 == 0)
        {
            try { s.ModuleBase = s.Scanner.Scan(); }
            catch (Exception ex) { log.Debug(ex, "[HF] hdebug: module scan failed."); }
        }
        var cpuByMod = new Dictionary<string, (double Cpu, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in threads)
        {
            var plus = t.Module.IndexOf('+');
            var mod  = plus > 0 ? t.Module[..plus] : t.Module;
            var e    = cpuByMod.GetValueOrDefault(mod);
            cpuByMod[mod] = (e.Cpu + t.CpuPct, e.Count + 1);
        }
        var modules = new List<ModuleRow>(s.ModuleBase.Count);
        foreach (var b in s.ModuleBase)
        {
            var c = cpuByMod.GetValueOrDefault(b.FileName);
            modules.Add(new ModuleRow
            {
                Label = b.Label, FileName = b.FileName, Category = b.Category, Path = b.Path,
                Company = b.Company, Product = b.Product, Size = b.Size, CpuPct = c.Cpu, Threads = c.Count,
            });
        }

        NetSnapshot? net = null;
        try { net = s.Net.Sample(pid, s.HasPrev ? elapsed.TotalSeconds : 0); }
        catch (Exception ex) { log.Debug(ex, "[HF] hdebug: network sample failed."); }

        // Hooks + textures per plugin; both collections are thread-safe on Dalamud's side
        if (refl != null) SampleExtras(m, s);

        s.LastWallTicks = wall;
        var first = !s.HasPrev;
        s.HasPrev = true;
        if (first) return; // first pass only seeds deltas; percentages would read as zero

        Threads = threads;
        Memory  = m;
        Modules = modules;
        Network = net;
        IoReadHistory  = Push(ioReadHist,  (float)(m.IoReadPerSec  / 1048576));
        IoWriteHistory = Push(ioWriteHist, (float)(m.IoWritePerSec / 1048576));
        if (net != null)
        {
            NetDownHistory = Push(downHist, (float)(net.SysDown / 1024));
            NetUpHistory   = Push(upHist,   (float)(net.SysUp / 1024));
        }
        CpuHistory     = Push(cpuHist,  (float)m.ProcCpuPct);
        PrivateHistory = Push(privHist, m.PrivateBytes / 1048576f);
        HeapHistory    = Push(heapHist, m.GcHeap / 1048576f);
    }

    private static float[] Push(Queue<float> q, float v)
    {
        q.Enqueue(v);
        while (q.Count > HistoryLen) q.Dequeue();
        return q.ToArray();
    }

    private void SampleExtras(MemorySnapshot m, SamplerState s)
    {
        try
        {
            var result   = new Dictionary<string, (int Hooks, int TexCount, long TexBytes)>();
            var asmToKey = new Dictionary<Assembly, string>();
            var lpToKey  = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
            foreach (var lp in refl!.InstalledPlugins())
            {
                var key = refl.InternalName(lp);
                lpToKey[lp] = key;
                var asm = refl.Assembly(lp);
                if (asm != null) asmToKey[asm] = key;
            }

            foreach (var (asm, alive) in refl.Hooks())
            {
                if (!alive || !asmToKey.TryGetValue(asm, out var key)) continue;
                var e = result.GetValueOrDefault(key);
                result[key] = (e.Hooks + 1, e.TexCount, e.TexBytes);
            }

            int dalCount = 0; long dalBytes = 0;
            foreach (var (owners, bytes) in refl.Textures())
            {
                m.TexCount++;
                m.TexBytes += bytes;
                var any = false;
                // A texture shared by several plugins is counted in full for each owner
                foreach (var o in owners)
                {
                    if (!lpToKey.TryGetValue(o, out var key)) continue;
                    any = true;
                    var e = result.GetValueOrDefault(key);
                    result[key] = (e.Hooks, e.TexCount + 1, e.TexBytes + bytes);
                }
                if (!any) { dalCount++; dalBytes += bytes; }
            }

            extras     = result;

            // Trend points every 5 s; every loaded plugin gets one, so zero-texture plugins still have a line
            if (s.TrendTick++ % 5 == 0)
            {
                var snap = new Dictionary<string, (float[] TexMb, long TexStart, int HookStart)>();
                foreach (var key in lpToKey.Values)
                {
                    var cur = result.GetValueOrDefault(key);
                    if (!s.TrendAcc.TryGetValue(key, out var acc))
                    {
                        acc = (new Queue<float>(), cur.TexBytes, cur.Hooks);
                        s.TrendAcc[key] = acc;
                    }
                    acc.Tex.Enqueue(cur.TexBytes / 1048576f);
                    while (acc.Tex.Count > TrendLen) acc.Tex.Dequeue();
                    snap[key] = (acc.Tex.ToArray(), acc.TexStart, acc.HookStart);
                }
                trends = snap;
            }
            dalamudTex = new() { [""] = (dalCount, dalBytes) };
        }
        catch (Exception ex) { log.Debug(ex, "[HF] hdebug: extras sample failed."); }
    }

    private static string ResolveStartModule(nint hThread, SamplerState s)
    {
        if (NtQueryInformationThread(hThread, ThreadQuerySetWin32StartAddress, out var addr, nint.Size, 0) != 0 || addr == 0)
            return "?";
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, addr, out var hMod) || hMod == 0)
            return $"0x{addr:X}";
        if (!s.ModuleNames.TryGetValue(hMod, out var name))
        {
            var buf = new char[520];
            var len = GetModuleFileNameW(hMod, buf, buf.Length);
            var full = len > 0 ? new string(buf, 0, (int)len) : "?";
            name = System.IO.Path.GetFileName(full);
            s.ModuleNames[hMod] = name;
        }
        return $"{name}+0x{addr - hMod:X}";
    }

    // ---------------------------------------------------------------- Win32

    private const uint TH32CS_SNAPTHREAD = 0x4;
    private const uint THREAD_QUERY_INFORMATION = 0x40;
    private const uint THREAD_QUERY_LIMITED_INFORMATION = 0x800;
    private const int  ThreadQuerySetWin32StartAddress = 9;
    private const uint GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x4;
    private const uint GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x2;
    private static readonly nint INVALID_HANDLE = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct THREADENTRY32
    {
        public uint dwSize, cntUsage, th32ThreadID, th32OwnerProcessID;
        public int  tpBasePri, tpDeltaPri;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint  dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Thread32First(nint snap, ref THREADENTRY32 te);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Thread32Next(nint snap, ref THREADENTRY32 te);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenThread(uint access, bool inherit, uint tid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadTimes(nint h, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern int GetThreadDescription(nint h, out nint desc);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint mem);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessIoCounters(nint process, out IO_COUNTERS counters);

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX ms);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetModuleHandleExW(uint flags, nint addr, out nint hMod);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern uint GetModuleFileNameW(nint hMod, char[] buf, int size);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationThread(nint h, int cls, out nint info, int len, nint retLen);

    // ---------------------------------------------------------------- Dalamud internals

    // Everything /xlstats and /xldata read is internal. Resolved once up front so a Dalamud update that
    // renames something fails loudly at construction instead of throwing mid-draw.
    private sealed class DalamudReflection
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        private readonly object pluginManager;
        private readonly PropertyInfo installedPlugins;
        private readonly PropertyInfo lpName, lpInternalName, lpIsLoaded, lpIsDev, lpAssembly, lpInterface;
        private readonly PropertyInfo ifUiBuilder, uiDrawStats, uiDoStats;
        private readonly PropertyInfo dsLast, dsAvg, dsMax, dsLeaked;
        private readonly MethodInfo   dsReset;
        private readonly PropertyInfo fwStatsEnabled, fwStatsHistory;
        private readonly PropertyInfo hookTracked, hiHook, hiAssembly;
        private readonly object?      textureManager;
        private readonly PropertyInfo? tmBlame, tmBlameLock, btOwners, btSpecs, specBytes;

        public DalamudReflection()
        {
            var dal = typeof(IDalamudPluginInterface).Assembly;
            Type T(string n) => dal.GetType(n, true)!;
            PropertyInfo P(Type t, string n) => t.GetProperty(n, Any) ?? throw new MissingMemberException(t.Name, n);
            object Svc(string n) => T("Dalamud.Service`1").MakeGenericType(T(n)).GetMethod("Get", Any)!.Invoke(null, null)!;

            var pmType = T("Dalamud.Plugin.Internal.PluginManager");
            pluginManager    = Svc(pmType.FullName!);
            installedPlugins = P(pmType, "InstalledPlugins");

            var lp = T("Dalamud.Plugin.Internal.Types.LocalPlugin");
            lpName         = P(lp, "Name");
            lpInternalName = P(lp, "InternalName");
            lpIsLoaded     = P(lp, "IsLoaded");
            lpIsDev        = P(lp, "IsDev");
            lpAssembly     = P(lp, "Assembly");
            lpInterface    = P(lp, "DalamudInterface");

            ifUiBuilder = P(T("Dalamud.Plugin.DalamudPluginInterface"), "LocalUiBuilder");
            var ui      = T("Dalamud.Interface.UiBuilder");
            uiDrawStats = P(ui, "PluginDrawStatistics");
            uiDoStats   = P(ui, "DoStats");

            var ds   = T("Dalamud.Interface.PluginDrawStatistics");
            dsLast   = P(ds, "LastDrawTime");
            dsAvg    = P(ds, "AverageDrawTime");
            dsMax    = P(ds, "MaxDrawTime");
            dsLeaked = P(ds, "LeakedStacks");
            dsReset  = ds.GetMethod("Reset", Any) ?? throw new MissingMethodException(ds.Name, "Reset");

            var fw = T("Dalamud.Game.Framework");
            fwStatsEnabled = P(fw, "StatsEnabled");
            fwStatsHistory = P(fw, "StatsHistory");

            hookTracked = P(T("Dalamud.Hooking.Internal.HookManager"), "TrackedHooks");
            var hi      = T("Dalamud.Hooking.Internal.HookInfo");
            hiHook      = P(hi, "Hook");
            hiAssembly  = P(hi, "Assembly");

            // Texture blame is a nice-to-have; missing it should not take the whole window down
            try
            {
                var tmType = T("Dalamud.Interface.Textures.Internal.TextureManager");
                textureManager = Svc(tmType.FullName!);
                tmBlame        = P(tmType, "BlameTracker");
                tmBlameLock    = P(tmType, "BlameTrackerLock");
                var bt         = T("Dalamud.Interface.Textures.Internal.TextureManager+IBlameableDalamudTextureWrap");
                btOwners       = P(bt, "OwnerPlugins");
                btSpecs        = P(bt, "RawSpecs");
                specBytes      = P(typeof(RawImageSpecification), "EstimatedBytes");
            }
            catch { textureManager = null; }
        }

        public bool DoStats
        {
            get => (bool)uiDoStats.GetValue(null)!;
            set => uiDoStats.SetValue(null, value);
        }

        public bool FrameworkStatsEnabled
        {
            get => (bool)fwStatsEnabled.GetValue(null)!;
            set => fwStatsEnabled.SetValue(null, value);
        }

        public Dictionary<string, List<double>>? FrameworkStats() =>
            fwStatsHistory.GetValue(null) as Dictionary<string, List<double>>;

        public IEnumerable<object> InstalledPlugins() =>
            ((IEnumerable)installedPlugins.GetValue(pluginManager)!).Cast<object>();

        public string Name(object lp)         => (string)lpName.GetValue(lp)!;
        public string InternalName(object lp) => (string)lpInternalName.GetValue(lp)!;
        public bool   IsLoaded(object lp)     => (bool)lpIsLoaded.GetValue(lp)!;
        public bool   IsDev(object lp)        => (bool)lpIsDev.GetValue(lp)!;
        public Assembly? Assembly(object lp)  => lpAssembly.GetValue(lp) as Assembly;

        public object? DrawStatsObject(object lp) => DrawStatsObj(lp);

        public long LastDraw(object ds) => (long)dsLast.GetValue(ds)!;

        private object? DrawStatsObj(object lp)
        {
            var iface = lpInterface.GetValue(lp);
            if (iface == null) return null;
            var ui = ifUiBuilder.GetValue(iface);
            return ui == null ? null : uiDrawStats.GetValue(ui);
        }

        public (long Last, long Avg, long Max, int Leaked)? DrawStats(object lp)
        {
            var ds = DrawStatsObj(lp);
            if (ds == null) return null;
            return ((long)dsLast.GetValue(ds)!, (long)dsAvg.GetValue(ds)!, (long)dsMax.GetValue(ds)!, (int)dsLeaked.GetValue(ds)!);
        }

        public void ResetDrawStats(object lp)
        {
            var ds = DrawStatsObj(lp);
            if (ds != null) dsReset.Invoke(ds, null);
        }

        public IEnumerable<(Assembly Asm, bool Alive)> Hooks()
        {
            if (hookTracked.GetValue(null) is not IDictionary dict) yield break;
            foreach (var info in dict.Values.Cast<object>().ToList())
            {
                if (hiAssembly.GetValue(info) is not Assembly asm) continue;
                var alive = hiHook.GetValue(info) is IDalamudHook h && !h.IsDisposed;
                yield return (asm, alive);
            }
        }

        public List<(List<object> Owners, long Bytes)> Textures()
        {
            var result = new List<(List<object>, long)>();
            if (textureManager == null) return result;
            if (tmBlame!.GetValue(textureManager) is not IList list) return result;
            if (tmBlameLock!.GetValue(textureManager) is not Lock lk) return result;

            using (lk.EnterScope())
            {
                foreach (var tex in list)
                {
                    if (tex == null) continue;
                    var specs = btSpecs!.GetValue(tex);
                    var bytes = specs != null && specBytes!.GetValue(specs) is int b ? b : 0;
                    var owners = new List<object>();
                    if (btOwners!.GetValue(tex) is IList ol)
                        lock (ol) foreach (var o in ol) if (o != null) owners.Add(o);
                    result.Add((owners, Math.Max(0, bytes)));
                }
            }
            return result;
        }
    }
}
