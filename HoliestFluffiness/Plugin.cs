using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Chat;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Hooking;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using HoliestFluffiness.Handlers;
using Dalamud.Game.ClientState.Objects.SubKinds;
using HoliestFluffiness.Windows;
using System.Reflection;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

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
    [PluginService] private INotificationManager NotificationManager { get; init; } = null!;
    [PluginService] private IGameInteropProvider GameInterop { get; init; } = null!;
    [PluginService] private IDtrBar DtrBar { get; init; } = null!;
    [PluginService] private ISigScanner SigScanner { get; init; } = null!;
    [PluginService] private IGameGui GameGui { get; init; } = null!;
    [PluginService] private IPartyList PartyList { get; init; } = null!;
    [PluginService] private ITargetManager TargetManager { get; init; } = null!;

    private readonly Configuration configuration;
    private readonly WindowSystem windowSystem = new("HoliestFluffiness");
    private readonly ConfigWindow configWindow;
    private readonly LoginInfoWindow loginInfoWindow;
    private readonly NoKillWindow noKillWindow;
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
    private readonly CommendationHandler commendationHandler;
    private readonly DoorbellHandler doorbellHandler;

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
    private string? pendingLifestreamArgs;
    private string? lastKnownName;
    private string? lastKnownWorld;

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
        loginInfoHandler    = new LoginInfoHandler(configuration, ChatGui, Framework, ObjectTable, loginInfoWindow, characterDb, Log, NotificationManager);
        noKillHandler          = new NoKillHandler(configuration, SigScanner, GameInterop, Log);
        physicsHandler         = new PhysicsHandler(configuration, SigScanner, Framework, GameInterop, Log);
        antiAfkHandler         = new AntiAfkHandler(configuration, Framework, Log, windowHandle);
        readyCheckHandler      = new ReadyCheckHandler(configuration, GameInterop, ClientState, ChatGui, Framework, ObjectTable, Log);
        readyCheckOverlay      = new ReadyCheckOverlay(configuration, readyCheckHandler, GameGui, TextureProvider, DataManager);
        noKillWindow           = new NoKillWindow();
        noKillHandler.OnLobbyError += (isAuth) =>
        {
            if (!configuration.NoKillDisablePopup) noKillWindow.Show(noKillHandler.InterceptCount, lastKnownName, lastKnownWorld);
            if (!isAuth && !string.IsNullOrEmpty(lastKnownName) && !string.IsNullOrEmpty(lastKnownWorld))
                noKillHandler.SetAutoLoginTarget(lastKnownName, lastKnownWorld);
        };
        charaSelectHandler     = new CharaSelectHandler(configuration, characterDb, AddonLifecycle, DataManager, Framework, noKillHandler, SwitchToCharacter);
        housingLotteryHandler  = new HousingLotteryHandler(characterDb, AddonLifecycle, AddonEventManager, ObjectTable, ChatGui, NotificationManager, Log);
        serverInfoHandler      = new ServerInfoHandler(configuration, DtrBar, Framework, ClientState, ObjectTable, Log);
        repairHandler          = new RepairHandler(configuration, SigScanner, GameInterop, AddonLifecycle, ClientState, Log);
        nearbyHandler          = new NearbyHandler(configuration, ObjectTable, Framework, PartyList, TargetManager);
        nearbyHandler.NewTargeter += OnNewTargeter;
        commendationHandler    = new CommendationHandler(configuration, ClientState, Framework, PartyList);
        commendationHandler.OnCommendation += OnCommendationReceived;
        doorbellHandler        = new DoorbellHandler(configuration, ClientState, ObjectTable, Framework);
        doorbellHandler.OnEntered     += OnDoorbellEntered;
        doorbellHandler.OnLeft        += OnDoorbellLeft;
        doorbellHandler.OnAlreadyHere += OnDoorbellAlreadyHere;
        nearbyWindow           = new NearbyWindow(configuration, nearbyHandler, ObjectTable, TargetManager, Condition, CommandManager, GameGui);
        configWindow = new ConfigWindow(configuration, loginInfoHandler, accessoryHandler, repairHandler, noKillHandler, physicsHandler, antiAfkHandler, readyCheckHandler, ObjectTable, PluginInterface, characterDb, ClientState, SwitchToCharacter, GoToBid, UpdateClientTitle);
        configWindow.SetNearbyHandler(nearbyHandler);
        windowSystem.AddWindow(configWindow);
        windowSystem.AddWindow(loginInfoWindow);
        windowSystem.AddWindow(noKillWindow);
        windowSystem.AddWindow(readyCheckOverlay);
        windowSystem.AddWindow(nearbyWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open The Holiest Fluffiness settings. Use '/hf about' to open the about page."
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
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi   += OpenMainUi;

        var icon = TextureProvider.GetFromManifestResource(Assembly.GetExecutingAssembly(), "HoliestFluffiness.Images.menu_icon.png");
        TitleScreenMenu.AddEntry("Change Character", icon, OpenMainUi);

        ClientState.Login  += OnLogin;
        ClientState.Logout += OnLogout;

        ChatGui.ChatMessage += OnChatMessageFlash;
        unsafe
        {
            readyCheckHook = GameInterop.HookFromAddress<InitiateReadyCheckDelegate>(
                AgentReadyCheck.MemberFunctionPointers.InitiateReadyCheck,
                OnReadyCheckInitiated);
        }
        readyCheckHook.Enable();
    }

    private void OpenConfigUi() => configWindow.IsOpen = true;
    private void OpenMainUi()   { configWindow.IsOpen = true; configWindow.NavigateTo(5); }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("about", StringComparison.OrdinalIgnoreCase))
        {
            configWindow.IsOpen = true;
            configWindow.NavigateTo(7);
            return;
        }
        configWindow.IsOpen = !configWindow.IsOpen;
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

    private void OnDoorbellEntered(string name, string world, uint worldId)
    {
        if (!configuration.DoorbellEnterEnabled) return;
        if (configuration.DoorbellEnterSound)
            SoundEngine.Play(ResolveSound(configuration.DoorbellEnterSoundPath, "Sounds/Doorbell/doorbell.wav"), configuration.DoorbellEnterSoundVolume);
        if (configuration.DoorbellEnterChat)
            PrintDoorbellChat("<link> has come inside.", name, world, worldId);
    }

    private void OnDoorbellLeft(string name, string world, uint worldId)
    {
        if (!configuration.DoorbellLeaveEnabled) return;
        if (configuration.DoorbellLeaveSound)
            SoundEngine.Play(ResolveSound(configuration.DoorbellLeaveSoundPath, "Sounds/Doorbell/leave.wav"), configuration.DoorbellLeaveSoundVolume);
        if (configuration.DoorbellLeaveChat)
            PrintDoorbellChat("<link> has left the house.", name, world, worldId);
    }

    private void OnDoorbellAlreadyHere(List<(string Name, string World, uint WorldId)> players)
    {
        if (!configuration.DoorbellAlreadyHereEnabled) return;
        if (configuration.DoorbellAlreadyHereSound)
            SoundEngine.Play(ResolveSound(configuration.DoorbellAlreadyHereSoundPath, "Sounds/Doorbell/doorbell.wav"), configuration.DoorbellAlreadyHereSoundVolume);
        if (configuration.DoorbellAlreadyHereChat)
            foreach (var p in players)
                PrintDoorbellChat("<link> was here when you arrived.", p.Name, p.World, p.WorldId);
    }

    private void PrintDoorbellChat(string format, string name, string world, uint worldId)
    {
        var builder = new SeStringBuilder();
        var i = 0;
        while (i < format.Length)
        {
            if (format[i] == '<')
            {
                var end = format.IndexOf('>', i + 1);
                if (end > i)
                {
                    switch (format.Substring(i, end - i + 1))
                    {
                        case "<link>":
                            builder.Add(new PlayerPayload(name, worldId));
                            i = end + 1;
                            continue;
                        case "<name>":
                            builder.AddText(name);
                            i = end + 1;
                            continue;
                        case "<world>":
                            builder.AddText(world);
                            i = end + 1;
                            continue;
                    }
                }
            }
            builder.AddText(format[i].ToString());
            i++;
        }
        ChatGui.Print(new XivChatEntry { Message = builder.Build() });
    }

    private string ResolveSound(string configPath, string defaultRelative) =>
        string.IsNullOrEmpty(configPath)
            ? Path.Combine(PluginInterface.AssemblyLocation.DirectoryName!, defaultRelative)
            : configPath;

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
            InvokeLifestreamTeleport(args);
        else
        {
            pendingLifestreamArgs = args;
            SwitchToCharacter(rec.Name, rec.World);
        }
    }

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
        try
        {
            await loginInfoHandler.QuickSaveAsync();
            // IPC must be called on the framework thread; after the async save we may be on a pool thread
            await Framework.RunOnFrameworkThread(() =>
            {
                NotificationManager.AddNotification(new Dalamud.Interface.ImGuiNotification.Notification
                {
                    Content   = $"Switching to {name} ({world})",
                    Type      = Dalamud.Interface.ImGuiNotification.NotificationType.Info,
                    Minimized = false,
                });
                // Return type is ErrorCode enum, use object to avoid InvalidCastException
                PluginInterface.GetIpcSubscriber<string, string, object>("Lifestream.ChangeCharacter")
                               .InvokeFunc(name, world);
                loginInfoWindow.SetChangingState(name, world);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed during character switch to {Name}@{World}", name, world);
            await Framework.RunOnFrameworkThread(() =>
                ChatGui.PrintError($"[HF] Failed to switch to {name}@{world}. Is Lifestream installed and loaded?"));
        }
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

    private void OnLogin()
    {
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

            var tp = pendingLifestreamArgs;
            pendingLifestreamArgs = null;
            if (tp != null)
                await Framework.RunOnFrameworkThread(() => InvokeLifestreamTeleport(tp));

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

    private unsafe void OnReadyCheckInitiated(AgentReadyCheck* self)
    {
        readyCheckHook!.Original(self);
        if (configuration.ClientFlashOnReadyCheck)
            FlashTaskbar();
        readyCheckHandler.OnBegin();
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
        ChatGui.ChatMessage -= OnChatMessageFlash;
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
        characterDb.Dispose();
    }
}
