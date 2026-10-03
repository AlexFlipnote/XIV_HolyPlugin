using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace HoliestFluffiness;

public class AccessoryHandler(Configuration configuration, IChatGui chatGui, IFramework framework, IObjectTable objectTable)
{
    // True only when /fashion was actually sent, so callers know whether to give it time to land
    public async Task<bool> RunAsync(CancellationToken token)
    {
        if (!configuration.AccessoryEnabled) return false;

        bool alreadyEquipped = false;
        await framework.RunOnFrameworkThread(() => { alreadyEquipped = IsEquipped(); });

        if (alreadyEquipped)
        {
            await framework.RunOnFrameworkThread(() => chatGui.Print("Accessory already equipped, skipping"));
            return false;
        }

        if (configuration.AccessoryInventory >= 1 || configuration.AccessoryInventoryMin >= 1)
        {
            bool whitelisted = false;
            await framework.RunOnFrameworkThread(() =>
            {
                if (objectTable[0] is not IPlayerCharacter player) return;
                var key = Common.CharacterKey(player);
                whitelisted = configuration.AccessoryWhitelist.Contains(key);
            });

            if (!whitelisted)
            {
                int freeSlots = 0;
                await framework.RunOnFrameworkThread(() => { freeSlots = GetFreeInventorySlots(); });

                if (configuration.AccessoryInventory >= 1 && freeSlots <= configuration.AccessoryInventory)
                {
                    await framework.RunOnFrameworkThread(() => chatGui.Print("Not enough empty space, stopping equip"));
                    return false;
                }

                if (configuration.AccessoryInventoryMin >= 1 && freeSlots >= configuration.AccessoryInventoryMin)
                {
                    await framework.RunOnFrameworkThread(() => chatGui.Print("Too much empty space, stopping equip"));
                    return false;
                }
            }
        }

        token.ThrowIfCancellationRequested();

        await framework.RunOnFrameworkThread(() => Common.ExecuteCommand($"/fashion \"{configuration.AccessoryName}\""));
        return true;
    }

    // Polls until the accessory shows up on the player, or gives up after timeoutMs
    public async Task WaitForEquipAsync(int timeoutMs, CancellationToken token)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            bool equipped = false;
            await framework.RunOnFrameworkThread(() => { equipped = IsEquipped(); });
            if (equipped) return;
            await Task.Delay(250, token);
        }
    }

    private unsafe bool IsEquipped()
    {
        if (objectTable[0] is not IPlayerCharacter pc) return false;
        var bchara = (BattleChara*)pc.Address;
        return bchara->OrnamentData.OrnamentObject != null;
    }

    private static unsafe int GetFreeInventorySlots()
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return 0;
        return (int)manager->GetEmptySlotsInBag();
    }
}
