using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace HoliestFluffiness.Windows;

public class ConfigWindow : Window
{
    private readonly Configuration configuration;
    private readonly FcInfoHandler fcInfoHandler;
    private readonly AccessoryHandler accessoryHandler;

    private CancellationTokenSource? fcCts;
    private CancellationTokenSource? accessoryCts;

    public ConfigWindow(Configuration configuration, FcInfoHandler fcInfoHandler, AccessoryHandler accessoryHandler)
        : base("The Holiest Fluffiness##Config")
    {
        this.configuration = configuration;
        this.fcInfoHandler = fcInfoHandler;
        this.accessoryHandler = accessoryHandler;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 300),
            MaximumSize = new Vector2(600, 450),
        };
    }

    public override void Draw()
    {
        DrawFcInfoSection();
        ImGui.Separator();
        DrawAccessorySection();
    }

    private void DrawFcInfoSection()
    {
        var infoEnabled = configuration.InfoEnabled;
        if (ImGui.Checkbox("Show FC info on login", ref infoEnabled))
        {
            configuration.InfoEnabled = infoEnabled;
            configuration.Save();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(!infoEnabled);
        if (ImGui.Button("Test##fc"))
        {
            fcCts?.Cancel();
            fcCts?.Dispose();
            fcCts = new CancellationTokenSource();
            Task.Run(() => fcInfoHandler.RunAsync(fcCts.Token));
        }
        ImGui.EndDisabled();
    }

    private void DrawAccessorySection()
    {
        var accessoryEnabled = configuration.AccessoryEnabled;
        if (ImGui.Checkbox("Equip accessory on login", ref accessoryEnabled))
        {
            configuration.AccessoryEnabled = accessoryEnabled;
            configuration.Save();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(!accessoryEnabled);
        if (ImGui.Button("Test##accessory"))
        {
            accessoryCts?.Cancel();
            accessoryCts?.Dispose();
            accessoryCts = new CancellationTokenSource();
            Task.Run(() => accessoryHandler.RunAsync(accessoryCts.Token));
        }
        ImGui.EndDisabled();

        ImGui.BeginDisabled(!accessoryEnabled);

        var accessoryName = configuration.AccessoryName;
        ImGui.SetNextItemWidth(220);
        if (ImGui.InputText("Accessory name", ref accessoryName, 128))
        {
            configuration.AccessoryName = accessoryName;
            configuration.Save();
        }

        var accessoryEquipDelay = configuration.AccessoryEquipDelay;
        ImGui.SetNextItemWidth(100);
        if (ImGui.SliderInt("Equip delay (seconds)", ref accessoryEquipDelay, 1, 10))
        {
            configuration.AccessoryEquipDelay = accessoryEquipDelay;
            configuration.Save();
        }

        var accessoryInventory = configuration.AccessoryInventory;
        ImGui.SetNextItemWidth(100);
        if (ImGui.SliderInt("Min free inventory slots", ref accessoryInventory, 0, 140))
        {
            configuration.AccessoryInventory = accessoryInventory;
            configuration.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(0 = skip inventory check)");

        ImGui.EndDisabled();
    }

    public override void OnClose()
    {
        fcCts?.Cancel();
        accessoryCts?.Cancel();
    }
}
