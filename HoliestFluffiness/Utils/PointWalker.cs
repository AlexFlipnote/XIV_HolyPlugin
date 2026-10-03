using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace HoliestFluffiness;

// Walks the local player toward a world position with no target to lock on to. While a
// destination is set, the game's own ground movement input is filled in with the direction to it
// whenever the player isn't pressing anything; a trimmed port of Lifestream's OverrideMovement
// (ground only, no flying). Real input always wins, so the player can still steer out of trouble.
public sealed unsafe class PointWalker : IDisposable
{
    private delegate bool RMIWalkIsInputEnabledDelegate(void* self);
    private delegate void RMIWalkDelegate(void* self, float* sumLeft, float* sumForward, float* sumTurnLeft,
        byte* haveBackwardOrStrafe, byte* a6, byte bAdditiveUnk);

    private const uint LegacyMoveMode = 1;

    private readonly IObjectTable objectTable;
    private readonly IGameConfig gameConfig;
    private readonly Hook<RMIWalkDelegate>? walkHook;
    private readonly RMIWalkIsInputEnabledDelegate? isInputEnabled1;
    private readonly RMIWalkIsInputEnabledDelegate? isInputEnabled2;
    private Vector3? destination;

    public bool IsAvailable => walkHook != null && isInputEnabled1 != null && isInputEnabled2 != null;

    // Null stops walking and takes the hook out of the input path entirely.
    public Vector3? Destination
    {
        get => destination;
        set
        {
            destination = value;
            if (walkHook == null) return;
            if (value != null && IsAvailable) walkHook.Enable();
            else walkHook.Disable();
        }
    }

    public PointWalker(IObjectTable objectTable, IGameConfig gameConfig, ISigScanner sigScanner,
        IGameInteropProvider gameInterop, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.gameConfig  = gameConfig;

        try
        {
            isInputEnabled1 = Marshal.GetDelegateForFunctionPointer<RMIWalkIsInputEnabledDelegate>(sigScanner.ScanText(Sigs.RMIWalkIsInputEnabled1));
            isInputEnabled2 = Marshal.GetDelegateForFunctionPointer<RMIWalkIsInputEnabledDelegate>(sigScanner.ScanText(Sigs.RMIWalkIsInputEnabled2));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[HF] PointWalker: input check sigs not found; walking to points is disabled.");
        }

        walkHook = Common.TryCreateHookFromSignature<RMIWalkDelegate>(Sigs.RMIWalk, WalkDetour, gameInterop, log,
            "[HF] PointWalker: RMIWalk hook failed; walking to points is disabled.", enable: false);
    }

    private void WalkDetour(void* self, float* sumLeft, float* sumForward, float* sumTurnLeft,
        byte* haveBackwardOrStrafe, byte* a6, byte bAdditiveUnk)
    {
        walkHook!.Original(self, sumLeft, sumForward, sumTurnLeft, haveBackwardOrStrafe, a6, bAdditiveUnk);

        // Never let our steering throw out of a detour; the game is on the other side.
        try
        {
            if (destination is not { } dest || bAdditiveUnk != 0) return;
            if (*sumLeft != 0f || *sumForward != 0f) return;
            if (!isInputEnabled1!(self) || !isInputEnabled2!(self)) return;
            if (!Common.TryGetLocalPlayer(objectTable, out var player)) return;

            var dir = dest - player.Position;
            if (dir.X * dir.X + dir.Z * dir.Z < 0.0001f) return;

            // Input is relative to the character's facing, or to the camera in legacy movement mode.
            var heading = MathF.Atan2(dir.X, dir.Z);
            var facing  = IsLegacyMode() ? CameraFacing() : player.Rotation;
            var rel     = heading - facing;
            *sumLeft    = MathF.Sin(rel);
            *sumForward = MathF.Cos(rel);
        }
        catch
        {
            // Leave the game's own input untouched.
        }
    }

    private bool IsLegacyMode() =>
        gameConfig.UiControl.TryGetUInt("MoveMode", out var mode) && mode == LegacyMoveMode;

    private static float CameraFacing()
    {
        var camera = CameraManager.Instance()->GetActiveCamera();
        return camera == null ? 0f : camera->DirH + MathF.PI;
    }

    public void Dispose()
    {
        destination = null;
        walkHook?.Dispose();
    }
}
