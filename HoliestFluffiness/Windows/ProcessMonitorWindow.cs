using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using HoliestFluffiness.Handlers;

namespace HoliestFluffiness.Windows;

// /hdebug: what is eating the frame, the CPU and the memory. Sampling only runs while this window is open.
public sealed class ProcessMonitorWindow : Window, IDisposable
{
    private readonly Configuration         config;
    private readonly ProcessMonitorHandler handler;

    private string pluginSearch = "";
    private string sourceSearch = "";
    // View options, loaded from and saved to the config so they survive a restart
    private bool   hideIdlePlugins;
    private bool   hideSelf;
    private bool   hideIdleThreads;

    // Snapshot of the plugin rows while frozen. The handler swaps in a new list on every refresh,
    // so holding the old reference is enough to stop the table moving.
    private List<PluginStatRow>? frozenPlugins;

    private const int PCName = 0, PCFrame = 1, PCTotal = 2, PCDraw = 3, PCUpdate = 4, PCPeak = 5, PCHooks = 6, PCTex = 7, PCTrend = 8;
    private const int TCTid = 0, TCName = 1, TCStart = 2, TCCpu = 3, TCTime = 4;

    private readonly List<PluginStatRow> pluginBuf = [];
    private readonly List<ThreadStatRow> threadBuf = [];

    internal ProcessMonitorWindow(Configuration config, ProcessMonitorHandler handler) : base("hdebug##HFProcessMonitor")
    {
        this.config   = config;
        this.handler  = handler;
        hideIdlePlugins          = config.HdebugHideIdlePlugins;
        hideSelf                 = config.HdebugHideSelf;
        hideIdleThreads          = config.HdebugHideIdleThreads;
        handler.SpikeThresholdMs = Math.Clamp(config.HdebugSpikeThresholdMs, 10f, 200f);
        handler.IgnoreLoading    = config.HdebugIgnoreLoading;
        Size          = new Vector2(720, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void OnOpen()  => handler.Start();
    public override void OnClose()
    {
        handler.Stop();
        frozenPlugins = null;
    }

    public override void PreDraw()
    {
        Common.PushWindowTheme();
        // Tabs follow the gold accent; the count stays fixed whatever the theme says
        ImGui.PushStyleColor(ImGuiCol.Tab,         Pick(Theme.ColPrimary, ImGuiCol.Tab));
        ImGui.PushStyleColor(ImGuiCol.TabHovered,  Pick(Theme.ColGoldMid, ImGuiCol.TabHovered));
        ImGui.PushStyleColor(ImGuiCol.TabActive,   Pick(Theme.ColGoldSub, ImGuiCol.TabActive));
        ImGui.PushStyleColor(ImGuiCol.TableRowBgAlt, Pick(new Vector4(1, 1, 1, 0.025f), ImGuiCol.TableRowBgAlt));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(4);
        Common.PopWindowTheme();
    }

    private static Vector4 Pick(Vector4 custom, ImGuiCol slot) =>
        Theme.UseCustom ? custom : ImGui.GetStyle().Colors[(int)slot];

    // Scrolling tables fill the space left, but never shrink below a header plus 5 rows; on a short
    // window the window itself scrolls to reach them instead of the table collapsing to nothing.
    private const int MinTableRows = 5;
    private static Vector2 ScrollTableSize()
    {
        var rowH = ImGui.GetTextLineHeight() + ImGui.GetStyle().CellPadding.Y * 2;
        var minH = rowH * (MinTableRows + 1) + ImGui.GetStyle().ScrollbarSize * 0.5f;
        return new Vector2(0, Math.Max(ImGui.GetContentRegionAvail().Y, minH));
    }

    public override void Draw()
    {
        // Plugin stats refresh from the handler's own draw hook, so they keep up even while collapsed
        DrawMeters();
        ImGui.Spacing();

        if (!ImGui.BeginTabBar("##hdbgTabs")) return;
        if (ImGui.BeginTabItem("Plugins"))  { DrawPlugins();  ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Sources"))  { DrawSources();  ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Spikes"))   { DrawSpikes();   ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Threads"))  { DrawThreads();  ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Injected")) { DrawInjected(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("I/O"))      { DrawIo();       ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Memory"))   { DrawMemory();   ImGui.EndTabItem(); }
        ImGui.EndTabBar();
    }

    // ---------------------------------------------------------------- header meters

    private void DrawMeters()
    {
        var m       = handler.Memory;
        var fps     = ImGui.GetIO().Framerate;
        var frameMs = fps > 0 ? 1000.0 / fps : 0;
        var plugMs  = handler.Plugins.Sum(p => p.TotalAvg);

        // The header is a fixed set of rows from the first frame on: placeholders before the first
        // sample, "unavailable" when a source is missing, so nothing below it ever jumps.
        var verdict = m != null ? Verdict(m) : default;
        string? Tag(string kind) => verdict.Kind == kind ? "bottleneck" : null;
        string? Tip(string kind) => verdict.Kind == kind ? verdict.Text : null;

        if (m == null)
        {
            Meter("CPU",  0, "sampling...");
            Meter("Mem",  0, "sampling...");
            Meter("GPU",  0, "sampling...");
            Meter("VRAM", 0, "sampling...");
        }
        else
        {
            var cpuCores = m.ProcCpuPct / 100.0 * m.Cores;
            Meter("CPU", m.ProcCpuPct / 100.0,
                  $"{m.ProcCpuPct:F1}%  ({cpuCores:F1} of {m.Cores} cores)   main thread {m.MainThreadPct:F0}%",
                  tag: Tag("CPU"), tip: Tip("CPU"));

            var used = m.SysTotal - m.SysAvail;
            Meter("Mem", m.SysTotal > 0 ? (double)used / m.SysTotal : 0,
                  $"game {Bytes(m.PrivateBytes)}   system {Bytes(used)} / {Bytes(m.SysTotal)}",
                  m.SysTotal > 0 ? (double)m.PrivateBytes / m.SysTotal : 0);

            if (!double.IsNaN(m.GpuTotalPct))
            {
                var eng = m.GpuEngine is "" or "3D" ? "" : $" ({m.GpuEngine})";
                Meter("GPU", Math.Max(m.GpuTotalPct, m.GpuPct) / 100.0,
                      $"game {m.GpuPct:F0}%{eng}   all apps {m.GpuTotalPct:F0}%",
                      m.GpuPct / 100.0, Tag("GPU"), Tip("GPU"));
            }
            else Meter("GPU", 0, "unavailable (no GPU performance counters)");

            if (m.VramUsage >= 0 && m.VramBudget > 0)
                Meter("VRAM", (double)m.VramUsage / m.VramBudget,
                      $"game {Bytes(m.VramUsage)} / {Bytes(m.VramBudget)} budget   {m.GpuName}",
                      tag: Tag("VRAM"), tip: Tip("VRAM"));
            else Meter("VRAM", 0, "unavailable");
        }

        var plugFrac = frameMs > 0 ? plugMs / frameMs : 0;
        Meter("Frm", plugFrac, $"plugins {plugMs:F2} ms of {frameMs:F1} ms   {FpsText()}");

        InfoRow("Disk", m != null
            ? $"game read {Bytes((long)m.IoReadPerSec)}/s  write {Bytes((long)m.IoWritePerSec)}/s"
            : "sampling...");

        var net = handler.Network;
        var netText = net == null
            ? "sampling..."
            : $"system down {Bytes((long)net.SysDown)}/s  up {Bytes((long)net.SysUp)}/s" +
              (double.IsNaN(net.GameDown) ? "" : $"   game down {Bytes((long)net.GameDown)}/s  up {Bytes((long)net.GameUp)}/s");
        InfoRow("Net", netText);

        // Copy report, pinned to the right end of the Net row, in the accent colour so it is easy to find
        const string copyLabel = "Copy report";
        var pad  = new Vector2(12f, 3f) * ImGuiHelpers.GlobalScale;
        var btnW = ImGui.CalcTextSize(copyLabel).X + pad.X * 2;
        ImGui.SameLine();
        var off = ImGui.GetContentRegionAvail().X - btnW - 4f * ImGuiHelpers.GlobalScale;
        if (off > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + off);
        // The button is 2 * pad.Y taller than the text row; lifting it by pad.Y centres it on that row
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - pad.Y);
        Common.PushGoldButton();
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, pad);
        if (ImGui.Button(copyLabel))
        {
            ImGui.SetClipboardText(BuildReport());
            Common.ShowToast("hdebug", "Report copied to clipboard", 3f);
        }
        ImGui.PopStyleVar();
        Common.PopGoldButton();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copies a text summary (FPS, plugins, spikes, CPU/GPU/VRAM, memory) ready to paste into Discord.");
    }

