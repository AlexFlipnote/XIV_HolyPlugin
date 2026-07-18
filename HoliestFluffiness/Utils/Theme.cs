using System;
using System.Numerics;

namespace HoliestFluffiness;

internal static class Theme
{
    internal static Configuration? Config;

    // Window-background opacity (0.30-1.00). Applied only to background colours so
    // panels turn translucent while text, widgets and accents stay fully solid.
    internal static float Opacity => Math.Clamp(Config?.ThemeOpacity ?? 1f, 0.1f, 1f);
    internal static Vector4 Fade(Vector4 c) => c with { W = c.W * Opacity };

    // When false the plugin pushes no colours at all and Dalamud's own theme shows
    // through. Every Push* helper and inline colour push is gated on this flag, so
    // toggling it off leaves the windows looking exactly like stock Dalamud.
    //
    // The value is snapshotted once per frame via Sync(): the "Disable custom theme"
    // checkbox writes the config mid-frame, and if the flag flipped between a
    // PushStyleColor and its matching PopStyleColor the ImGui colour stack would
    // leak (PushStyleColor/PopStyleColor mismatch). Reading the cached snapshot keeps
    // every push balanced with its pop within the frame; the change lands next frame.
    private static bool cachedUseCustom = true;
    internal static bool UseCustom => cachedUseCustom;

    // Called once at the very start of each frame, before any window draws.
    internal static void Sync() => cachedUseCustom = !(Config?.ThemeDisableCustom ?? false);

    // Three-shade hierarchy, all structural backgrounds derive from these
    internal static readonly Vector4 ColHighlight = new(24/255f, 24/255f, 24/255f, 1f); // #181818, topbar, FrameBg, scrollbar track
    internal static readonly Vector4 ColPrimary   = new(40/255f, 40/255f, 40/255f, 1f); // #282828, sidebar, sections, panels on background
    internal static readonly Vector4 ColSecondary = new(48/255f, 48/255f, 48/255f, 1f); // #303030, window background

    // Text
    internal static readonly Vector4 ColWhite    = new(249/255f, 248/255f, 244/255f, 1f);    // #F9F8F4
    internal static readonly Vector4 ColWhiteDim = new(249/255f, 248/255f, 244/255f, 0.55f); // #F9F8F4 @ 55%

    // Gold accent
    internal static readonly Vector4 ColGold    = new(235/255f, 230/255f, 114/255f, 1f);    // #EBE672
    internal static readonly Vector4 ColGoldMid = new(235/255f, 230/255f, 114/255f, 0.35f); // #EBE672 @ 35%
    internal static readonly Vector4 ColGoldSub = new(235/255f, 230/255f, 114/255f, 0.18f); // #EBE672 @ 18%

    // Secondary (neutral) button
    internal static readonly Vector4 ColGrey    = new( 60/255f,  60/255f,  60/255f, 1f); // #3C3C3C
    internal static readonly Vector4 ColGreyHov = new( 80/255f,  80/255f,  80/255f, 1f); // #505050
    internal static readonly Vector4 ColGreyAct = new(100/255f, 100/255f, 100/255f, 1f); // #646464

    // Status
    internal static readonly Vector4 ColGreen = new( 80/255f, 200/255f,  80/255f, 1f); // #50C850
    internal static readonly Vector4 ColRed   = new(220/255f,  80/255f,  80/255f, 1f); // #DC5050

    // Job-role colours (nearby players "colour job names" option)
    internal static readonly Vector4 ColRoleTank   = new( 64/255f, 158/255f, 255/255f, 1f); // #409EFF
    internal static readonly Vector4 ColRoleHealer = new(127/255f, 247/255f,  94/255f, 1f); // #7FF75E
    internal static readonly Vector4 ColRoleDps    = new(255/255f, 125/255f, 125/255f, 1f); // #FF7D7D
    internal static readonly Vector4 ColRoleOther  = new(204/255f, 204/255f, 204/255f, 1f); // #CCCCCC
}
