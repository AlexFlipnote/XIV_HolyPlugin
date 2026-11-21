using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Dalamud.Plugin;
using TerraFX.Interop.DirectX;

namespace HoliestFluffiness.Handlers;

internal sealed class ModuleRow
{
    public string Label    = "";  // friendly name when known, else the file name
    public string FileName = "";
    public string Category = "";
    public string Path     = "";
    public string Company  = "";
    public string Product  = "";
    public long   Size;
    public double CpuPct;
    public int    Threads;
}

// GPU engine utilisation from the same performance counters Task Manager reads. Per process, per engine
// type; the busiest engine is the process's GPU %, like Task Manager's GPU column.
internal sealed class GpuCounters : IDisposable
{
    private nint query, counter;
    private readonly bool ok;

    public GpuCounters()
    {
        if (PdhOpenQueryW(null, 0, out query) != 0) return;
        if (PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", 0, out counter) != 0)
        {
            PdhCloseQuery(query);
            query = 0;
            return;
        }
        PdhCollectQueryData(query); // rate counter: the first collect only seeds the baseline
        ok = true;
    }

    public bool Sample(uint pid, out double gamePct, out string gameEngine, out double total3dPct)
    {
        gamePct = 0; gameEngine = ""; total3dPct = 0;
        if (!ok || PdhCollectQueryData(query) != 0) return false;

        uint size = 0;
        if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out _, 0) != PDH_MORE_DATA || size == 0)
            return false;

        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out var count, buf) != 0) return false;

            var tag    = $"pid_{pid}_";
            var byEng  = new Dictionary<string, double>();
            // PDH_FMT_COUNTERVALUE_ITEM_W: name pointer, then { DWORD status, pad, double }
            const int itemSize = 24;
            for (var i = 0; i < count; i++)
            {
                var p      = buf + i * itemSize;
                var status = (uint)Marshal.ReadInt32(p, 8);
                if (status > 1) continue; // VALID_DATA or NEW_DATA only
                var val  = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p, 16));
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p)) ?? "";
                var ei   = name.IndexOf("engtype_", StringComparison.Ordinal);
                var eng  = ei >= 0 ? name[(ei + 8)..] : "?";

                if (eng == "3D") total3dPct += val;
                if (name.StartsWith(tag, StringComparison.Ordinal))
                    byEng[eng] = byEng.GetValueOrDefault(eng) + val;
            }

            foreach (var (eng, v) in byEng)
                if (v > gamePct) { gamePct = v; gameEngine = eng; }
            gamePct    = Math.Min(100, gamePct);
            total3dPct = Math.Min(100, total3dPct);
            return true;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose()
    {
        if (query != 0) PdhCloseQuery(query);
        query = 0;
    }

    private const uint PDH_FMT_DOUBLE = 0x200;
    private const int  PDH_MORE_DATA  = unchecked((int)0x800007D2);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQueryW(string? source, nint user, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounterW(nint query, string path, nint user, out nint counter);
    [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint size, out uint count, nint buffer);
    [DllImport("pdh.dll")] private static extern int PdhCloseQuery(nint query);
}

// Dedicated VRAM this process uses against the budget Windows gives it. Past the budget the driver
// starts evicting to system RAM, which is when VRAM pressure turns into stutter.
internal sealed unsafe class VramReader : IDisposable
{
    private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IidAdapter3 = new("645967a4-1392-4310-a798-8053ce3e93fd");

    private readonly List<(nint Adapter, string Name)> adapters = [];

    public VramReader()
    {
        IDXGIFactory1* factory = null;
        var iidF = IidFactory1;
        if (DirectX.CreateDXGIFactory1(&iidF, (void**)&factory).FAILED || factory == null) return;
        try
        {
            for (uint i = 0; ; i++)
            {
                IDXGIAdapter1* a1 = null;
                if (factory->EnumAdapters1(i, &a1).FAILED || a1 == null) break;
                try
                {
                    DXGI_ADAPTER_DESC1 desc;
                    if (a1->GetDesc1(&desc).FAILED) continue;
                    if ((desc.Flags & (uint)DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE) != 0) continue;

                    IDXGIAdapter3* a3 = null;
                    var iid3 = IidAdapter3;
                    if (a1->QueryInterface(&iid3, (void**)&a3).FAILED || a3 == null) continue;
                    adapters.Add(((nint)a3, new string((char*)&desc.Description)));
                }
                finally { a1->Release(); }
            }
        }
        finally { factory->Release(); }
    }

    // The adapter the game renders on is the one with this process's allocations on it
    public bool Query(out long usage, out long budget, out string name)
    {
        usage = -1; budget = 0; name = "";
        foreach (var (ptr, n) in adapters)
        {
            DXGI_QUERY_VIDEO_MEMORY_INFO info;
            if (((IDXGIAdapter3*)ptr)->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP.DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &info).FAILED)
                continue;
            if ((long)info.CurrentUsage > usage)
            {
                usage  = (long)info.CurrentUsage;
                budget = (long)info.Budget;
                name   = n;
            }
        }
        return usage >= 0;
    }

    public void Dispose()
    {
        foreach (var (ptr, _) in adapters) ((IDXGIAdapter3*)ptr)->Release();
        adapters.Clear();
    }
}

