using System;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace HoliestFluffiness;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool AccessoryEnabled { get; set; } = true;
    public int AccessoryEquipDelay { get; set; } = 5;
    public int AccessoryInventory { get; set; } = 0;
    public string AccessoryName { get; set; } = "Angel Wings";
    public bool InfoEnabled { get; set; } = true;

    private IDalamudPluginInterface pluginInterface = null!;

    public void Initialize(IDalamudPluginInterface pi)
    {
        pluginInterface = pi;
    }

    public void Save()
    {
        pluginInterface.SavePluginConfig(this);
    }
}
