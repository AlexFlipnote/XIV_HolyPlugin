using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace HoliestFluffiness;

// "FA" = "Find Anything" — Wotsit's actual IPC prefix (not "Wotsit.*")
public sealed class WotsitIpc : IDisposable
{
    private const string DisplayName  = "The Holiest Fluffiness";
    private const uint   IconId       = 0u;

    private readonly IDalamudPluginInterface pi;
    private readonly IPluginLog log;
    private readonly CharacterDb db;
    private readonly Action<string, string> onSwitch;

    private ICallGateSubscriber<string, string, string, uint, string>? registerGate;
    private ICallGateSubscriber<string, bool>? unregisterGate;
    private readonly Dictionary<string, string> guidToKey = new();

    public bool IsAvailable { get; private set; }

    public WotsitIpc(IDalamudPluginInterface pi, IPluginLog log, CharacterDb db, Action<string, string> onSwitch)
    {
        this.pi       = pi;
        this.log      = log;
        this.db       = db;
        this.onSwitch = onSwitch;

        try
        {
            registerGate   = pi.GetIpcSubscriber<string, string, string, uint, string>("FA.RegisterWithSearch");
            unregisterGate = pi.GetIpcSubscriber<string, bool>("FA.UnregisterAll");
            pi.GetIpcSubscriber<string, bool>("FA.Invoke").Subscribe(OnInvoke);
        }
        catch (Exception ex) { log.Warning(ex, "[Wotsit] Failed to set up FA gates."); }

        try { pi.GetIpcSubscriber<bool>("FA.Available").Subscribe(OnAvailable); }
        catch (Exception ex) { log.Warning(ex, "[Wotsit] Failed to subscribe to FA.Available."); }

        RegisterAll();
    }

    public void RegisterAll()
    {
        Clear();
        if (registerGate == null) return;

        foreach (var rec in db.GetAll())
        {
            try
            {
                var guid = registerGate.InvokeFunc(DisplayName, $"{rec.Name} ({rec.World})", $"Switch to {rec.Name} on {rec.World}", IconId);
                guidToKey[guid] = rec.Key;
                IsAvailable = true;
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[Wotsit] FA.RegisterWithSearch failed — Wotsit not ready yet.");
                return;
            }
        }
    }

    private void OnAvailable()
    {
        log.Debug("[Wotsit] FA.Available fired — re-registering entries.");
        RegisterAll();
    }

    private void OnInvoke(string guid)
    {
        if (guidToKey.TryGetValue(guid, out var key))
        {
            var rec = db.GetByKey(key);
            if (rec != null) onSwitch(rec.Name, rec.World);
        }
    }

    private void Clear()
    {
        IsAvailable = false;
        try { unregisterGate?.InvokeFunc(DisplayName); } catch { }
        guidToKey.Clear();
    }

    public void Dispose()
    {
        try { Clear(); } catch { }
        try { pi.GetIpcSubscriber<bool>("FA.Available").Unsubscribe(OnAvailable); } catch { }
        try { pi.GetIpcSubscriber<string, bool>("FA.Invoke").Unsubscribe(OnInvoke); } catch { }
    }
}
