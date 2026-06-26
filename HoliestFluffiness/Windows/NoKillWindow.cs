using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace HoliestFluffiness.Windows;

public class NoKillWindow : Window
{

    private int interceptCount;
    private string? autoLoginName;
    private string? autoLoginWorld;

    public NoKillWindow()
        : base("No-kill##NoKillPopup",
               ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar |
               ImGuiWindowFlags.NoSavedSettings  | ImGuiWindowFlags.NoCollapse)
    {
        RespectCloseHotkey = true;
    }

    public void Show(int count, string? name, string? world)
    {
        interceptCount  = count;
        autoLoginName   = name;
        autoLoginWorld  = world;
        IsOpen = true;
    }

    public override void PreDraw()
    {
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.PushStyleColor(ImGuiCol.Text,          Theme.ColWhite);
        ImGui.PushStyleColor(ImGuiCol.WindowBg,      Theme.ColSecondary);
        ImGui.PushStyleColor(ImGuiCol.TitleBg,       Theme.ColHighlight);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Theme.ColHighlight);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 12));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar(2);
    }

    public override void Draw()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColGold);
        ImGui.TextUnformatted("A lobby error was intercepted.");
        ImGui.PopStyleColor();

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColWhiteDim);
        ImGui.TextUnformatted("The error was converted to a connection lost.");
        ImGui.TextUnformatted("Auto-dismiss + reconnect will be attempted.");
        ImGui.PopStyleColor();

        ImGui.Dummy(new Vector2(0, 4));

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColWhiteDim);
        ImGui.TextUnformatted($"Intercepted this session: {interceptCount}");
        var charLabel = (autoLoginName != null && autoLoginWorld != null)
            ? $"{autoLoginName} @ {autoLoginWorld}"
            : "none (log in first)";
        ImGui.TextUnformatted($"Reconnect target: {charLabel}");
        ImGui.PopStyleColor();

        ImGui.Dummy(new Vector2(0, 4));

        const float btnW = 80f;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - btnW) * 0.5f);
        ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ColGold);
        ImGui.PushStyleColor(ImGuiCol.Text,          Theme.ColGold);
        if (ImGui.Button("OK", new Vector2(btnW, 0)))
            IsOpen = false;
        ImGui.PopStyleColor(4);
    }
}
