using System;
using System.Collections.Frozen;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Gui.FlyText;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using BattleChara = FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace HoliestFluffiness.Handlers;

public sealed unsafe class CombatHitHandler : IDisposable
{
    private readonly Configuration           config;
    private readonly IFlyTextGui             flyTextGui;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IObjectTable            objectTable;
    private readonly IPluginLog              log;

    private static readonly FrozenSet<FlyTextKind> DcKinds = FrozenSet.ToFrozenSet(
    [
        FlyTextKind.AutoAttackOrDotCritDh, FlyTextKind.DamageCritDh,
    ]);

    private static readonly FrozenSet<FlyTextKind> DKinds = FrozenSet.ToFrozenSet(
    [
        FlyTextKind.AutoAttackOrDotDh, FlyTextKind.DamageDh,
    ]);

    private static readonly FrozenSet<FlyTextKind> CKinds = FrozenSet.ToFrozenSet(
    [
        FlyTextKind.AutoAttackOrDotCrit, FlyTextKind.CriticalHit4,
        FlyTextKind.DamageCrit, FlyTextKind.NamedCriticalHitWithMp, FlyTextKind.NamedCriticalHitWithTp,
    ]);

    private static readonly FrozenSet<FlyTextKind> HealKinds = FrozenSet.ToFrozenSet(
    [
        FlyTextKind.HealingCrit,
    ]);

    private bool lastHealWasOwn;
    private bool lastHealTargetedMe;

    // Must match BattleLog.AddToScreenLogWithScreenLogKind exactly; Dalamud verifies hook delegates
    // against ClientStructs and a mismatch (e.g. a byte where the game passes an int) corrupts the call.
    private delegate void AddToScreenLogDelegate(
        BattleChara* target, BattleChara* source,
        int kind, byte option, byte actionKind,
        uint actionId, int val1, int val2, int damageType);

    private readonly Hook<AddToScreenLogDelegate>? screenLogHook;

    public CombatHitHandler(Configuration config, IFlyTextGui flyTextGui,
                            IDalamudPluginInterface pluginInterface, IObjectTable objectTable,
                            IGameInteropProvider gameInterop, IPluginLog log)
    {
        this.config          = config;
        this.flyTextGui      = flyTextGui;
        this.pluginInterface = pluginInterface;
        this.objectTable     = objectTable;
        this.log             = log;

        flyTextGui.FlyTextCreated += OnFlyText;

        screenLogHook = Common.TryCreateHookFromSignature<AddToScreenLogDelegate>(
            Sigs.AddToScreenLog, OnScreenLog, gameInterop, log,
            "[HF] CombatHit: screen-log sig scan failed, damage crits still work but heal-crit ownership will not");
    }

    private void OnScreenLog(BattleChara* target, BattleChara* source, int kind,
                             byte option, byte actionKind, uint actionId, int val1, int val2, int damageType)
    {
        if (HealKinds.Contains((FlyTextKind)kind) && source != null && target != null)
        {
            // BattleChara starts with its Character base, so the same address reads as either
            var src = (Character*)source;
            var tgt = (Character*)target;
            var localId       = objectTable.LocalPlayer?.EntityId ?? 0;
            lastHealWasOwn    = src->GameObject.GetGameObjectId() == localId ||
                                (src->GameObject.SubKind == (int)BattleNpcSubKind.Pet &&
                                 src->CompanionOwnerId   == localId);
            lastHealTargetedMe = tgt->GameObject.GetGameObjectId() == localId;
        }
        screenLogHook!.Original(target, source, kind, option, actionKind, actionId, val1, val2, damageType);
    }

    private void OnFlyText(ref FlyTextKind kind, ref int val1, ref int val2,
                           ref SeString text1, ref SeString text2,
                           ref uint color, ref uint icon, ref uint damageTypeIcon,
                           ref float yOffset, ref bool handled)
    {
        if      (DcKinds.Contains(kind) && config.CombatDcEnabled)
            Apply(ref text2, config.CombatDcShowText, config.CombatDcText,
                  config.CombatDcSound, "Sounds/Combat/direct_critical.wav", config.CombatDcVol);
        else if (DKinds.Contains(kind)  && config.CombatDEnabled)
            Apply(ref text2, config.CombatDShowText,  config.CombatDText,
                  config.CombatDSound,  "Sounds/Combat/critical.wav", config.CombatDVol);
        else if (CKinds.Contains(kind)  && config.CombatCEnabled)
            Apply(ref text2, config.CombatCShowText,  config.CombatCText,
                  config.CombatCSound,  "Sounds/Combat/critical.wav",        config.CombatCVol);
        else if (HealKinds.Contains(kind))
        {
            if (lastHealWasOwn && config.CombatChoEnabled)
                Apply(ref text2, config.CombatChoShowText, config.CombatChoText,
                      config.CombatChoSound, "Sounds/Combat/critical.wav", config.CombatChoVol);
            else if (!lastHealWasOwn && lastHealTargetedMe && config.CombatChtEnabled)
                Apply(ref text2, config.CombatChtShowText, config.CombatChtText,
                      config.CombatChtSound, "Sounds/Combat/heal.mp3",    config.CombatChtVol);
        }
    }

    private void Apply(ref SeString text2, bool showText, string text,
                       string soundPath, string defaultRelative, float volume)
    {
        if (showText && !string.IsNullOrEmpty(text))
            text2 = new SeStringBuilder().AddText(text).Build();
        SoundEngine.Play(Resolve(soundPath, defaultRelative), volume);
    }

    // Config UI Test button. Each half previews only when its own toggle is on, so the test matches
    // exactly what the current settings would produce.
    public void TestHit(FlyTextKind kind, bool showText, string text, bool soundEnabled,
                        string soundPath, string defaultAbsolute, float volume)
    {
        if (showText)
        {
            var label = string.IsNullOrEmpty(text)
                ? SeString.Empty
                : new SeStringBuilder().AddText(text).Build();
            flyTextGui.AddFlyText(kind, 1, 3333, 0, SeString.Empty, label, 0, 0, 0);
        }
        if (soundEnabled)
            SoundEngine.Play(string.IsNullOrEmpty(soundPath) ? defaultAbsolute : soundPath, volume);
    }

    private string Resolve(string configPath, string defaultRelative) =>
        SoundEngine.Resolve(configPath, defaultRelative, pluginInterface.AssemblyLocation.DirectoryName!);

    public void Dispose()
    {
        flyTextGui.FlyTextCreated -= OnFlyText;
        screenLogHook?.Dispose();
    }
}
