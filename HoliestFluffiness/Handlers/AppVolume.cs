using System;
using System.Collections.Generic;
using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace HoliestFluffiness.Handlers;

// Per-app volume through Windows' audio sessions, the same slider as the Volume Mixer. Media sessions
// name an app while audio sessions name a process, so they are matched by process name, and every
// matching session on every output device moves together; browsers play from several child processes.
// Only used from the media worker thread.
internal sealed class AppVolume : IDisposable
{
    private const int MaxCachedProcesses = 256;

    private readonly Dictionary<uint, string> processNames = new();
    private MMDeviceEnumerator? enumerator;

    // Null when the app has no audio session right now (not started, or between tracks in some players)
    // Audio devices come and go under us, so a failure only costs this read or write, never the poll
    public float? Read(string processName)
    {
        float? volume = null;
        try
        {
            ForEachSession(processName, session => volume ??= session.Volume);
        }
        catch (Exception)
        {
            return null;
        }
        return volume;
    }

    public bool Write(string processName, float volume)
    {
        try
        {
            return ForEachSession(processName, session => session.Volume = Math.Clamp(volume, 0f, 1f)) > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private int ForEachSession(string processName, Action<SimpleAudioVolume> action)
    {
        if (processName.Length == 0) return 0;
        enumerator ??= new MMDeviceEnumerator();

        var count = 0;
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                var manager = device.AudioSessionManager;
                manager.RefreshSessions();
                var sessions = manager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    if (session.IsSystemSoundsSession) continue;
                    if (!string.Equals(ProcessName(session.GetProcessID), processName, StringComparison.OrdinalIgnoreCase)) continue;
                    action(session.SimpleAudioVolume);
                    count++;
                }
            }
        }
        return count;
    }

    private string ProcessName(uint pid)
    {
        if (processNames.TryGetValue(pid, out var name)) return name;
        if (processNames.Count >= MaxCachedProcesses) processNames.Clear();
        try
        {
            using var process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch (Exception)
        {
            // Exited or not ours to inspect
            name = "";
        }
        return processNames[pid] = name;
    }

    public void Dispose()
    {
        enumerator?.Dispose();
        enumerator = null;
    }
}
