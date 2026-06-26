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

    private static string CsvEscape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? $"\"{s.Replace("\"", "\"\"")}\""
            : s;
    }

    private void DrawDatabaseSection()
    {
        BeginSection("Database");

        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Stores character info to a local SQLite database on every login.");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 6));
        SectionRow();

        PushCheckbox();
        var dbEnabled = configuration.CharactersDbEnabled;
        if (ImGui.Checkbox("Enable character database", ref dbEnabled))
        {
            configuration.CharactersDbEnabled = dbEnabled;
            configuration.Save();
        }
        PopCheckbox();

        ImGui.Dummy(new Vector2(0, 4));
        SectionRow();

        if (bulkUpdateTotal > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
            ImGui.TextUnformatted($"Processing {bulkUpdateProgress}/{bulkUpdateTotal}...");
            ImGui.PopStyleColor();
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
                sb.AppendLine("Key,Name,World,DataCenter,Slot,FreeCompany,SearchInfo,PrivateHouse,FcHouse,Gil,LastSeen");
                foreach (var r in characterDb.GetAll().OrderBy(r => r.World).ThenBy(r => r.Slot == 0 ? int.MaxValue : r.Slot))
                    sb.AppendLine(string.Join(",", CsvEscape(r.Key), CsvEscape(r.Name), CsvEscape(r.World), CsvEscape(r.DataCenter),
                        r.Slot > 0 ? r.Slot.ToString() : "", CsvEscape(r.FreeCompany), CsvEscape(r.SearchInfo),
                        CsvEscape(r.PrivateHouse), CsvEscape(r.FcHouse),
                        r.Gil < 0 ? "" : r.Gil.ToString(),
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
            ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
            ImGui.TextUnformatted(csvExportMessage);
            ImGui.PopStyleColor();
        }

        ImGui.Dummy(new Vector2(0, 8));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);
        ImGui.PushStyleColor(ImGuiCol.Text, ColGold);
        ImGui.TextUnformatted("Did you know?");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 2));
        SectionRow();

        var count         = characterDb.Count();
        var totalGil      = characterDb.TotalGil();
        var withFc        = characterDb.CountWithFc();
        var uniqueFc      = characterDb.CountUniqueFc();
        var uniqueFcHouse = characterDb.CountUniqueFcHouse();
        var withHouse     = characterDb.CountWithPrivateHouse();
        var loneWolves    = count - withFc;
        var withStory     = characterDb.CountWithSearchInfo();
        var richest       = characterDb.RichestCharacter();
        var avgGil        = characterDb.AverageGil();
        var invTotals     = characterDb.TotalInventoryItems();
        var totalCeruleum = invTotals.GetValueOrDefault(10155u);
        var totalMagitek  = invTotals.GetValueOrDefault(10373u);

        var statNums   = new[] { $"{count:N0}", $"{withFc:N0}", $"{loneWolves:N0}", $"{uniqueFcHouse:N0}", $"{withHouse:N0}", $"{withStory:N0}", $"{totalGil:N0}", $"{avgGil:N0}" };
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
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
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
                var richestNum = $"{richest.Gil:N0}";
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + numColW - ImGui.CalcTextSize(richestNum).X);
                ImGui.TextUnformatted(richestNum);
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted($"is the highest gil amount, owned by {richest.Name} @ {richest.World}");
            }

            foreach (var (num, label) in new[] { ($"{totalCeruleum:N0}", "Ceruleum Tanks across all your characters"), ($"{totalMagitek:N0}", "Magitek Repair Materials across all your characters") })
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

                var loginTcs   = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var infoTcs    = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnLogin()     => loginTcs.TrySetResult(true);
                void OnInfoReady() => infoTcs.TrySetResult(true);
                clientState.Login            += OnLogin;
                loginInfoHandler.OnInfoReady += OnInfoReady;
                try
                {
                    onSwitchCharacter(rec.Name, rec.World);
                    await loginTcs.Task.WaitAsync(TimeSpan.FromSeconds(60), token);
                    await infoTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                }
                catch (TimeoutException) { /* character didn't respond in time, skip */ }
                finally
                {
                    clientState.Login            -= OnLogin;
                    loginInfoHandler.OnInfoReady -= OnInfoReady;
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
