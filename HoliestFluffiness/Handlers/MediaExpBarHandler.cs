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
// song progress, and two text nodes of our own (styled as a copy of the EXP text) show the song on the
// left and the time on the right, in place of either the chain EXP line or the job/level/EXP line.
// When nothing is playing the addon is asked to redraw from the game's data, which restores the gauge.
// _Exp ignores the mouse, so clicks go to a borderless ImGui window laid over the bar instead: the song
// row runs the same action as the server info bar entry, the bar itself seeks, and three more
// text nodes on the other row act as previous, play/pause and next buttons, with the rested EXP moon hidden.
public sealed unsafe class MediaExpBarHandler : IDisposable
{
    private const string AddonName    = "_Exp";
    private const uint   LabelNodeId  = 0x48460001;
    private const uint   TimeNodeId   = 0x48460002;
    private const uint   ButtonNodeId = 0x48460003;
    private const int    MaxHidden   = 8;
    private const float  TimeGap     = 12f;
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

    // How long a released scrub keeps showing its target, so the bar does not snap back
    // to the old position before the player reports the new one
    private static readonly TimeSpan SeekHold = TimeSpan.FromSeconds(1.5);

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
    private int          hiddenCount;
    private MediaBarText hiddenMode;
    private bool         overriding;
    private int          lastGauge = -1;
    private string       lastTime  = "";
    private string       lastLabel = "";
    private float        lastLabelWidth;
    // Distance from the time row's right edge to the "/" in the time, in node units
    private float        timeAnchor;
    private AtkResNode*  moonNode;
    private bool         moonHidden;
    // Set by the overlay, coloured by the next addon update; -1 when no button is hovered
    private int          hoveredButton = -1;
    private int          buttonsPlaying = -1;

