using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace HoliestFluffiness;

public class AccessoryHandler(Configuration configuration, IChatGui chatGui, IFramework framework, IObjectTable objectTable)
{
    public async Task RunAsync(CancellationToken token)
    {
        if (!configuration.AccessoryEnabled) return;

        bool alreadyEquipped = false;
        await framework.RunOnFrameworkThread(() =>
        {
            if (objectTable[0] is not IPlayerCharacter pc) return;
            unsafe
            {
                var bchara = (BattleChara*)pc.Address;
                alreadyEquipped = bchara->OrnamentData.OrnamentObject != null;
            }
        });

        if (alreadyEquipped)
        {
            await framework.RunOnFrameworkThread(() => chatGui.Print("Accessory already equipped, skipping"));
            return;
        }

        if (configuration.AccessoryInventory >= 1 || configuration.AccessoryInventoryMin >= 1)
        {
            bool whitelisted = false;
            await framework.RunOnFrameworkThread(() =>
            {
                if (objectTable[0] is not IPlayerCharacter player) return;
                var key = $"{player.Name.TextValue}@{player.HomeWorld.ValueNullable?.Name.ExtractText()}";
                whitelisted = configuration.AccessoryWhitelist.Contains(key);
            });

            if (!whitelisted)
            {
                int freeSlots = 0;
                await framework.RunOnFrameworkThread(() => { freeSlots = GetFreeInventorySlots(); });

                if (configuration.AccessoryInventory >= 1 && freeSlots <= configuration.AccessoryInventory)
                {
                    await framework.RunOnFrameworkThread(() => chatGui.Print("Not enough empty space, stopping equip"));
                    return;
                }

                if (configuration.AccessoryInventoryMin >= 1 && freeSlots >= configuration.AccessoryInventoryMin)
                {
                    await framework.RunOnFrameworkThread(() => chatGui.Print("Too much empty space, stopping equip"));
                    return;
                }
            }
        }

        token.ThrowIfCancellationRequested();

        int delay = configuration.AccessoryEquipDelay;
        string name = configuration.AccessoryName;

        await framework.RunOnFrameworkThread(() => chatGui.Print($"Waiting {delay}s to equip '{name}'"));
        await Task.Delay(delay * 1000, token);

        token.ThrowIfCancellationRequested();

        await framework.RunOnFrameworkThread(() => ExecuteCommand($"/fashion \"{name}\""));
    }

    private static unsafe void ExecuteCommand(string command)
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null) return;
        var shellModule = uiModule->GetRaptureShellModule();
        if (shellModule == null) return;
        var str = Utf8String.FromString(command);
        shellModule->ExecuteCommandInner(str, uiModule);
        str->Dtor(true);
    }

    private static unsafe int GetFreeInventorySlots()
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return 0;
        return (int)manager->GetEmptySlotsInBag();
    }
}
