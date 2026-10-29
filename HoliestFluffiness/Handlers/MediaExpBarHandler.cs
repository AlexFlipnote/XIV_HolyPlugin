using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace HoliestFluffiness.Handlers;

// Borrows the game's own experience bar (_Exp) while something is playing on the PC. The gauge shows
// song progress, and text nodes of our own (styled as a copy of the EXP text) put the song, the time,
// the volume and previous, play/pause and next in the corners the settings pick: the top row takes the
// chain EXP line's place, hiding it while it holds something of ours, and the bottom row shares the
// job/level/EXP line's, which keeps showing on the left unless the settings hide it. The rested EXP
// moon is hidden throughout. When nothing is
// playing the addon is asked to redraw from the game's data, which restores everything.
// _Exp ignores the mouse, so clicks go to a borderless ImGui window laid over the bar instead: the song
// and time run the same action as the server info bar entry, the bar itself seeks, the buttons act, and
// the volume track (drawn by that window) takes drags and the mouse wheel.
public sealed unsafe class MediaExpBarHandler : IDisposable
{
    private const string AddonName    = "_Exp";
    private const uint   LabelNodeId  = 0x48460001;
    private const uint   TimeNodeId   = 0x48460002;
    private const uint   ButtonNodeId = 0x48460003;
    private const uint   VolumeNodeId = 0x48460006;
    private const uint   LastNodeId   = VolumeNodeId;
    private const int    MaxHidden   = 8;
    // Space between neighbouring items in a row
    private const float  ItemGap     = 12f;
    // Indexes into the per-item spans, in left-to-right reading order within a corner
    private const int    SongItem     = 0;
    private const int    TimeItem     = 1;
    private const int    VolumeItem   = 2;
    private const int    ControlsItem = 3;
    private const int    ItemCount    = 4;
    // The game's text boxes carry more empty space above the glyphs than below, so a plain mirror of
    // the job line lands on the bar; tuned in game until the gap matches the one under the bar
    private const float  ChainLift   = 6f;
    // The bar is only a few pixels tall, so the seek area is padded out to at least this height
    private const float  SeekMinHeight = 10f;
    private static readonly Vector2 TooltipPadding = new(8f, 4f);
    private const ushort MoonMinSize   = 8;
    private const ushort MoonMaxSize   = 64;
    // Space between the media buttons; each one's click area reaches halfway into it
    private const float  ButtonSpacing = 8f;
    private static readonly MediaAction[] Buttons = [MediaAction.Previous, MediaAction.TogglePlayPause, MediaAction.Next];
    // The percentage, right-aligned in a box as wide as "100%" so the track never moves, then a track
    // this wide VolumeGap after it
    private const float  VolumeWidth       = 80f;
    private const float  VolumeGap         = 6f;
    private const float  VolumeTrackHeight = 2f;
    // Half-width and half-height of the diamond handle
    private static readonly Vector2 VolumeHandle = new(4f, 5f);
    // How far past either end of the track a press still counts as grabbing it
    private const float  VolumeGrabSlack   = 4f;
    private const float  VolumeWheelStep   = 0.05f;

    // How long a released scrub keeps showing its target, so the bar does not snap back
    // to the old position before the player reports the new one
    private static readonly TimeSpan SeekHold = TimeSpan.FromSeconds(1.5);
    // The same for volume, which the worker only reads back once a second
    private static readonly TimeSpan VolumeHold = TimeSpan.FromSeconds(1.5);

    private const ImGuiWindowFlags ClickAreaFlags =
        ImGuiWindowFlags.NoDecoration       | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings |
        ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav        | ImGuiWindowFlags.NoBringToFrontOnFocus;

    private readonly Configuration   config;
    private readonly MediaHandler    media;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameGui        gameGui;
    private readonly nint[]          hiddenText  = new nint[MaxHidden];
    private readonly nint[]          buttonNodes = new nint[Buttons.Length];

    private Action? clickAction;

    private AtkTextNode* labelNode;
    private AtkTextNode* timeNode;
    private AtkTextNode* volumeNode;
    private int          hiddenCount;
    // Which rows' game text is hidden: 1 for the top row, 2 for the bottom
    private int          hiddenRows;
    private bool         overriding;
    private int          lastGauge = -1;
    private string       lastTime  = "";
    private string       lastLabel = "";
    private float        lastLabelWidth;
    private float        songWidth;
    // The time's width, its width without " (Paused)", and how far left of that plain text's end the
    // middle of its "/" sits, all in node units
    private float        timeWidth;
    private float        timePlainWidth;
    private float        timeAnchor;
    private AtkResNode*  moonNode;
    private bool         moonHidden;
    // Set by the overlay, coloured by the next addon update; -1 when no button is hovered
    private int          hoveredButton = -1;
    private int          buttonsPlaying = -1;

    private bool     scrubbing;
    private float    scrubFraction;
    private DateTime seekShownUntil;

    private bool     volumeDragging;
    private bool     volumeGrabbed;
    private float    volumeTarget;
    private string   lastVolumeText = "";
    private DateTime volumeShownUntil;

