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
// The game toggles weapon state through WeaponState.SetUnsheathed. The native keybind and the
// emotes both call it, so we hook it and only intervene on the genuine keybind press, detected
// via the SWARD / NOTARGET_SWORD input id being pressed. When we intervene we swallow the native
// toggle and queue the matching emote instead; the emote then performs its own SetUnsheathed to
// actually move the weapon.
//
// The catch: the emote's own call happens while the key is often still held, so it also reads as
// "from keybind". We must NOT swallow that one, or the weapon never really toggles and the game
// reconciles it back. Right before running the emote we arm a one-shot flag; the very next
// keybind-flagged call is the emote's own toggle and goes to the game untouched. Using a one-shot
// flag rather than a time window means a rapid second keypress still emotes instead of falling
// back to the native toggle.
//
// We fall back to the native toggle when the feature is off, the matching emote isn't unlocked on
// this character, or the player is moving (emotes can't play while moving).
public sealed unsafe class DrawSheatheHandler : IDisposable
{
    // Emote row ids and their text commands (verified against the Emote sheet).
    private const ushort DrawEmoteId    = 238;   // "/draw"
    private const ushort SheatheEmoteId = 237;   // "/sheathe"

    // Squared distance the player must move between frames to count as "moving".
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

        try
        {
            hook = gameInterop.HookFromAddress<SetUnsheathedDelegate>(
                WeaponState.MemberFunctionPointers.SetUnsheathed, OnSetUnsheathed);
            hook.Enable();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[HF] DrawSheathe: failed to hook SetUnsheathed, feature disabled.");
        }

        framework.Update += OnFrameworkUpdate;
    }

    // Tracks whether the player is moving (read by the hook, which runs on this same thread) and
    // fires any emote queued by the hook. Running the emote here keeps it off the input code path
    // that triggered the toggle.
    private void OnFrameworkUpdate(IFramework fw)
    {
        var pos = objectTable.LocalPlayer?.Position;
        isMoving = pos.HasValue && lastPosition.HasValue &&
                   Vector3.DistanceSquared(pos.Value, lastPosition.Value) > MoveThresholdSq;
        lastPosition = pos;

        var emote = pendingEmote;
        if (emote != null)
        {
            pendingEmote = null;
            // Arm before executing, since the emote's own SetUnsheathed can fire synchronously from
            // inside ExecuteCommand.
            passThroughEmoteToggle = true;
            Common.ExecuteCommand(emote);
        }
    }

    private byte OnSetUnsheathed(WeaponState* thisPtr, byte newState, byte sendPacket, byte isInstant)
    {
        if (!config.DrawSheatheEmoteEnabled)
            return hook!.Original(thisPtr, newState, sendPacket, isInstant);

        // Anything not driven by the draw/sheathe keybind (combat auto-draw, auto-sheathe, and the
        // emote's own toggle once the key is released) passes straight through.
        var input = UIInputData.Instance();
        bool fromKeybind = input != null &&
                           (input->IsInputIdPressed(InputId.SWARD) || input->IsInputIdPressed(InputId.NOTARGET_SWORD));
        if (!fromKeybind)
            return hook!.Original(thisPtr, newState, sendPacket, isInstant);

        // The emote we just triggered performs its own toggle while the key may still be held; let
        // that single call through so the weapon state actually changes.
        if (passThroughEmoteToggle)
        {
            passThroughEmoteToggle = false;
            return hook!.Original(thisPtr, newState, sendPacket, isInstant);
        }

        bool   drawing = newState != 0;
        ushort emoteId = drawing ? DrawEmoteId : SheatheEmoteId;

        // Fall back to the normal toggle while moving or when the emote isn't unlocked, so the key
        // still works everywhere.
        var ui = UIState.Instance();
        bool unlocked = ui != null && ui->IsEmoteUnlocked(emoteId);
        if (isMoving || !unlocked)
            return hook!.Original(thisPtr, newState, sendPacket, isInstant);

        // Genuine keybind press: swallow the native toggle and let the emote do it instead.
        pendingEmote = drawing ? "/draw" : "/sheathe";
        return 0;
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        hook?.Dispose();
    }
}
