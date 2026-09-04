using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Chat;
using Dalamud.Hooking;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using HoliestFluffiness.Handlers;
using Dalamud.Game.ClientState.Objects.SubKinds;
using HoliestFluffiness.Windows;
using System.Reflection;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Rx = System.Text.RegularExpressions.Regex;

namespace HoliestFluffiness;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName       = "/hf";
    private const string HwCommand        = "/hw";
    private const string HwPlusCommand    = "/hw+";
    private const string HwMinCommand     = "/hw-";
    private const string NearbyCommand    = "/nearby";
    private const string FoodCheckCommand = "/foodcheck";
    private const string FatesCommand     = "/fates";
    private const string NotesCommand     = "/notes";

    [PluginService] private IDalamudPluginInterface PluginInterface { get; init; } = null!;
    [PluginService] private IClientState ClientState { get; init; } = null!;
    [PluginService] private IChatGui ChatGui { get; init; } = null!;
    [PluginService] private IFramework Framework { get; init; } = null!;
    [PluginService] private IPluginLog Log { get; init; } = null!;
    [PluginService] private ICommandManager CommandManager { get; init; } = null!;
    [PluginService] private IObjectTable ObjectTable { get; init; } = null!;
    [PluginService] private ICondition Condition { get; init; } = null!;
    [PluginService] private IAddonLifecycle AddonLifecycle { get; init; } = null!;
    [PluginService] private IAddonEventManager AddonEventManager { get; init; } = null!;
    [PluginService] private IDataManager DataManager { get; init; } = null!;
    [PluginService] private ITitleScreenMenu TitleScreenMenu { get; init; } = null!;
    [PluginService] private ITextureProvider TextureProvider { get; init; } = null!;
    [PluginService] private IGameInteropProvider GameInterop { get; init; } = null!;
    [PluginService] private IDtrBar DtrBar { get; init; } = null!;
    [PluginService] private ISigScanner SigScanner { get; init; } = null!;
    [PluginService] private IGameGui GameGui { get; init; } = null!;
    [PluginService] private IPartyList PartyList { get; init; } = null!;
    [PluginService] private ITargetManager TargetManager { get; init; } = null!;
    [PluginService] private IFlyTextGui    FlyTextGui    { get; init; } = null!;
    [PluginService] private INamePlateGui NamePlateGui  { get; init; } = null!;
    [PluginService] private IFateTable FateTable { get; init; } = null!;
    [PluginService] private IDutyState DutyState { get; init; } = null!;

    private readonly Configuration configuration;
    private readonly WindowSystem windowSystem = new("HoliestFluffiness");
    private readonly ConfigWindow configWindow;
    private readonly LoginInfoWindow loginInfoWindow;
    private readonly NoKillWindow noKillWindow;
    private readonly CharacterPickerWindow charPickerWindow;
    private readonly AccessoryHandler accessoryHandler;
    private readonly LoginInfoHandler loginInfoHandler;
    private readonly CharacterDb characterDb;
    private readonly NotesHandler notesHandler;
    private readonly NotesWindow notesWindow;
    private readonly NotePreviewWindow notePreviewWindow;
    private readonly CharaSelectHandler charaSelectHandler;
    private readonly HousingLotteryHandler housingLotteryHandler;
    private readonly ServerInfoHandler serverInfoHandler;
    private readonly RepairHandler repairHandler;
    private readonly NoKillHandler noKillHandler;
    private readonly PhysicsHandler physicsHandler;
    private readonly AntiAfkHandler antiAfkHandler;
    private readonly FastMouseClickFixHandler fastMouseClickFixHandler;
    private readonly ReadyCheckHandler readyCheckHandler;
    private readonly ReadyCheckOverlay readyCheckOverlay;
    private readonly NearbyHandler nearbyHandler;
    private readonly NearbyWindow nearbyWindow;
    private readonly FateListWindow fateListWindow;
    private readonly PingChartWindow pingChartWindow;
    private readonly FpsChartWindow fpsChartWindow;
    private readonly CommendationHandler commendationHandler;
    private readonly DoorbellHandler doorbellHandler;
    private readonly CombatHitHandler combatHitHandler;
    private readonly DynamicTravelerHandler  dynamicTravelerHandler;
    private readonly ClientTweaksHandler     clientTweaksHandler;
    private readonly DrawSheatheHandler       drawSheatheHandler;
    private readonly LootFadeHandler          lootFadeHandler;
    private readonly HideMpBarsHandler        hideMpBarsHandler;
    private readonly DutyTimerHandler dutyTimerHandler;
    private readonly CastBarHandler castBarHandler;
    private readonly LoginEnhancementHandler loginEnhancementHandler;
    private readonly FoodCheckHandler foodCheckHandler;
    private readonly FoodCheckOverlay foodCheckOverlay;
    private readonly IFontHandle titleFont;
    private IReadOnlyTitleScreenMenuEntry? titleMenuEntry;
    private ISharedImmediateTexture? titleMenuIcon;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetWindowText(IntPtr hwnd, string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [DllImport("user32.dll", CharSet = CharSet.Auto, ExactSpelling = true)]
    private static extern IntPtr GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_ALL       = 3;
    private const uint FLASHW_TIMERNOFG = 12;

    private unsafe delegate void InitiateReadyCheckDelegate(AgentReadyCheck* self);
    private Hook<InitiateReadyCheckDelegate>? readyCheckHook;

    private readonly IntPtr windowHandle;
    private readonly string originalTitle;
    private uint? lastTitleWorldId;

    private CancellationTokenSource? loginCts;
    private readonly object ctsLock = new();
    private bool switchingCharacter;
    private string? pendingLifestreamArgs;
    private HousingBidRecord? pendingBid;
    private (string district, int ward)? _loginZone;
    private string? lastKnownName;
    private string? lastKnownWorld;

    private static readonly System.Text.RegularExpressions.Regex LoginZoneRx =
        new(@"^(Mist|The Lavender Beds|The Goblet|Shirogane|Empyreum), Ward (\d+)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    public Plugin()
    {
        configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        configuration.Initialize(PluginInterface);
        Theme.Config = configuration;
        Theme.Sync();
        SoundEngine.Initialize(Log);

        using var proc = Process.GetCurrentProcess();
        windowHandle  = proc.MainWindowHandle;
        originalTitle = proc.MainWindowTitle;
        ApplyClientTitle();
        Framework.Update += OnClientTitleFrameworkUpdate;

        var dbPath = Path.Combine(PluginInterface.GetPluginConfigDirectory(), "storage.db");
        characterDb = new CharacterDb(dbPath);

        notesHandler      = new NotesHandler(characterDb, ClientState, Condition, ObjectTable, DataManager, Framework, DutyState);
        notePreviewWindow = new NotePreviewWindow();
        notesWindow       = new NotesWindow(characterDb, notesHandler, DataManager, notePreviewWindow);
        notesHandler.RequestShowPreview += OnRequestShowNotePreview;
        notesHandler.RequestUpdateDutyPreview += OnRequestUpdateDutyPreview;

        accessoryHandler    = new AccessoryHandler(configuration, ChatGui, Framework, ObjectTable);
        loginInfoWindow     = new LoginInfoWindow(() => { configWindow!.IsOpen = true; configWindow.NavigateTo(ConfigSection.Characters); },
                                                  () => configuration.CharactersDbEnabled);
        loginInfoHandler    = new LoginInfoHandler(configuration, ChatGui, Framework, ObjectTable, Condition, loginInfoWindow, characterDb, Log);
        noKillHandler          = new NoKillHandler(configuration, GameInterop, Log);
        physicsHandler         = new PhysicsHandler(configuration, Framework, GameInterop, Log);
        antiAfkHandler         = new AntiAfkHandler(configuration, Framework, ObjectTable, Log, windowHandle);
        fastMouseClickFixHandler = new FastMouseClickFixHandler(configuration, SigScanner, Log);
        readyCheckHandler      = new ReadyCheckHandler(configuration, GameInterop, ClientState, ChatGui, Framework, ObjectTable, Log);
        readyCheckOverlay      = new ReadyCheckOverlay(configuration, readyCheckHandler, GameGui, TextureProvider, DataManager);
        noKillWindow           = new NoKillWindow();
        charPickerWindow       = new CharacterPickerWindow(SwitchToCharacter);
        noKillHandler.OnLobbyError += OnNoKillLobbyError;
        charaSelectHandler     = new CharaSelectHandler(configuration, characterDb, AddonLifecycle, DataManager, Framework, Log, noKillHandler, SwitchToCharacter);
        housingLotteryHandler  = new HousingLotteryHandler(characterDb, AddonLifecycle, AddonEventManager, ObjectTable, ChatGui, Log);
        serverInfoHandler      = new ServerInfoHandler(configuration, DtrBar, Framework, ClientState, ObjectTable, Log);
        repairHandler          = new RepairHandler(configuration, SigScanner, GameInterop, AddonLifecycle, ClientState, Log);
        nearbyHandler          = new NearbyHandler(configuration, ObjectTable, Framework, PartyList, TargetManager);
        nearbyHandler.NewTargeter += OnNewTargeter;
        serverInfoHandler.SetNearbyHandler(nearbyHandler);
        commendationHandler    = new CommendationHandler(configuration, ClientState, Framework, PartyList);
        commendationHandler.OnCommendation += OnCommendationReceived;
        doorbellHandler        = new DoorbellHandler(ClientState, ObjectTable, Framework);
        combatHitHandler       = new CombatHitHandler(configuration, FlyTextGui, PluginInterface, ObjectTable, GameInterop, Log);
        dynamicTravelerHandler  = new DynamicTravelerHandler(configuration, NamePlateGui, DataManager);
        clientTweaksHandler     = new ClientTweaksHandler(configuration, AddonLifecycle, Framework, windowHandle);
        drawSheatheHandler      = new DrawSheatheHandler(configuration, GameInterop, Framework, ObjectTable, Log);
        lootFadeHandler         = new LootFadeHandler(configuration, AddonLifecycle);
        hideMpBarsHandler       = new HideMpBarsHandler(configuration, AddonLifecycle, ClientState, ObjectTable, DataManager);
        dutyTimerHandler       = new DutyTimerHandler(configuration, AddonLifecycle, DataManager);
        castBarHandler         = new CastBarHandler(configuration, GameInterop, AddonLifecycle, DataManager, ClientState, Log);
        loginEnhancementHandler = new LoginEnhancementHandler(configuration, GameInterop, AddonLifecycle, DataManager, Log);
        foodCheckHandler       = new FoodCheckHandler(configuration, PartyList, ObjectTable, ClientState, ChatGui, DataManager, Framework, GameInterop, Log, PluginInterface.AssemblyLocation.DirectoryName!);
        foodCheckOverlay       = new FoodCheckOverlay(configuration, foodCheckHandler, GameGui);
        titleFont              = PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamily.Axis, 32f));
        doorbellHandler.OnEntered     += OnDoorbellEntered;
        doorbellHandler.OnLeft        += OnDoorbellLeft;
        doorbellHandler.OnAlreadyHere += OnDoorbellAlreadyHere;
        readyCheckHandler.ReadyCheckEnded += foodCheckHandler.Invalidate;
        foodCheckHandler.CountdownStarted += OnCountdownStartedFlash;
        nearbyWindow           = new NearbyWindow(configuration, nearbyHandler, ObjectTable, TargetManager, Condition, CommandManager, GameGui);
        nearbyHandler.ShouldRun = () => nearbyWindow.IsOpen || configuration.NearbyDtrEnabled;
        fateListWindow         = new FateListWindow(FateTable, TextureProvider);
        pingChartWindow        = new PingChartWindow(serverInfoHandler);
        fpsChartWindow         = new FpsChartWindow(serverInfoHandler);
        serverInfoHandler.SetNearbyClickAction(() => CommandManager.ProcessCommand(NearbyCommand));
        serverInfoHandler.SetPingClickAction(() => pingChartWindow.IsOpen = !pingChartWindow.IsOpen);
        serverInfoHandler.SetFpsClickAction(() => fpsChartWindow.IsOpen = !fpsChartWindow.IsOpen);
        configWindow = new ConfigWindow(configuration, loginInfoHandler, accessoryHandler, repairHandler, noKillHandler, physicsHandler, antiAfkHandler, fastMouseClickFixHandler, readyCheckHandler, ObjectTable, PluginInterface, characterDb, ClientState, SwitchToCharacter, GoToBid, UpdateClientTitle);
        configWindow.SetTitleFont(titleFont);
        configWindow.SetFoodCheckHandler(foodCheckHandler);
        configWindow.SetCombatHitHandler(combatHitHandler);
        configWindow.SetDoorbellTest(TestDoorbell);
        configWindow.SetClientTweaksHandler(clientTweaksHandler);
        configWindow.SetHideMpBarsHandler(hideMpBarsHandler);

        windowSystem.AddWindow(configWindow);
        windowSystem.AddWindow(loginInfoWindow);
        windowSystem.AddWindow(noKillWindow);
        windowSystem.AddWindow(charPickerWindow);
        windowSystem.AddWindow(readyCheckOverlay);
        windowSystem.AddWindow(nearbyWindow);
        windowSystem.AddWindow(fateListWindow);
        windowSystem.AddWindow(pingChartWindow);
        windowSystem.AddWindow(fpsChartWindow);
        windowSystem.AddWindow(foodCheckOverlay);
        windowSystem.AddWindow(notesWindow);
        windowSystem.AddWindow(notePreviewWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open The Holiest Fluffiness settings. Use '/hf about' for the about page, '/hf ping' for the ping chart, '/hf foodcheck' to run a food check, '/hf notes' (or '/notes') to open the notes window."
        });
        CommandManager.AddHandler(HwCommand, new CommandInfo(OnHwCommand)
        {
            HelpMessage = "Open the character list. /hw SEARCH fuzzy-finds a character, /hw WORLD INDEX switches to a slot, and /hw WORLD INDEX DESTINATION also travels after login (e.g. /hw Ragnarok 2 fc, or /hw Ragnarok 2 mist 5 30)."
        });
        CommandManager.AddHandler(HwPlusCommand, new CommandInfo(OnHwPlusCommand)
        {
            HelpMessage = "Switch to the next character on your current world (cycles through slots 1-8). An optional trailing DESTINATION also travels after login, e.g. /hw+ fc."
        });
        CommandManager.AddHandler(HwMinCommand, new CommandInfo(OnHwMinusCommand)
        {
            HelpMessage = "Switch to the previous character on your current world (cycles through slots 1-8). An optional trailing DESTINATION also travels after login, e.g. /hw- fc."
        });
        CommandManager.AddHandler(NearbyCommand, new CommandInfo(OnNearbyCommand)
        {
            HelpMessage = "Toggle the Nearby Players window."
        });
        CommandManager.AddHandler(FoodCheckCommand, new CommandInfo(OnFoodCheckCommand)
        {
            HelpMessage = "Run a food check on the current party (alias for /hf foodcheck)."
        });
        CommandManager.AddHandler(FatesCommand, new CommandInfo(OnFatesCommand)
        {
            HelpMessage = "Toggle the Active FATEs window."
        });
        CommandManager.AddHandler(NotesCommand, new CommandInfo(OnNotesCommand)
        {
            HelpMessage = "Toggle the Notes window."
        });

        // Snapshot before anything draws, so a mid-frame toggle cannot desync the colour stack
        PluginInterface.UiBuilder.Draw += Theme.Sync;
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.Draw += nearbyWindow.DrawMarkers;
        PluginInterface.UiBuilder.Draw += () => Common.DrawToasts();
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi   += OpenMainUi;

        titleMenuIcon = TextureProvider.GetFromManifestResource(Assembly.GetExecutingAssembly(), "HoliestFluffiness.Images.menu_icon.png");
        SyncTitleMenuEntry();

        ClientState.Login  += OnLogin;
        ClientState.Logout += OnLogout;

        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "_TitleMenu",           OnCharaSelectForPicker);
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "_CharaSelectListMenu", OnCharaSelectListOpened);

        ChatGui.ChatMessage += OnChatMessageFlash;
        ChatGui.ChatMessage += OnChatMessageZone;
        unsafe
        {
            readyCheckHook = Common.TryCreateHook<InitiateReadyCheckDelegate>(
                (nint)AgentReadyCheck.MemberFunctionPointers.InitiateReadyCheck,
                OnReadyCheckInitiated, GameInterop, Log,
                "[HF] ReadyCheck: InitiateReadyCheck hook failed, ready-check overlay disabled.");
        }

        Condition.ConditionChange += OnConditionChange;
        ChatGui.LogMessage += OnLogMessageFlash;

    }

    private void OpenConfigUi() => configWindow.IsOpen = true;
    private void OpenMainUi()   { configWindow.IsOpen = true; configWindow.NavigateTo(ConfigSection.Characters); }

    // Toggled live, so the title-screen entry is added and removed to match the setting
    private void SyncTitleMenuEntry()
    {
        bool shouldShow = configuration.CharactersDbEnabled;
        if (shouldShow && titleMenuEntry == null && titleMenuIcon != null)
        {
            titleMenuEntry = TitleScreenMenu.AddEntry("Change Character", titleMenuIcon, OpenMainUi);
        }
        else if (!shouldShow && titleMenuEntry != null)
        {
            TitleScreenMenu.RemoveEntry(titleMenuEntry);
            titleMenuEntry = null;
        }
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "about":
                configWindow.IsOpen = true;
                configWindow.NavigateTo(ConfigSection.About);
                break;
            case "ping":
                pingChartWindow.IsOpen = !pingChartWindow.IsOpen;
                break;
            case "foodcheck":
                foodCheckHandler.ForceCheck();
                break;
            case "notes":
                OnNotesCommand(command, "");
                break;
            default:
                configWindow.IsOpen = !configWindow.IsOpen;
                break;
        }
    }

    private void OnHwCommand(string command, string args)
    {
        var trimmed = args.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            configWindow.IsOpen = true;
            configWindow.NavigateTo(ConfigSection.Characters);
            return;
        }

        // world, index, and (optionally) a Lifestream destination string
        var parts = trimmed.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            var query = parts[0];
            var matches = characterDb.GetAll()
                .Where(r => r.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            r.World.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 1)
                SwitchToCharacter(matches[0].Name, matches[0].World);
            else if (matches.Count == 0)
                ChatGui.PrintError($"[HF] No character matching '{query}'.");
            else
                ChatGui.PrintError($"[HF] Ambiguous: {string.Join(", ", matches.Select(r => $"{r.Name}@{r.World}"))}");
        }
        else if (int.TryParse(parts[1], out int slot) && slot >= 1 && slot <= 8)
        {
            var world = WorldResolver.Resolve(parts[0], DataManager) ?? parts[0];
            var rec = characterDb.GetByWorldAndSlot(world, slot);
            if (rec == null)
            {
                ChatGui.PrintError($"[HF] No character in slot {slot} on world '{world}'.");
                return;
            }

            var destination = parts.Length >= 3 ? parts[2].Trim() : null;
            if (string.IsNullOrEmpty(destination))
                SwitchToCharacter(rec.Name, rec.World);
            else
                GoToDestination(rec, destination);
        }
        else
        {
            ChatGui.PrintError("[HF] Usage: /hw WORLD INDEX [DESTINATION]  (e.g. /hw Ragnarok 2, /hw Ragnarok 2 fc, /hw Ragnarok 2 mist 5 30)");
        }
    }

    private void OnHwPlusCommand(string command, string args)  => CycleCharacter(+1, args);
    private void OnHwMinusCommand(string command, string args) => CycleCharacter(-1, args);

    private void OnFoodCheckCommand(string command, string args) => foodCheckHandler.ForceCheck();

    private void OnFatesCommand(string command, string args) => fateListWindow.IsOpen = !fateListWindow.IsOpen;

    private void OnNotesCommand(string command, string args) => notesWindow.IsOpen = !notesWindow.IsOpen;

    private void OnRequestShowNotePreview(List<NoteRecord> notes) => notePreviewWindow.Show(notes);

    private void OnRequestUpdateDutyPreview(List<NoteRecord> notes, int? dutyId) => notePreviewWindow.UpdateDutyPreview(notes, dutyId);

    private void OnNearbyCommand(string command, string args)
    {
        if (ClientState.IsPvP)
        {
            ChatGui.Print("Nope, you cannot check nearby people while batteling in PvP, no cheating~");
            return;
        }
        nearbyWindow.IsOpen = !nearbyWindow.IsOpen;
    }

    private void OnNoKillLobbyError(bool isAuth)
    {
        if (!configuration.NoKillDisablePopup) noKillWindow.Show(noKillHandler.InterceptCount, lastKnownName, lastKnownWorld, noKillHandler.InterceptLog);
        if (!isAuth && !string.IsNullOrEmpty(lastKnownName) && !string.IsNullOrEmpty(lastKnownWorld))
            noKillHandler.SetAutoLoginTarget(lastKnownName, lastKnownWorld);
    }

    private void OnNewTargeter(Handlers.Targeter t)
    {
        if (!configuration.NearbyTargeterSound) return;
        if (ClientState.IsPvP) return;
        SoundEngine.Play(ResolveSound(configuration.NearbyTargeterSoundPath, "Sounds/Targeting/looking.mp3"), configuration.NearbyTargeterSoundVolume);
    }

    private void OnCommendationReceived(int count, int matchmadePlayers)
    {
        string configPath, defaultFile;
        float volume;
        var norm = count / (float)matchmadePlayers;
        if (count == 7)
        {
            configPath = configuration.CommendationAllSevenPath;
            defaultFile = "Sounds/Congratulations/all-seven.mp3";
            volume = configuration.CommendationAllSevenVolume;
        }
        else if (norm > 2f / 3f)
        {
            configPath = configuration.CommendationThreeThirdsPath;
            defaultFile = "Sounds/Congratulations/three-thirds.mp3";
            volume = configuration.CommendationThreeThirdsVolume;
        }
        else if (norm > 1f / 3f)
        {
            configPath = configuration.CommendationTwoThirdsPath;
            defaultFile = "Sounds/Congratulations/two-thirds.mp3";
            volume = configuration.CommendationTwoThirdsVolume;
        }
        else
        {
            configPath = configuration.CommendationOneThirdPath;
            defaultFile = "Sounds/Congratulations/one-third.mp3";
            volume = configuration.CommendationOneThirdVolume;
        }
        SoundEngine.Play(ResolveSound(configPath, defaultFile), volume);
    }

    private void OnDoorbellEntered(string name, string _, uint worldId)
    {
        if (configuration.DoorbellEnterSound)
            SoundEngine.Play(ResolveSound(configuration.DoorbellEnterSoundPath, "Sounds/Doorbell/doorbell.mp3"), configuration.DoorbellEnterSoundVolume);
        if (configuration.DoorbellEnterChat)
            PrintDoorbellChat(name, worldId, configuration.DoorbellEnterText);
    }

    private void OnDoorbellLeft(string name, string _, uint worldId)
    {
        if (configuration.DoorbellLeaveSound)
            SoundEngine.Play(ResolveSound(configuration.DoorbellLeaveSoundPath, "Sounds/Doorbell/leave.mp3"), configuration.DoorbellLeaveSoundVolume);
        if (configuration.DoorbellLeaveChat)
            PrintDoorbellChat(name, worldId, configuration.DoorbellLeaveText);
    }

    private void OnDoorbellAlreadyHere(List<(string Name, string World, uint WorldId)> players)
    {
        if (configuration.DoorbellAlreadyHereSound)
            SoundEngine.Play(ResolveSound(configuration.DoorbellAlreadyHereSoundPath, "Sounds/Doorbell/doorbell.mp3"), configuration.DoorbellAlreadyHereSoundVolume);
        if (configuration.DoorbellAlreadyHereChat)
            foreach (var p in players)
                PrintDoorbellChat(p.Name, p.WorldId, configuration.DoorbellAlreadyHereText);
    }

    // Config UI Test button; which: 0=enter, 1=already-here, 2=leave
    private void TestDoorbell(int which)
    {
        var (chat, sound, text, soundPath, defaultRel, vol) = which switch
        {
            1 => (configuration.DoorbellAlreadyHereChat, configuration.DoorbellAlreadyHereSound, configuration.DoorbellAlreadyHereText,
                  configuration.DoorbellAlreadyHereSoundPath, "Sounds/Doorbell/doorbell.mp3", configuration.DoorbellAlreadyHereSoundVolume),
            2 => (configuration.DoorbellLeaveChat, configuration.DoorbellLeaveSound, configuration.DoorbellLeaveText,
                  configuration.DoorbellLeaveSoundPath, "Sounds/Doorbell/leave.mp3", configuration.DoorbellLeaveSoundVolume),
            _ => (configuration.DoorbellEnterChat, configuration.DoorbellEnterSound, configuration.DoorbellEnterText,
                  configuration.DoorbellEnterSoundPath, "Sounds/Doorbell/doorbell.mp3", configuration.DoorbellEnterSoundVolume),
        };

        if (sound)
            SoundEngine.Play(ResolveSound(soundPath, defaultRel), vol);
        if (chat)
        {
            var player  = ObjectTable[0] as IPlayerCharacter;
            var name    = player?.Name.TextValue ?? "Firstname Lastname";
            var worldId = player?.HomeWorld.RowId ?? 0;
            PrintDoorbellChat(name, worldId, text);
        }
    }

    // Every "<player>" token in the user template becomes a clickable player link
    private void PrintDoorbellChat(string name, uint worldId, string template)
    {
        var builder = new SeStringBuilder().AddText("[Doorbell] ");
        var parts = Rx.Split(template, "<player>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) builder.Add(new PlayerPayload(name, worldId));
            if (parts[i].Length > 0) builder.AddText(parts[i]);
        }
        ChatGui.Print(new XivChatEntry { Message = builder.Build() });
    }

    private string ResolveSound(string configPath, string defaultRelative) =>
        SoundEngine.Resolve(configPath, defaultRelative, PluginInterface.AssemblyLocation.DirectoryName!);

    private void CycleCharacter(int direction, string args = "")
    {
        if (ObjectTable[0] is not IPlayerCharacter player) { ChatGui.PrintError("[HF] Not logged in."); return; }

        var world = Common.WorldName(player);
        if (string.IsNullOrEmpty(world)) { ChatGui.PrintError("[HF] Could not determine home world."); return; }

        var slotted = characterDb.GetByWorld(world).Where(r => r.Slot > 0).ToList();
        if (slotted.Count == 0) { ChatGui.PrintError($"[HF] No characters with known slots found for {world}."); return; }

        var currentKey = $"{player.Name.TextValue}@{world}";
        int idx = slotted.FindIndex(r => r.Key == currentKey);
        if (idx < 0) idx = 0;

        int nextIdx = (idx + direction + slotted.Count) % slotted.Count;
        var next = slotted[nextIdx];

        // Everything after the +/- alias is treated as a Lifestream destination, same as /hw's trailing args
        var destination = args.Trim();
        if (string.IsNullOrEmpty(destination))
            SwitchToCharacter(next.Name, next.World);
        else
            GoToDestination(next, destination);
    }

    private void GoToBid(CharacterRecord rec, HousingBidRecord bid)
    {
        var args = $"{rec.World}, {bid.District}, ward {bid.Ward}, plot {bid.Plot}";

        var currentKey = ObjectTable[0] is IPlayerCharacter player
            ? Common.CharacterKey(player)
            : null;

        if (currentKey == rec.Key)
        {
            if (IsAlreadyInBidLocation(bid))
            {
                Log.Debug("[GoToBid] Already in {D} W{W}, skipping teleport.", bid.District, bid.Ward);
                return;
            }
            InvokeLifestreamTeleport(args);
        }
        else
        {
            pendingLifestreamArgs = args;
            pendingBid            = bid;
            SwitchToCharacter(rec.Name, rec.World);
        }
    }

    // Hands the raw destination to /li once logged in; Lifestream parses it itself, and retries plot
    // addresses with the current world prepended, so no world token is needed here.
    private void GoToDestination(CharacterRecord rec, string destination)
    {
        var currentKey = ObjectTable[0] is IPlayerCharacter player
            ? Common.CharacterKey(player)
            : null;

        if (currentKey == rec.Key)
        {
            InvokeLifestreamTeleport(destination);
        }
        else
        {
            pendingLifestreamArgs = destination;
            SwitchToCharacter(rec.Name, rec.World);
        }
    }

    // Friendly label for the swap toast, or null when there is no follow-up destination
    private static string? DescribeLifestreamDestination(string? raw)
    {
        var d = raw?.Trim();
        if (string.IsNullOrEmpty(d)) return null;

        var lower = d.ToLowerInvariant();
        var head  = lower.Split(' ', 2)[0];
        return lower switch
        {
            "fc" or "free" or "company" or "free company" => "Free Company estate",
            "home" or "house" or "private"                => "Private estate",
            "apt" or "apartment"                          => "Apartment",
            "ws" or "workshop"                            => "FC workshop",
            "shared"                                      => "Shared estate",
            "auto"                                        => "Estate",
            "mb" or "market"                              => "Market board",
            _ when head is "gc" or "gcc" or "hc" or "hcc" or "fcgc" or "gcfc" => "Grand Company",
            _ when head is "inn" or "hinn"                => "Inn",
            _ when head is "island" or "is" or "sanctuary" => "Island Sanctuary",
            _                                             => FormatHousingAddress(d) ?? d,
        };
    }

    private static readonly (string Alias, string Name)[] DistrictAliases =
    {
        ("the lavender beds", "The Lavender Beds"),
        ("lavender beds",     "The Lavender Beds"),
        ("lavender",          "The Lavender Beds"),
        ("lb",                "The Lavender Beds"),
        ("the goblet",        "The Goblet"),
        ("goblet",            "The Goblet"),
        ("empyreum",          "Empyreum"),
        ("empy",              "Empyreum"),
        ("shirogane",         "Shirogane"),
        ("shiro",             "Shirogane"),
        ("mist",              "Mist"),
    };

    // "mist 5 30" or "Ragnarok, Shirogane, ward 7, plot 30" become "Shirogane, Ward 7, Plot 30".
    // Null when the string is not a recognisable housing address.
    private static string? FormatHousingAddress(string raw)
    {
        var s = Rx.Replace(raw.ToLowerInvariant(), @"[,\.\(\)\t]", " ");
        s = " " + Rx.Replace(s, @"\s+", " ").Trim() + " ";

        string? district = null;
        foreach (var (alias, name) in DistrictAliases)
        {
            if (s.Contains($" {alias} "))
            {
                district = name;
                s = s.Replace($" {alias} ", " ");
                break;
            }
        }
        if (district == null) return null;

        bool isApartment = Rx.IsMatch(s, @"\b(?:apartment|apt)\b") || Rx.IsMatch(s, @"\ba\s*\d");
        bool isSub       = Rx.IsMatch(s, @"\b(?:subdivision|sub)\b");

        int? ward = MatchNum(s, @"\b(?:ward|w)\s*(\d{1,2})\b");
        int? unit = isApartment
            ? MatchNum(s, @"\b(?:apartment|apt|a)\s*(\d{1,3})\b")
            : MatchNum(s, @"\b(?:plot|p)\s*(\d{1,2})\b");

        // Keyword-less form ("mist 5 30"): fall back to bare numbers in order.
        if (ward == null || unit == null)
        {
            var nums = Rx.Matches(s, @"\d{1,3}");
            if (ward == null && nums.Count >= 1) ward = int.Parse(nums[0].Value);
            if (unit == null && nums.Count >= 2) unit = int.Parse(nums[1].Value);
        }

        if (ward == null) return district;

        var sub  = isSub ? ", Subdivision" : "";
        var kind = isApartment ? "Apartment" : "Plot";
        return unit == null
            ? $"{district}, Ward {ward}{sub}"
            : $"{district}, Ward {ward}, {kind} {unit}{sub}";
    }

    private static int? MatchNum(string s, string pattern)
    {
        var m = Rx.Match(s, pattern);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    // Normalize bid.District first: legacy records predate the "The Lavender Beds" prefix fix.
    private bool IsAlreadyInBidLocation(HousingBidRecord bid) =>
        HousingDistricts.TerritoryIds.TryGetValue(HousingDistricts.Normalize(bid.District), out var expected) &&
        ClientState.TerritoryType == expected;

    private void InvokeLifestreamTeleport(string args)
    {
        try
        {
            CommandManager.ProcessCommand($"/li {args}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Lifestream teleport failed: {Args}", args);
            ChatGui.PrintError("[HF] Failed to teleport via Lifestream. Is Lifestream installed and loaded?");
        }
    }

    private async void SwitchToCharacter(string name, string world)
    {
        switchingCharacter = true;
        // Set by GoToDestination/GoToBid before switching, so the toast shows the full journey
        var destLabel = DescribeLifestreamDestination(pendingLifestreamArgs);
        try
        {
            await loginInfoHandler.QuickSaveAsync();
            // IPC must be called on the framework thread; after the async save we may be on a pool thread
            await Framework.RunOnFrameworkThread(() =>
            {
                Common.ShowToast(
                    "Swap character",
                    destLabel == null
                        ? $"Switching to {name} ({world})"
                        : $"Switching to {name} ({world})\nDestination: {destLabel}"
                );
                // Return type is ErrorCode enum, use object to avoid InvalidCastException
                PluginInterface.GetIpcSubscriber<string, string, object>("Lifestream.ChangeCharacter")
                               .InvokeFunc(name, world);
                loginInfoWindow.SetChangingState(name, world);
            });
        }
        catch (Exception ex)
        {
            switchingCharacter = false;
            Log.Error(ex, "Failed during character switch to {Name}@{World}", name, world);
            await Framework.RunOnFrameworkThread(() =>
                ChatGui.PrintError($"[HF] Failed to switch to {name}@{world}. Is Lifestream installed and loaded?"));
        }
    }

    private bool IsLifestreamBusy()
    {
        try { return PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc(); }
        catch { return false; }
    }

    private void OnCharaSelectForPicker(AddonEvent type, AddonArgs args)
    {
        if (!configuration.CharacterPickerOnMainMenu) return;
        if (switchingCharacter) return;
        if (noKillHandler.PendingAutoLogin || noKillHandler.IsReconnecting) return;
        if (IsLifestreamBusy()) return;
        var chars = characterDb.GetAll();
        if (chars.Count == 0) return;
        charPickerWindow.Show(chars);
    }

    private void OnCharaSelectListOpened(AddonEvent type, AddonArgs args)
    {
        // Manual navigation to character select dismisses the picker
        charPickerWindow.IsOpen = false;
    }

    private void OnLogout(int type, int code)
    {
        lastTitleWorldId = null;
        ApplyClientTitle();
        lock (ctsLock)
        {
            loginCts?.Cancel();
            loginCts?.Dispose();
            loginCts = null;
        }

        if (configuration.NoKillEnabled && (code == 90001 || code == 90002 || code == 90006 || code == 90007))
        {
            if (!string.IsNullOrEmpty(lastKnownName) && !string.IsNullOrEmpty(lastKnownWorld))
                noKillHandler.SetAutoLoginTarget(lastKnownName, lastKnownWorld);
        }
    }

    private void OnChatMessageZone(IHandleableChatMessage message)
    {
        var m = LoginZoneRx.Match(message.Message.ToString());
        if (m.Success)
            _loginZone = (m.Groups[1].Value, int.Parse(m.Groups[2].Value));
    }

    private void OnLogin()
    {
        _loginZone         = null;
        switchingCharacter = false;
        if (ObjectTable[0] is IPlayerCharacter player)
        {
            lastKnownName  = player.Name.TextValue;
            lastKnownWorld = Common.WorldName(player);
        }
        noKillHandler.ClearReconnecting();
        UpdateClientTitle();
        CancellationTokenSource newCts;
        lock (ctsLock)
        {
            loginCts?.Cancel();
            loginCts?.Dispose();
            newCts = new CancellationTokenSource();
            loginCts = newCts;
        }

        Task.Run(() => RunLoginSequenceAsync(newCts.Token), newCts.Token);
    }

    private async Task RunLoginSequenceAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                bool loading = true;
                await Framework.RunOnFrameworkThread(() => { loading = Condition[ConditionFlag.BetweenAreas] || Condition[ConditionFlag.BetweenAreas51]; });
                if (!loading) break;
                await Task.Delay(500, token);
            }

            token.ThrowIfCancellationRequested();

            await loginInfoHandler.RunAsync(token);
            await accessoryHandler.RunAsync(token);

            var tp  = pendingLifestreamArgs;
            var bid = pendingBid;
            pendingLifestreamArgs = null;
            pendingBid            = null;

            if (tp != null)
            {
                if (bid != null)
                {
                    // Wait up to 2s for the "Shirogane, Ward 7" zone announcement chat line
                    var deadline = Environment.TickCount64 + 2000;
                    while (_loginZone == null && Environment.TickCount64 < deadline && !token.IsCancellationRequested)
                        await Task.Delay(100, token);
                }

                var zone = _loginZone;
                if (bid != null && zone.HasValue && HousingDistricts.Normalize(zone.Value.district) == HousingDistricts.Normalize(bid.District) && zone.Value.ward == bid.Ward)
                    Log.Debug("[GoToBid] Already in {D} W{W} after login, skipping teleport.", bid.District, bid.Ward);
                else
                    await Framework.RunOnFrameworkThread(() => InvokeLifestreamTeleport(tp));
            }

            await loginInfoHandler.RunPeriodicUpdatesAsync(token);
        }
        catch (OperationCanceledException)
        {
            Log.Debug("Login sequence cancelled.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in login sequence.");
        }
    }

    private void FlashTaskbar()
    {
        if (GetForegroundWindow() == windowHandle) return;
        var fi = new FLASHWINFO
        {
            cbSize   = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd     = windowHandle,
            dwFlags  = FLASHW_ALL | FLASHW_TIMERNOFG,
            uCount   = uint.MaxValue,
            dwTimeout = 0,
        };
        FlashWindowEx(ref fi);
    }

    private void OnChatMessageFlash(IHandleableChatMessage message)
    {
        if (configuration.ClientFlashOnTell && message.LogKind == XivChatType.TellIncoming)
            FlashTaskbar();
    }

    private void OnLogMessageFlash(ILogMessage message)
    {
        if (configuration.ClientFlashOnAlarm && message.LogMessageId == 3906)
            FlashTaskbar();
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (configuration.ClientFlashOnCombat && flag == ConditionFlag.InCombat && value)
            FlashTaskbar();
    }

    private void OnCountdownStartedFlash()
    {
        if (configuration.ClientFlashOnCountdown)
            FlashTaskbar();
    }

    private unsafe void OnReadyCheckInitiated(AgentReadyCheck* self)
    {
        readyCheckHook?.Original(self);
        if (configuration.ClientFlashOnReadyCheck)
            FlashTaskbar();
        readyCheckHandler.OnBegin();
        foodCheckHandler.OnReadyCheck();
    }

    private void ApplyClientTitle()
    {
        var prefix = configuration.ClientTitlePrefix.Trim();
        SetWindowText(windowHandle, string.IsNullOrEmpty(prefix) ? "FINAL FANTASY XIV" : prefix);
    }

    private void UpdateClientTitle()
    {
        if (!configuration.ClientAppendNameOnLogin) { ApplyClientTitle(); return; }
        if (ObjectTable[0] is not IPlayerCharacter player) { ApplyClientTitle(); return; }
        var world  = player.CurrentWorld.ValueNullable?.Name.ExtractText() ?? "";
        var prefix = configuration.ClientTitlePrefix.Trim();
        SetWindowText(windowHandle, string.IsNullOrEmpty(prefix)
            ? $"{player.Name.TextValue} @ {world}"
            : $"{prefix} / {player.Name.TextValue} @ {world}");
    }

    private void OnClientTitleFrameworkUpdate(IFramework fw)
    {
        SyncTitleMenuEntry();

        if (!configuration.ClientAppendNameOnLogin) return;
        var player  = ObjectTable[0] as IPlayerCharacter;
        var worldId = player?.CurrentWorld.IsValid == true ? (uint?)player.CurrentWorld.RowId : null;
        if (worldId == lastTitleWorldId) return;
        lastTitleWorldId = worldId;
        UpdateClientTitle();
    }

    public void Dispose()
    {
        Framework.Update -= OnClientTitleFrameworkUpdate;
        SetWindowText(windowHandle, originalTitle);
        ClientState.Login  -= OnLogin;
        ClientState.Logout -= OnLogout;
        AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "_TitleMenu",           OnCharaSelectForPicker);
        AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "_CharaSelectListMenu", OnCharaSelectListOpened);
        ChatGui.ChatMessage -= OnChatMessageFlash;
        ChatGui.ChatMessage -= OnChatMessageZone;
        ChatGui.LogMessage -= OnLogMessageFlash;
        Condition.ConditionChange -= OnConditionChange;
        readyCheckHandler.ReadyCheckEnded -= foodCheckHandler.Invalidate;
        readyCheckHook?.Dispose();
        lock (ctsLock)
        {
            loginCts?.Cancel();
            loginCts?.Dispose();
            loginCts = null;
        }
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(HwCommand);
        CommandManager.RemoveHandler(HwPlusCommand);
        CommandManager.RemoveHandler(HwMinCommand);
        CommandManager.RemoveHandler(NearbyCommand);
        CommandManager.RemoveHandler(FoodCheckCommand);
        CommandManager.RemoveHandler(FatesCommand);
        CommandManager.RemoveHandler(NotesCommand);
        PluginInterface.UiBuilder.Draw -= Theme.Sync;
        PluginInterface.UiBuilder.Draw -= nearbyWindow.DrawMarkers;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi   -= OpenMainUi;
        if (titleMenuEntry != null)
        {
            TitleScreenMenu.RemoveEntry(titleMenuEntry);
            titleMenuEntry = null;
        }
        windowSystem.RemoveAllWindows();
        // RemoveAllWindows only unregisters from the draw loop, it does not dispose
        notesHandler.RequestShowPreview -= OnRequestShowNotePreview;
        notesHandler.RequestUpdateDutyPreview -= OnRequestUpdateDutyPreview;
        notesHandler.Dispose();
        notesWindow.Dispose();
        fateListWindow.Dispose();
        nearbyWindow.Dispose();
        pingChartWindow.Dispose();
        fpsChartWindow.Dispose();
        charaSelectHandler.Dispose();
        housingLotteryHandler.Dispose();
        serverInfoHandler.Dispose();
        repairHandler.Dispose();
        noKillHandler.OnLobbyError -= OnNoKillLobbyError;
        noKillHandler.Dispose();
        physicsHandler.Dispose();
        antiAfkHandler.Dispose();
        fastMouseClickFixHandler.Dispose();
        readyCheckHandler.Dispose();
        readyCheckOverlay.Dispose();
        nearbyHandler.NewTargeter -= OnNewTargeter;
        nearbyHandler.Dispose();
        commendationHandler.OnCommendation -= OnCommendationReceived;
        commendationHandler.Dispose();
        doorbellHandler.OnEntered     -= OnDoorbellEntered;
        doorbellHandler.OnLeft        -= OnDoorbellLeft;
        doorbellHandler.OnAlreadyHere -= OnDoorbellAlreadyHere;
        doorbellHandler.Dispose();
        combatHitHandler.Dispose();
        dynamicTravelerHandler.Dispose();
        clientTweaksHandler.Dispose();
        drawSheatheHandler.Dispose();
        lootFadeHandler.Dispose();
        hideMpBarsHandler.Dispose();
        dutyTimerHandler.Dispose();
        castBarHandler.Dispose();
        loginEnhancementHandler.Dispose();
        foodCheckHandler.CountdownStarted -= OnCountdownStartedFlash;
        foodCheckHandler.Dispose();
        foodCheckOverlay.Dispose();
        titleFont.Dispose();
        characterDb.Dispose();
    }
}