    private bool     scrubbing;
    private float    scrubFraction;
    private DateTime seekShownUntil;

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
        scrubbing     = false;
        hoveredButton = -1;
    }

    // One window spans the song row, the bar and the button. ImGui gives an overlap to the item submitted
    // first, so the bar and button go in before the row.
    private bool TryDrawOverlay()
    {
        if (!overriding || timeNode == null || !timeNode->AtkResNode.IsVisible() || Song is not { } song) return false;
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(AddonName).Address;
        if (addon == null || !Common.IsAddonVisible(addon)) return false;

        var scale    = addon->Scale;
        var viewport = ImGui.GetMainViewport().Pos;

        // The time node spans the whole row, so it doubles as the click area
        var row    = &timeNode->AtkResNode;
        var rowMin = Common.GetNodePosition(row) + viewport;
        var rowMax = rowMin + new Vector2(row->Width, row->Height) * scale;

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

        var min = rowMin;
        var max = rowMax;
        if (canSeek) { min = Vector2.Min(min, seekMin); max = Vector2.Max(max, seekMax); }
        for (var i = 0; showButtons && i < Buttons.Length; i++)
        {
            var node   = &((AtkTextNode*)buttonNodes[i])->AtkResNode;
            var bMin   = Common.GetNodePosition(node) + viewport - buttonPad;
            var bMax   = bMin + buttonPad * 2 + new Vector2(node->Width, node->Height) * scale;
            buttonRects[i] = (bMin, bMax);
            min = Vector2.Min(min, bMin);
            max = Vector2.Max(max, bMax);
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

            if (clickAction != null && HitArea("##hfmediaexpbarclick", rowMin, rowMax, out _)) clickAction();
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
        var mode      = config.MediaExpBarText;

        var template = FindVanillaText(addon, gaugeMid, above: false);
        if (template == null) return;

        overriding = true;
        var preview = SeekPreview;
        UpdateGauge(gauge, preview ?? song.Progress);
        UpdateMoon(addon, gaugePos.X + gaugeNode->AtkResNode.Width);

        if (labelNode == null) CreateNodes(addon, template);

        if (hiddenCount > 0 && hiddenMode != mode)
        {
            // Switched modes: put the other line back and let the game re-apply its own visibility
            ShowHiddenText();
            RequestRefresh(addon);
        }
        hiddenMode = mode;
        HideVanillaText(addon, gaugeMid, above: mode == MediaBarText.Chain);

        // The gauge component is much taller than the thin bar it draws, so measure the bar itself and
        // mirror the job line: the song sits as far above the bar as the job line sits below it
        var height   = template->AtkResNode.Height;
        var jobY     = PositionInRoot(addon, &template->AtkResNode).Y;
        var (barTop, barBottom) = VisibleBar(addon, gauge, gaugePos.Y, gaugeNode->AtkResNode.Height);
        ushort drawW = 0, drawH = 0;
        template->GetTextDrawSize(&drawW, &drawH);
        var chainY = barTop - (jobY - barBottom) - drawH - ChainLift;
        var y      = mode == MediaBarText.Chain ? chainY : jobY;
        var x     = gaugePos.X;
        var width = gaugeNode->AtkResNode.Width;

        // The buttons anchor on the time without " (Paused)", so pausing does not slide them sideways
        var plainTime = BuildTime(song, preview);
        var time      = WithPaused(plainTime, song);
        if (time != lastTime)
        {
            timeAnchor = MeasureAnchor(timeNode, plainTime);
            timeNode->SetText(time);
            lastTime = time;
        }
        timeNode->AtkResNode.SetPositionFloat(x, y);
        timeNode->AtkResNode.SetWidth(width);
        timeNode->AtkResNode.SetHeight(height);

        ushort timeWidth = 0, unused = 0;
        if (time.Length > 0) timeNode->GetTextDrawSize(&timeWidth, &unused);
        var labelWidth = Math.Max(0f, width - timeWidth - (time.Length > 0 ? TimeGap : 0));
        if (song.Display != lastLabel || Math.Abs(labelWidth - lastLabelWidth) > 0.5f)
        {
            FitText(labelNode, song.Display, labelWidth);
            lastLabel      = song.Display;
            lastLabelWidth = labelWidth;
        }
        labelNode->AtkResNode.SetPositionFloat(x, y);
        labelNode->AtkResNode.SetWidth((ushort)labelWidth);
        labelNode->AtkResNode.SetHeight(height);

        labelNode->AtkResNode.ToggleVisibility(true);
        timeNode->AtkResNode.ToggleVisibility(true);

        // The buttons take the row the song text left free, under or over the time's "/"
        UpdateButtons(song, template, x + width - timeAnchor, mode == MediaBarText.Chain ? jobY : chainY, height);
    }

    // Play/pause sits centred on the anchor with previous and next either side of it, so the group
    // does not shift when the middle one flips between "II" and ">"
    private void UpdateButtons(MediaSnapshot song, AtkTextNode* template, float centerX, float y, ushort height)
    {
        var playing = song.IsPlaying ? 1 : 0;
        if (playing != buttonsPlaying)
        {
            for (var i = 0; i < Buttons.Length; i++)
            {
                var node = (AtkTextNode*)buttonNodes[i];
                node->SetText(ButtonText(Buttons[i], song.IsPlaying));
                node->AtkResNode.SetWidth((ushort)Math.Ceiling(Measure(node)));
            }
            buttonsPlaying = playing;
        }

        var prev = &((AtkTextNode*)buttonNodes[0])->AtkResNode;
        var mid  = &((AtkTextNode*)buttonNodes[1])->AtkResNode;
        var midX = centerX - mid->Width / 2f;
        Span<float> xs = [midX - ButtonSpacing - prev->Width, midX, midX + mid->Width + ButtonSpacing];

        var hover = new ByteColor
        {
            R = (byte)(Theme.ColGold.X * 255), G = (byte)(Theme.ColGold.Y * 255),
            B = (byte)(Theme.ColGold.Z * 255), A = 255,
        };
        for (var i = 0; i < Buttons.Length; i++)
        {
            var node = (AtkTextNode*)buttonNodes[i];
            node->AtkResNode.SetPositionFloat(xs[i], y);
            node->AtkResNode.SetHeight(height);
            node->TextColor = i == hoveredButton ? hover : template->TextColor;
            node->AtkResNode.ToggleVisibility(true);
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
        if (labelNode != null) labelNode->AtkResNode.ToggleVisibility(false);
        if (timeNode  != null) timeNode->AtkResNode.ToggleVisibility(false);
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

    // How far left of the right-aligned text's end the middle of its "/" sits, measured with the game's
    // own font by trying the text up to either side of it; the middle of the text when there is none.
    // Leaves other text in the node, so the caller sets the real text afterwards.
    private static float MeasureAnchor(AtkTextNode* node, string time)
    {
        if (time.Length == 0) return 0f;
        node->SetText(time);
        var full  = Measure(node);
        var slash = time.IndexOf('/');
        if (slash < 0) return full / 2f;

        node->SetText(time[..slash]);
        var before = Measure(node);
        node->SetText(time[..(slash + 1)]);
        var after = Measure(node);
        return full - (before + after) / 2f;
    }

    private static float Measure(AtkTextNode* node)
    {
        ushort w = 0, h = 0;
        node->GetTextDrawSize(&w, &h);
        return w;
    }

    // Runs every update, since the game shows the chain line again whenever a chain starts
    private void HideVanillaText(AtkUnitBase* addon, float gaugeMid, bool above)
    {
        for (var i = 0; i < addon->UldManager.NodeListCount && hiddenCount < MaxHidden; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (!IsVanillaText(node) || !node->IsVisible() || IsAbove(addon, node, gaugeMid) != above) continue;
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

    private static bool IsAbove(AtkUnitBase* addon, AtkResNode* node, float gaugeMid) =>
        PositionInRoot(addon, node).Y + node->Height / 2f < gaugeMid;

    private void CreateNodes(AtkUnitBase* addon, AtkTextNode* template)
    {
        labelNode = CreateTextNode(addon, template, LabelNodeId, AlignmentType.Left);
        timeNode  = CreateTextNode(addon, template, TimeNodeId,  AlignmentType.Right);
        for (var i = 0; i < Buttons.Length; i++)
            buttonNodes[i] = (nint)CreateTextNode(addon, template, ButtonNodeId + (uint)i, AlignmentType.Left);
        addon->UldManager.UpdateDrawNodeList();
        lastLabel = lastTime = "";
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
        node != null && node->Type == NodeType.Text && node->NodeId is not (>= LabelNodeId and < ButtonNodeId + 3);

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
