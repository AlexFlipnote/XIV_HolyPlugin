using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;

namespace HoliestFluffiness;

internal static class Theme
{
    internal static Configuration? Config;

    // Applied only to background colours, so text and accents stay solid
    internal static float Opacity => Math.Clamp(Config?.ThemeOpacity ?? 1f, 0.1f, 1f);
    internal static Vector4 Fade(Vector4 c) => c with { W = c.W * Opacity };

    // False means push nothing and let Dalamud's own theme show through. Snapshotted per frame:
    // the config checkbox writes mid-frame, and a flip between a PushStyleColor and its PopStyleColor
    // would leak the ImGui colour stack. The change lands next frame instead.
    private static bool cachedUseCustom = true;
    internal static bool UseCustom => cachedUseCustom;

    // Call at the very start of each frame, before any window draws
    internal static void Sync() => cachedUseCustom = !(Config?.ThemeDisableCustom ?? false);

    // Three-shade hierarchy, all structural backgrounds derive from these
    internal static readonly Vector4 ColHighlight = new(24/255f, 24/255f, 24/255f, 1f); // #181818, topbar, FrameBg, scrollbar track
    internal static readonly Vector4 ColPrimary   = new(40/255f, 40/255f, 40/255f, 1f); // #282828, sidebar, sections, panels on background
    internal static readonly Vector4 ColSecondary = new(48/255f, 48/255f, 48/255f, 1f); // #303030, window background

    // Semantic colours below resolve to Dalamud's style editor when the custom theme is off,
    // so text, status and role colours match whatever theme the user picked in /xlstyle
    private static Vector4 Style(ImGuiCol slot) => ImGui.GetStyle().Colors[(int)slot];

    // Hover shade for a style Background colour, which ships at ~40% alpha
    private static Vector4 Lift(Vector4 c) => c with { W = Math.Min(1f, c.W * 1.6f) };

    // Text
    internal static Vector4 ColWhite    => UseCustom ? new(249/255f, 248/255f, 244/255f, 1f)    : Style(ImGuiCol.Text);         // #F9F8F4
    internal static Vector4 ColWhiteDim => UseCustom ? new(249/255f, 248/255f, 244/255f, 0.55f) : Style(ImGuiCol.TextDisabled); // #F9F8F4 @ 55%

    // Gold accent; the style's Attention colours stand in for it on the default theme
    internal static Vector4 ColGold    => UseCustom ? new(235/255f, 230/255f, 114/255f, 1f)    : ImGuiColors.AttentionForeground;       // #EBE672
    internal static Vector4 ColGoldMid => UseCustom ? new(235/255f, 230/255f, 114/255f, 0.35f) : Lift(ImGuiColors.AttentionBackground); // #EBE672 @ 35%
    internal static Vector4 ColGoldSub => UseCustom ? new(235/255f, 230/255f, 114/255f, 0.18f) : ImGuiColors.AttentionBackground;       // #EBE672 @ 18%

    // Secondary (neutral) button
    internal static Vector4 ColGrey    => UseCustom ? new( 60/255f,  60/255f,  60/255f, 1f) : Style(ImGuiCol.Button);        // #3C3C3C
    internal static Vector4 ColGreyHov => UseCustom ? new( 80/255f,  80/255f,  80/255f, 1f) : Style(ImGuiCol.ButtonHovered); // #505050
    internal static Vector4 ColGreyAct => UseCustom ? new(100/255f, 100/255f, 100/255f, 1f) : Style(ImGuiCol.ButtonActive);  // #646464

    // Line charts
    internal static Vector4 ColPlot => UseCustom ? ColGold : Style(ImGuiCol.PlotLines);

    // Status
    internal static Vector4 ColGreen    => UseCustom ? new( 80/255f, 200/255f,  80/255f, 1f) : ImGuiColors.SuccessForeground; // #50C850
    internal static Vector4 ColGreenMid => UseCustom ? ColGreen with { W = 0.35f } : Lift(ImGuiColors.SuccessBackground);
    internal static Vector4 ColGreenSub => UseCustom ? ColGreen with { W = 0.18f } : ImGuiColors.SuccessBackground;
    internal static Vector4 ColRed      => UseCustom ? new(220/255f,  80/255f,  80/255f, 1f) : ImGuiColors.ErrorForeground;   // #DC5050
    internal static Vector4 ColWarning  => UseCustom ? ColGold                                : ImGuiColors.WarningForeground;

    // Tinted fills behind content (fate progress bars)
    internal static Vector4 ColRedBg     => UseCustom ? ColRed  with { W = 0.45f } : ImGuiColors.ErrorBackground;
    internal static Vector4 ColWarningBg => UseCustom ? ColGold with { W = 0.40f } : ImGuiColors.WarningBackground;

    // Job-role colours (nearby players "colour job names" option)
    internal static Vector4 ColRoleTank   => UseCustom ? new( 64/255f, 158/255f, 255/255f, 1f) : ImGuiColors.TankBlue;    // #409EFF
    internal static Vector4 ColRoleHealer => UseCustom ? new(127/255f, 247/255f,  94/255f, 1f) : ImGuiColors.HealerGreen; // #7FF75E
    internal static Vector4 ColRoleDps    => UseCustom ? new(255/255f, 125/255f, 125/255f, 1f) : ImGuiColors.DPSRed;      // #FF7D7D
    internal static Vector4 ColRoleOther  => UseCustom ? new(204/255f, 204/255f, 204/255f, 1f) : ImGuiColors.DalamudGrey; // #CCCCCC
}
