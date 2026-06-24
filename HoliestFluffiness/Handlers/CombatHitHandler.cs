using System;
using System.Collections.Immutable;
using System.IO;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Gui.FlyText;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace HoliestFluffiness.Handlers;

public sealed unsafe class CombatHitHandler : IDisposable
{
    private readonly Configuration           config;
    private readonly IFlyTextGui             flyTextGui;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IObjectTable            objectTable;

    private static readonly ImmutableHashSet<FlyTextKind> DcKinds = ImmutableHashSet.Create(
        FlyTextKind.AutoAttackOrDotCritDh, FlyTextKind.DamageCritDh);

    private static readonly ImmutableHashSet<FlyTextKind> DKinds = ImmutableHashSet.Create(
        FlyTextKind.AutoAttackOrDotDh, FlyTextKind.DamageDh);

    private static readonly ImmutableHashSet<FlyTextKind> CKinds = ImmutableHashSet.Create(
        FlyTextKind.AutoAttackOrDotCrit, FlyTextKind.CriticalHit4,
        FlyTextKind.DamageCrit, FlyTextKind.NamedCriticalHitWithMp, FlyTextKind.NamedCriticalHitWithTp);

    private static readonly ImmutableHashSet<FlyTextKind> HealKinds = ImmutableHashSet.Create(
        FlyTextKind.HealingCrit);

    private bool lastHealWasOwn       = false;
    private bool lastHealTargetedMe   = false;

    private delegate void AddToScreenLogDelegate(
        Character* target, Character* source,
        FlyTextKind kind, byte option, byte actionKind,
        int actionId, int val1, int val2, byte damageType);

    private readonly Hook<AddToScreenLogDelegate>? screenLogHook;

    public CombatHitHandler(Configuration config, IFlyTextGui flyTextGui,
                            IDalamudPluginInterface pluginInterface, IObjectTable objectTable,
                            ISigScanner sigScanner, IGameInteropProvider gameInterop)
    {
        this.config          = config;
        this.flyTextGui      = flyTextGui;
        this.pluginInterface = pluginInterface;
        this.objectTable     = objectTable;

        flyTextGui.FlyTextCreated += OnFlyText;

        try
        {
            var address = sigScanner.ScanText("E8 ?? ?? ?? ?? BF ?? ?? ?? ?? EB 39");
            screenLogHook = gameInterop.HookFromAddress<AddToScreenLogDelegate>(address, OnScreenLog);
            screenLogHook.Enable();
        }
        catch { /* sig may change with patches; damage crits still work without this hook */ }
    }

    private void OnScreenLog(Character* target, Character* source, FlyTextKind kind,
                             byte option, byte actionKind, int actionId, int val1, int val2, byte damageType)
    {
        if (HealKinds.Contains(kind))
        {
            var localId       = objectTable.LocalPlayer?.EntityId ?? 0;
            lastHealWasOwn    = source->GameObject.GetGameObjectId() == localId ||
                                (source->GameObject.SubKind == (int)BattleNpcSubKind.Pet &&
                                 source->CompanionOwnerId   == localId);
            lastHealTargetedMe = target->GameObject.GetGameObjectId() == localId;
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

    // Called by the Test button in the config UI. defaultAbsolute is the full path to the bundled default sound.
    public void TestHit(FlyTextKind kind, bool showText, string text, string soundPath, string defaultAbsolute, float volume)
    {
        var label = showText && !string.IsNullOrEmpty(text)
            ? new SeStringBuilder().AddText(text).Build()
            : SeString.Empty;
        flyTextGui.AddFlyText(kind, 1, 3333, 0, SeString.Empty, label, 0, 0, 0);
        SoundEngine.Play(string.IsNullOrEmpty(soundPath) ? defaultAbsolute : soundPath, volume);
    }

    private string Resolve(string configPath, string defaultRelative) =>
        string.IsNullOrEmpty(configPath)
            ? Path.Combine(pluginInterface.AssemblyLocation.DirectoryName!, defaultRelative)
            : configPath;

    public void Dispose()
    {
        flyTextGui.FlyTextCreated -= OnFlyText;
        screenLogHook?.Dispose();
    }
}
