using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays.Common;
using FFXIVClientStructs.FFXIV.Component.GUI;
using static FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarUIIntermediate;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace HoliestFluffiness.Handlers;

// Fades hotbar actions that are above your level, which mostly shows up when synced down.
// Idea from VanillaPlus (MidoriKami); runs inside the game's own per-slot hotbar update.
public sealed unsafe class ActionFadeHandler : IDisposable
{
    private static readonly string[] Bars =
    [
        "_ActionBar", "_ActionBar01", "_ActionBar02", "_ActionBar03", "_ActionBar04", "_ActionBar05",
        "_ActionBar06", "_ActionBar07", "_ActionBar08", "_ActionBar09",
        "_ActionCross", "_ActionDoubleCrossL", "_ActionDoubleCrossR",
    ];

    private readonly Configuration config;
    private readonly IFramework    framework;
    private readonly IObjectTable  objectTable;
    private readonly IDataManager  data;
    private readonly IPluginLog    log;
    private readonly Hook<AddonActionBarBase.Delegates.UpdateHotbarSlot>? hook;

    // Last state we painted with, so turning a setting off puts every slot back once
    private bool wasActive;

    // Action id to the level it unlocks at, 0 for actions that never fade (role actions, unknown rows)
    private readonly Dictionary<uint, byte> actionLevels = [];

    public ActionFadeHandler(Configuration config, IGameInteropProvider gameInterop, IFramework framework,
        IObjectTable objectTable, IDataManager data, IPluginLog log)
    {
        this.config      = config;
        this.framework   = framework;
        this.objectTable = objectTable;
        this.data        = data;
        this.log         = log;

        try
        {
            hook = gameInterop.HookFromAddress<AddonActionBarBase.Delegates.UpdateHotbarSlot>(
                AddonActionBarBase.MemberFunctionPointers.UpdateHotbarSlot, Detour);
            hook.Enable();
        }
        catch (Exception ex)
        {
            log.Error(ex, "[HF] Hooking the hotbar slot update failed, action fading is unavailable.");
        }
    }

    public bool IsAvailable => hook != null;

    private bool Active => config.ActionFadeEnabled;

    private void Detour(AddonActionBarBase* addon, ActionBarSlot* slot, NumberArrayData* numbers,
        StringArrayData* strings, int numberIndex, int stringIndex)
    {
        try
        {
            Paint(slot, numbers, numberIndex);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[HF] Action fade failed.");
        }
        finally
        {
            hook!.Original(addon, slot, numbers, strings, numberIndex, stringIndex);
        }
    }

    private void Paint(ActionBarSlot* slot, NumberArrayData* numbers, int numberIndex)
    {
        var active = Active;
        if (!active)
        {
            if (wasActive) ResetAll();
            wasActive = false;
            return;
        }
        wasActive = true;

        if (slot == null || numbers == null) return;

        // Level is the synced level while synced, so this only catches actions the sync took away;
        // cooldowns, resources and range are left to the game's own greying
        var entry = (ActionBarSlotNumberArray*)&numbers->IntArray[numberIndex];
        var level = objectTable.LocalPlayer?.Level ?? 0;
        var fade  = entry->Type == SlotType.Action && level > 0 && ActionLevel(entry->ActionId) > level;

        Apply(slot, fade);
    }

    private byte ActionLevel(uint actionId)
    {
        // The slot may hold the base action of a combo or upgrade; the level that matters is the one in use
        var id = ActionManager.Instance()->GetAdjustedActionId(actionId);
        if (actionLevels.TryGetValue(id, out var level)) return level;

        // Role actions are left alone, the same way VanillaPlus handles them
        level = data.GetExcelSheet<LuminaAction>().TryGetRow(id, out var row) && !row.IsRoleAction ? row.ClassJobLevel : (byte)0;
        actionLevels[id] = level;
        return level;
    }

    private void Apply(ActionBarSlot* slot, bool fade)
    {
        // The slot's icon component holds both the action image and the border around it
        if (slot->Icon == null) return;
        var component = (AtkComponentIcon*)slot->Icon->Component;
        if (component == null || component->IconImage == null || component->Frame == null) return;

        var alpha = fade ? (byte)(255 * (1f - Math.Clamp(config.ActionFadePercent, 0f, 1f))) : (byte)255;
        component->IconImage->AtkResNode.Color.A = alpha;
        component->Frame->Color.A = alpha;
    }

    // Puts every slot back to plain white and opaque
    private void ResetAll()
    {
        var stage = AtkStage.Instance();
        if (stage == null) return;

        foreach (var name in Bars)
        {
            var bar = (AddonActionBarBase*)stage->RaptureAtkUnitManager->GetAddonByName(name);
            if (bar == null || bar->ActionBarSlotVector.First == null) continue;

            // Same nodes Apply paints, so the reset can't miss one
            foreach (ref var slot in bar->ActionBarSlotVector)
                Apply((ActionBarSlot*)Unsafe.AsPointer(ref slot), false);
        }
    }

    public void Dispose()
    {
        hook?.Dispose();
        if (wasActive) framework.RunOnFrameworkThread(ResetAll).Wait();
    }
}