    // Same shape before and after the first lows are ready, so the bar text does not reflow
    private string FpsText()
    {
        var s = handler.FpsStats;
        if (s.Frames < 10) return $"{ImGui.GetIO().Framerate:F0} fps   1% low -   0.1% low -";
        return $"{s.Avg:F0} fps   1% low {s.Low1:F0}   0.1% low {s.Low01:F0}";
    }

    // The worst current bottleneck, worst first. Shown as a tag on its bar (full text on hover)
    // rather than as its own line, so it never changes the header's height.
    private static (string? Kind, string? Text) Verdict(MemorySnapshot m)
    {
        if (m.VramUsage > 0 && m.VramBudget > 0 && m.VramUsage >= m.VramBudget * 0.95)
            return ("VRAM", $"VRAM full: the game uses {Bytes(m.VramUsage)} of its {Bytes(m.VramBudget)} budget, so textures spill into system RAM " +
                            "and cause stutter. Lower texture quality, ReShade effects or heavy Penumbra mods.");
        if (!double.IsNaN(m.GpuTotalPct) && m.GpuTotalPct >= 90)
        {
            var other = m.GpuTotalPct - m.GpuPct >= 20 ? $" Other apps use {m.GpuTotalPct - m.GpuPct:F0}% of it." : "";
            return ("GPU", $"GPU bound: the GPU is at {m.GpuTotalPct:F0}%. Resolution, graphics settings and ReShade set your FPS, not plugins.{other}");
        }
        if (m.MainThreadPct >= 85)
            return ("CPU", $"CPU bound: the main thread is at {m.MainThreadPct:F0}% of a core. Game logic and plugins set your FPS; see the Plugins tab.");
        return default;
    }

    // ---------------------------------------------------------------- report

