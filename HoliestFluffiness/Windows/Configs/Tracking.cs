using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;

namespace HoliestFluffiness.Windows;

public partial class ConfigWindow
{
    private CancellationTokenSource? bulkUpdateCts;
    private int bulkUpdateProgress;
    private int bulkUpdateTotal;

    private static string FormatStatNum(long n, bool shorten) => shorten ? Common.ShortenNumber(n) : n.ToString("N0");

    private static string CsvEscape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? $"\"{s.Replace("\"", "\"\"")}\""
            : s;
    }

    private void DrawTrackingSection()
    {
        BeginSection("Tracking", "Tracks info for your own characters only, across every world and data center you play on. " +
            "Everything is stored in a local SQLite database on your machine, nothing is ever sent anywhere.");

        ConfigCheckbox(
            "Enable character database##dbenabled",
            configuration.CharactersDbEnabled,
            v => configuration.CharactersDbEnabled = v,
            "This will also allow you to use /hw command");

        if (configuration.CharactersDbEnabled)
        {
            ConfigCheckbox(
                "Shorten numbers##dbshortennumbers",
                configuration.CharactersDbShortenNumbers,
                v => configuration.CharactersDbShortenNumbers = v,
                "Displays large numbers as 100K, 1.2M, 3.4B, etc. instead of the full value");

            ConfigCheckbox(
                "Enable FC points tracking##fcpointstracking",
                configuration.FcPointsTrackingEnabled,
                v => configuration.FcPointsTrackingEnabled = v,
                "Unlike gil/MGP, FC points can only be read by opening the FC window. With this on, " +
                "the plugin briefly opens it once per login (if you're in an FC) and closes it again to " +
                "grab the value.");

            ImGui.Dummy(new Vector2(0, 4));
            SectionRow();

            if (bulkUpdateTotal > 0)
            {
                Common.DimmedText($"Processing {bulkUpdateProgress}/{bulkUpdateTotal}...");
                ImGui.SameLine();
                PushButton();
                if (ImGui.Button("Cancel##bulkupdate")) bulkUpdateCts?.Cancel();
                PopButton();
            }
            else
            {
                PushButton();
                if (ImGui.Button("Update all characters"))
                {
                    bulkUpdateCts?.Cancel();
                    bulkUpdateCts?.Dispose();
                    bulkUpdateCts = new CancellationTokenSource();
                    _ = RunBulkUpdateAsync(bulkUpdateCts.Token);
                }
                ImGui.SameLine();
                if (ImGui.Button("Export CSV##dbexport"))
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("Key,Name,World,DataCenter,Slot,FreeCompany,SearchInfo,PrivateHouse,FcHouse,Gil,Mgp,FcPoints,LastSeen");
                    foreach (var r in characterDb.GetAll().OrderBy(r => r.World).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot))
                        sb.AppendLine(string.Join(",", CsvEscape(r.Key), CsvEscape(r.Name), CsvEscape(r.World), CsvEscape(r.DataCenter),
                            r.Slot > 0 ? r.Slot.ToString() : "", CsvEscape(r.FreeCompany), CsvEscape(r.SearchInfo),
                            CsvEscape(r.PrivateHouse), CsvEscape(r.FcHouse),
                            r.Gil < 0 ? "" : r.Gil.ToString(),
                            r.Mgp < 0 ? "" : r.Mgp.ToString(),
                            r.FcPoints < 0 ? "" : r.FcPoints.ToString(),
                            r.LastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));
                    var csv = sb.ToString();
                    fileDialogManager.SaveFileDialog("Export characters", "CSV{.csv}", "characters_export.csv", ".csv",
                        (ok, path) => { if (ok) { File.WriteAllText(path, csv, Encoding.UTF8); csvExportMessage = $"Saved: {path}"; } },
                        pluginInterface.ConfigDirectory.FullName);
                }
                PopButton();
            }

            if (csvExportMessage != null)
            {
                ImGui.Dummy(new Vector2(0, 2));
                SectionRow();
                Common.DimmedText(csvExportMessage);
            }

            ImGui.Dummy(new Vector2(0, 8));
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);
            Common.GoldText("Did you know?");
            ImGui.Dummy(new Vector2(0, 2));
            SectionRow();

            var stats         = characterDb.GetStats();
            var count         = stats.Count;
            var totalGil      = stats.TotalGil;
            var totalMgp      = stats.TotalMgp;
            var withFc        = stats.WithFc;
            var uniqueFc      = stats.UniqueFc;
            var uniqueFcHouse = stats.UniqueFcHouse;
            var withHouse     = stats.WithPrivateHouse;
            var loneWolves    = count - withFc;
            var withStory     = stats.WithSearchInfo;
            var richest       = stats.Richest;
            var avgGil        = stats.AverageGil;
            var totalCeruleum = stats.InventoryTotals.GetValueOrDefault(10155u);
            var totalMagitek  = stats.InventoryTotals.GetValueOrDefault(10373u);
            var totalFcPoints = stats.TotalFcPoints;

            var shorten  = configuration.CharactersDbShortenNumbers;
            var statNums = new[]
            {
                FormatStatNum(count, shorten), FormatStatNum(withFc, shorten), FormatStatNum(loneWolves, shorten),
                FormatStatNum(uniqueFcHouse, shorten), FormatStatNum(withHouse, shorten), FormatStatNum(withStory, shorten),
                FormatStatNum(totalGil, shorten), FormatStatNum(avgGil, shorten),
            };
            var statLabels = new[]
            {
                $"character{(count == 1 ? "" : "s")} are indexed",
                $"are in a free company ({uniqueFc:N0} being unique)",
                $"lone {(loneWolves == 1 ? "wolf roams" : "wolves roam")} without a free company",
                $"free {(uniqueFcHouse == 1 ? "company has" : "companies have")} a house",
                $"character{(withHouse == 1 ? "" : "s")} have a private house",
                $"character{(withStory == 1 ? "" : "s")} have written their search comment",
                "gil is spread across all your characters",
                "is the average gil per character",
            };

            var numColW = statNums.Max(n => ImGui.CalcTextSize(n).X) + 4f;
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColWhiteDim);
            if (ImGui.BeginTable("##dbstats", 2))
            {
                ImGui.TableSetupColumn("##n", ImGuiTableColumnFlags.WidthFixed, numColW);
                ImGui.TableSetupColumn("##l", ImGuiTableColumnFlags.WidthStretch);

                for (var i = 0; i < statNums.Length; i++)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + numColW - ImGui.CalcTextSize(statNums[i]).X);
                    ImGui.TextUnformatted(statNums[i]);
                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextUnformatted(statLabels[i]);
                }

                if (richest != null)
                {
                    var richestNum = FormatStatNum(richest.Gil, shorten);
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + numColW - ImGui.CalcTextSize(richestNum).X);
                    ImGui.TextUnformatted(richestNum);
                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextUnformatted($"is the highest gil amount, owned by {richest.Name} @ {richest.World}");
                }

                foreach (var (num, label) in new[]
                {
                    (FormatStatNum(totalMgp, shorten), "MGP across all your characters"),
                    (FormatStatNum(totalFcPoints, shorten), "FC points earned across your unique free companies"),
                    (FormatStatNum(totalCeruleum, shorten), "Ceruleum Tanks across all your characters"),
                    (FormatStatNum(totalMagitek, shorten), "Magitek Repair Materials across all your characters"),
                })
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + numColW - ImGui.CalcTextSize(num).X);
                    ImGui.TextUnformatted(num);
                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextUnformatted(label);
                }

                ImGui.EndTable();
            }
            ImGui.PopStyleColor();
        }

        EndSection(10);
    }

    private async Task RunBulkUpdateAsync(CancellationToken token)
    {
        var chars = characterDb.GetAll()
            .OrderBy(r => r.World).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot)
            .ToList();
        bulkUpdateTotal    = chars.Count;
        bulkUpdateProgress = 0;

        try
        {
            foreach (var rec in chars)
            {
                token.ThrowIfCancellationRequested();
                bulkUpdateProgress++;

                var loginTcs    = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var infoTcs     = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var fcPointsTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnLogin()         => loginTcs.TrySetResult(true);
                void OnInfoReady()     => infoTcs.TrySetResult(true);
                void OnFcPointsReady() => fcPointsTcs.TrySetResult(true);
                clientState.Login                += OnLogin;
                loginInfoHandler.OnInfoReady      += OnInfoReady;
                loginInfoHandler.OnFcPointsReady  += OnFcPointsReady;
                try
                {
                    onSwitchCharacter(rec.Name, rec.World);
                    await loginTcs.Task.WaitAsync(TimeSpan.FromSeconds(60), token);
                    await infoTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                    // FC points may still be mid-refresh (forces the FC window open/closed) after
                    // OnInfoReady fires; wait for it too so we don't switch characters mid-refresh.
                    await fcPointsTcs.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
                }
                catch (TimeoutException) { /* character didn't respond in time, skip */ }
                finally
                {
                    clientState.Login                -= OnLogin;
                    loginInfoHandler.OnInfoReady      -= OnInfoReady;
                    loginInfoHandler.OnFcPointsReady  -= OnFcPointsReady;
                }
            }
        }
        finally
        {
            bulkUpdateTotal    = 0;
            bulkUpdateProgress = 0;
        }
    }
}
