using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace HoliestFluffiness.Handlers;

// Borrows the game's own experience bar (_Exp) while something is playing on the PC. The gauge shows
// song progress, and two text nodes of our own (styled as a copy of the EXP text) show the song on the
// left and the time on the right, in place of either the chain EXP line or the job/level/EXP line.
// When nothing is playing the addon is asked to redraw from the game's data, which restores the gauge.
// _Exp ignores the mouse, so clicks go to a borderless ImGui window laid over the song row instead;
// it runs the same action as the server info bar entry.
public sealed unsafe class MediaExpBarHandler : IDisposable
{
    private const string AddonName   = "_Exp";
    private const uint   LabelNodeId = 0x48460001;
    private const uint   TimeNodeId  = 0x48460002;
    private const int    MaxHidden   = 8;
    private const float  TimeGap     = 12f;
    // The game's text boxes carry more empty space above the glyphs than below, so a plain mirror of
    // the job line lands on the bar; tuned in game until the gap matches the one under the bar
    private const float  ChainLift   = 6f;

    private const ImGuiWindowFlags ClickAreaFlags =
        ImGuiWindowFlags.NoDecoration       | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings |
        ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav        | ImGuiWindowFlags.NoBringToFrontOnFocus;

    private readonly Configuration   config;
    private readonly MediaHandler    media;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameGui        gameGui;
    private readonly nint[]          hiddenText = new nint[MaxHidden];

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

    // Called from UiBuilder.Draw. The time node spans the whole row, so it doubles as the click area.
    public void DrawClickArea()
    {
        if (!overriding || clickAction == null || timeNode == null || !timeNode->AtkResNode.IsVisible()) return;
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(AddonName).Address;
        if (addon == null || !Common.IsAddonVisible(addon)) return;

        var node = &timeNode->AtkResNode;
        var pos  = Common.GetNodePosition(node) + ImGui.GetMainViewport().Pos;
        var size = new Vector2(node->Width, node->Height) * addon->Scale;
        if (size.X < 1 || size.Y < 1) return;

        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(size);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,    Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize,    Vector2.One);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        if (ImGui.Begin("##HFMediaExpBarClick", ClickAreaFlags))
        {
            if (ImGui.InvisibleButton("##hfmediaexpbarclick", size)) clickAction();
            if (ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        ImGui.End();
        ImGui.PopStyleVar(3);
    }

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
        UpdateGauge(gauge, song);

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
        var y     = mode == MediaBarText.Chain ? barTop - (jobY - barBottom) - drawH - ChainLift : jobY;
        var x     = gaugePos.X;
        var width = gaugeNode->AtkResNode.Width;

        var time = BuildTime(song);
        if (time != lastTime)
        {
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
    }

    private void UpdateGauge(AtkComponentGaugeBar* gauge, MediaSnapshot song)
    {
        var value = gauge->MinValue + (int)((gauge->MaxValue - gauge->MinValue) * song.Progress);
        if (value == lastGauge) return;
        // Second value is the rested EXP overlay, which means nothing for a song
        gauge->SetGaugeValue(value, 0, true);
        lastGauge = value;
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
    }

    private void Restore(AtkUnitBase* addon)
    {
        overriding = false;
        lastGauge  = -1;
        ShowHiddenText();
        if (labelNode != null) labelNode->AtkResNode.ToggleVisibility(false);
        if (timeNode  != null) timeNode->AtkResNode.ToggleVisibility(false);
        RequestRefresh(addon);
    }

    private static void RequestRefresh(AtkUnitBase* addon)
    {
        var stage = AtkStage.Instance();
        if (stage != null)
            addon->OnRequestedUpdate(stage->GetNumberArrayData(), stage->GetStringArrayData());
    }

    private static string BuildTime(MediaSnapshot song)
    {
        var time = song.TimeText;
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
        addon->UldManager.UpdateDrawNodeList();
        lastLabel = lastTime = "";
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

    // The widest visible image inside the gauge is its track; falls back to the whole component
    private static (float Top, float Bottom) VisibleBar(AtkUnitBase* addon, AtkComponentGaugeBar* gauge, float gaugeY, float gaugeHeight)
    {
        AtkResNode* track = null;
        var manager = &gauge->AtkComponentBase.UldManager;
        for (var i = 0; i < manager->NodeListCount; i++)
        {
            var node = manager->NodeList[i];
            if (node == null || !node->IsVisible() || node->Type is not (NodeType.Image or NodeType.NineGrid)) continue;
            if (track == null || node->Width > track->Width) track = node;
        }

        if (track == null) return (gaugeY, gaugeY + gaugeHeight);
        var top = PositionInRoot(addon, track).Y;
        return (top, top + track->Height);
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
        node != null && node->Type == NodeType.Text && node->NodeId is not (LabelNodeId or TimeNodeId);

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
