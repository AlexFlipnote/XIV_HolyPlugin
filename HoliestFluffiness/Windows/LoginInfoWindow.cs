using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace HoliestFluffiness.Windows;

public class LoginInfoWindow : Window
{
    private string? characterDisplay;
    private string? fcDisplay;
    private string? plateDisplay;
    private string? privateHouseDisplay;
    private string? fcHouseDisplay;
    private List<int> order = [0, 1, 2, 3];
    private bool   changingCharacter;
    private string changingToText = "Changing character...";

    private static readonly Vector4 ColBg      = new(30f / 255f,  30f / 255f,  30f / 255f,  1f);
    private static readonly Vector4 ColSection = new(40f / 255f,  40f / 255f,  40f / 255f,  1f);
    private static readonly Vector4 ColWhite   = new(249f / 255f, 248f / 255f, 244f / 255f, 1f);
    private static readonly Vector4 ColGold    = new(235f / 255f, 230f / 255f, 114f / 255f, 1f);
    private static readonly Vector4 ColGoldSub = new(235f / 255f, 230f / 255f, 114f / 255f, 0.18f);
    private static readonly Vector4 ColGoldMid = new(235f / 255f, 230f / 255f, 114f / 255f, 0.35f);
    private static readonly Vector4 ColGrey    = new(60f / 255f,  60f / 255f,  60f / 255f,  1f);
    private static readonly Vector4 ColGreyHov = new(80f / 255f,  80f / 255f,  80f / 255f,  1f);
    private static readonly Vector4 ColGreyAct = new(100f / 255f, 100f / 255f, 100f / 255f, 1f);

    private readonly Action? onOpenCharList;

    public LoginInfoWindow(Action? onOpenCharList = null)
        : base("Character info##LoginInfoPopup",
               ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse)
    {
        this.onOpenCharList  = onOpenCharList;
        RespectCloseHotkey   = true;
    }

    public void SetChangingState(string name, string world, int? slot)
    {
        changingToText    = slot.HasValue ? $"Changing to {name} @ {world}/{slot}..." : $"Changing to {name} @ {world}...";
        changingCharacter = true;
    }

    public void SetData(string? character, string? fc, string? plate, string? privateHouse, string? fcHouse, List<int> displayOrder)
    {
        changingCharacter = false;
        changingToText    = "Changing character...";
        characterDisplay    = character;
        fcDisplay           = fc;
        plateDisplay        = plate;
        privateHouseDisplay = privateHouse;
        fcHouseDisplay      = fcHouse;
        order               = displayOrder;
        IsOpen              = true;
    }

    public override void PreDraw()
    {
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.PushStyleColor(ImGuiCol.Text,              ColWhite);
        ImGui.PushStyleColor(ImGuiCol.WindowBg,          ColBg);
        ImGui.PushStyleColor(ImGuiCol.TitleBg,           ColSection);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive,     ColSection);
        ImGui.PushStyleColor(ImGuiCol.TableBorderLight,  ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.TableBorderStrong, ColGold);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12, 10));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding,   new Vector2(8, 5));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(6);
        ImGui.PopStyleVar(3);
    }

    public override void Draw()
    {
        if (changingCharacter)
        {
            ImGui.Dummy(new Vector2(0, 4));
            ImGui.PushStyleColor(ImGuiCol.Text, ColGold);
            ImGui.TextUnformatted(changingToText);
            ImGui.PopStyleColor();
            ImGui.Dummy(new Vector2(0, 4));
            return;
        }

        var tableFlags = ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingFixedFit;
        if (ImGui.BeginTable("##charinfo", 2, tableFlags))
        {
            ImGui.TableSetupColumn("##label", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch);

            foreach (var slot in order)
            {
                string? label = null;
                string? value = null;

                switch (slot)
                {
                    case 0 when characterDisplay    != null: label = "Character";     value = characterDisplay;    break;
                    case 1 when plateDisplay        != null: label = "Search info";   value = plateDisplay;        break;
                    case 2 when privateHouseDisplay != null: label = "Private House"; value = privateHouseDisplay; break;
                    case 3 when fcDisplay           != null: label = "Free Company";  value = fcDisplay;           break;
                    case 4 when fcHouseDisplay      != null: label = "FC House";      value = fcHouseDisplay;      break;
                }

                if (label == null) continue;

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.PushStyleColor(ImGuiCol.Text, ColGold);
                ImGui.TextUnformatted(label);
                ImGui.PopStyleColor();
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(value!);
            }

            ImGui.EndTable();
        }

        ImGui.Dummy(new Vector2(0, 2));

        const float okWidth     = 60f;
        const float changeWidth = 136f;
        const float gap         = 8f;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - okWidth - changeWidth - gap) * 0.5f);

        // OK button first
        ImGui.PushStyleColor(ImGuiCol.Button,        ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  ColGold);
        ImGui.PushStyleColor(ImGuiCol.Text,          ColGold);
        if (ImGui.Button("OK", new Vector2(okWidth, 0)))
            IsOpen = false;
        ImGui.PopStyleColor(4);

        ImGui.SameLine(0, gap);

        // Change character button second (grey)
        ImGui.PushStyleColor(ImGuiCol.Button,        ColGrey);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColGreyHov);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  ColGreyAct);
        if (ImGui.Button("Change character", new Vector2(changeWidth, 0)))
            onOpenCharList?.Invoke();
        ImGui.PopStyleColor(3);
    }
}
