using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace HoliestFluffiness;

internal static class Common
{
    internal static void DimmedText(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColWhiteDim);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    internal static void DimmedTextWrapped(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColWhiteDim);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    internal static bool IsPluginLoaded(IDalamudPluginInterface pluginInterface, string name) =>
        pluginInterface.InstalledPlugins.Any(p => p.InternalName == name && p.IsLoaded);

    internal static string? GetCurrentPlayerKey(IObjectTable objectTable)
    {
        var player = objectTable[0] as IPlayerCharacter;
        return player != null
            ? $"{player.Name.TextValue}@{player.HomeWorld.ValueNullable?.Name.ExtractText()}"
            : null;
    }

    // ── ImGui style helpers ───────────────────────────────────────────────────

    // Full themed window: WindowBg, Text, TitleBar, FrameBg, Scrollbar, ResizeGrip (13 colors)
    internal static void PushWindowTheme()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg,             Theme.ColSecondary);
        ImGui.PushStyleColor(ImGuiCol.Text,                 Theme.ColWhite);
        ImGui.PushStyleColor(ImGuiCol.TitleBg,              Theme.ColHighlight);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive,        Theme.ColHighlight);
        ImGui.PushStyleColor(ImGuiCol.FrameBg,              Theme.ColPrimary);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered,       Theme.ColHighlight);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,          Theme.ColHighlight);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab,        Theme.ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Theme.ColGold);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive,  Theme.ColGold);
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip,           Theme.ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered,    Theme.ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive,     Theme.ColGold);
    }
    internal static void PopWindowTheme() => ImGui.PopStyleColor(13);

    // Scrollbar sub-theme (4 colors) use standalone or as part of a manual push block
    internal static void PushScrollbarTheme()
    {
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,          Theme.ColHighlight);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab,        Theme.ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Theme.ColGold);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive,  Theme.ColGold);
    }
    internal static void PopScrollbarTheme() => ImGui.PopStyleColor(4);

    // Resize grip sub-theme (3 colors)
    internal static void PushResizeGripTheme()
    {
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip,        Theme.ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, Theme.ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive,  Theme.ColGold);
    }
    internal static void PopResizeGripTheme() => ImGui.PopStyleColor(3);

    // Gold button (4 colors: Button + ButtonHovered + ButtonActive + Text)
    internal static void PushGoldButton()
    {
        ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ColGold);
        ImGui.PushStyleColor(ImGuiCol.Text,          Theme.ColGold);
    }
    internal static void PopGoldButton() => ImGui.PopStyleColor(4);

    // Grey (secondary) button (4 colors: Button + ButtonHovered + ButtonActive + Text)
    internal static void PushGreyButton()
    {
        ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ColGrey);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ColGreyHov);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ColGreyAct);
        ImGui.PushStyleColor(ImGuiCol.Text,          Theme.ColWhite);
    }
    internal static void PopGreyButton() => ImGui.PopStyleColor(4);

    // Gold-coloured TextUnformatted (1 color push/pop)
    internal static void GoldText(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColGold);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    // Green-coloured TextUnformatted (1 color push/pop)
    internal static void GreenText(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColGreen);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    // Red-coloured TextUnformatted (1 color push/pop)
    internal static void RedText(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColRed);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    // Table header theme (2 colors: TableHeaderBg + Text)
    internal static void PushTableHeader()
    {
        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, Theme.ColPrimary);
        ImGui.PushStyleColor(ImGuiCol.Text,          Theme.ColGold);
    }
    internal static void PopTableHeader() => ImGui.PopStyleColor(2);

    // Search/filter InputTextWithHint border theme (1 color + 1 style var)
    internal static void PushSearchInput()
    {
        ImGui.PushStyleColor(ImGuiCol.Border, Theme.ColGoldMid);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
    }
    internal static void PopSearchInput()
    {
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    // Horizontally centers the next widget by offsetting CursorPosX
    internal static void CenterCursorForWidth(float width) =>
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - width) * 0.5f);
}
