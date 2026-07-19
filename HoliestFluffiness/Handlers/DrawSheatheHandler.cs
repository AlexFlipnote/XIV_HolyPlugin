using System;
using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace HoliestFluffiness.Handlers;

// Replaces the native "Draw/Sheathe Weapon" keybind with the /draw and /sheathe emotes.
//
// Both the keybind and the emotes toggle weapon state via WeaponState.SetUnsheathed, so we hook it
// and swallow only the genuine keybind press, queueing the emote instead. The emote's own call also
// reads as "from keybind" (the key is usually still held), so a one-shot flag lets exactly that one
// through; a time window instead would make a rapid second press fall back to the native toggle.
public sealed unsafe class DrawSheatheHandler : IDisposable
{
    private const ushort DrawEmoteId    = 238;   // "/draw"
    private const ushort SheatheEmoteId = 237;   // "/sheathe"

    // Squared distance moved between frames to count as "moving"
    private const float MoveThresholdSq = 0.0001f;

    private delegate byte SetUnsheathedDelegate(WeaponState* thisPtr, byte newState, byte sendPacket, byte isInstant);

    private readonly Configuration config;
    private readonly IFramework    framework;
    private readonly IObjectTable  objectTable;
    private readonly IPluginLog    log;

    private readonly Hook<SetUnsheathedDelegate>? hook;

    private Vector3? lastPosition;
    private bool     isMoving;
    private string?  pendingEmote;
    private bool     passThroughEmoteToggle;

    public DrawSheatheHandler(Configuration config, IGameInteropProvider gameInterop,
                              IFramework framework, IObjectTable objectTable, IPluginLog log)
    {
        this.config      = config;
        this.framework   = framework;
        this.objectTable = objectTable;
        this.log         = log;

        hook = Common.TryCreateHook<SetUnsheathedDelegate>(
            (nint)WeaponState.MemberFunctionPointers.SetUnsheathed, OnSetUnsheathed, gameInterop, log,
            "[HF] DrawSheathe: failed to hook SetUnsheathed, feature disabled.");

        framework.Update += OnFrameworkUpdate;
    }

    // Running the queued emote here keeps it off the input code path that triggered the toggle.
    private void OnFrameworkUpdate(IFramework fw)
    {
        if (!config.DrawSheatheEmoteEnabled)
        {
            lastPosition = null;
            return;
        }

        var pos = objectTable.LocalPlayer?.Position;
        isMoving = pos.HasValue && lastPosition.HasValue &&
                   Vector3.DistanceSquared(pos.Value, lastPosition.Value) > MoveThresholdSq;
        lastPosition = pos;

        var emote = pendingEmote;
        if (emote != null)
        {
            pendingEmote = null;
            // Arm first: the emote's SetUnsheathed can fire synchronously from inside ExecuteCommand
            passThroughEmoteToggle = true;
            Common.ExecuteCommand(emote);
        }
    }

    private byte OnSetUnsheathed(WeaponState* thisPtr, byte newState, byte sendPacket, byte isInstant)
    {
        if (!config.DrawSheatheEmoteEnabled)
            return hook!.Original(thisPtr, newState, sendPacket, isInstant);

        // Combat auto-draw/sheathe passes straight through. Keyboard shows up as SWARD /
        // NOTARGET_SWORD; gamepad never sets those and is checked separately.
        var input = UIInputData.Instance();
        bool fromKeybind = (input != null &&
                            (input->IsInputIdPressed(InputId.SWARD) || input->IsInputIdPressed(InputId.NOTARGET_SWORD)))
                           || IsGamepadDrawSheathePressed();
        if (!fromKeybind)
            return hook!.Original(thisPtr, newState, sendPacket, isInstant);

        if (passThroughEmoteToggle)
        {
            passThroughEmoteToggle = false;
            return hook!.Original(thisPtr, newState, sendPacket, isInstant);
        }

        bool   drawing = newState != 0;
        ushort emoteId = drawing ? DrawEmoteId : SheatheEmoteId;

        // Fall back to the native toggle while moving or when the emote isn't unlocked
        var ui = UIState.Instance();
        bool unlocked = ui != null && ui->IsEmoteUnlocked(emoteId);
        if (isMoving || !unlocked)
            return hook!.Original(thisPtr, newState, sendPacket, isInstant);

        // "motion" suppresses the emote's chat log line while still playing the animation
        pendingEmote = drawing ? "/draw motion" : "/sheathe motion";
        return 0;
    }

    // Gamepad draw/sheathe is L1+R1 with no dedicated input id; the shoulders come through as their
    // hotbar-cycle bindings, so both being down is the combo. A remapped cycle binding would move it.
    private bool IsGamepadDrawSheathePressed()
    {
        var input = UIInputData.Instance();
        return input != null &&
               input->IsInputIdDown(InputId.TAB_BOTH_NEXT) &&
               input->IsInputIdDown(InputId.TAB_BOTH_PREV);
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        hook?.Dispose();
    }
}
