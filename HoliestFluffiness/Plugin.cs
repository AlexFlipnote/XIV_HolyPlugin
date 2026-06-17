using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using HoliestFluffiness.Handlers;
using Dalamud.Game.ClientState.Objects.SubKinds;
using HoliestFluffiness.Windows;
using System.Reflection;

namespace HoliestFluffiness;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName    = "/hf";
    private const string HwCommand     = "/hw";
    private const string HwPlusCommand = "/hw+";
    private const string HwMinCommand  = "/hw-";

    [PluginService] private IDalamudPluginInterface PluginInterface { get; init; } = null!;
    [PluginService] private IClientState ClientState { get; init; } = null!;
    [PluginService] private IChatGui ChatGui { get; init; } = null!;
    [PluginService] private IFramework Framework { get; init; } = null!;
    [PluginService] private IPluginLog Log { get; init; } = null!;
    [PluginService] private ICommandManager CommandManager { get; init; } = null!;
    [PluginService] private IObjectTable ObjectTable { get; init; } = null!;
    [PluginService] private IAddonLifecycle AddonLifecycle { get; init; } = null!;
    [PluginService] private IDataManager DataManager { get; init; } = null!;
    [PluginService] private ITitleScreenMenu TitleScreenMenu { get; init; } = null!;
    [PluginService] private ITextureProvider TextureProvider { get; init; } = null!;
    [PluginService] private INotificationManager NotificationManager { get; init; } = null!;

    private readonly Configuration configuration;
    private readonly WindowSystem windowSystem = new("HoliestFluffiness");
    private readonly ConfigWindow configWindow;
    private readonly LoginInfoWindow loginInfoWindow;
    private readonly AccessoryHandler accessoryHandler;
    private readonly LoginInfoHandler loginInfoHandler;
    private readonly CharacterDb characterDb;
    private readonly CharaSelectHandler charaSelectHandler;
    private readonly WotsitIpc wotsitIpc;

    private CancellationTokenSource? loginCts;
    private readonly object ctsLock = new();
    private string? pendingLifestreamArgs;

    public Plugin()
    {
        configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        configuration.Initialize(PluginInterface);

        var dbPath = Path.Combine(PluginInterface.GetPluginConfigDirectory(), "storage.db");
        characterDb = new CharacterDb(dbPath);

        accessoryHandler    = new AccessoryHandler(configuration, ChatGui, Framework, ObjectTable);
        loginInfoWindow     = new LoginInfoWindow(() => { configWindow!.IsOpen = true; configWindow.NavigateTo(3); });
        loginInfoHandler    = new LoginInfoHandler(configuration, ChatGui, Framework, ObjectTable, loginInfoWindow, characterDb, Log, NotificationManager);
        charaSelectHandler  = new CharaSelectHandler(configuration, characterDb, AddonLifecycle, DataManager, Framework);
        wotsitIpc              = new WotsitIpc(PluginInterface, Log, characterDb, SwitchToCharacter);
        characterDb.Changed   += wotsitIpc.RegisterAll;

        configWindow = new ConfigWindow(configuration, loginInfoHandler, accessoryHandler, ObjectTable, PluginInterface, characterDb, ClientState, SwitchToCharacter, wotsitIpc, GoToBid);
        windowSystem.AddWindow(configWindow);
        windowSystem.AddWindow(loginInfoWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open The Holiest Fluffiness settings."
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

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi   += OpenMainUi;

        var icon = TextureProvider.GetFromManifestResource(Assembly.GetExecutingAssembly(), "HoliestFluffiness.images.menu_icon.png");
        TitleScreenMenu.AddEntry("Holy Plugin", icon, OpenMainUi);

        ClientState.Login  += OnLogin;
        ClientState.Logout += OnLogout;
    }

    private void OpenConfigUi() => configWindow.IsOpen = true;
    private void OpenMainUi()   { configWindow.IsOpen = true; configWindow.NavigateTo(3); }

    private void OnCommand(string command, string args) =>
        configWindow.IsOpen = !configWindow.IsOpen;

    private void OnHwCommand(string command, string args)
    {
        var trimmed = args.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            configWindow.IsOpen = true;
            configWindow.NavigateTo(3);
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
        lock (ctsLock)
        {
            loginCts?.Cancel();
            loginCts?.Dispose();
            loginCts = null;
        }
    }

    private void OnLogin()
    {
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
                bool ready = false;
                await Framework.RunOnFrameworkThread(() => { ready = ClientState.IsLoggedIn; });
                if (ready) break;
                await Task.Delay(1000, token);
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

    public void Dispose()
    {
        ClientState.Login  -= OnLogin;
        ClientState.Logout -= OnLogout;
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
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi   -= OpenMainUi;
        windowSystem.RemoveAllWindows();
        characterDb.Changed   -= wotsitIpc.RegisterAll;
        charaSelectHandler.Dispose();
        wotsitIpc.Dispose();
        characterDb.Dispose();
    }
}
