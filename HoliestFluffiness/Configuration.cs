using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace HoliestFluffiness;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool AccessoryEnabled { get; set; } = false;
    public int AccessoryEquipDelay { get; set; } = 5;
    public int AccessoryInventory { get; set; } = 0;
    public string AccessoryName { get; set; } = "Angel Wings";
    public List<string> AccessoryWhitelist { get; set; } = [];
    public bool ShowCharacterInfo { get; set; } = true;
    public bool InfoEnabled { get; set; } = false;
    public bool AdventurePlateEnabled { get; set; } = false;
    public bool ShowPrivateHouseLocation { get; set; } = false;
    public bool ShowFcHouseLocation { get; set; } = false;
    public bool LoginInfoAsPopup { get; set; } = false;
    // Order of the 5 info items: 0=Character, 1=SearchInfo, 2=PrivateHouse, 3=FreeCompany, 4=FcHouse
    public List<int> LoginInfoOrder { get; set; } = [0, 1, 2, 3, 4];
    public bool CharactersDbEnabled { get; set; } = false;
    // Visibility of the 9 Characters table columns: LastSeen, Name, World, DC, FC, SearchInfo, PrivateHouse, FcHouse, Gil
    public List<bool> CharactersColumns { get; set; } = [true, true, true, true, true, true, true, true, true];
    public int LastSelectedSection { get; set; } = 0;

    private IDalamudPluginInterface pluginInterface = null!;

    public void Initialize(IDalamudPluginInterface pi)
    {
        pluginInterface = pi;

        // Ensure order list is valid (covers upgrades from old saves)
        var expected = Enumerable.Range(0, 5).ToList();
        if (LoginInfoOrder.Count != 5 || !expected.All(LoginInfoOrder.Contains))
            LoginInfoOrder = expected;

        if (CharactersColumns.Count != 9)
            CharactersColumns = [true, true, true, true, true, true, true, true, true];
    }

    public void Save()
    {
        pluginInterface.SavePluginConfig(this);
    }
}
