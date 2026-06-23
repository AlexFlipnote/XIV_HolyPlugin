using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using HoliestFluffiness.Handlers;

namespace HoliestFluffiness.Windows;

public sealed class NearbyWindow : Window, IDisposable
{
    private readonly Configuration   config;
    private readonly NearbyHandler   handler;
    private readonly IObjectTable    objectTable;
    private readonly ITargetManager  targetManager;
    private readonly ICondition      condition;
    private readonly ICommandManager commandManager;
    private readonly IGameGui        gameGui;

    private string searchText      = string.Empty;
    private bool   hoveredTargeterRow;
    private bool   hoveredNearbyRow;
    private bool   historyOpen;

    // ── Colours (matching HF theme) ───────────────────────────────────────────
    private static readonly Vector4 ColBg        = new(24 / 255f,  24 / 255f,  24 / 255f, 1f);
    private static readonly Vector4 ColBgDeep    = new(18 / 255f,  18 / 255f,  18 / 255f, 1f);
    private static readonly Vector4 ColSection   = new(40 / 255f,  40 / 255f,  40 / 255f, 1f);
    private static readonly Vector4 ColWhite     = new(249/255f, 248/255f, 244/255f, 1f);
    private static readonly Vector4 ColDim       = new(249/255f, 248/255f, 244/255f, 0.45f);
    private static readonly Vector4 ColGold      = new(235/255f, 230/255f, 114/255f, 1f);
    private static readonly Vector4 ColGoldMid   = new(235/255f, 230/255f, 114/255f, 0.35f);
    private static readonly Vector4 ColGoldSub   = new(235/255f, 230/255f, 114/255f, 0.18f);
    private static readonly Vector4 ColTargeter  = new(235/255f, 130/255f,  80/255f, 1f);
    private static readonly Vector4 ColHistory   = new(235/255f, 130/255f,  80/255f, 0.4f);