    public MediaExpBarHandler(Configuration config, MediaHandler media, IAddonLifecycle addonLifecycle, IGameGui gameGui)
    {
        this.config         = config;
        this.media          = media;
        this.addonLifecycle = addonLifecycle;
        this.gameGui        = gameGui;

        addonLifecycle.RegisterListener(AddonEvent.PostUpdate,          AddonName, OnUpdate);
        addonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, AddonName, OnRequestedUpdate);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize,         AddonName, OnFinalize);
    }

    public void SetClickAction(Action action) => clickAction = action;

    // Called from UiBuilder.Draw. A scrub only ends when its button sees the release, so one that loses
    // the overlay mid-drag (song gone, UI hidden) is dropped instead of left hanging.
    public void DrawOverlay()
    {
        if (TryDrawOverlay()) return;
        scrubbing      = false;
        volumeDragging = false;
        hoveredButton  = -1;
    }

    // One window spans everything clickable. ImGui gives an overlap to the item submitted first, so the
    // bar, buttons and volume go in before the song and time.
    private bool TryDrawOverlay()
    {
        if (!overriding || timeNode == null || Song is not { } song) return false;
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(AddonName).Address;
        if (addon == null || !Common.IsAddonVisible(addon)) return false;

        var scale    = addon->Scale;
        var viewport = ImGui.GetMainViewport().Pos;

        // The song and time each open the player, wherever they sit
        Span<(Vector2 Min, Vector2 Max)> textRects = stackalloc (Vector2, Vector2)[2];
        Span<bool> textShown = [labelNode->AtkResNode.IsVisible(), timeNode->AtkResNode.IsVisible()];
        for (var i = 0; i < textRects.Length; i++)
        {
            var node = i == 0 ? &labelNode->AtkResNode : &timeNode->AtkResNode;
            var tMin = Common.GetNodePosition(node) + viewport;
            textRects[i] = (tMin, tMin + new Vector2(node->Width, node->Height) * scale);
        }

        var gauge = FindGauge(addon);
        var track = gauge == null ? null : FindTrack(gauge);

        // The bar padded out to a clickable height; the buttons keep clear of it too
        Vector2 seekMin = default, seekMax = default;
        if (track != null)
        {
            var trackMin  = Common.GetNodePosition(track) + viewport;
            var trackSize = new Vector2(track->Width, track->Height) * scale;
            var pad       = new Vector2(0, Math.Max(0f, SeekMinHeight * scale - trackSize.Y) / 2f);
            seekMin = trackMin - pad;
            seekMax = trackMin + trackSize + pad;
        }
        var canSeek = track != null && song.CanSeek && song.HasTimeline && seekMax.X - seekMin.X >= 1;

        // The button nodes are laid out by the addon update; their click areas reach into the gaps between
        Span<(Vector2 Min, Vector2 Max)> buttonRects = stackalloc (Vector2, Vector2)[Buttons.Length];
        var showButtons = ButtonsVisible();
        var buttonPad   = new Vector2(ButtonSpacing / 2f * scale, 0);

        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        if (canSeek) { min = Vector2.Min(min, seekMin); max = Vector2.Max(max, seekMax); }
        for (var i = 0; i < textRects.Length; i++)
        {
            if (!textShown[i]) continue;
            min = Vector2.Min(min, textRects[i].Min);
            max = Vector2.Max(max, textRects[i].Max);
        }
        for (var i = 0; showButtons && i < Buttons.Length; i++)
        {
            var node   = &((AtkTextNode*)buttonNodes[i])->AtkResNode;
            var bMin   = Common.GetNodePosition(node) + viewport - buttonPad;
            var bMax   = bMin + buttonPad * 2 + new Vector2(node->Width, node->Height) * scale;
            buttonRects[i] = (bMin, bMax);
            min = Vector2.Min(min, bMin);
            max = Vector2.Max(max, bMax);
        }

        // The track runs from just past the percentage, on the row's middle line
        var showVolume = volumeNode != null && volumeNode->AtkResNode.IsVisible();
        Vector2 volumeMin = default, volumeMax = default, trackStart = default;
        if (showVolume)
        {
            var valueRes = &volumeNode->AtkResNode;
            volumeMin  = Common.GetNodePosition(valueRes) + viewport;
            trackStart = new Vector2(volumeMin.X + (valueRes->Width + VolumeGap) * scale, volumeMin.Y + valueRes->Height * scale / 2f);
            volumeMax  = new Vector2(trackStart.X + (VolumeWidth + VolumeHandle.X) * scale, volumeMin.Y + valueRes->Height * scale);
            min = Vector2.Min(min, volumeMin);
            max = Vector2.Max(max, volumeMax);
        }
        if (max.X - min.X < 1 || max.Y - min.Y < 1) return false;

        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(max - min);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,    Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize,    Vector2.One);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        if (ImGui.Begin("##HFMediaExpBarClick", ClickAreaFlags))
        {
            if (canSeek) DrawSeekArea(song, seekMin, seekMax);
            else scrubbing = false;

            hoveredButton = -1;
            for (var i = 0; showButtons && i < Buttons.Length; i++)
            {
                if (HitArea($"##hfmediaexpbar{Buttons[i]}", buttonRects[i].Min, buttonRects[i].Max, out var hovered))
                    media.Send(Buttons[i]);
                if (hovered) hoveredButton = i;
            }

            if (showVolume) DrawVolume(song, volumeMin, volumeMax, trackStart, scale);
            else volumeDragging = false;

            for (var i = 0; clickAction != null && i < textRects.Length; i++)
                if (textShown[i] && HitArea($"##hfmediaexpbarclick{i}", textRects[i].Min, textRects[i].Max, out _)) clickAction();
        }
        ImGui.End();
        ImGui.PopStyleVar(3);
        return true;
    }

    // Click or hold-and-drag on the bar to scrub; the seek is sent once the mouse is released
    private void DrawSeekArea(MediaSnapshot song, Vector2 min, Vector2 max)
    {
        HitArea("##hfmediaexpbarseek", min, max, out var hovered);
        var fraction = Math.Clamp((ImGui.GetMousePos().X - min.X) / (max.X - min.X), 0f, 1f);
        if (ImGui.IsItemActive())
        {
            scrubbing     = true;
            scrubFraction = fraction;
        }
        else if (hovered)
        {
            // The overlay runs with no window padding, which the tooltip would otherwise inherit
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, TooltipPadding);
            ImGui.SetTooltip(MediaSnapshot.FormatTime(song.Duration * fraction));
            ImGui.PopStyleVar();
        }

        if (scrubbing && ImGui.IsItemDeactivated())
        {
            scrubbing      = false;
            seekShownUntil = DateTime.UtcNow + SeekHold;
            media.Seek(song.Duration * scrubFraction);
        }
    }

    // Drag along the track to set the volume, or scroll anywhere over the percentage or the track.
    // A drag sends each whole percent as it passes, so the music follows the knob.
    private void DrawVolume(MediaSnapshot song, Vector2 min, Vector2 max, Vector2 trackStart, float scale)
    {
        var trackWidth = VolumeWidth * scale;
        var shown      = ShownVolume(song) ?? 0f;

        // Only a press that starts on the track drags, so clicking the percentage never mutes
        var slack  = VolumeGrabSlack * scale;
        HitArea("##hfmediaexpbarvolume", min, max, out var hovered);
        if (ImGui.IsItemActivated())
        {
            var mouseX = ImGui.GetMousePos().X;
            volumeGrabbed = mouseX >= trackStart.X - slack && mouseX <= trackStart.X + trackWidth + slack;
        }
        if (ImGui.IsItemActive() && volumeGrabbed)
        {
            var target = Math.Clamp((ImGui.GetMousePos().X - trackStart.X) / trackWidth, 0f, 1f);
            if (!volumeDragging || MathF.Round(target * 100) != MathF.Round(volumeTarget * 100))
                media.SetVolume(target);
            volumeDragging = true;
            volumeTarget   = target;
            shown          = target;
        }
        else if (volumeDragging)
        {
            volumeDragging   = false;
            volumeShownUntil = DateTime.UtcNow + VolumeHold;
        }
        else if (hovered && ImGui.GetIO().MouseWheel is var wheel and not 0f)
        {
            volumeTarget     = Math.Clamp(MathF.Round((shown + wheel * VolumeWheelStep) * 20f) / 20f, 0f, 1f);
            volumeShownUntil = DateTime.UtcNow + VolumeHold;
            shown            = volumeTarget;
            media.SetVolume(volumeTarget);
        }

        // Like the game's own settings sliders: a thin line, light up to the handle and dim after it, with
        // a white diamond handle. Outlined dark so it holds up over any scenery, and pixel-snapped so the
        // line stays sharp; the handle turns gold while hovered or held.
        var dl     = ImGui.GetWindowDrawList();
        var half   = VolumeTrackHeight * scale / 2f;
        var border = MathF.Round(Math.Max(1f, scale));
        var a      = Vector2.Round(new Vector2(trackStart.X, trackStart.Y - half));
        var b      = Vector2.Round(new Vector2(trackStart.X + trackWidth, trackStart.Y + half));
        var knobX  = MathF.Round(a.X + (b.X - a.X) * shown);
        var edge   = Rgb(0f, 0f, 0f, 0.8f);

        dl.AddRectFilled(a - new Vector2(border), b + new Vector2(border), edge);
        dl.AddRectFilled(a, b, Rgb(0.42f, 0.42f, 0.42f));
        if (knobX > a.X) dl.AddRectFilled(a, new Vector2(knobX, b.Y), Rgb(0.85f, 0.85f, 0.85f));

        // Corners listed clockwise, which ImGui needs to anti-alias a filled shape cleanly
        var size  = VolumeHandle * scale;
        var mid   = new Vector2(knobX, MathF.Round(trackStart.Y));
        var top   = mid - new Vector2(0, size.Y);
        var right = mid + new Vector2(size.X, 0);
        var low   = mid + new Vector2(0, size.Y);
        var left  = mid - new Vector2(size.X, 0);
        dl.AddQuadFilled(top + new Vector2(0, -border), right + new Vector2(border, 0), low + new Vector2(0, border), left - new Vector2(border, 0), edge);
        dl.AddQuadFilled(top, right, low, left, hovered || volumeDragging ? Rgb(Theme.ColGold) : Rgb(1f, 1f, 1f));
    }

    private static uint Rgb(float r, float g, float b, float a = 1f) =>
        ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, a));

    private static uint Rgb(Vector4 color) => ImGui.ColorConvertFloat4ToU32(color);

    // While dragging, and briefly after a drag or scroll, the target instead of the last reading
    private float? ShownVolume(MediaSnapshot song) =>
        volumeDragging || DateTime.UtcNow < volumeShownUntil ? volumeTarget : song.Volume;

    private static bool HitArea(string id, Vector2 min, Vector2 max, out bool hovered)
    {
        ImGui.SetCursorScreenPos(min);
        var clicked = ImGui.InvisibleButton(id, Vector2.Max(max - min, Vector2.One));
        hovered = ImGui.IsItemHovered() || ImGui.IsItemActive();
        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        return clicked;
    }

    private bool ButtonsVisible() =>
        buttonNodes[0] != 0 && ((AtkTextNode*)buttonNodes[0])->AtkResNode.IsVisible();

    // While scrubbing, and briefly after, the bar and time show the target instead of the live position
    private float? SeekPreview => scrubbing || DateTime.UtcNow < seekShownUntil ? scrubFraction : null;

    private MediaSnapshot? Song =>
        config.MediaEnabled && config.MediaExpBarEnabled && media.Current is { Title.Length: > 0 } song ? song : null;

    private void OnUpdate(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || addon->RootNode == null) return;

        if (Song is not { } song)
        {
            if (overriding) Restore(addon);
            return;
        }

        var gauge = FindGauge(addon);
        if (gauge == null) return;

        // Everything is laid out in the root node's space. Text whose centre sits above the gauge's
        // middle is the chain EXP line; text below it is the job/level/EXP line.
        var gaugeNode = gauge->OwnerNode;
        var gaugePos  = PositionInRoot(addon, (AtkResNode*)gaugeNode);
        var gaugeMid  = gaugePos.Y + gaugeNode->AtkResNode.Height / 2f;

        var template = FindVanillaText(addon, gaugeMid, above: false);
        if (template == null) return;

        overriding = true;
        var preview = SeekPreview;
        UpdateGauge(gauge, preview ?? song.Progress);
        UpdateMoon(addon, gaugePos.X + gaugeNode->AtkResNode.Width);

        if (labelNode == null) CreateNodes(addon, template);

        // The gauge component is much taller than the thin bar it draws, so measure the bar itself and
        // mirror the job line: the top row sits as far above the bar as the job line sits below it
        var height   = template->AtkResNode.Height;
        var bottomY  = PositionInRoot(addon, &template->AtkResNode).Y;
        var (barTop, barBottom) = VisibleBar(addon, gauge, gaugePos.Y, gaugeNode->AtkResNode.Height);
        ushort drawW = 0, drawH = 0;
        template->GetTextDrawSize(&drawW, &drawH);
        var topY  = barTop - (bottomY - barBottom) - drawH - ChainLift;
        var x     = gaugePos.X;
        var width = (float)gaugeNode->AtkResNode.Width;

        var plainTime = BuildTime(song, preview);
        var time      = WithPaused(plainTime, song);
        if (time != lastTime)
        {
            (timePlainWidth, timeAnchor) = MeasureAnchor(timeNode, plainTime);
            timeNode->SetText(time);
            timeWidth = time.Length > 0 ? Measure(timeNode) : 0f;
            lastTime  = time;
        }
        var volume = ShownVolume(song);
        UpdateVolumeText(volume);
        UpdateButtonTexts(song);

        Span<bool> shown =
        [
            config.MediaBarShowSong,
            config.MediaBarShowTime && time.Length > 0,
            config.MediaBarShowVolume && volume != null,
            config.MediaBarShowControls,
        ];
        Span<float> widths = [0f, timeWidth, VolumeItemWidth(), ControlsWidth()];
        Span<float> xs     = stackalloc float[ItemCount];

        // The job line is only hidden when asked; while it shows, the bottom-left corner starts after it
        HideVanillaText(addon, gaugeMid, RowUsed(shown, top: true), !config.MediaBarShowJob);
        var jobWidth = config.MediaBarShowJob ? JobLineWidth(addon, gaugeMid, x, width) : 0f;
        LayoutRow(song.Display, shown, widths, xs, x, width, top: true,  reserved: 0f);
        LayoutRow(song.Display, shown, widths, xs, x, width, top: false, reserved: jobWidth > 0 ? jobWidth + ItemGap : 0f);
        AnchorControls(shown, widths, xs, x, width);

        float RowY(int item) => IsTop(Slot(item)) ? topY : bottomY;
        Place(&labelNode->AtkResNode,  shown[SongItem],   xs[SongItem],   RowY(SongItem),   widths[SongItem], height);
        Place(&timeNode->AtkResNode,   shown[TimeItem],   xs[TimeItem],   RowY(TimeItem),   widths[TimeItem], height);
        Place(&volumeNode->AtkResNode, shown[VolumeItem], xs[VolumeItem], RowY(VolumeItem), volumeNode->AtkResNode.Width, height);
        PlaceButtons(template, shown[ControlsItem], xs[ControlsItem], RowY(ControlsItem), height);
    }

    private MediaBarSlot Slot(int item) => item switch
    {
        SongItem   => config.MediaBarSongSlot,
        TimeItem   => config.MediaBarTimeSlot,
        VolumeItem => config.MediaBarVolumeSlot,
        _          => config.MediaBarControlsSlot,
    };

    private static bool IsTop(MediaBarSlot slot)  => slot is MediaBarSlot.TopLeft or MediaBarSlot.TopRight;
    private static bool IsLeft(MediaBarSlot slot) => slot is MediaBarSlot.TopLeft or MediaBarSlot.BottomLeft;

    private bool RowUsed(Span<bool> shown, bool top)
    {
        for (var i = 0; i < ItemCount; i++)
            if (shown[i] && IsTop(Slot(i)) == top) return true;
        return false;
    }

    // Left-corner items run from the row's left edge and right-corner items from its right edge, both in
    // the order song, time, volume, buttons as read left to right. The song gets whatever width the rest
    // of the row leaves, cut with "..." to fit, and then only as much as its text needs. The left corner
    // starts `reserved` in, past the game's own text that still shows there.
    private void LayoutRow(string display, Span<bool> shown, Span<float> widths, Span<float> xs, float x, float width, bool top,
        float reserved)
    {
        var count  = 0;
        var fixedW = 0f;
        for (var i = 0; i < ItemCount; i++)
        {
            if (!shown[i] || IsTop(Slot(i)) != top) continue;
            count++;
            if (i != SongItem) fixedW += widths[i];
        }
        if (count == 0) return;

        if (shown[SongItem] && IsTop(Slot(SongItem)) == top)
        {
            var available = Math.Max(0f, width - reserved - fixedW - ItemGap * (count - 1));
            if (display != lastLabel || Math.Abs(available - lastLabelWidth) > 0.5f)
            {
                FitText(labelNode, display, available);
                lastLabel      = display;
                lastLabelWidth = available;
                songWidth      = Measure(labelNode);
            }
            widths[SongItem] = songWidth;
        }

        var left = x + reserved;
        for (var i = 0; i < ItemCount; i++)
        {
            if (!shown[i] || IsTop(Slot(i)) != top || !IsLeft(Slot(i))) continue;
            xs[i] = left;
            left += widths[i] + ItemGap;
        }

        var right = x + width;
        for (var i = ItemCount - 1; i >= 0; i--)
        {
            if (!shown[i] || IsTop(Slot(i)) != top || IsLeft(Slot(i))) continue;
            right -= widths[i];
            xs[i]  = right;
            right -= ItemGap;
        }
    }

    // Buttons alone in their corner sit with play/pause centred on the time's "/" when the time is on the
    // same side of the other row, as in the default layout. Right-aligned time grows leftwards when
    // " (Paused)" is added, so there the "/" is found from its right end and the buttons hold still.
    private void AnchorControls(Span<bool> shown, Span<float> widths, Span<float> xs, float x, float width)
    {
        if (!shown[ControlsItem] || !shown[TimeItem]) return;
        var controls = Slot(ControlsItem);
        var timeSlot = Slot(TimeItem);
        if (IsTop(controls) == IsTop(timeSlot) || IsLeft(controls) != IsLeft(timeSlot)) return;
        for (var i = 0; i < ItemCount; i++)
            if (i != ControlsItem && shown[i] && Slot(i) == controls) return;

        var slashX = IsLeft(timeSlot)
            ? xs[TimeItem] + timePlainWidth - timeAnchor
            : xs[TimeItem] + widths[TimeItem] - timeAnchor;
        var start = slashX - ButtonNode(1)->Width / 2f - ButtonSpacing - ButtonNode(0)->Width;
        xs[ControlsItem] = Math.Clamp(start, x, Math.Max(x, x + width - widths[ControlsItem]));
    }

    private static void Place(AtkResNode* node, bool shown, float x, float y, float width, ushort height)
    {
        node->ToggleVisibility(shown);
        if (!shown) return;
        node->SetPositionFloat(x, y);
        node->SetWidth((ushort)Math.Ceiling(width));
        node->SetHeight(height);
    }

    private AtkResNode* ButtonNode(int i) => &((AtkTextNode*)buttonNodes[i])->AtkResNode;

    // The number box, the gap, the track and the half of the handle that overhangs its end
    private float VolumeItemWidth() => volumeNode->AtkResNode.Width + VolumeGap + VolumeWidth + VolumeHandle.X;

    private float ControlsWidth() =>
        ButtonNode(0)->Width + ButtonNode(1)->Width + ButtonNode(2)->Width + ButtonSpacing * 2;

    // Sized once to the widest it can read; the text is right-aligned in it, so the track never moves
    private void UpdateVolumeText(float? volume)
    {
        var value = &volumeNode->AtkResNode;
        if (value->Width == 0)
        {
            volumeNode->SetText("100%");
            value->SetWidth((ushort)Math.Ceiling(Measure(volumeNode)));
            lastVolumeText = "";
        }
        if (volume is not { } level) return;

        var text = $"{MathF.Round(level * 100)}%";
        if (text == lastVolumeText) return;
        volumeNode->SetText(text);
        lastVolumeText = text;
    }

    // Play/pause is as wide as the wider of "II" and ">" with its text centred, so the group keeps its
    // size and nothing beside it shifts when it flips
    private void UpdateButtonTexts(MediaSnapshot song)
    {
        var playing = song.IsPlaying ? 1 : 0;
        if (playing == buttonsPlaying) return;
        for (var i = 0; i < Buttons.Length; i++)
        {
            var node  = (AtkTextNode*)buttonNodes[i];
            var width = 0f;
            if (Buttons[i] == MediaAction.TogglePlayPause)
            {
                node->SetText(ButtonText(Buttons[i], true));
                width = Measure(node);
                node->SetText(ButtonText(Buttons[i], false));
                width = Math.Max(width, Measure(node));
            }
            node->SetText(ButtonText(Buttons[i], song.IsPlaying));
            if (width == 0f) width = Measure(node);
            node->AtkResNode.SetWidth((ushort)Math.Ceiling(width));
        }
        buttonsPlaying = playing;
    }

    private void PlaceButtons(AtkTextNode* template, bool shown, float x, float y, ushort height)
    {
        var hover = new ByteColor
        {
            R = (byte)(Theme.ColGold.X * 255), G = (byte)(Theme.ColGold.Y * 255),
            B = (byte)(Theme.ColGold.Z * 255), A = 255,
        };
        for (var i = 0; i < Buttons.Length; i++)
        {
            var node = (AtkTextNode*)buttonNodes[i];
            node->AtkResNode.ToggleVisibility(shown);
            if (!shown) continue;
            node->AtkResNode.SetPositionFloat(x, y);
            node->AtkResNode.SetHeight(height);
            node->TextColor = i == hoveredButton ? hover : template->TextColor;
            x += node->AtkResNode.Width + ButtonSpacing;
        }
    }

    private static string ButtonText(MediaAction action, bool playing) => action switch
    {
        MediaAction.Previous => "<<",
        MediaAction.Next     => ">>",
        _                    => playing ? "II" : ">",
    };

    private void UpdateGauge(AtkComponentGaugeBar* gauge, float progress)
    {
        var value = gauge->MinValue + (int)((gauge->MaxValue - gauge->MinValue) * progress);
        if (value == lastGauge) return;
        // Second value is the rested EXP overlay, which means nothing for a song
        gauge->SetGaugeValue(value, 0, true);
        lastGauge = value;
    }

    // Runs every update, since the game shows the moon again whenever rested EXP changes
    private void UpdateMoon(AtkUnitBase* addon, float gaugeRight)
    {
        moonNode = FindMoon(addon, gaugeRight);
        if (moonNode == null || !moonNode->IsVisible()) return;
        moonNode->ToggleVisibility(false);
        moonHidden = true;
    }

    private void ShowMoon()
    {
        if (moonHidden && moonNode != null) moonNode->ToggleVisibility(true);
        moonHidden = false;
    }

    // The rested EXP moon has no known node id, so take the right-most small graphic that reaches past
    // the bar's right end; the gauge's own parts live in its component and never show up here
    private static AtkResNode* FindMoon(AtkUnitBase* addon, float gaugeRight)
    {
        AtkResNode* best  = null;
        var         bestX = 0f;
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || node->Type != NodeType.Image) continue;
            if (node->Width is < MoonMinSize or > MoonMaxSize || node->Height is < MoonMinSize or > MoonMaxSize) continue;
            var x = PositionInRoot(addon, node).X;
            if (x + node->Width <= gaugeRight || (best != null && x <= bestX)) continue;
            best  = node;
            bestX = x;
        }
        return best;
    }

    // The game just rewrote the gauge with experience; force ours back on the next update
    private void OnRequestedUpdate(AddonEvent type, AddonArgs args) => lastGauge = -1;

    private void OnFinalize(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon != null) DestroyNodes(addon);
        overriding  = false;
        hiddenCount = 0;
        hiddenRows  = 0;
        lastGauge   = -1;
        moonNode    = null;
        moonHidden  = false;
    }

    private void Restore(AtkUnitBase* addon)
    {
        overriding = false;
        lastGauge  = -1;
        ShowHiddenText();
        ShowMoon();
        if (labelNode       != null) labelNode->AtkResNode.ToggleVisibility(false);
        if (timeNode        != null) timeNode->AtkResNode.ToggleVisibility(false);
        if (volumeNode      != null) volumeNode->AtkResNode.ToggleVisibility(false);
        foreach (var button in buttonNodes)
            if (button != 0) ((AtkResNode*)button)->ToggleVisibility(false);
        RequestRefresh(addon);
    }

    private static void RequestRefresh(AtkUnitBase* addon)
    {
        var stage = AtkStage.Instance();
        if (stage != null)
            addon->OnRequestedUpdate(stage->GetNumberArrayData(), stage->GetStringArrayData());
    }

    private static string BuildTime(MediaSnapshot song, float? preview) =>
        preview is { } fraction && song.HasTimeline
            ? $"{MediaSnapshot.FormatTime(song.Duration * fraction)} / {MediaSnapshot.FormatTime(song.Duration)}"
            : song.TimeText;

    private static string WithPaused(string time, MediaSnapshot song)
    {
        if (song.IsPlaying) return time;
        return time.Length > 0 ? $"{time} (Paused)" : "(Paused)";
    }

    // Trims to whole characters with a trailing "..." until the game's own measurement fits
    private static void FitText(AtkTextNode* node, string text, float maxWidth)
    {
        node->SetText(text);
        if (Measure(node) <= maxWidth) return;

        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            node->SetText(text[..mid].TrimEnd() + "...");
            if (Measure(node) <= maxWidth) lo = mid; else hi = mid - 1;
        }
        node->SetText(lo > 0 ? text[..lo].TrimEnd() + "..." : "");
    }

    // The text's width, and how far left of its end the middle of its "/" sits, measured with the game's
    // own font by trying the text up to either side of it; the middle of the text when there is none.
    // Leaves other text in the node, so the caller sets the real text afterwards.
    private static (float Width, float Anchor) MeasureAnchor(AtkTextNode* node, string time)
    {
        if (time.Length == 0) return (0f, 0f);
        node->SetText(time);
        var full  = Measure(node);
        var slash = time.IndexOf('/');
        if (slash < 0) return (full, full / 2f);

        node->SetText(time[..slash]);
        var before = Measure(node);
        node->SetText(time[..(slash + 1)]);
        var after = Measure(node);
        return (full, full - (before + after) / 2f);
    }

    private static float Measure(AtkTextNode* node)
    {
        ushort w = 0, h = 0;
        node->GetTextDrawSize(&w, &h);
        return w;
    }

    // Only the rows holding something of ours; runs every update, since the game shows the chain line
    // again whenever a chain starts. When the rows change, everything is shown again and the game
    // re-applies its own visibility before the new rows are hidden.
    private void HideVanillaText(AtkUnitBase* addon, float gaugeMid, bool top, bool bottom)
    {
        var rows = (top ? 1 : 0) | (bottom ? 2 : 0);
        if (hiddenCount > 0 && rows != hiddenRows)
        {
            ShowHiddenText();
            RequestRefresh(addon);
        }
        hiddenRows = rows;

        for (var i = 0; i < addon->UldManager.NodeListCount && hiddenCount < MaxHidden; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (!IsVanillaText(node) || !node->IsVisible()) continue;
            if (!(IsAbove(addon, node, gaugeMid) ? top : bottom)) continue;
            node->ToggleVisibility(false);
            hiddenText[hiddenCount++] = (nint)node;
        }
    }

    private void ShowHiddenText()
    {
        for (var i = 0; i < hiddenCount; i++)
            ((AtkResNode*)hiddenText[i])->ToggleVisibility(true);
        hiddenCount = 0;
    }

    // How far right of the bar's left edge the game's own job/level/EXP text reaches, going by where each
    // node draws its text within its box; zero while none of it is drawn. The row also holds empty,
    // bar-wide text boxes, so empty ones and any whose text starts past the bar's middle are skipped:
    // the job line always sits on the left.
    private static float JobLineWidth(AtkUnitBase* addon, float gaugeMid, float x, float width)
    {
        var right = x;
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (!IsVanillaText(node) || !node->IsVisible() || IsAbove(addon, node, gaugeMid)) continue;
            var text  = node->GetAsAtkTextNode();
            var drawn = Measure(text);
            if (drawn < 1f) continue;

            var left  = PositionInRoot(addon, node).X;
            var start = text->AlignmentType switch
            {
                AlignmentType.TopRight or AlignmentType.Right or AlignmentType.BottomRight => left + node->Width - drawn,
                AlignmentType.Top or AlignmentType.Center or AlignmentType.Bottom          => left + (node->Width - drawn) / 2f,
                _                                                                          => left,
            };
            if (start > x + width / 2f) continue;
            right = Math.Max(right, start + drawn);
        }
        return Math.Min(right - x, width);
    }

    private static bool IsAbove(AtkUnitBase* addon, AtkResNode* node, float gaugeMid) =>
        PositionInRoot(addon, node).Y + node->Height / 2f < gaugeMid;

    private void CreateNodes(AtkUnitBase* addon, AtkTextNode* template)
    {
        labelNode = CreateTextNode(addon, template, LabelNodeId, AlignmentType.Left);
        timeNode  = CreateTextNode(addon, template, TimeNodeId,  AlignmentType.Left);
        for (var i = 0; i < Buttons.Length; i++)
            buttonNodes[i] = (nint)CreateTextNode(addon, template, ButtonNodeId + (uint)i,
                Buttons[i] == MediaAction.TogglePlayPause ? AlignmentType.Center : AlignmentType.Left);
        volumeNode = CreateTextNode(addon, template, VolumeNodeId, AlignmentType.Right);
        addon->UldManager.UpdateDrawNodeList();
        lastLabel = lastTime = lastVolumeText = "";
        lastLabelWidth = -1;
        buttonsPlaying = -1;
    }

    private static AtkTextNode* CreateTextNode(AtkUnitBase* addon, AtkTextNode* template, uint id, AlignmentType alignment)
    {
        var node = IMemorySpace.GetUISpace()->Create<AtkTextNode>();
        node->AtkResNode.Type      = NodeType.Text;
        node->AtkResNode.NodeId    = id;
        node->AtkResNode.NodeFlags = NodeFlags.AnchorLeft | NodeFlags.AnchorTop | NodeFlags.Enabled;
        node->AtkResNode.Color     = template->AtkResNode.Color;
        node->TextColor            = template->TextColor;
        node->EdgeColor            = template->EdgeColor;
        node->BackgroundColor      = template->BackgroundColor;
        node->FontType             = template->FontType;
        node->FontSize             = template->FontSize;
        node->TextFlags            = template->TextFlags;
        node->LineSpacing          = template->LineSpacing;
        node->AlignmentFontType    = template->AlignmentFontType;
        node->AlignmentType        = alignment;

        // Append as the last sibling under the root so the game's own node order is untouched
        var root = addon->RootNode;
        var last = root->ChildNode;
        if (last == null)
        {
            root->ChildNode = (AtkResNode*)node;
        }
        else
        {
            while (last->PrevSiblingNode != null) last = last->PrevSiblingNode;
            last->PrevSiblingNode            = (AtkResNode*)node;
            node->AtkResNode.NextSiblingNode = last;
        }
        node->AtkResNode.ParentNode = root;
        return node;
    }

    private void DestroyNodes(AtkUnitBase* addon)
    {
        DestroyTextNode(addon, ref labelNode);
        DestroyTextNode(addon, ref timeNode);
        DestroyTextNode(addon, ref volumeNode);
        for (var i = 0; i < Buttons.Length; i++)
        {
            var button = (AtkTextNode*)buttonNodes[i];
            DestroyTextNode(addon, ref button);
            buttonNodes[i] = 0;
        }
        addon->UldManager.UpdateDrawNodeList();
    }

    private static void DestroyTextNode(AtkUnitBase* addon, ref AtkTextNode* node)
    {
        if (node == null) return;
        var res = &node->AtkResNode;
        if (res->PrevSiblingNode != null) res->PrevSiblingNode->NextSiblingNode = res->NextSiblingNode;
        if (res->NextSiblingNode != null) res->NextSiblingNode->PrevSiblingNode = res->PrevSiblingNode;
        if (addon->RootNode != null && addon->RootNode->ChildNode == res) addon->RootNode->ChildNode = res->PrevSiblingNode;
        res->Destroy(false);
        IMemorySpace.Free(node, (ulong)sizeof(AtkTextNode));
        node = null;
    }

    private static Vector2 PositionInRoot(AtkUnitBase* addon, AtkResNode* node)
    {
        var pos = Vector2.Zero;
        for (var current = node; current != null && current != addon->RootNode; current = current->ParentNode)
            pos += new Vector2(current->X, current->Y);
        return pos;
    }

    // Falls back to the whole component when the track is not found
    private static (float Top, float Bottom) VisibleBar(AtkUnitBase* addon, AtkComponentGaugeBar* gauge, float gaugeY, float gaugeHeight)
    {
        var track = FindTrack(gauge);
        if (track == null) return (gaugeY, gaugeY + gaugeHeight);
        var top = PositionInRoot(addon, track).Y;
        return (top, top + track->Height);
    }

    // The widest visible image inside the gauge is its track
    private static AtkResNode* FindTrack(AtkComponentGaugeBar* gauge)
    {
        AtkResNode* track = null;
        var manager = &gauge->AtkComponentBase.UldManager;
        for (var i = 0; i < manager->NodeListCount; i++)
        {
            var node = manager->NodeList[i];
            if (node == null || !node->IsVisible() || node->Type is not (NodeType.Image or NodeType.NineGrid)) continue;
            if (track == null || node->Width > track->Width) track = node;
        }
        return track;
    }

    private static AtkComponentGaugeBar* FindGauge(AtkUnitBase* addon)
    {
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || (int)node->Type < 1000) continue;
            var component = ((AtkComponentNode*)node)->Component;
            if (component != null && component->GetComponentType() == ComponentType.GaugeBar)
                return (AtkComponentGaugeBar*)component;
        }
        return null;
    }

    // The right-most text on the chosen side of the gauge. Below, that is the EXP text, which doubles
    // as the style template; above, it is the chain EXP line we take the place of.
    private static AtkTextNode* FindVanillaText(AtkUnitBase* addon, float gaugeMid, bool above)
    {
        AtkTextNode* best = null;
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (!IsVanillaText(node) || IsAbove(addon, node, gaugeMid) != above) continue;
            var text = node->GetAsAtkTextNode();
            if (best == null || node->X > best->AtkResNode.X) best = text;
        }
        return best;
    }

    private static bool IsVanillaText(AtkResNode* node) =>
        node != null && node->Type == NodeType.Text && node->NodeId is not (>= LabelNodeId and <= LastNodeId);

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(AddonEvent.PostUpdate,          AddonName, OnUpdate);
        addonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, AddonName, OnRequestedUpdate);
        addonLifecycle.UnregisterListener(AddonEvent.PreFinalize,         AddonName, OnFinalize);

        var addon = (AtkUnitBase*)gameGui.GetAddonByName(AddonName).Address;
        if (addon == null) return;
        if (overriding) Restore(addon);
        DestroyNodes(addon);
    }
}
