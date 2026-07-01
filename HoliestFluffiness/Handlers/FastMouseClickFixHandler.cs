using System;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;

namespace HoliestFluffiness.Handlers;

// Patches out a jump in the client's mouse click handler that otherwise imposes an
// artificial delay between clicks. First two bytes of the sig are fixed (EB 3F = "jmp
// short +0x3F"), so we can restore them directly on disable without reading back memory.
public sealed class FastMouseClickFixHandler : IDisposable
{
    private const string Sig = "EB 3F B8 ?? ?? ?? ?? 48 8B D7";
    private static readonly byte[] OriginalBytes = [0xEB, 0x3F];
    private static readonly byte[] PatchedBytes  = [0x90, 0x90];

    private const uint PAGE_EXECUTE_READWRITE = 0x40;

    [DllImport("kernel32.dll")]
    private static extern bool VirtualProtect(nint lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);

    private readonly Configuration config;
    private readonly IPluginLog log;

    private nint address;
    private bool patched;

    public bool IsAvailable => address != nint.Zero;
    public bool IsEnabled   => patched;

    public FastMouseClickFixHandler(Configuration config, ISigScanner sigScanner, IPluginLog log)
    {
        this.config = config;
        this.log    = log;

        try
        {
            address = sigScanner.ScanText(Sig);
            if (config.FastMouseClickFixEnabled)
                EnableInternal();
        }
        catch (Exception ex) { log.Warning(ex, "[HF] FastMouseClickFix: sig scan failed."); }
    }

    public void Enable()
    {
        config.FastMouseClickFixEnabled = true;
        config.Save();
        EnableInternal();
    }

    public void Disable()
    {
        config.FastMouseClickFixEnabled = false;
        config.Save();
        DisableInternal();
    }

    private void EnableInternal()
    {
        if (patched || address == nint.Zero) return;
        if (WriteBytes(address, PatchedBytes))
            patched = true;
        else
            log.Warning("[HF] FastMouseClickFix: patch write failed.");
    }

    private void DisableInternal()
    {
        if (!patched) return;
        WriteBytes(address, OriginalBytes);
        patched = false;
    }

    private static bool WriteBytes(nint address, byte[] bytes)
    {
        if (!VirtualProtect(address, (nuint)bytes.Length, PAGE_EXECUTE_READWRITE, out var oldProtect))
            return false;
        Marshal.Copy(bytes, 0, address, bytes.Length);
        VirtualProtect(address, (nuint)bytes.Length, oldProtect, out _);
        return true;
    }

    public void Dispose() => DisableInternal();
}
