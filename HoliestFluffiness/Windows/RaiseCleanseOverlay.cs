using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace HoliestFluffiness.Windows;

// A trimmed down RezPls (Ottermandias): highlights party list members who are raised, being raised,
// or carry a cleansable debuff. Party list only, no in-world markers. Colours default to RezPls'.
public enum RaiseCleanseState { Raised = 0, Raising = 1, Redundant = 2, Cleanse = 3 }

public class RaiseCleanseOverlay : HfOverlayWindow
{
    public static readonly string[] StateNames = ["Already raised", "Being raised", "Redundant cast", "Needs cleanse"];

    // Session only, so a forgotten preview never survives a restart
    public bool              DebugEnabled;
    public RaiseCleanseState DebugState;

    private readonly Configuration config;
    private readonly IGameGui      gameGui;
    private readonly IObjectTable  objectTable;
    private readonly IClientState  clientState;
    private readonly HashSet<uint> cleansable;

    private static readonly HashSet<uint> RaiseStatuses = [148, 1140];

    private static readonly HashSet<uint> RaiseActions =
    [
        173,   // Resurrection (ACN, SMN, SCH)
        125,   // Raise (CNJ, WHM)
        3603,  // Ascend (AST)
        24287, // Egeiro (SGE)
        7523,  // Verraise (RDM)
        18317, // Angel Whisper (BLU)
        208, 4247, 4248, 24859, // Healer limit break 3s
        22345, 20730, // Bozja
        12996, // Eureka
    ];

    // ClassJob ids; RDM and BRD only learn theirs at 64 and 35
    private static readonly HashSet<uint> RaiseJobs   = [6, 24, 26, 27, 28, 33, 35, 36, 40];
    private static readonly HashSet<uint> CleanseJobs = [6, 23, 24, 28, 33, 36, 40];

    // One summary per raise target, rebuilt each frame into the same dictionary so drawing allocates nothing
    private struct TargetCasts
    {
        public IPlayerCharacter Lead;
        public float            LeadProgress;
        public bool             ByMe;
        public bool             ByOthers;
    }
    private readonly Dictionary<ulong, TargetCasts> casts = new();

    public RaiseCleanseOverlay(Configuration config, IGameGui gameGui, IObjectTable objectTable, IClientState clientState, IDataManager dataManager)
        : base("##HFRaiseCleanseOverlay")
    {
        this.config      = config;
        this.gameGui     = gameGui;
        this.objectTable = objectTable;
        this.clientState = clientState;
        cleansable = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>()
            .Where(s => s.CanDispel && !s.Name.IsEmpty)
            .Select(s => s.RowId)
            .ToHashSet();
    }

    public override void PreOpenCheck()
    {
        IsOpen = DebugEnabled || ((config.RaiseIndicatorEnabled || config.CleanseIndicatorEnabled) && !clientState.IsPvP);
    }

