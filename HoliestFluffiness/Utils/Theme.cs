using System.Numerics;

namespace HoliestFluffiness;

internal static class Theme
{
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
}
