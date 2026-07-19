using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace HoliestFluffiness.Handlers;

public sealed class NoKillHandler : IDisposable
{
    private delegate char LobbyErrorDelegate(long a1, long a2, long a3);
    private readonly Hook<LobbyErrorDelegate>? hook;
    private readonly IPluginLog log;

    public event Action<bool>? OnLobbyError; // bool = isAuthError
    public int    InterceptCount   { get; private set; }
    public IReadOnlyList<DateTime> InterceptLog => interceptLog;
    private readonly List<DateTime> interceptLog = [];
    public bool   PendingAutoLogin { get; set; }
    public bool   IsReconnecting   { get; private set; }
    public string? AutoLoginName   { get; private set; }
    public string? AutoLoginWorld  { get; private set; }

    public void SetAutoLoginTarget(string name, string world)
    {
        AutoLoginName    = name;
        AutoLoginWorld   = world;
        PendingAutoLogin = true;
        IsReconnecting   = true;
    }

    public void ClearAutoLogin()
    {
        PendingAutoLogin = false;
        // keep AutoLoginName/World + IsReconnecting alive for retries
    }

    public void ClearReconnecting()
    {
        IsReconnecting = false;
        AutoLoginName  = null;
        AutoLoginWorld = null;
    }

    public NoKillHandler(Configuration config, IGameInteropProvider gameInterop, IPluginLog log)
    {
        this.log = log;
        hook = Common.TryCreateHookFromSignature<LobbyErrorDelegate>(
            Sigs.LobbyError, Detour, gameInterop, log,
            "[HF] NoKill: sig scan failed.", enable: config.NoKillEnabled);
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled) hook?.Enable();
        else         hook?.Disable();
    }

    private char Detour(long a1, long a2, long a3)
    {
        // Reading through a3 is raw memory access, and an access violation here is uncatchable, so
        // the null check is the only real guard. Bail to the original if the shape looks wrong.
        if (a3 == 0) return hook!.Original(a1, a2, a3);

        var p3   = new IntPtr(a3);
        var t1   = Marshal.ReadByte(p3);
        var v4   = ((t1 & 0xF) > 0) ? (uint)Marshal.ReadInt32(p3 + 8) : 0;
        var v4_16 = (ushort)v4;
        log.Debug($"[HF] NoKill: LobbyError t1:{t1} v4:{v4_16}");

        if (v4 > 0)
        {
            InterceptCount++;
            interceptLog.Add(DateTime.Now);
            OnLobbyError?.Invoke(v4_16 == 0x332C);
            if (v4_16 != 0x332C) // skip auth errors; they require re-login anyway
                Marshal.WriteInt64(p3 + 8, 0x3E80); // server connection lost
        }
        return hook!.Original(a1, a2, a3);
    }

    public void Dispose() => hook?.Dispose();
}