    public override unsafe void Draw()
    {
        if (objectTable.LocalPlayer is not { } me) return;

        var party = (AddonPartyList*)gameGui.GetAddonByName("_PartyList").Address;
        var hud   = AgentHUD.Instance();
        if (party == null || hud == null || !Common.IsAddonVisible(&party->AtkUnitBase)) return;
        var drawList = ImGui.GetWindowDrawList();

        if (DebugEnabled)
        {
            // Preview on the top row; the raise cast loops every two seconds so the fill can be seen moving
            var progress = DebugState == RaiseCleanseState.Raising ? (float)(ImGui.GetTime() % 2.0 / 2.0) : 1f;
            DrawState(drawList, party, 0, DebugState, DebugState == RaiseCleanseState.Raising ? me.Name.TextValue : null, progress);
            return;
        }

        var showRaise   = config.RaiseIndicatorEnabled;
        var showCleanse = config.CleanseIndicatorEnabled;
        if (config.RaiseCleanseOwnJobOnly)
        {
            var job   = me.ClassJob.RowId;
            var level = me.Level;
            showRaise   &= RaiseJobs.Contains(job)   && (job != 35 || level >= 64);
            showCleanse &= CleanseJobs.Contains(job) && (job != 23 || level >= 35);
        }
        if (!showRaise && !showCleanse) return;

        casts.Clear();
        if (showRaise) CollectRaiseCasts(me.GameObjectId);

        for (var i = 0; i < 8; i++)
        {
            var entityId = hud->PartyMembers[i].EntityId;
            if (entityId is 0 or 0xE0000000) continue;
            if (objectTable.SearchByEntityId(entityId) is not IBattleChara member) continue;

            if (member.IsDead)
            {
                if (!showRaise) continue;
                var hasRaise = HasAnyStatus(member, RaiseStatuses);
                casts.TryGetValue(member.GameObjectId, out var onMember);

                // Your raise is wasted if they already have one, or someone else got there first
                if (onMember.ByMe && (hasRaise || onMember.ByOthers))
                    DrawState(drawList, party, i, RaiseCleanseState.Redundant);
                else if (onMember.Lead != null)
                    DrawState(drawList, party, i, RaiseCleanseState.Raising, onMember.Lead.Name.TextValue, onMember.LeadProgress);
                else if (hasRaise)
                    DrawState(drawList, party, i, RaiseCleanseState.Raised);
            }
            else if (showCleanse && HasAnyStatus(member, cleansable))
            {
                DrawState(drawList, party, i, RaiseCleanseState.Cleanse);
            }
        }
    }

    // Raise casts in progress, keyed by the target they are aimed at
    private void CollectRaiseCasts(ulong meId)
    {
        foreach (var obj in objectTable.PlayerObjects)
        {
            if (obj is not IPlayerCharacter p || !p.IsCasting) continue;
            if (p.CastActionType != (byte)ActionType.Action || !RaiseActions.Contains(p.CastActionId)) continue;

            var progress = p.TotalCastTime > 0 ? Math.Clamp(p.CurrentCastTime / p.TotalCastTime, 0f, 1f) : 1f;
            casts.TryGetValue(p.CastTargetObjectId, out var t);
            if (t.Lead == null || progress > t.LeadProgress) { t.Lead = p; t.LeadProgress = progress; }
            if (p.GameObjectId == meId) t.ByMe = true;
            else                        t.ByOthers = true;
            casts[p.CastTargetObjectId] = t;
        }
    }

    private static bool HasAnyStatus(IBattleChara chara, HashSet<uint> ids)
    {
        foreach (var status in chara.StatusList)
            if (ids.Contains(status.StatusId)) return true;
        return false;
    }

    // Raising shows the caster's name instead of a fixed label; redundant and cleanse pulse for attention
    private unsafe void DrawState(ImDrawListPtr drawList, AddonPartyList* party, int idx, RaiseCleanseState state,
        string? caster = null, float progress = 1f)
    {
        var (color, label, pulse) = state switch
        {
            RaiseCleanseState.Raised    => (config.RaiseColRaised,    StateNames[0], false),
            RaiseCleanseState.Raising   => (config.RaiseColRaising,   caster ?? StateNames[1], false),
            RaiseCleanseState.Redundant => (config.RaiseColRedundant, StateNames[2], true),
            _                           => (config.RaiseColCleanse,   StateNames[3], true),
        };
        DrawMember(drawList, party, idx, color, label, progress, pulse);
    }

    private static unsafe void DrawMember(ImDrawListPtr drawList, AddonPartyList* party, int idx, Vector4 color, string label,
        float progress = 1f, bool pulse = false)
    {
        var glow = (AtkResNode*)party->PartyMembers[idx].TargetGlow;
        if (glow == null) return;

        // Same row rect as FoodCheckOverlay
        var scale = party->Scale;
        var min   = Common.GetNodePosition(glow) + new Vector2(5, 5) * scale + ImGui.GetMainViewport().Pos;
        var max   = min + (new Vector2(glow->Width, glow->Height) - new Vector2(8, 10)) * scale;
        Common.DrawHighlightRect(drawList, min, max, 10f * scale, color, label, pulse, scale, progress);
    }
}
