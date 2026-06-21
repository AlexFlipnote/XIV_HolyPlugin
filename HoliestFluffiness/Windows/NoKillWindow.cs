using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace HoliestFluffiness.Windows;

public class NoKillWindow : Window
{
    private static readonly Vector4 ColBg      = new(30f / 255f,  30f / 255f,  30f / 255f,  1f);
    private static readonly Vector4 ColSection = new(40f / 255f,  40f / 255f,  40f / 255f,  1f);
    private static readonly Vector4 ColWhite   = new(249f / 255f, 248f / 255f, 244f / 255f, 1f);
    private static readonly Vector4 ColWhiteDim = new(249f / 255f, 248f / 255f, 244f / 255f, 0.55f);
    private static readonly Vector4 ColGold    = new(235f / 255f, 230f / 255f, 114f / 255f, 1f);
    private static readonly Vector4 ColGoldSub = new(235f / 255f, 230f / 255f, 114f / 255f, 0.18f);
    private static readonly Vector4 ColGoldMid = new(235f / 255f, 230f / 255f, 114f / 255f, 0.35f);

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
        ImGui.PushStyleColor(ImGuiCol.Text,          ColWhite);
        ImGui.PushStyleColor(ImGuiCol.WindowBg,      ColBg);
        ImGui.PushStyleColor(ImGuiCol.TitleBg,       ColSection);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, ColSection);
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
        ImGui.PushStyleColor(ImGuiCol.Text, ColGold);
        ImGui.TextUnformatted("A lobby error was intercepted.");
        ImGui.PopStyleColor();

        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("The error was converted to a connection lost.");
        ImGui.TextUnformatted("Auto-dismiss + reconnect will be attempted.");
        ImGui.PopStyleColor();

        ImGui.Dummy(new Vector2(0, 4));

        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted($"Intercepted this session: {interceptCount}");
        var charLabel = (autoLoginName != null && autoLoginWorld != null)
            ? $"{autoLoginName} @ {autoLoginWorld}"
            : "none (log in first)";
        ImGui.TextUnformatted($"Reconnect target: {charLabel}");
        ImGui.PopStyleColor();

        ImGui.Dummy(new Vector2(0, 4));

        const float btnW = 80f;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - btnW) * 0.5f);
        ImGui.PushStyleColor(ImGuiCol.Button,        ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  ColGold);
        ImGui.PushStyleColor(ImGuiCol.Text,          ColGold);
        if (ImGui.Button("OK", new Vector2(btnW, 0)))
            IsOpen = false;
        ImGui.PopStyleColor(4);
    }
}