    // Plain text in a code block so the columns survive a Discord paste
    private string BuildReport()
    {
        var sb  = new System.Text.StringBuilder();
        var m   = handler.Memory;
        var fps = handler.FpsStats;

        sb.AppendLine("```");
        sb.AppendLine($"hdebug report  {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        if (fps.Frames >= 10)
            sb.AppendLine($"FPS      avg {fps.Avg:F0}, 1% low {fps.Low1:F0}, 0.1% low {fps.Low01:F0}  (last {fps.Frames} frames)");
        var plugMs = handler.Plugins.Sum(p => p.TotalAvg);
        sb.AppendLine($"Plugins  {plugMs:F2} ms per frame in total");

        if (m != null)
        {
            sb.AppendLine($"CPU      {m.ProcCpuPct:F1}% of {m.Cores} cores, main thread {m.MainThreadPct:F0}% of a core");
            if (!double.IsNaN(m.GpuTotalPct))
                sb.AppendLine($"GPU      game {m.GpuPct:F0}%, all apps {m.GpuTotalPct:F0}%{(m.GpuName.Length > 0 ? $"  ({m.GpuName})" : "")}");
            if (m.VramUsage >= 0 && m.VramBudget > 0)
                sb.AppendLine($"VRAM     {Bytes(m.VramUsage)} of {Bytes(m.VramBudget)} budget");
            sb.AppendLine($"Memory   game {Bytes(m.PrivateBytes)}, .NET heap {Bytes(m.GcHeap)}, system {Bytes(m.SysTotal - m.SysAvail)} / {Bytes(m.SysTotal)}");
            sb.AppendLine($"GC       {Bytes((long)m.AllocPerSec)}/s allocated, gen2 {m.Gen2PerSec:F2}/s, pause {m.GcPausePct:F2}%");
            sb.AppendLine($"Disk     read {Bytes((long)m.IoReadPerSec)}/s, write {Bytes((long)m.IoWritePerSec)}/s");
            if (Verdict(m).Text is { } v) { sb.AppendLine(); sb.AppendLine(v); }
        }

        if (handler.ReflectionError != null)
        {
            sb.AppendLine();
            sb.AppendLine($"Plugin stats unavailable: {handler.ReflectionError}");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("Top plugins (avg ms per frame)");
            sb.AppendLine("  total   draw    update  peak    hooks  textures         plugin");
            foreach (var p in handler.Plugins.OrderByDescending(p => p.TotalAvg).Take(12))
            {
                var tex = p.TexCount > 0 ? Bytes(p.TexBytes) : "-";
                if (p.TexGrowth > 0) tex += $" (+{Bytes(p.TexGrowth)})";
                var name = p.IsSelf ? $"{p.Name} (runs hdebug)" : p.Name;
                sb.AppendLine($"  {p.TotalAvg,6:F3}  {Nz(p.DrawAvg),6:F3}  {Nz(p.UpdAvg),6:F3}  {p.Peak,6:F2}  {p.Hooks,5}  {tex,-15}  {name}");
            }
        }

        if (handler.Spikes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Recent spikes over {handler.SpikeThresholdMs:F0} ms (newest first)");
            foreach (var sp in handler.Spikes.Take(10))
            {
                var top   = sp.Plugins.Count > 0 ? sp.Plugins[0] : default;
                var topMs = top.Name != null ? top.Draw + top.Upd : 0;
                var cause = sp.OtherMs > topMs ? "game, GPU or Dalamud" : $"{top.Name} {topMs:F1} ms ({(top.Draw >= top.Upd ? "draw" : "update")})";
                sb.AppendLine($"  {sp.Time:HH:mm:ss}  {sp.FrameMs,6:F1} ms  plugins {sp.PluginsMs,5:F1}  other {sp.OtherMs,5:F1}  {cause}");
            }
        }

        // Injected DLLs and network connections stay out on purpose: they describe the person's PC
        // (installed software, IPs) and this is meant to be pasted somewhere public.
        sb.AppendLine("```");
        return sb.ToString();
    }

    // htop-ish bar: label, filled track, text drawn on top. `inner` draws a brighter segment (the game's share).
    // Text is left-anchored so changing digits only move its tail; `tag` sits pinned at the right end and
    // `tip` explains it on hover. Every bar is the same fixed height whatever it shows.
    private static void Meter(string label, double frac, string text, double inner = -1, string? tag = null, string? tip = null)
    {
        frac = Math.Clamp(frac, 0, 1);
        Common.GoldText(label);
        ImGui.SameLine(MeterLabelW * ImGuiHelpers.GlobalScale);

        var dl    = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var h     = ImGui.GetTextLineHeight() + 2;
        var w     = ImGui.GetContentRegionAvail().X;
        var end   = start + new Vector2(w, h);

        var fill = frac >= 0.85 ? Theme.ColRed : frac >= 0.6 ? Theme.ColGold : Theme.ColGreen;
        dl.AddRectFilled(start, end, ImGui.GetColorU32(Theme.ColWhite with { W = 0.06f }), 3f);
        dl.AddRectFilled(start, start + new Vector2((float)(w * frac), h), ImGui.GetColorU32(fill with { W = 0.35f }), 3f);
        if (inner >= 0)
            dl.AddRectFilled(start, start + new Vector2((float)(w * Math.Clamp(inner, 0, frac)), h), ImGui.GetColorU32(fill with { W = 0.7f }), 3f);

        // Clip so a long line on a narrow window cannot spill past the bar
        dl.PushClipRect(start, end, true);
        dl.AddText(new Vector2(start.X + 6, start.Y + 1), ImGui.GetColorU32(Theme.ColWhite), text);
        if (tag != null)
        {
            var ts = ImGui.CalcTextSize(tag);
            dl.AddText(new Vector2(end.X - ts.X - 6, start.Y + 1), ImGui.GetColorU32(Theme.ColWhite), tag);
        }
        dl.PopClipRect();

        ImGui.Dummy(new Vector2(w, h));
        if (tip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
    }

    private const float MeterLabelW = 48f;

    // Same layout as a meter row, for text-only rows (Disk, Net) so they line up with the bars
    private static void InfoRow(string label, string text)
    {
        Common.GoldText(label);
        ImGui.SameLine(MeterLabelW * ImGuiHelpers.GlobalScale);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 6);
        Common.DimmedText(text);
    }

    // ---------------------------------------------------------------- plugins

    private void DrawPlugins()
    {
        if (handler.ReflectionError != null)
        {
            Common.RedText("Plugin stats are unavailable on this Dalamud version.");
            Common.DimmedTextWrapped(handler.ReflectionError);
            Common.DimmedTextWrapped("Threads and Memory still work. /xlstats has the same data in the meantime.");
            return;
        }

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        Common.PushSearchInput();
        ImGui.InputTextWithHint("##hdbgSearch", "Search", ref pluginSearch, 100);
        Common.PopSearchInput();
        ImGui.SameLine();
        Common.PushGoldCheckbox();
        if (ImGui.Checkbox("Hide idle", ref hideIdlePlugins)) { config.HdebugHideIdlePlugins = hideIdlePlugins; config.Save(); }
        ImGui.SameLine();
        if (ImGui.Checkbox("Hide self", ref hideSelf)) { config.HdebugHideSelf = hideSelf; config.Save(); }
        Common.PopGoldCheckbox();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Hides this plugin, whose draw time includes this window itself.");
        ImGui.SameLine();
        Common.PushGreyButton();
        if (ImGui.Button("Reset peaks")) handler.ResetStats();
        Common.PopGreyButton();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clears Dalamud's draw and update history, so peaks from loading screens or plugin startup drop out.");

        ImGui.SameLine();
        var frozen = frozenPlugins != null;
        if (frozen) Common.PushBlueButton(); else Common.PushGreyButton();
        if (ImGui.Button(frozen ? "Unfreeze##hdbgFreeze" : "Freeze##hdbgFreeze"))
            frozenPlugins = frozen ? null : handler.Plugins;
        if (frozen) Common.PopBlueButton(); else Common.PopGreyButton();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(frozen ? "Table is frozen. Click to resume live numbers."
                                    : "Holds the table still so the numbers stop jumping while you read them.");

        var fps     = ImGui.GetIO().Framerate;
        var frameMs = fps > 0 ? 1000.0 / fps : 0;
        var plugins = frozenPlugins ?? handler.Plugins;

        pluginBuf.Clear();
        foreach (var p in plugins)
        {
            if (pluginSearch.Length > 0 && !p.Name.Contains(pluginSearch, StringComparison.OrdinalIgnoreCase)) continue;
            if (hideIdlePlugins && p.TotalAvg < 0.01) continue;
            if (hideSelf && p.IsSelf) continue;
            pluginBuf.Add(p);
        }

        var flags = ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable | ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.Hideable | ImGuiTableFlags.Reorderable;
        if (!ImGui.BeginTable("##hdbgPlugins", 9, flags, ScrollTableSize())) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Plugin",   ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoHide, 3f);
        ImGui.TableSetupColumn("Frame%",   ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.PreferSortDescending, 0.8f);
        ImGui.TableSetupColumn("Total ms", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending, 0.9f);
        ImGui.TableSetupColumn("Draw",     ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.PreferSortDescending, 0.8f);
        ImGui.TableSetupColumn("Update",   ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.PreferSortDescending, 0.8f);
        ImGui.TableSetupColumn("Peak",     ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.PreferSortDescending, 0.8f);
        ImGui.TableSetupColumn("Hooks",    ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.PreferSortDescending, 0.6f);
        ImGui.TableSetupColumn("Textures", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.PreferSortDescending, 1f);
        ImGui.TableSetupColumn("Trend",    ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.PreferSortDescending, 1.1f);

        Common.PushTableHeader();
        // Manual header row so each header can carry a tooltip with that column's totals
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        PluginTotals? totals = null;
        for (var i = 0; i <= PCTrend; i++)
        {
            if (!ImGui.TableSetColumnIndex(i)) continue;
            ImGui.TableHeader(ImGui.TableGetColumnName(i));
            if (ImGui.IsItemHovered())
            {
                totals ??= ComputeTotals(plugins, frameMs);
                // Pop the header text colour for the tooltip so its body is not all gold
                Common.PopTableHeader();
                HeaderTooltip(i, totals.Value);
                Common.PushTableHeader();
            }
        }
        Common.PopTableHeader();

        var specs = ImGui.TableGetSortSpecs();
        int col = PCTotal; var asc = false;
        if (!specs.IsNull && specs.SpecsCount > 0)
        {
            col = specs.Specs.ColumnIndex;
            asc = specs.Specs.SortDirection == ImGuiSortDirection.Ascending;
        }
        pluginBuf.Sort((a, b) =>
        {
            var c = col switch
            {
                PCName   => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
                PCDraw   => Nz(a.DrawAvg).CompareTo(Nz(b.DrawAvg)),
                PCUpdate => Nz(a.UpdAvg).CompareTo(Nz(b.UpdAvg)),
                PCPeak   => a.Peak.CompareTo(b.Peak),
                PCHooks  => a.Hooks.CompareTo(b.Hooks),
                PCTex    => a.TexBytes.CompareTo(b.TexBytes),
                PCTrend  => a.TexGrowth.CompareTo(b.TexGrowth),
                _        => a.TotalAvg.CompareTo(b.TotalAvg),
            };
            return asc ? c : -c;
        });

        foreach (var p in pluginBuf)
        {
            ImGui.TableNextRow();
            var hovered = false;

            if (ImGui.TableSetColumnIndex(PCName))
            {
                var nameCol = p.IsSelf ? Theme.ColGold : p.IsDalamud ? Theme.ColWhiteDim : Theme.ColWhite;
                ImGui.PushStyleColor(ImGuiCol.Text, nameCol);
                ImGui.TextUnformatted(p.IsDev ? $"{p.Name} [dev]" : p.Name);
                ImGui.PopStyleColor();
                hovered |= ImGui.IsItemHovered();
                if (p.LeakedStacks > 0)
                {
                    ImGui.SameLine();
                    Common.RedText("!");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Leaked {p.LeakedStacks} ImGui style/colour/font pushes.");
                }
            }

            if (ImGui.TableSetColumnIndex(PCFrame))
            {
                var pct = frameMs > 0 ? p.TotalAvg / frameMs * 100 : 0;
                MsColored($"{pct:F1}%", pct, 5, 15);
            }
            if (ImGui.TableSetColumnIndex(PCTotal))  MsColored(p.TotalAvg.ToString("F3"), p.TotalAvg, 0.5, 2);
            if (ImGui.TableSetColumnIndex(PCDraw))   MsCell(p.DrawAvg);
            if (ImGui.TableSetColumnIndex(PCUpdate)) MsCell(p.UpdAvg);
            if (ImGui.TableSetColumnIndex(PCPeak))   MsColored(p.Peak > 0 ? p.Peak.ToString("F2") : "-", p.Peak, 5, 16.6);
            if (ImGui.TableSetColumnIndex(PCHooks))
            {
                if (p.Hooks > 0) ImGui.TextUnformatted(p.Hooks.ToString()); else Common.DimmedText("-");
            }
            if (ImGui.TableSetColumnIndex(PCTex))
            {
                if (p.TexCount > 0) ImGui.TextUnformatted($"{Bytes(p.TexBytes)} ({p.TexCount})"); else Common.DimmedText("-");
            }
            if (ImGui.TableSetColumnIndex(PCTrend)) DrawTrend(p);

            if (hovered) PluginTooltip(p);
        }

        ImGui.EndTable();
    }

    // Totals across every loaded plugin (not just the filtered rows), for the column header tooltips
    private readonly record struct PluginTotals(int Loaded, int Installed, int Dev, double Frame, double Draw, double Update,
                                                string? PeakName, double Peak, int Hooks, int TexCount, long TexBytes, long TexGrowth);

    private PluginTotals ComputeTotals(List<PluginStatRow> plugins, double frameMs)
    {
        int loaded = 0, dev = 0, hooks = 0, texCount = 0;
        double total = 0, draw = 0, upd = 0, peak = 0;
        long texBytes = 0, growth = 0;
        string? peakName = null;
        foreach (var p in plugins)
        {
            if (p.IsDalamud) continue;
            loaded++;
            if (p.IsDev) dev++;
            total += p.TotalAvg;
            draw  += Nz(p.DrawAvg);
            upd   += Nz(p.UpdAvg);
            hooks += p.Hooks;
            texCount += p.TexCount;
            texBytes += p.TexBytes;
            growth   += p.TexGrowth;
            if (p.Peak > peak) { peak = p.Peak; peakName = p.Name; }
        }
        var framePct = frameMs > 0 ? total / frameMs * 100 : 0;
        return new PluginTotals(loaded, Math.Max(handler.InstalledCount, loaded), dev, framePct, draw, upd,
                                peakName, peak, hooks, texCount, texBytes, growth);
    }

    // Header tooltip: the totals for that column in the accent colour, then what the column means
    private static void HeaderTooltip(int column, in PluginTotals t)
    {
        var (summary, desc) = column switch
        {
            PCName   => ($"{t.Loaded} of {t.Installed} plugins loaded" + (t.Dev > 0 ? $", {t.Dev} dev" : ""),
                         "Names in gold are this plugin, which also draws this window."),
            PCFrame  => ($"All plugins: {t.Frame:F1}% of the frame",
                         "Share of the current frame time each plugin spends drawing and updating, averaged."),
            PCTotal  => ($"All plugins: {t.Draw + t.Update:F2} ms per frame",
                         "Average ms per frame: draw + all Framework.Update handlers."),
            PCDraw   => ($"All plugins: {t.Draw:F2} ms",
                         "Average ms per frame spent in the plugin's UI draw (last 100 frames)."),
            PCUpdate => ($"All plugins: {t.Update:F2} ms",
                         "Average ms per frame across the plugin's Framework.Update handlers."),
            PCPeak   => (t.PeakName != null ? $"Worst: {t.PeakName}, {t.Peak:F2} ms" : "No peaks yet",
                         "Worst single draw or update since the window opened or the last reset."),
            PCHooks  => ($"{t.Hooks} hooks across all plugins",
                         "Active function hooks the plugin has placed into the game."),
            PCTex    => ($"{Bytes(t.TexBytes)} in {t.TexCount} textures",
                         "Textures the plugin created and their estimated size. A shared texture counts for each owner."),
            PCTrend  => ($"All plugins: {(t.TexGrowth >= 0 ? "+" : "-")}{Bytes(Math.Abs(t.TexGrowth))} since the window opened",
                         "Texture memory while this window has been open, one point every 5 s. A line that only climbs is a leak."),
            _        => ("", ""),
        };
        if (summary.Length == 0) return;

        ImGui.BeginTooltip();
        Common.GoldText(summary);
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 24f);
        Common.DimmedTextWrapped(desc);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    // Sparkline of texture memory with the growth written over it
    private static void DrawTrend(PluginStatRow p)
    {
        var data = p.TexTrend;
        if (data.Length < 2 || (data.Max() <= 0 && p.HookGrowth == 0))
        {
            Common.DimmedText(data.Length < 2 ? "collecting..." : "-");
            return;
        }

        var growth = p.TexGrowth == 0 ? "flat" : (p.TexGrowth > 0 ? "+" : "-") + Bytes(Math.Abs(p.TexGrowth));
        if (p.HookGrowth != 0) growth += $"  {p.HookGrowth:+#;-#} hooks";

        float lo = data.Min(), hi = data.Max();
        var pad = Math.Max(0.5f, (hi - lo) * 0.2f);
        ImGui.PushStyleColor(ImGuiCol.PlotLines, Theme.ColPlot);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PlotLines($"##trend{p.InternalName}", data, data.Length, growth, Math.Max(0, lo - pad), hi + pad,
                        new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetTextLineHeight()));
        ImGui.PopStyleColor(2);
    }