// Native DLLs loaded into the game that are not Windows, the game, Dalamud, .NET or a plugin:
// overlays, recorders, ReShade, drivers. They cost frame time without ever showing up as a plugin.
internal sealed class ModuleScanner
{
    private static readonly (string Match, string Label, string Category)[] Known =
    [
        ("reshade",             "ReShade",                     "Post-processing"),
        ("gshade",              "GShade",                      "Post-processing"),
        ("specialk",            "Special K",                   "Post-processing"),
        ("discordhook",         "Discord overlay",             "Overlay"),
        ("gameoverlayrenderer", "Steam overlay",               "Overlay"),
        ("rtsshooks",           "RivaTuner (RTSS)",            "Overlay"),
        ("overwolf",            "Overwolf",                    "Overlay"),
        ("rzoverlay",           "Razer overlay",               "Overlay"),
        ("eosovh",              "Epic overlay",                "Overlay"),
        ("graphics-hook",       "OBS game capture",            "Capture"),
        ("nvspcap",             "NVIDIA overlay / ShadowPlay", "Capture"),
        ("medal",               "Medal.tv",                    "Capture"),
        ("bdcam",               "Bandicam",                    "Capture"),
        ("action_x64",          "Action! recorder",            "Capture"),
        ("fraps",               "FRAPS",                       "Capture"),
        ("nahimic",             "Nahimic audio",               "Audio"),
        ("sonic",               "Sonic Studio audio",          "Audio"),
        ("nvwgf2um",            "NVIDIA driver",               "Driver"),
        ("nvldumd",             "NVIDIA driver",               "Driver"),
        ("nvd3dum",             "NVIDIA driver",               "Driver"),
        ("atidxx",              "AMD driver",                  "Driver"),
        ("amdxx",               "AMD driver",                  "Driver"),
        ("amdihk",              "AMD driver",                  "Driver"),
        ("igd10",               "Intel driver",                "Driver"),
        ("igd11",               "Intel driver",                "Driver"),
        ("igdumd",              "Intel driver",                "Driver"),
    ];

    private readonly Dictionary<string, FileVersionInfo?> versionCache = new(StringComparer.OrdinalIgnoreCase);

    public List<ModuleRow> Scan()
    {
        var rows    = new List<ModuleRow>();
        var winDir  = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var skipDirs = new[]
            {
                System.IO.Path.GetDirectoryName(typeof(IDalamudPluginInterface).Assembly.Location),
                RuntimeEnvironment.GetRuntimeDirectory(),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher"),
            }
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => d!.TrimEnd('\\', '/') + "\\")
            .ToArray();

        // Plugin and Dalamud assemblies are mapped like native DLLs; they belong in the Plugins tab
        var managed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ctx in AssemblyLoadContext.All)
            foreach (var asm in ctx.Assemblies)
            {
                try { if (!asm.IsDynamic && asm.Location.Length > 0) managed.Add(asm.Location); }
                catch { /* some assemblies refuse Location */ }
            }

        using var proc = Process.GetCurrentProcess();
        var mainPath = proc.MainModule?.FileName ?? "";

        foreach (ProcessModule m in proc.Modules)
        {
            using (m)
            {
                var path = m.FileName ?? "";
                if (path.Length == 0 || path.Equals(mainPath, StringComparison.OrdinalIgnoreCase) || managed.Contains(path)) continue;
                if (skipDirs.Any(d => path.StartsWith(d, StringComparison.OrdinalIgnoreCase))) continue;

                if (!versionCache.TryGetValue(path, out var fvi))
                {
                    try { fvi = FileVersionInfo.GetVersionInfo(path); } catch { fvi = null; }
                    versionCache[path] = fvi;
                }

                var file    = System.IO.Path.GetFileName(path);
                var company = fvi?.CompanyName?.Trim() ?? "";
                var product = fvi?.ProductName?.Trim() ?? "";
                var known   = Match(file, product);

                // Windows' own DLLs are noise; anything from Microsoft outside the Windows folder still counts
                if (known == null && company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
                                  && path.StartsWith(winDir, StringComparison.OrdinalIgnoreCase))
                    continue;

                rows.Add(new ModuleRow
                {
                    Label    = known?.Label ?? (product.Length > 0 ? product : file),
                    FileName = file,
                    Category = known?.Category ?? (path.StartsWith(winDir, StringComparison.OrdinalIgnoreCase) ? "System" : "Other"),
                    Path     = path,
                    Company  = company,
                    Product  = product,
                    Size     = m.ModuleMemorySize,
                });
            }
        }
        return rows;
    }

    private static (string Label, string Category)? Match(string file, string product)
    {
        foreach (var (match, label, cat) in Known)
            if (file.Contains(match, StringComparison.OrdinalIgnoreCase) || product.Contains(match, StringComparison.OrdinalIgnoreCase))
                return (label, cat);
        return null;
    }
}
