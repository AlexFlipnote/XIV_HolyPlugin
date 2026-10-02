using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using HoliestFluffiness.Handlers;

namespace HoliestFluffiness.Windows;

// Progress popup for ApartmentSweepHandler while it runs (with Cancel), then the outcome with an
// OK button once it finishes. Only ever appears as the direct result of pressing "Find apartment".
public sealed class ApartmentSweepWindow : Window
{
    private const float ContentWidth = 280f;

    private readonly ApartmentSweepHandler handler;

    public ApartmentSweepWindow(ApartmentSweepHandler handler)
        : base("Apartment Sweep##HFApartmentSweep",
               ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar |
               ImGuiWindowFlags.NoSavedSettings  | ImGuiWindowFlags.NoCollapse)
    {
        this.handler       = handler;
        ShowCloseButton    = false;
        RespectCloseHotkey = false;
    }

    public override void PreOpenCheck() => IsOpen = handler.IsRunning || handler.Result != null;

    public override void PreDraw()
    {
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        Common.PushPopupTheme();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 12));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
    }

    public override void PostDraw()
    {
        Common.PopPopupTheme();
        ImGui.PopStyleVar(2);
    }

    public override void Draw()
    {
        if (handler.IsRunning) DrawProgress();
        else if (handler.Result is { } result) DrawResult(result);
    }

    private void DrawProgress()
    {
        Common.GoldText($"Searching {handler.District} for a vacant apartment");
        ImGui.Dummy(new Vector2(0, 4));

        var division = handler.CurrentIsSubdivision ? "subdivision" : "main";
        ImGui.TextUnformatted($"Ward {handler.CurrentWard}, {division}");
        Common.DimmedText(handler.Status);
        if (handler.FoundCount > 0)
            Common.DimmedText($"Found so far: {handler.FoundCount}");
        ImGui.Dummy(new Vector2(0, 4));

        var done = handler.BuildingIndex;
        ImGui.ProgressBar((float)done / ApartmentSweepHandler.BuildingCount, new Vector2(ContentWidth, 0),
            $"{done}/{ApartmentSweepHandler.BuildingCount}");
        ImGui.Dummy(new Vector2(0, 4));

        const float btnW = 80f;
        Common.CenterCursorForWidth(btnW);
        Common.PushGreyButton();
        if (ImGui.Button("Cancel", new Vector2(btnW, 0)))
            handler.Cancel();
        Common.PopGreyButton();
    }

    private void DrawResult(ApartmentSweepResult result)
    {
        switch (result.Outcome)
        {
            case ApartmentSweepOutcome.Found:
                var latest = result.Found[^1];
                Common.GoldText("Vacant apartment found!");
                ImGui.Dummy(new Vector2(0, 4));
                ImGui.TextUnformatted($"{result.District}, {Describe(latest)}");
                Common.DimmedText(latest.Vacant > 1
                    ? $"{latest.Vacant} rooms free, the first is room {latest.Room}."
                    : $"Room {latest.Room} is free.");
                Common.DimmedText($"Checked {result.Checked} of {ApartmentSweepHandler.BuildingCount} buildings.");
                if (result.Found.Count > 1)
                    DrawFoundList(result, "Found this run:");
                break;

            case ApartmentSweepOutcome.Exhausted:
                Common.GoldText(result.Found.Count == 0 ? "No vacant apartments" : "Apartment sweep finished");
                ImGui.Dummy(new Vector2(0, 4));
                if (result.Found.Count == 0)
                    Common.DimmedText($"Every building in {result.District} is full.");
                else
                    DrawFoundList(result, $"Buildings with vacancies in {result.District}:");
                break;

            default:
                Common.GoldText("Apartment sweep stopped");
                ImGui.Dummy(new Vector2(0, 4));
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ContentWidth);
                Common.DimmedTextWrapped(result.Error ?? "Unknown error.");
                ImGui.PopTextWrapPos();
                if (result.Found.Count > 0)
                    DrawFoundList(result, "Found before stopping:");
                break;
        }

        ImGui.Dummy(new Vector2(ContentWidth, 4));

        const float btnW = 80f;
        var canContinue = handler.CanContinue;
        var rowWidth = canContinue ? btnW * 2 + ImGui.GetStyle().ItemSpacing.X : btnW;
        Common.CenterCursorForWidth(rowWidth);

        if (canContinue)
        {
            Common.PushGreyButton();
            if (ImGui.Button("Continue", new Vector2(btnW, 0)))
                handler.Continue();
            Common.PopGreyButton();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Keep searching from the next building and collect more vacancies.");
            ImGui.SameLine();
        }

        Common.PushGoldButton();
        if (ImGui.Button("OK", new Vector2(btnW, 0)))
            handler.ClearResult();
        Common.PopGoldButton();
    }

    private static void DrawFoundList(ApartmentSweepResult result, string heading)
    {
        ImGui.Dummy(new Vector2(0, 4));
        ImGui.TextUnformatted(heading);
        foreach (var v in result.Found)
            Common.DimmedText($"- {Describe(v)}: {v.Vacant} free (first: room {v.Room})");
    }

    private static string Describe(ApartmentVacancy v) => $"Ward {v.Ward} ({(v.Subdivision ? "subdivision" : "main")})";
}
