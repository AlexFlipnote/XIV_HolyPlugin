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
using Dalamud.Game.Gui.Dtr;
using Dalamud.Hooking;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using HoliestFluffiness.Handlers;
using Dalamud.Game.ClientState.Objects.SubKinds;
using HoliestFluffiness.Windows;
using System.Reflection;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace HoliestFluffiness;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName    = "/hf";
    private const string HwCommand     = "/hw";
    private const string HwPlusCommand = "/hw+";
    private const string HwMinCommand  = "/hw-";
    private const string NearbyCommand = "/nearby";

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

    private readonly Configuration configuration;
    private readonly WindowSystem windowSystem = new("HoliestFluffiness");
    private readonly ConfigWindow configWindow;
    private readonly LoginInfoWindow loginInfoWindow;
    private readonly NoKillWindow noKillWindow;
    private readonly CharacterPickerWindow charPickerWindow;
    private readonly AccessoryHandler accessoryHandler;
    private readonly LoginInfoHandler loginInfoHandler;
    private readonly CharacterDb characterDb;
    private readonly CharaSelectHandler charaSelectHandler;
    private readonly HousingLotteryHandler housingLotteryHandler;
    private readonly ServerInfoHandler serverInfoHandler;
    private readonly RepairHandler repairHandler;
    private readonly NoKillHandler noKillHandler;
    private readonly PhysicsHandler physicsHandler;
    private readonly AntiAfkHandler antiAfkHandler;
    private readonly ReadyCheckHandler readyCheckHandler;
    private readonly ReadyCheckOverlay readyCheckOverlay;
    private readonly NearbyHandler nearbyHandler;
    private readonly NearbyWindow nearbyWindow;
    private readonly PingChartWindow pingChartWindow;
    private readonly CommendationHandler commendationHandler;
    private readonly DoorbellHandler doorbellHandler;
    private readonly CombatHitHandler combatHitHandler;
    private readonly DynamicTravelerHandler  dynamicTravelerHandler;
    private readonly ClientTweaksHandler     clientTweaksHandler;
    private readonly DutyTimerHandler dutyTimerHandler;
    private readonly CastBarHandler castBarHandler;
    private readonly LoginEnhancementHandler loginEnhancementHandler;
    private readonly FoodCheckHandler foodCheckHandler;
    private readonly FoodCheckOverlay foodCheckOverlay;
    private readonly IFontHandle titleFont;

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

    private const string NormalCraftSig = "48 89 5C 24 ?? 48 89 74 24 ?? 48 89 7C 24 ?? 41 56 48 83 EC ?? 49 8B F0 48 8B FA 4C 8B F1 45 85 C9";
    private unsafe delegate AtkValue* NormalCraftCallbackDelegate(AtkModuleInterface.AtkEventInterface* thisPtr, AtkValue* returnValue, AtkValue* values, uint valueCount, ulong eventKind);
    private Hook<NormalCraftCallbackDelegate>? normalCraftHook;

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

        using var proc = Process.GetCurrentProcess();
        windowHandle  = proc.MainWindowHandle;
        originalTitle = proc.MainWindowTitle;
        ApplyClientTitle();
        Framework.Update += OnClientTitleFrameworkUpdate;

        var dbPath = Path.Combine(PluginInterface.GetPluginConfigDirectory(), "storage.db");
        characterDb = new CharacterDb(dbPath);

        accessoryHandler    = new AccessoryHandler(configuration, ChatGui, Framework, ObjectTable);
        loginInfoWindow     = new LoginInfoWindow(() => { configWindow!.IsOpen = true; configWindow.NavigateTo(5); });
        loginInfoHandler    = new LoginInfoHandler(configuration, ChatGui, Framework, ObjectTable, loginInfoWindow, characterDb, Log);
        noKillHandler          = new NoKillHandler(configuration, SigScanner, GameInterop, Log);
        physicsHandler         = new PhysicsHandler(configuration, SigScanner, Framework, GameInterop, Log);
        antiAfkHandler         = new AntiAfkHandler(configuration, Framework, Log, windowHandle);
        readyCheckHandler      = new ReadyCheckHandler(configuration, GameInterop, ClientState, ChatGui, Framework, ObjectTable, Log);
        readyCheckOverlay      = new ReadyCheckOverlay(configuration, readyCheckHandler, GameGui, TextureProvider, DataManager);
        noKillWindow           = new NoKillWindow();
        charPickerWindow       = new CharacterPickerWindow(SwitchToCharacter);
        noKillHandler.OnLobbyError += (isAuth) =>
        {
            if (!configuration.NoKillDisablePopup) noKillWindow.Show(noKillHandler.InterceptCount, lastKnownName, lastKnownWorld, noKillHandler.InterceptLog);
            if (!isAuth && !string.IsNullOrEmpty(lastKnownName) && !string.IsNullOrEmpty(lastKnownWorld))
                noKillHandler.SetAutoLoginTarget(lastKnownName, lastKnownWorld);
        };
        charaSelectHandler     = new CharaSelectHandler(configuration, characterDb, AddonLifecycle, DataManager, Framework, noKillHandler, SwitchToCharacter);
        housingLotteryHandler  = new HousingLotteryHandler(characterDb, AddonLifecycle, AddonEventManager, ObjectTable, ChatGui, Log);
        serverInfoHandler      = new ServerInfoHandler(configuration, DtrBar, Framework, ClientState, ObjectTable, Log);
        repairHandler          = new RepairHandler(configuration, SigScanner, GameInterop, AddonLifecycle, ClientState, Log);
        nearbyHandler          = new NearbyHandler(configuration, ObjectTable, Framework, PartyList, TargetManager);
        nearbyHandler.NewTargeter += OnNewTargeter;
        serverInfoHandler.SetNearbyHandler(nearbyHandler);
        commendationHandler    = new CommendationHandler(configuration, ClientState, Framework, PartyList);
        commendationHandler.OnCommendation += OnCommendationReceived;
        doorbellHandler        = new DoorbellHandler(configuration, ClientState, ObjectTable, Framework);
        combatHitHandler       = new CombatHitHandler(configuration, FlyTextGui, PluginInterface, ObjectTable, SigScanner, GameInterop);
        dynamicTravelerHandler  = new DynamicTravelerHandler(configuration, NamePlateGui, DataManager);
        clientTweaksHandler     = new ClientTweaksHandler(configuration, AddonLifecycle, Framework);
        dutyTimerHandler       = new DutyTimerHandler(configuration, AddonLifecycle, DataManager);
        castBarHandler         = new CastBarHandler(configuration, SigScanner, GameInterop, AddonLifecycle, DataManager, ClientState, Log);
        loginEnhancementHandler = new LoginEnhancementHandler(configuration, GameInterop, AddonLifecycle, DataManager, Log);
        foodCheckHandler       = new FoodCheckHandler(configuration, PartyList, ObjectTable, ClientState, ChatGui, DataManager, Framework, GameInterop, Log, PluginInterface.AssemblyLocation.DirectoryName!);
        foodCheckOverlay       = new FoodCheckOverlay(configuration, foodCheckHandler, GameGui);
        titleFont              = PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamily.Axis, 32f));
        doorbellHandler.OnEntered     += OnDoorbellEntered;
        doorbellHandler.OnLeft        += OnDoorbellLeft;
        doorbellHandler.OnAlreadyHere += OnDoorbellAlreadyHere;
        readyCheckHandler.ReadyCheckEnded += foodCheckHandler.Invalidate;
        nearbyWindow           = new NearbyWindow(configuration, nearbyHandler, ObjectTable, TargetManager, Condition, CommandManager, GameGui);
        pingChartWindow        = new PingChartWindow(serverInfoHandler, configuration);
        serverInfoHandler.SetNearbyClickAction(() => CommandManager.ProcessCommand(NearbyCommand));
        serverInfoHandler.SetPingClickAction(() => pingChartWindow.IsOpen = !pingChartWindow.IsOpen);
        configWindow = new ConfigWindow(configuration, loginInfoHandler, accessoryHandler, repairHandler, noKillHandler, physicsHandler, antiAfkHandler, readyCheckHandler, ObjectTable, PluginInterface, characterDb, ClientState, SwitchToCharacter, GoToBid, UpdateClientTitle);
        configWindow.SetTitleFont(titleFont);
        configWindow.SetFoodCheckHandler(foodCheckHandler);
        configWindow.SetNearbyHandler(nearbyHandler);
        configWindow.SetCombatHitHandler(combatHitHandler);
        configWindow.SetClientTweaksHandler(clientTweaksHandler);
        configWindow.SetLoginEnhancementHandler(loginEnhancementHandler);

        windowSystem.AddWindow(configWindow);
        windowSystem.AddWindow(loginInfoWindow);
        windowSystem.AddWindow(noKillWindow);
        windowSystem.AddWindow(charPickerWindow);
        windowSystem.AddWindow(readyCheckOverlay);
        windowSystem.AddWindow(nearbyWindow);
        windowSystem.AddWindow(pingChartWindow);
        windowSystem.AddWindow(foodCheckOverlay);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open The Holiest Fluffiness settings. Use '/hf about' for the about page, '/hf ping' for the ping chart."
        });
        CommandManager.AddHandler(HwCommand, new CommandInfo(OnHwCommand)
        {
            HelpMessage = "Open the character list. Use /hw SEARCH to fuzzy-find a character, or /hw WORLD INDEX to switch to a specific slot."
        });
        CommandManager.AddHandler(HwPlusCommand, new CommandInfo(OnHwPlusCommand)
        {
            HelpMessage = "Switch to the next character on your current world (cycles through slots 1–8)."
        });
        CommandManager.AddHandler(HwMinCommand, new CommandInfo(OnHwMinusCommand)
        {
            HelpMessage = "Switch to the previous character on your current world (cycles through slots 1–8)."
        });
        CommandManager.AddHandler(NearbyCommand, new CommandInfo(OnNearbyCommand)
        {
            HelpMessage = "Toggle the Nearby Players window."
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.Draw += nearbyWindow.DrawMarkers;
        PluginInterface.UiBuilder.Draw += () => Common.DrawToasts(configuration);
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi   += OpenMainUi;

        var icon = TextureProvider.GetFromManifestResource(Assembly.GetExecutingAssembly(), "HoliestFluffiness.Images.menu_icon.png");
        TitleScreenMenu.AddEntry("Change Character", icon, OpenMainUi);

        ClientState.Login  += OnLogin;
        ClientState.Logout += OnLogout;

        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "_TitleMenu",           OnCharaSelectForPicker);
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "_CharaSelectListMenu", OnCharaSelectListOpened);

        ChatGui.ChatMessage += OnChatMessageFlash;
        ChatGui.ChatMessage += OnChatMessageZone;
        unsafe
        {
            readyCheckHook = GameInterop.HookFromAddress<InitiateReadyCheckDelegate>(
                AgentReadyCheck.MemberFunctionPointers.InitiateReadyCheck,
                OnReadyCheckInitiated);
        }
        readyCheckHook.Enable();

        Condition.ConditionChange += OnConditionChange;
        ChatGui.LogMessage += OnLogMessageFlash;

        try
        {
            unsafe
            {
                normalCraftHook = GameInterop.HookFromSignature<NormalCraftCallbackDelegate>(NormalCraftSig, OnNormalCraftCallback);
            }
            normalCraftHook.Enable();
        }
        catch (Exception ex) { Log.Warning(ex, "[HF] Plugin: NormalCraft hook failed."); }

        AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "SynthesisSimple", OnSynthesisSimpleRefresh);
    }

    private void OpenConfigUi() => configWindow.IsOpen = true;
    private void OpenMainUi()   { configWindow.IsOpen = true; configWindow.NavigateTo(5); }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "about":
                configWindow.IsOpen = true;
                configWindow.NavigateTo(7);
                break;
            case "ping":
                pingChartWindow.IsOpen = !pingChartWindow.IsOpen;
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
            configWindow.NavigateTo(5);
            return;
        }

        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
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
        else if (parts.Length == 2 && int.TryParse(parts[1], out int slot) && slot >= 1 && slot <= 8)
        {
            var world = WorldResolver.Resolve(parts[0], DataManager) ?? parts[0];
            var rec = characterDb.GetByWorldAndSlot(world, slot);
            if (rec != null)
                SwitchToCharacter(rec.Name, rec.World);
            else
                ChatGui.PrintError($"[HF] No character in slot {slot} on world '{world}'.");
        }
        else
        {
            ChatGui.PrintError("[HF] Usage: /hw WORLD INDEX  (e.g. /hw Ragnarok 2)");
        }
    }

    private void OnHwPlusCommand(string command, string args)  => CycleCharacter(+1);
    private void OnHwMinusCommand(string command, string args) => CycleCharacter(-1);

    private void OnNearbyCommand(string command, string args)
    {
        if (!configuration.NearbyEnabled)
        {
            configuration.NearbyEnabled = true;
            configuration.Save();
        }
        nearbyWindow.IsOpen = !nearbyWindow.IsOpen;
    }

    private void OnNewTargeter(Handlers.Targeter t)
    {
        if (!configuration.NearbyTargeterSound) return;
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
        if (!configuration.DoorbellEnterEnabled) return;
        if (configuration.DoorbellEnterSound)
            SoundEngine.Play(ResolveSound(configuration.DoorbellEnterSoundPath, "Sounds/Doorbell/doorbell.wav"), configuration.DoorbellEnterSoundVolume);
        if (configuration.DoorbellEnterChat)
            PrintDoorbellChat(name, worldId, " has come inside.");
    }

    private void OnDoorbellLeft(string name, string _, uint worldId)
    {
        if (!configuration.DoorbellLeaveEnabled) return;
        if (configuration.DoorbellLeaveSound)
            SoundEngine.Play(ResolveSound(configuration.DoorbellLeaveSoundPath, "Sounds/Doorbell/leave.wav"), configuration.DoorbellLeaveSoundVolume);
        if (configuration.DoorbellLeaveChat)
            PrintDoorbellChat(name, worldId, " has left the house.");
    }

    private void OnDoorbellAlreadyHere(List<(string Name, string World, uint WorldId)> players)
    {
        if (!configuration.DoorbellAlreadyHereEnabled) return;
        if (configuration.DoorbellAlreadyHereSound)
            SoundEngine.Play(ResolveSound(configuration.DoorbellAlreadyHereSoundPath, "Sounds/Doorbell/doorbell.wav"), configuration.DoorbellAlreadyHereSoundVolume);
        if (configuration.DoorbellAlreadyHereChat)
            foreach (var p in players)
                PrintDoorbellChat(p.Name, p.WorldId, " was here when you arrived.");
    }

    private void PrintDoorbellChat(string name, uint worldId, string suffix)
    {
        ChatGui.Print(new XivChatEntry
        {
            Message = new SeStringBuilder()
                .Add(new PlayerPayload(name, worldId))
                .AddText(suffix)
                .Build()
        });
    }

    private string ResolveSound(string configPath, string defaultRelative) =>
        SoundEngine.Resolve(configPath, defaultRelative, PluginInterface.AssemblyLocation.DirectoryName!);

    private void CycleCharacter(int direction)
    {
        var player = ObjectTable[0] as IPlayerCharacter;
        if (player == null) { ChatGui.PrintError("[HF] Not logged in."); return; }

        var world = player.HomeWorld.ValueNullable?.Name.ExtractText();
        if (string.IsNullOrEmpty(world)) { ChatGui.PrintError("[HF] Could not determine home world."); return; }

        var slotted = characterDb.GetByWorld(world).Where(r => r.Slot > 0).ToList();
        if (slotted.Count == 0) { ChatGui.PrintError($"[HF] No characters with known slots found for {world}."); return; }

        var currentKey = $"{player.Name.TextValue}@{world}";
        int idx = slotted.FindIndex(r => r.Key == currentKey);
        if (idx < 0) idx = 0;

        int nextIdx = (idx + direction + slotted.Count) % slotted.Count;
        var next = slotted[nextIdx];
        SwitchToCharacter(next.Name, next.World);
    }

    private void GoToBid(CharacterRecord rec, HousingBidRecord bid)
    {
        var args = $"{rec.World}, {bid.District}, ward {bid.Ward}, plot {bid.Plot}";

        var player     = ObjectTable[0] as IPlayerCharacter;
        var currentKey = player != null
            ? $"{player.Name.TextValue}@{player.HomeWorld.ValueNullable?.Name.ExtractText()}"
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

    private bool IsAlreadyInBidLocation(HousingBidRecord bid) =>
        HousingDistricts.TerritoryIds.TryGetValue(bid.District, out var expected) &&
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
        try
        {
            await loginInfoHandler.QuickSaveAsync();
            // IPC must be called on the framework thread; after the async save we may be on a pool thread
            await Framework.RunOnFrameworkThread(() =>
            {
                Common.ShowToast(
                    "Swap character",
                    $"Switching to {name} ({world})"
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
        // User navigated to the character select screen manually, dismiss the picker
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
        var player = ObjectTable[0] as IPlayerCharacter;
        if (player != null)
        {
            lastKnownName  = player.Name.TextValue;
            lastKnownWorld = player.HomeWorld.ValueNullable?.Name.ExtractText();
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
                if (bid != null && zone.HasValue && zone.Value.district == bid.District && zone.Value.ward == bid.Ward)
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

    private unsafe AtkValue* OnNormalCraftCallback(AtkModuleInterface.AtkEventInterface* thisPtr, AtkValue* returnValue, AtkValue* values, uint valueCount, ulong eventKind)
    {
        if (configuration.ClientFlashOnSynthesis && valueCount > 0 && values[0].Int == -2)
            FlashTaskbar();
        return normalCraftHook!.Original(thisPtr, returnValue, values, valueCount, eventKind);
    }

    private unsafe void OnSynthesisSimpleRefresh(AddonEvent type, AddonArgs args)
    {
        if (!configuration.ClientFlashOnSynthesis) return;
        if (args is not AddonRefreshArgs refreshArgs) return;
        if (refreshArgs.AtkValueCount < 5) return;
        var values = (AtkValue*)refreshArgs.AtkValues;
        if (values[3].UInt == 0 || values[3].UInt != values[4].UInt) return;
        FlashTaskbar();
    }

    private unsafe void OnReadyCheckInitiated(AgentReadyCheck* self)
    {
        readyCheckHook!.Original(self);
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
        var player = ObjectTable[0] as IPlayerCharacter;
        if (player == null) { ApplyClientTitle(); return; }
        var world  = player.CurrentWorld.ValueNullable?.Name.ExtractText() ?? "";
        var prefix = configuration.ClientTitlePrefix.Trim();
        SetWindowText(windowHandle, string.IsNullOrEmpty(prefix)
            ? $"{player.Name.TextValue} @ {world}"
            : $"{prefix} / {player.Name.TextValue} @ {world}");
    }

    private void OnClientTitleFrameworkUpdate(IFramework fw)
    {
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
        readyCheckHook?.Dispose();
        normalCraftHook?.Dispose();
        AddonLifecycle.UnregisterListener(AddonEvent.PostRefresh, "SynthesisSimple", OnSynthesisSimpleRefresh);
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
        PluginInterface.UiBuilder.Draw -= nearbyWindow.DrawMarkers;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi   -= OpenMainUi;
        windowSystem.RemoveAllWindows();
        charaSelectHandler.Dispose();
        housingLotteryHandler.Dispose();
        serverInfoHandler.Dispose();
        repairHandler.Dispose();
        noKillHandler.Dispose();
        physicsHandler.Dispose();
        antiAfkHandler.Dispose();
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
        dutyTimerHandler.Dispose();
        castBarHandler.Dispose();
        loginEnhancementHandler.Dispose();
        foodCheckHandler.Dispose();
        foodCheckOverlay.Dispose();
        titleFont.Dispose();
        characterDb.Dispose();
    }
}
