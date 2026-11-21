using System;
using System.Runtime.CompilerServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays.Common;
using FFXIVClientStructs.FFXIV.Component.GUI;
using static FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarUIIntermediate;

namespace HoliestFluffiness.Handlers;

// Fades hotbar actions you can't use right now.
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
    private readonly IPluginLog    log;
    private readonly Hook<AddonActionBarBase.Delegates.UpdateHotbarSlot>? hook;

    // Last state we painted with, so turning a setting off puts every slot back once
    private bool wasActive;

    public ActionFadeHandler(Configuration config, IGameInteropProvider gameInterop, IFramework framework,
        IObjectTable objectTable, IPluginLog log)
    {
        this.config      = config;
        this.framework   = framework;
        this.objectTable = objectTable;
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

        // Every action greys out for the length of a cast; fading them all for it would just flicker
        if (objectTable.LocalPlayer is { IsCasting: true }) return;
        if (slot == null || numbers == null) return;

        var data = (ActionBarSlotNumberArray*)&numbers->IntArray[numberIndex];
        var fade = data->Type is SlotType.Action or SlotType.CraftAction && !(data->Executable && data->Executable2);

        Apply(slot, fade);
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