    public NearbyWindow(
        Configuration config, NearbyHandler handler, IObjectTable objectTable,
        ITargetManager targetManager, ICondition condition, ICommandManager commandManager, IGameGui gameGui)
        : base("Nearby Players##HFNearby")
    {
        this.config         = config;
        this.handler        = handler;
        this.objectTable    = objectTable;
        this.targetManager  = targetManager;
        this.condition      = condition;
        this.commandManager = commandManager;
        this.gameGui        = gameGui;

        Size          = new Vector2(500, 430);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override bool DrawConditions()
    {
        if (!config.NearbyEnabled) return false;
        if (config.NearbyHideInCombat && condition[ConditionFlag.InCombat]) return false;
        if (config.NearbyHideInDuty && (
            condition[ConditionFlag.BoundByDuty] ||
            condition[ConditionFlag.BoundByDuty56] ||
            condition[ConditionFlag.BoundByDuty95])) return false;
        return true;
    }

    public override void PreDraw()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg,             ColBg);
        ImGui.PushStyleColor(ImGuiCol.Text,                 ColWhite);
        ImGui.PushStyleColor(ImGuiCol.TitleBg,              ColBg);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive,        ColBg);
        ImGui.PushStyleColor(ImGuiCol.FrameBg,              ColBgDeep);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered,       ColSection);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,          ColBgDeep);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab,        ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, ColGold);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive,  ColGold);
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip,           ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered,    ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive,     ColGold);
        ImGui.PushStyleColor(ImGuiCol.Border,               ColGoldMid);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding,   4f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(14);
        ImGui.PopStyleVar(2);
    }

    public override void Draw()
    {
        DrawNearbyTable();
        DrawFooter();

        if (historyOpen)
            DrawAttachedHistoryWindow();
    }

    public void DrawMarkers()
    {
        if (!config.NearbyMarkTargeting || !config.NearbyShowTargeters) return;

        var dl   = ImGui.GetBackgroundDrawList();
        var col  = ImGui.GetColorU32(config.NearbyMarkTargetingColour);
        var size = (float)config.NearbyMarkTargetingSize;

        foreach (var t in handler.CurrentTargeters)
        {
            var obj = objectTable.FirstOrDefault(o => o.GameObjectId == t.GameObjectId);
            if (obj == null) continue;
            if (!gameGui.WorldToScreen(obj.Position, out var screen)) continue;

            dl.PushClipRect(ImGuiHelpers.MainViewport.Pos, ImGuiHelpers.MainViewport.Pos + ImGuiHelpers.MainViewport.Size, false);
            dl.AddCircleFilled(new Vector2(screen.X, screen.Y), size, col, 100);
            dl.PopClipRect();
        }
    }

    // ── Nearby table ──────────────────────────────────────────────────────────

    private void DrawNearbyTable()
    {
        var footerH = ImGui.GetStyle().ItemSpacing.Y + ImGui.GetFrameHeightWithSpacing();
        var tableH  = ImGui.GetContentRegionAvail().Y - footerH;

        var tableFlags = ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH |
                         ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Hideable |
                         ImGuiTableFlags.Reorderable;

        if (!ImGui.BeginTable("##nearby", 5, tableFlags, new Vector2(0, tableH)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Name",  ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoHide, 3f);
        ImGui.TableSetupColumn("Job",   ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Lv",    ImGuiTableColumnFlags.WidthStretch, 0.6f);
        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("FC",    ImGuiTableColumnFlags.WidthStretch, 1f);

        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, ColSection);
        ImGui.PushStyleColor(ImGuiCol.Text,          ColGold);
        ImGui.TableHeadersRow();
        ImGui.PopStyleColor(2);

        var targeterIds = handler.CurrentTargeters.Select(t => t.GameObjectId).ToHashSet();

        var source = string.IsNullOrEmpty(searchText)
            ? handler.NearbyPlayers
            : handler.NearbyPlayers.Where(p =>
                p.Name.Contains(searchText,       StringComparison.OrdinalIgnoreCase) ||
                p.HomeWorld.Contains(searchText,  StringComparison.OrdinalIgnoreCase) ||
                p.CompanyTag.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                p.JobAbbr.Contains(searchText,    StringComparison.OrdinalIgnoreCase));

        // Targeters float to top; stable so existing sort order is preserved within each group
        var sorted = source.OrderByDescending(p => targeterIds.Contains(p.GameObjectId)).ToList();

        var prevHoveredNearby = hoveredNearbyRow;
        hoveredNearbyRow = false;

        foreach (var p in sorted)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            var rowMin     = ImGui.GetCursorScreenPos() with { X = ImGui.GetWindowPos().X };
            var rowMax     = new Vector2(ImGui.GetWindowPos().X + ImGui.GetWindowSize().X, rowMin.Y + ImGui.GetTextLineHeightWithSpacing());
            var rowHovered = ImGui.IsMouseHoveringRect(rowMin, rowMax);
            var isTargeter = targeterIds.Contains(p.GameObjectId);

            if (rowHovered)
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(ColGoldSub));

            var col = p.IsParty   ? config.NearbyColParty
                    : p.IsFriend  ? config.NearbyColFriend
                    : p.IsLocalFc ? config.NearbyColLocalFc
                    : ColWhite;

            ImGui.PushStyleColor(ImGuiCol.Text, col);
            ImGui.TextUnformatted(isTargeter ? $"+ {p.Name}" : p.Name);
            ImGui.PopStyleColor();

            if (rowHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                ImGui.OpenPopup($"##ctx{p.GameObjectId}");
            DrawContextMenu(p.Name, p.HomeWorld, p.GameObjectId, forTargeter: false);

            if (rowHovered)
            {
                hoveredNearbyRow = true;
                var focusObj = objectTable.FirstOrDefault(o => o.GameObjectId == p.GameObjectId);
                if (focusObj != null) targetManager.FocusTarget = focusObj;
            }

            ImGui.TableNextColumn();
            ImGui.PushStyleColor(ImGuiCol.Text, ColDim);
            ImGui.TextUnformatted(p.JobAbbr);
            ImGui.PopStyleColor();

            ImGui.TableNextColumn();
            ImGui.PushStyleColor(ImGuiCol.Text, ColDim);
            ImGui.TextUnformatted(p.Level.ToString());
            ImGui.PopStyleColor();

            ImGui.TableNextColumn();
            ImGui.PushStyleColor(ImGuiCol.Text, ColDim);
            ImGui.TextUnformatted(p.HomeWorld);
            ImGui.PopStyleColor();

            ImGui.TableNextColumn();
            ImGui.PushStyleColor(ImGuiCol.Text, ColDim);
            ImGui.TextUnformatted(p.CompanyTag);
            ImGui.PopStyleColor();
        }

        if (prevHoveredNearby && !hoveredNearbyRow)
            targetManager.FocusTarget = null;

        ImGui.EndTable();
    }

    // ── Footer ────────────────────────────────────────────────────────────────

    private void DrawFooter()
    {
        const string historyLabel = "Target History";
        var buttonW   = ImGui.CalcTextSize(historyLabel).X + ImGui.GetStyle().FramePadding.X * 2;
        var hasActive = handler.CurrentTargeters.Count > 0;

        ImGui.PushStyleColor(ImGuiCol.Border, ColGoldMid);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - buttonW - ImGui.GetStyle().ItemSpacing.X);
        ImGui.InputTextWithHint("##nearbysearch", $"Search... ({handler.NearbyPlayers.Count} nearby)", ref searchText, 64);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();

        ImGui.SameLine();

        var isActive = historyOpen || hasActive;
        if (isActive)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        ColGoldSub);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColGoldMid);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  ColGold);
            ImGui.PushStyleColor(ImGuiCol.Text,          ColGold);
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        ColBgDeep);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColSection);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  ColSection);
            ImGui.PushStyleColor(ImGuiCol.Text,          ColDim);
        }
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        if (ImGui.Button(historyLabel))
            ToggleHistory();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);
    }

    private void ToggleHistory() => historyOpen = !historyOpen;

    // ── Attached history window ───────────────────────────────────────────────

    private void DrawAttachedHistoryWindow()
    {
        var mainPos  = ImGui.GetWindowPos();
        var mainSize = ImGui.GetWindowSize();

        ImGui.SetNextWindowPos(new Vector2(mainPos.X + mainSize.X + 2, mainPos.Y), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(275, mainSize.Y), ImGuiCond.Always);

        ImGui.PushStyleColor(ImGuiCol.WindowBg,      ColBg);
        ImGui.PushStyleColor(ImGuiCol.Border,        ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.Text,          ColWhite);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,   ColBgDeep);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, ColGoldMid);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 10));

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar    |
            ImGuiWindowFlags.NoResize      |
            ImGuiWindowFlags.NoMove        |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing;

        if (ImGui.Begin("##nearbyhistorypanel", flags))
            DrawHistoryPanel();
        ImGui.End();

        ImGui.PopStyleColor(5);
        ImGui.PopStyleVar(2);
    }

    // ── History panel content ─────────────────────────────────────────────────

    private void DrawHistoryPanel()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ColGold);
        ImGui.TextUnformatted("Target History");
        ImGui.PopStyleColor();
        ImGui.Separator();
        ImGui.Spacing();

        var current  = handler.CurrentTargeters;
        var previous = handler.PreviousTargeters;

        var btnH     = ImGui.GetStyle().ItemSpacing.Y + ImGui.GetFrameHeightWithSpacing();
        var scrollH  = ImGui.GetContentRegionAvail().Y - btnH;

        ImGui.BeginChild("##historyscroll", new Vector2(0, scrollH));

        var prevHovered    = hoveredTargeterRow;
        hoveredTargeterRow = false;

        if (current.Count == 0 && previous.Count == 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ColDim);
            ImGui.TextUnformatted("No one is targeting you.");
            ImGui.PopStyleColor();
        }
        else
        {
            if (current.Count > 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ColGold);
                ImGui.TextUnformatted("Currently targeting you");
                ImGui.PopStyleColor();
                ImGui.Separator();
                foreach (var t in current)
                    DrawTargeterRow(t, false);
            }

            if (previous.Count > 0)
            {
                if (current.Count > 0) ImGui.Spacing();
                ImGui.PushStyleColor(ImGuiCol.Text, ColDim);
                ImGui.TextUnformatted("Previously");
                ImGui.PopStyleColor();
                ImGui.Separator();
                foreach (var t in previous)
                    DrawTargeterRow(t, true);
            }
        }

        if (prevHovered && !hoveredTargeterRow)
            targetManager.FocusTarget = null;

        ImGui.EndChild();

        var clearW = ImGui.CalcTextSize("Clear history").X + ImGui.GetStyle().FramePadding.X * 2 + 16;
        ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - clearW) * 0.5f);
        ImGui.PushStyleColor(ImGuiCol.Button,        ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  ColGold);
        ImGui.PushStyleColor(ImGuiCol.Text,          ColGold);
        if (ImGui.Button("Clear history##histclear", new Vector2(clearW, 0)))
            handler.ClearTargeterHistory();
        ImGui.PopStyleColor(4);
    }

    private void DrawTargeterRow(Targeter t, bool isHistory)
    {
        var elapsed = DateTime.Now - t.When;
        var timeStr = elapsed.TotalSeconds < 60 ? "just now"
                    : elapsed.TotalMinutes < 60 ? $"{(int)elapsed.TotalMinutes}m ago"
                    : $"{(int)elapsed.TotalHours}h ago";

        ImGui.PushStyleColor(ImGuiCol.Text, isHistory ? ColHistory : ColTargeter);
        ImGui.TextUnformatted($"{t.Name} @ {t.HomeWorld}");
        ImGui.PopStyleColor();
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup($"##ctx{t.GameObjectId}");

        var timeW  = ImGui.CalcTextSize(timeStr).X;
        var rightX = ImGui.GetWindowPos().X + ImGui.GetWindowSize().X - ImGui.GetStyle().WindowPadding.X - timeW;
        ImGui.GetWindowDrawList().AddText(
            new Vector2(rightX, ImGui.GetItemRectMin().Y),
            ImGui.GetColorU32(ColDim), timeStr);

        if (ImGui.IsItemHovered())
        {
            hoveredTargeterRow = true;
            var obj = objectTable.FirstOrDefault(o => o.GameObjectId == t.GameObjectId);
            if (obj != null) targetManager.FocusTarget = obj;
        }

        DrawContextMenu(t.Name, t.HomeWorld, t.GameObjectId, forTargeter: true);
    }

    // ── Context menu ──────────────────────────────────────────────────────────

    private void DrawContextMenu(string name, string world, ulong gameObjectId, bool forTargeter)
    {
        if (!ImGui.BeginPopup($"##ctx{gameObjectId}")) return;

        ImGui.TextDisabled($"{name} @ {world}");
        ImGui.Separator();

        var obj = objectTable.FirstOrDefault(o => o.GameObjectId == gameObjectId);

        if (!forTargeter)
        {
            if (ImGui.MenuItem("Examine") && obj != null)
                OpenExamine(obj.EntityId);

            if (ImGui.MenuItem("View Adventure Plate") && obj != null)
                OpenAdventurePlate(obj);
        }

        if (ImGui.MenuItem("Target") && obj != null)
            targetManager.Target = obj;

        if (ImGui.MenuItem("Focus Target") && obj != null)
            targetManager.FocusTarget = obj;

        if (ImGui.MenuItem("Send Tell"))
            SendTell(name, world);

        if (!forTargeter)
        {
            if (ImGui.MenuItem("Invite to Party"))
                commandManager.ProcessCommand($"/partycmd add {name}@{world}");

            if (ImGui.MenuItem("Add to Blacklist"))
                commandManager.ProcessCommand($"/blacklist add {name}@{world}");

            if (ImGui.MenuItem("Find on Map") && obj != null)
                OpenOnMap(obj.Position);
        }

        if (ImGui.MenuItem("Search on Lodestone"))
            Util.OpenLink($"https://eu.finalfantasyxiv.com/lodestone/character/?q={Uri.EscapeDataString(name)}&worldname={Uri.EscapeDataString(world)}");

        ImGui.EndPopup();
    }

    private static unsafe void OpenExamine(uint entityId)
        => AgentInspect.Instance()->ExamineCharacter(entityId);

    private static unsafe void OpenAdventurePlate(Dalamud.Game.ClientState.Objects.Types.IGameObject obj)
        => AgentCharaCard.Instance()->OpenCharaCard((GameObject*)obj.Address);

    private static unsafe void SendTell(string name, string world)
        => UIModule.Instance()->ProcessChatBoxEntry(Utf8String.FromString($"/tell {name}@{world} "));

    private static unsafe void OpenOnMap(Vector3 position)
    {
        var agent = AgentMap.Instance();
        agent->SetFlagMapMarker(agent->CurrentTerritoryId, agent->CurrentMapId, position);
        agent->OpenMap(agent->CurrentMapId, agent->CurrentTerritoryId);
    }
}