    private static void PluginTooltip(PluginStatRow p)
    {
        ImGui.BeginTooltip();
        Common.GoldText(p.Name);
        if (p.IsSelf)
            Common.DimmedText("This is the plugin drawing this window, so its draw time includes hdebug itself.\n" +
                              "It looks busier here than it is with the window closed.");
        if (!double.IsNaN(p.DrawAvg))
            ImGui.TextUnformatted($"Draw     last {p.DrawLast:F3}  avg {p.DrawAvg:F3}  max {p.DrawMax:F2} ms");
        if (!double.IsNaN(p.UpdAvg))
            ImGui.TextUnformatted($"Update   last {p.UpdLast:F3}  avg {p.UpdAvg:F3}  max {p.UpdMax:F2} ms");
        if (p.Handlers.Count > 0)
        {
            ImGui.Spacing();
            Common.DimmedText("Update handlers (avg / max ms)");
            foreach (var (name, avg, max) in p.Handlers.Take(12))
                ImGui.TextUnformatted($"  {avg,7:F3} / {max,6:F2}  {ShortHandler(name)}");
            if (p.Handlers.Count > 12) Common.DimmedText($"  ...and {p.Handlers.Count - 12} more");
        }
        ImGui.EndTooltip();
    }

    // "Some.Long.Namespace.Type+<>c::<Ctor>b__12_0" -> "Type+<>c::<Ctor>b__12_0"
    private static string ShortHandler(string key)
    {
        var sep    = key.LastIndexOf("::", StringComparison.Ordinal);
        var type   = sep > 0 ? key[..sep] : key;
        var plus   = type.IndexOf('+');
        var nsPart = plus >= 0 ? type[..plus] : type;
        var dot    = nsPart.LastIndexOf('.');
        return dot >= 0 ? key[(dot + 1)..] : key;
    }

    private static double Nz(double v) => double.IsNaN(v) ? 0 : v;

    private static void MsCell(double ms)
    {
        if (double.IsNaN(ms)) { Common.DimmedText("-"); return; }
        MsColored(ms.ToString("F3"), ms, 0.5, 2);
    }

    private static void MsColored(string text, double v, double warn, double bad)
    {
        if (v >= bad)        Common.RedText(text);
        else if (v >= warn)  Common.GoldText(text);
        else if (v < 0.005)  Common.DimmedText(text);
        else                 ImGui.TextUnformatted(text);
    }

    // ---------------------------------------------------------------- sources

    private const int SCName = 0, SCAuthor = 1, SCVersion = 2, SCStatus = 3, SCRepo = 4;

    // Every installed plugin split by where it came from: the official repo, third-party repos and
    // local dev plugins, A-Z with enabled before disabled on ties. Plugins installed from more than one
    // repo get one entry above (the loaded copy) and all their copies in a collapsed Duplicates section.
    // Custom repos that no installed plugin came from are listed last, as clean-up candidates.
    private void DrawSources()
    {
        handler.RefreshSources();
        var all = handler.Sources;
        if (all.Count == 0) { Common.DimmedText("Reading installed plugins..."); return; }

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        Common.PushSearchInput();
        ImGui.InputTextWithHint("##hdbgSrcSearch", "Search name, author or repo", ref sourceSearch, 100);
        Common.PopSearchInput();

        // One entry per InternalName in the main sections, preferring the loaded copy
        var groups     = all.GroupBy(s => s.InternalName).ToList();
        var primary    = groups.Select(g => g.FirstOrDefault(s => s.IsLoaded) ?? g.First()).ToList();
        var duplicates = groups.Where(g => g.Count() > 1).SelectMany(g => g).ToList();

        var official   = primary.Where(s => !s.IsDev && !s.IsThirdParty).ToList();
        var thirdParty = primary.Where(s => !s.IsDev && s.IsThirdParty).ToList();
        var dev        = primary.Where(s => s.IsDev).ToList();

        // Repos in Dalamud's list that no installed copy (loaded or not) was installed from
        var usedUrls = new HashSet<string>(all.Where(s => s.IsThirdParty).Select(s => NormUrl(s.Url)));
        var unused   = handler.Repos?.Where(r => !usedUrls.Contains(NormUrl(r.Url)))
                                     .OrderBy(r => r.Url, StringComparer.OrdinalIgnoreCase).ToList();

        var loaded = primary.Count(s => s.IsLoaded);
        ImGui.SameLine();
        Common.DimmedText($"{primary.Count} installed, {loaded} enabled, {thirdParty.Count} third-party");

        if (!ImGui.BeginChild("##hdbgSrcScroll", Vector2.Zero)) { ImGui.EndChild(); return; }

        ImGui.PushStyleColor(ImGuiCol.Header,        Pick(Theme.ColPrimary, ImGuiCol.Header));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Pick(Theme.ColGoldSub, ImGuiCol.HeaderHovered));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive,  Pick(Theme.ColGoldMid, ImGuiCol.HeaderActive));

        var open = config.HdebugSrcOfficialOpen;
        SourceSection("Official", official, false, ref open,
                      "Installed from Dalamud's main repo. These go through Dalamud's review.");
        if (open != config.HdebugSrcOfficialOpen) { config.HdebugSrcOfficialOpen = open; config.Save(); }

        open = config.HdebugSrcThirdPartyOpen;
        SourceSection("Third-party", thirdParty, true, ref open,
                      "Installed from custom repos added in Dalamud's settings. Not reviewed by Dalamud.");
        if (open != config.HdebugSrcThirdPartyOpen) { config.HdebugSrcThirdPartyOpen = open; config.Save(); }

        if (dev.Count > 0)
        {
            open = config.HdebugSrcDevOpen;
            SourceSection("Dev", dev, false, ref open, "Loaded from a local folder (dev plugin locations).");
            if (open != config.HdebugSrcDevOpen) { config.HdebugSrcDevOpen = open; config.Save(); }
        }

        if (duplicates.Count > 0)
        {
            open = config.HdebugSrcDuplicatesOpen;
            SourceSection("Duplicates", duplicates, true, ref open,
                          "Plugins installed from more than one source. Only one copy can be loaded; the rest just take up space.",
                          duplicates.Select(d => d.InternalName).Distinct().Count());
            if (open != config.HdebugSrcDuplicatesOpen) { config.HdebugSrcDuplicatesOpen = open; config.Save(); }
        }

        if (handler.Listings is { } listings)
        {
            open = config.HdebugSrcOverlapOpen;
            OverlapSection(listings, all, ref open);
            if (open != config.HdebugSrcOverlapOpen) { config.HdebugSrcOverlapOpen = open; config.Save(); }
        }

        if (unused is { Count: > 0 })
        {
            open = config.HdebugSrcUnusedReposOpen;
            UnusedRepoSection(unused, ref open);
            if (open != config.HdebugSrcUnusedReposOpen) { config.HdebugSrcUnusedReposOpen = open; config.Save(); }
        }

        ImGui.PopStyleColor(3);
        ImGui.EndChild();
    }

    // Compare repo urls loosely: case, surrounding spaces and a trailing slash do not matter
    private static string NormUrl(string url) => url.Trim().TrimEnd('/').ToLowerInvariant();

    private static ImGuiTableFlags SourceTableFlags =>
        ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp |
        ImGuiTableFlags.Sortable | ImGuiTableFlags.Resizable;

    // A collapsible header plus its table. `open` is the remembered state going in and the new state coming out.
    private void SourceSection(string title, List<PluginSourceRow> rows, bool showRepo, ref bool open, string tip, int countOverride = -1)
    {
        var shown = rows.Where(MatchesSourceSearch).ToList();
        var count = countOverride >= 0 ? countOverride : rows.Count;

        ImGui.SetNextItemOpen(open, ImGuiCond.Always);
        open = ImGui.CollapsingHeader($"{title} ({count})##src{title}");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
        if (!open) return;

        if (shown.Count == 0)
        {
            Common.DimmedText(rows.Count == 0 ? "  None" : "  No matches");
            ImGui.Spacing();
            return;
        }

        if (!ImGui.BeginTable($"##srcTbl{title}", showRepo ? 5 : 4, SourceTableFlags)) return;

        // Repo gets the room; without it the name column takes the slack instead
        ImGui.TableSetupColumn("Plugin",  ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort, showRepo ? 1.3f : 2.4f);
        ImGui.TableSetupColumn("Author",  ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Version", ImGuiTableColumnFlags.WidthStretch, 0.55f);
        ImGui.TableSetupColumn("Status",  ImGuiTableColumnFlags.WidthStretch, 0.55f);
        if (showRepo) ImGui.TableSetupColumn("Repo", ImGuiTableColumnFlags.WidthStretch, 3.2f);
        Common.PushTableHeader();
        ImGui.TableHeadersRow();
        Common.PopTableHeader();

        var specs = ImGui.TableGetSortSpecs();
        int col = SCName; var asc = true;
        if (!specs.IsNull && specs.SpecsCount > 0)
        {
            col = specs.Specs.ColumnIndex;
            asc = specs.Specs.SortDirection == ImGuiSortDirection.Ascending;
        }
        shown.Sort((a, b) =>
        {
            var c = col switch
            {
                SCAuthor  => string.Compare(a.Author, b.Author, StringComparison.OrdinalIgnoreCase),
                SCVersion => string.Compare(a.Version, b.Version, StringComparison.OrdinalIgnoreCase),
                SCStatus  => b.IsLoaded.CompareTo(a.IsLoaded),
                SCRepo    => string.Compare(RepoText(a), RepoText(b), StringComparison.OrdinalIgnoreCase),
                _         => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            };
            if (!asc) c = -c;
            // Secondary: enabled before disabled, then A-Z
            if (c == 0) c = b.IsLoaded.CompareTo(a.IsLoaded);
            if (c == 0) c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return c;
        });

        foreach (var s in shown)
        {
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(SCName);
            var nameCol = !s.IsLoaded ? Theme.ColWhiteDim : s.IsSelf ? Theme.ColGold : Theme.ColWhite;
            ImGui.PushStyleColor(ImGuiCol.Text, nameCol);
            ImGui.TextUnformatted(s.Name);
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered() && s.Name != s.InternalName) ImGui.SetTooltip(s.InternalName);
            SourceTags(s);

            ImGui.TableSetColumnIndex(SCAuthor);
            Common.DimmedText(s.Author.Length > 0 ? s.Author : "-");

            ImGui.TableSetColumnIndex(SCVersion);
            Common.DimmedText(s.Version.Length > 0 ? s.Version : "-");

            ImGui.TableSetColumnIndex(SCStatus);
            if (s.IsLoaded) ImGui.TextUnformatted("Enabled"); else Common.DimmedText("Disabled");

            if (showRepo && ImGui.TableSetColumnIndex(SCRepo))
            {
                Common.DimmedText(RepoText(s));
                // The column can still be narrower than a long url; the tooltip always has all of it
                if (ImGui.IsItemHovered() && s.IsThirdParty) ImGui.SetTooltip(s.Url);
            }
        }
        ImGui.EndTable();
        ImGui.Spacing();
    }

    private readonly record struct OverlapRow(string Name, string InternalName, string Version, string Url, bool IsThirdParty,
                                              bool InstalledHere, bool LoadedHere, bool InstalledAnywhere);

    // Plugins that more than one enabled repo offers, one row per (plugin, repo). Shows which copy is
    // installed, and calls out repos whose whole catalogue is available elsewhere, since those can go.
    private void OverlapSection(List<RepoListing> listings, List<PluginSourceRow> installed, ref bool open)
    {
        // InternalName -> every enabled repo offering it (a repo listing a plugin twice counts once)
        var offers = new Dictionary<string, List<(RepoListing Repo, string Name, string Version)>>();
        foreach (var repo in listings)
        {
            if (!repo.IsEnabled) continue;
            foreach (var (iname, name, ver) in repo.Plugins)
            {
                if (!offers.TryGetValue(iname, out var list)) offers[iname] = list = [];
                if (!list.Any(o => ReferenceEquals(o.Repo, repo))) list.Add((repo, name, ver));
            }
        }

        bool FromRepo(PluginSourceRow s, RepoListing r) =>
            !s.IsDev && (r.IsThirdParty ? s.IsThirdParty && NormUrl(s.Url) == NormUrl(r.Url) : !s.IsThirdParty);

        var installedNames = new HashSet<string>(installed.Select(s => s.InternalName));
        var installedOnly  = config.HdebugSrcOverlapInstalledOnly;
        var rows = new List<OverlapRow>();
        foreach (var (iname, list) in offers)
        {
            if (list.Count < 2) continue;
            var anywhere = installedNames.Contains(iname);
            if (installedOnly && !anywhere) continue;
            foreach (var (repo, name, ver) in list)
            {
                var copy = installed.FirstOrDefault(s => s.InternalName == iname && FromRepo(s, repo));
                rows.Add(new OverlapRow(name, iname, ver, repo.Url, repo.IsThirdParty, copy != null, copy?.IsLoaded == true, anywhere));
            }
        }

        // Third-party repos where every plugin is also offered by another enabled repo
        var covered = listings.Where(r => r.IsEnabled && r.IsThirdParty && r.Plugins.Count > 0 &&
                                          r.Plugins.All(p => offers.TryGetValue(p.InternalName, out var l) && l.Count > 1))
                              .Select(r => r.Url).OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToList();

        var pluginCount = rows.Select(r => r.InternalName).Distinct().Count();
        ImGui.SetNextItemOpen(open, ImGuiCond.Always);
        open = ImGui.CollapsingHeader($"Overlapping ({pluginCount})##srcOverlap");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Plugins offered by more than one of your enabled repos, from the lists Dalamud's installer already downloaded.\n" +
                             "Handy for spotting repos that only mirror what another repo has.");
        if (!open) return;

        Common.PushGoldCheckbox();
        if (ImGui.Checkbox("Installed only##srcOverlapInst", ref installedOnly))
        {
            config.HdebugSrcOverlapInstalledOnly = installedOnly;
            config.Save();
        }
        Common.PopGoldCheckbox();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Only show overlaps for plugins you have installed.");

        if (covered.Count > 0)
        {
            Common.GoldText($"{covered.Count} repo{(covered.Count == 1 ? "" : "s")} only offer{(covered.Count == 1 ? "s" : "")} plugins that another repo also has:");
            foreach (var url in covered)
            {
                Common.DimmedText($"  {url}");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(url);
            }
            ImGui.Spacing();
        }

        var shown = rows.Where(r => sourceSearch.Length == 0 ||
                                    r.Name.Contains(sourceSearch, StringComparison.OrdinalIgnoreCase) ||
                                    r.Url.Contains(sourceSearch, StringComparison.OrdinalIgnoreCase)).ToList();
        if (shown.Count == 0)
        {
            Common.DimmedText(rows.Count == 0 ? "  None" : "  No matches");
            ImGui.Spacing();
            return;
        }

        if (!ImGui.BeginTable("##srcTblOverlap", 4, SourceTableFlags)) return;
        ImGui.TableSetupColumn("Plugin",    ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort, 1.3f);
        ImGui.TableSetupColumn("Version",   ImGuiTableColumnFlags.WidthStretch, 0.55f);
        ImGui.TableSetupColumn("Installed", ImGuiTableColumnFlags.WidthStretch, 0.6f);
        ImGui.TableSetupColumn("Repo",      ImGuiTableColumnFlags.WidthStretch, 3.2f);
        Common.PushTableHeader();
        ImGui.TableHeadersRow();
        Common.PopTableHeader();

        var specs = ImGui.TableGetSortSpecs();
        int col = 0; var asc = true;
        if (!specs.IsNull && specs.SpecsCount > 0)
        {
            col = specs.Specs.ColumnIndex;
            asc = specs.Specs.SortDirection == ImGuiSortDirection.Ascending;
        }
        static int Rank(OverlapRow r) => r.LoadedHere ? 2 : r.InstalledHere ? 1 : 0;
        shown.Sort((a, b) =>
        {
            var c = col switch
            {
                1 => string.Compare(a.Version, b.Version, StringComparison.OrdinalIgnoreCase),
                2 => Rank(b).CompareTo(Rank(a)),
                3 => string.Compare(a.Url, b.Url, StringComparison.OrdinalIgnoreCase),
                _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            };
            if (!asc) c = -c;
            // Copies of one plugin stay together, the one you use first
            if (c == 0) c = string.Compare(a.InternalName, b.InternalName, StringComparison.OrdinalIgnoreCase);
            if (c == 0) c = Rank(b).CompareTo(Rank(a));
            if (c == 0) c = a.IsThirdParty.CompareTo(b.IsThirdParty);
            return c;
        });

        foreach (var r in shown)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            if (r.InstalledAnywhere) ImGui.TextUnformatted(r.Name); else Common.DimmedText(r.Name);
            if (ImGui.IsItemHovered() && r.Name != r.InternalName) ImGui.SetTooltip(r.InternalName);

            ImGui.TableSetColumnIndex(1);
            Common.DimmedText(r.Version.Length > 0 ? r.Version : "-");

            ImGui.TableSetColumnIndex(2);
            if (r.LoadedHere)         ImGui.TextUnformatted("Enabled");
            else if (r.InstalledHere) Common.DimmedText("Disabled");
            else                      Common.DimmedText("-");

            ImGui.TableSetColumnIndex(3);
            Common.DimmedText(r.IsThirdParty ? r.Url : "official");
            if (ImGui.IsItemHovered() && r.IsThirdParty) ImGui.SetTooltip(r.Url);
        }
        ImGui.EndTable();
        ImGui.Spacing();
    }

    // Repos added in Dalamud's settings that none of the installed plugins came from
    private void UnusedRepoSection(List<(string Url, bool Enabled)> repos, ref bool open)
    {
        var shown = repos.Where(r => sourceSearch.Length == 0 || r.Url.Contains(sourceSearch, StringComparison.OrdinalIgnoreCase)).ToList();

        ImGui.SetNextItemOpen(open, ImGuiCond.Always);
        open = ImGui.CollapsingHeader($"Unused third-party repos ({repos.Count})##srcUnused");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Custom repos in Dalamud's settings that none of your installed plugins came from.\n" +
                             "Safe to remove unless you plan to install something from them.");
        if (!open) return;

        if (shown.Count == 0) { Common.DimmedText("  No matches"); ImGui.Spacing(); return; }

        if (!ImGui.BeginTable("##srcTblUnused", 2, SourceTableFlags)) return;
        ImGui.TableSetupColumn("Repo",   ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort, 6f);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch, 0.55f);
        Common.PushTableHeader();
        ImGui.TableHeadersRow();
        Common.PopTableHeader();

        var specs = ImGui.TableGetSortSpecs();
        int col = 0; var asc = true;
        if (!specs.IsNull && specs.SpecsCount > 0)
        {
            col = specs.Specs.ColumnIndex;
            asc = specs.Specs.SortDirection == ImGuiSortDirection.Ascending;
        }
        shown.Sort((a, b) =>
        {
            var c = col == 1 ? b.Enabled.CompareTo(a.Enabled) : string.Compare(a.Url, b.Url, StringComparison.OrdinalIgnoreCase);
            if (!asc) c = -c;
            if (c == 0) c = string.Compare(a.Url, b.Url, StringComparison.OrdinalIgnoreCase);
            return c;
        });

        foreach (var (url, enabled) in shown)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            Common.DimmedText(url);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(url);
            ImGui.TableSetColumnIndex(1);
            if (enabled) ImGui.TextUnformatted("Enabled"); else Common.DimmedText("Disabled");
        }
        ImGui.EndTable();
        ImGui.Spacing();
    }

    // Small status tags after the name; only the ones that apply
    private static void SourceTags(PluginSourceRow s)
    {
        void Tag(string text, bool bad, string tip)
        {
            ImGui.SameLine();
            if (bad) Common.RedText(text); else Common.DimmedText(text);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
        }
        if (s.IsTesting)  Tag("[testing]",  false, "Running the testing build of this plugin.");
        if (s.IsOutdated) Tag("[outdated]", true,  "Built for an older Dalamud API; it will not load until it is updated.");
        if (s.IsOrphaned) Tag("[orphaned]", true,  "The repo this came from is no longer in your repo list, so it will not get updates.");
        if (s.IsBanned)   Tag("[removed]",  true,  "Banned or decommissioned by Dalamud.");
    }

    private bool MatchesSourceSearch(PluginSourceRow s) =>
        sourceSearch.Length == 0 ||
        s.Name.Contains(sourceSearch, StringComparison.OrdinalIgnoreCase) ||
        s.Author.Contains(sourceSearch, StringComparison.OrdinalIgnoreCase) ||
        s.Url.Contains(sourceSearch, StringComparison.OrdinalIgnoreCase);

    // Full repo url for third-party copies; the official repo and dev plugins get a plain word instead
    private static string RepoText(PluginSourceRow s) =>
        s.IsDev ? "local" : !s.IsThirdParty ? "official" : s.Url.Length > 0 ? s.Url : "-";

    // ---------------------------------------------------------------- threads

    private void DrawThreads()
    {
        var threads = handler.Threads;
        if (threads.Count == 0) { Common.DimmedText("Sampling threads..."); return; }

        Common.PushGoldCheckbox();
        if (ImGui.Checkbox("Hide idle", ref hideIdleThreads)) { config.HdebugHideIdleThreads = hideIdleThreads; config.Save(); }
        Common.PopGoldCheckbox();
        ImGui.SameLine();
        Common.DimmedText($"{threads.Count} threads, 100% = one full core");

        threadBuf.Clear();
        foreach (var t in threads)
            if (!hideIdleThreads || t.CpuPct >= 0.1 || t.IsMain) threadBuf.Add(t);

        var flags = ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable | ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable("##hdbgThreads", 5, flags, ScrollTableSize())) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("TID",      ImGuiTableColumnFlags.WidthStretch, 0.6f);
        ImGui.TableSetupColumn("Name",     ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableSetupColumn("Start",    ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableSetupColumn("CPU%",     ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending, 0.7f);
        ImGui.TableSetupColumn("CPU time", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.PreferSortDescending, 0.9f);

        Common.PushTableHeader();
        ImGui.TableHeadersRow();
        Common.PopTableHeader();

        var specs = ImGui.TableGetSortSpecs();
        int col = TCCpu; var asc = false;
        if (!specs.IsNull && specs.SpecsCount > 0)
        {
            col = specs.Specs.ColumnIndex;
            asc = specs.Specs.SortDirection == ImGuiSortDirection.Ascending;
        }
        threadBuf.Sort((a, b) =>
        {
            var c = col switch
            {
                TCTid   => a.Tid.CompareTo(b.Tid),
                TCName  => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
                TCStart => string.Compare(a.Module, b.Module, StringComparison.OrdinalIgnoreCase),
                TCTime  => a.CpuTotal.CompareTo(b.CpuTotal),
                _       => a.CpuPct.CompareTo(b.CpuPct),
            };
            return asc ? c : -c;
        });

        foreach (var t in threadBuf)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(TCTid);
            Common.DimmedText(t.Tid.ToString());

            ImGui.TableSetColumnIndex(TCName);
            if (t.IsMain)             Common.GoldText(t.Name);
            else if (t.Name.Length > 0) ImGui.TextUnformatted(t.Name);
            else                      Common.DimmedText("-");
            if (t.IsMain && ImGui.IsItemHovered())
                ImGui.SetTooltip("The game's main thread. Game logic, Dalamud, and every plugin's UI and Framework.Update run here,\nso its cost is broken down per plugin in the Plugins tab.");

            ImGui.TableSetColumnIndex(TCStart);
            Common.DimmedText(t.Module);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Module the thread was started from. coreclr.dll is .NET (Dalamud or a plugin; check the name),\nffxiv_dx11.exe is the game, anything else is an injected DLL (ReShade, overlays, ...).");

            ImGui.TableSetColumnIndex(TCCpu);
            MsColored($"{t.CpuPct:F1}", t.CpuPct, 25, 75);

            ImGui.TableSetColumnIndex(TCTime);
            Common.DimmedText(FormatCpuTime(t.CpuTotal));
        }

        ImGui.EndTable();
    }

    private static string FormatCpuTime(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}.{t.Milliseconds / 10:D2}";

    // ---------------------------------------------------------------- spikes

    private void DrawSpikes()
    {
        ImGui.SetNextItemWidth(170 * ImGuiHelpers.GlobalScale);
        Common.PushGoldSlider();
        ImGui.SliderFloat("##spikeThr", ref handler.SpikeThresholdMs, 10f, 200f, "over %.0f ms");
        // Save once the drag ends, not on every frame of it
        if (ImGui.IsItemDeactivatedAfterEdit()) { config.HdebugSpikeThresholdMs = handler.SpikeThresholdMs; config.Save(); }
        Common.PopGoldSlider();
        ImGui.SameLine();
        Common.PushGoldCheckbox();
        if (ImGui.Checkbox("Ignore loading screens", ref handler.IgnoreLoading)) { config.HdebugIgnoreLoading = handler.IgnoreLoading; config.Save(); }
        Common.PopGoldCheckbox();
        ImGui.SameLine();
        Common.PushGreyButton();
        if (ImGui.Button("Clear")) handler.ClearSpikes();
        Common.PopGreyButton();

        var typical = handler.TypicalFrameMs();
        Common.DimmedTextWrapped($"A spike is a frame over {handler.SpikeThresholdMs:F0} ms that is also at least twice the typical frame " +
                                 $"({typical:F1} ms right now). Hover a spike for the full breakdown.");

        // Frame time chart with the threshold drawn across it
        var ft = handler.FrameTimes();
        if (ft.Length > 1)
        {
            var max = Math.Max(handler.SpikeThresholdMs * 1.5f, ft.Max() * 1.1f);
            ImGui.PushStyleColor(ImGuiCol.PlotLines, Theme.ColPlot);
            ImGui.PlotLines("##frametimes", ft, ft.Length, "", 0f, max, new Vector2(ImGui.GetContentRegionAvail().X, 60 * ImGuiHelpers.GlobalScale));
            ImGui.PopStyleColor();
            var min = ImGui.GetItemRectMin();
            var mx  = ImGui.GetItemRectMax();
            var y   = mx.Y - (mx.Y - min.Y) * (handler.SpikeThresholdMs / max);
            ImGui.GetWindowDrawList().AddLine(new Vector2(min.X, y), new Vector2(mx.X, y), ImGui.GetColorU32(Theme.ColRed with { W = 0.6f }));
        }

        var spikes = handler.Spikes;
        if (spikes.Count == 0)
        {
            ImGui.Spacing();
            Common.DimmedText("No spikes caught yet. Leave this open while you play.");
            return;
        }

        var flags = ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable("##hdbgSpikes", 5, flags, ScrollTableSize())) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Time",          ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("Frame",         ImGuiTableColumnFlags.WidthStretch, 0.7f);
        ImGui.TableSetupColumn("Plugins",       ImGuiTableColumnFlags.WidthStretch, 0.7f);
        ImGui.TableSetupColumn("Game / other",  ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("Biggest cause", ImGuiTableColumnFlags.WidthStretch, 2.4f);
        Common.PushTableHeader();
        ImGui.TableHeadersRow();
        Common.PopTableHeader();

        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Pick(Theme.ColGoldSub, ImGuiCol.HeaderHovered));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive,  Pick(Theme.ColGoldMid, ImGuiCol.HeaderActive));
        for (var i = 0; i < spikes.Count; i++)
        {
            var sp = spikes[i];
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.Selectable($"{sp.Time:HH:mm:ss}##spike{i}", false, ImGuiSelectableFlags.SpanAllColumns);
            var hovered = ImGui.IsItemHovered();

            ImGui.TableSetColumnIndex(1);
            MsColored($"{sp.FrameMs:F1} ms", sp.FrameMs, 33, 100);
            ImGui.TableSetColumnIndex(2);
            MsColored($"{sp.PluginsMs:F1} ms", sp.PluginsMs, 5, 16);
            ImGui.TableSetColumnIndex(3);
            Common.DimmedText($"{sp.OtherMs:F1} ms");

            ImGui.TableSetColumnIndex(4);
            var top = sp.Plugins.Count > 0 ? sp.Plugins[0] : default;
            var topMs = top.Name != null ? top.Draw + top.Upd : 0;
            if (sp.OtherMs > topMs)
                Common.DimmedText("Game, GPU or Dalamud (not a plugin)");
            else
                ImGui.TextUnformatted($"{top.Name}  {topMs:F1} ms ({(top.Draw >= top.Upd ? "draw" : "update")})");

            if (hovered) SpikeTooltip(sp);
        }
        ImGui.PopStyleColor(2);
        ImGui.EndTable();
    }

    private static void SpikeTooltip(SpikeRecord sp)
    {
        ImGui.BeginTooltip();
        Common.GoldText($"{sp.FrameMs:F1} ms frame at {sp.Time:HH:mm:ss}  (typical {sp.TypicalMs:F1} ms)");
        ImGui.TextUnformatted($"Plugins {sp.PluginsMs:F2} ms   game, GPU wait and Dalamud {sp.OtherMs:F2} ms");
        if (sp.Plugins.Count > 0)
        {
            ImGui.Spacing();
            Common.DimmedText("   draw   update  plugin");
            foreach (var (name, draw, upd) in sp.Plugins.Take(10))
                ImGui.TextUnformatted($"{draw,7:F2} {upd,8:F2}  {name}");
        }
        if (sp.TopHandler != null)
        {
            ImGui.Spacing();
            Common.DimmedText($"Slowest update handler: {ShortHandler(sp.TopHandler)} ({sp.TopHandlerMs:F2} ms)");
        }
        ImGui.EndTooltip();
    }

    // ---------------------------------------------------------------- injected DLLs

    private void DrawInjected()
    {
        var mods = handler.Modules;
        if (mods.Count == 0) { Common.DimmedText("Scanning loaded DLLs..."); return; }

        Common.DimmedTextWrapped("Native DLLs inside the game that are not Windows, the game, Dalamud or a plugin. " +
                                 "CPU is what threads started by that DLL use; hooks running on the game's own threads (most overlays) " +
                                 "show up as main-thread time instead, so test by turning an overlay off.");

        var flags = ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable("##hdbgInjected", 5, flags, ScrollTableSize())) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Name",     ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableSetupColumn("Kind",     ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("CPU%",     ImGuiTableColumnFlags.WidthStretch, 0.6f);
        ImGui.TableSetupColumn("Company",  ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("File",     ImGuiTableColumnFlags.WidthStretch, 1.4f);
        Common.PushTableHeader();
        ImGui.TableHeadersRow();
        Common.PopTableHeader();

        // Things worth suspecting first, then whatever burns the most CPU
        foreach (var mod in mods.OrderBy(m => KindRank(m.Category)).ThenByDescending(m => m.CpuPct).ThenBy(m => m.Label))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            if (KindRank(mod.Category) < 4) Common.GoldText(mod.Label); else ImGui.TextUnformatted(mod.Label);
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                Common.GoldText(mod.Label);
                ImGui.TextUnformatted(mod.Path);
                if (mod.Product.Length > 0) Common.DimmedText(mod.Product);
                Common.DimmedText($"{Bytes(mod.Size)} mapped, {mod.Threads} thread(s) started here");
                ImGui.EndTooltip();
            }

            ImGui.TableSetColumnIndex(1);
            Common.DimmedText(mod.Category);
            ImGui.TableSetColumnIndex(2);
            if (mod.Threads > 0) MsColored($"{mod.CpuPct:F1}", mod.CpuPct, 10, 50); else Common.DimmedText("-");
            ImGui.TableSetColumnIndex(3);
            Common.DimmedText(mod.Company.Length > 0 ? mod.Company : "-");
            ImGui.TableSetColumnIndex(4);
            Common.DimmedText(mod.FileName);
        }
        ImGui.EndTable();
    }

    private static int KindRank(string category) => category switch
    {
        "Post-processing" => 0,
        "Overlay"         => 1,
        "Capture"         => 2,
        "Audio"           => 3,
        "Other"           => 4,
        "Driver"          => 5,
        _                 => 6,
    };

    // ---------------------------------------------------------------- network

    private void DrawIo()
    {
        var m = handler.Memory;
        if (m == null) { Common.DimmedText("Sampling..."); return; }

        Common.GoldText($"Disk / file  read {Bytes((long)m.IoReadPerSec)}/s   write {Bytes((long)m.IoWritePerSec)}/s");
        Common.DimmedTextWrapped("Everything the game process reads and writes, including Penumbra mods and plugin files. " +
                                 "Hitches that line up with read bursts are loading, not CPU.");
        Chart("Read (MB/s)",  handler.IoReadHistory);
        Chart("Write (MB/s)", handler.IoWriteHistory);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawNetwork();
    }

    private void DrawNetwork()
    {
        var net = handler.Network;
        if (net == null) { Common.DimmedText("Sampling network..."); return; }

        Common.GoldText($"System  down {Bytes((long)net.SysDown)}/s   up {Bytes((long)net.SysUp)}/s");
        if (!double.IsNaN(net.GameDown))
        {
            ImGui.SameLine();
            ImGui.TextUnformatted($"   Game  down {Bytes((long)net.GameDown)}/s   up {Bytes((long)net.GameUp)}/s");
        }
        if (!net.PerConnAvailable)
            Common.DimmedTextWrapped("Windows only gives traffic per connection to programs running as administrator, so the game's own " +
                                     "up/down is not available here. The connection list and system totals still work.");

        Chart("Down (KB/s, whole system)", handler.NetDownHistory);
        Chart("Up (KB/s, whole system)",   handler.NetUpHistory);

        var flags = ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable("##hdbgNet", 6, flags, ScrollTableSize())) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Remote", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("Port",   ImGuiTableColumnFlags.WidthStretch, 0.6f);
        ImGui.TableSetupColumn("Kind",   ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("State",  ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableSetupColumn("Down/s", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("Up/s",   ImGuiTableColumnFlags.WidthStretch, 0.8f);
        Common.PushTableHeader();
        ImGui.TableHeadersRow();
        Common.PopTableHeader();

        foreach (var c in net.Conns.OrderBy(c => c.Hint.StartsWith("Game", StringComparison.Ordinal) ? 0 : 1)
                                   .ThenBy(c => c.State == "Established" ? 0 : 1)
                                   .ThenByDescending(c => double.IsNaN(c.InPerSec) ? 0 : c.InPerSec))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(c.Remote);
            ImGui.TableSetColumnIndex(1);
            Common.DimmedText(c.RemotePort.ToString());
            ImGui.TableSetColumnIndex(2);
            if (c.Hint.StartsWith("Game", StringComparison.Ordinal)) Common.GoldText(c.Hint);
            else Common.DimmedText(c.Hint.Length > 0 ? c.Hint : "-");
            ImGui.TableSetColumnIndex(3);
            Common.DimmedText(c.State);
            ImGui.TableSetColumnIndex(4);
            Common.DimmedText(double.IsNaN(c.InPerSec) ? "-" : $"{Bytes((long)c.InPerSec)}");
            ImGui.TableSetColumnIndex(5);
            Common.DimmedText(double.IsNaN(c.OutPerSec) ? "-" : $"{Bytes((long)c.OutPerSec)}");
        }
        ImGui.EndTable();
    }

    // ---------------------------------------------------------------- memory

    private void DrawMemory()
    {
        var m = handler.Memory;
        if (m == null) { Common.DimmedText("Sampling memory..."); return; }

        var flags = ImGuiTableFlags.SizingStretchSame;
        if (ImGui.BeginTable("##hdbgMem", 4, flags))
        {
            Stat("Private",    Bytes(m.PrivateBytes), "Memory committed to the game process. This is the number that grows with leaks.");
            Stat("Working set", Bytes(m.WorkingSet),  "Physical RAM the process currently has paged in.");
            Stat(".NET heap",  Bytes(m.GcHeap),       "Managed heap shared by Dalamud and every plugin.");
            Stat("GC committed", Bytes(m.GcCommitted), "Memory the .NET runtime has reserved for the heap.");

            Stat("Alloc rate", $"{Bytes((long)m.AllocPerSec)}/s", "How fast Dalamud and plugins allocate managed memory. High values cause GC stutter.");
            Stat("GC/s",       $"{m.Gen0PerSec:F1} / {m.Gen1PerSec:F1} / {m.Gen2PerSec:F2}", "Gen0 / Gen1 / Gen2 collections per second. Frequent Gen2 means something holds a lot of memory and churns.");
            Stat("GC pause",   $"{m.GcPausePct:F2}%", "Share of time the runtime paused threads for garbage collection.");
            Stat("Fragmented", Bytes(m.GcFragmented), "Free space stuck between live objects in the managed heap.");

            Stat("Textures",   $"{Bytes(m.TexBytes)} ({m.TexCount})", "Textures created through Dalamud by plugins. Estimated, and mostly VRAM.");
            Stat("Handles",    m.Handles.ToString("N0"), "Open OS handles. A steadily climbing count is a leak.");
            Stat("Threads",    m.ThreadCount.ToString(), "OS threads in the game process.");
            Stat("Gen2 total", m.Gen2Total.ToString("N0"), "Full collections since the game started.");
            ImGui.EndTable();
        }

        ImGui.Spacing();
        Chart("Private memory (MB)", handler.PrivateHistory);
        Chart(".NET heap (MB)",      handler.HeapHistory);
        Chart("CPU (% of all cores)", handler.CpuHistory, 0, 100);

        ImGui.Spacing();
        Common.DimmedTextWrapped("Plugins share one .NET heap and one process, so memory cannot be split per plugin. " +
                                 "To find a leak, watch Private and .NET heap for a few minutes, then disable suspects one at a time " +
                                 "and see which one stops the climb. Per-plugin texture memory is in the Plugins tab.");
    }

    private static void Stat(string label, string value, string tip)
    {
        ImGui.TableNextColumn();
        Common.DimmedText(label);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
        Common.GoldText(value);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
    }

    private static void Chart(string label, float[] data, float min = float.NaN, float max = float.NaN)
    {
        if (data.Length < 2) return;
        float lo = data.Min(), hi = data.Max();
        var pad = Math.Max(1f, (hi - lo) * 0.15f);
        if (float.IsNaN(min)) min = Math.Max(0, lo - pad);
        if (float.IsNaN(max)) max = hi + pad;

        Common.DimmedText(label);
        var last = $"{data[^1]:F0}  (min {lo:F0}, max {hi:F0})";
        Common.SameLineRight(last);
        ImGui.TextUnformatted(last);

        ImGui.PushStyleColor(ImGuiCol.PlotLines, Theme.ColPlot);
        ImGui.PlotLines($"##{label}", data, data.Length, "", min, max, new Vector2(ImGui.GetContentRegionAvail().X, 50 * ImGuiHelpers.GlobalScale));
        ImGui.PopStyleColor();
    }

    private static string Bytes(long b)
    {
        double v = b;
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        var i = 0;
        while (Math.Abs(v) >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{v:F0} {u[i]}" : $"{v:F1} {u[i]}";
    }
}
