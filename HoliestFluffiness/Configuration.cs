using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace HoliestFluffiness;

public enum LoginInfoDisplay { Echo = 0, Popup = 1, Toast = 2 }
public enum PingDisplay { Last = 0, Average = 1, Both = 2 }

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool AccessoryEnabled { get; set; } = false;
    public int AccessoryInventory { get; set; } = 0;
    public int AccessoryInventoryMin { get; set; } = 0;
    public string AccessoryName { get; set; } = "Angel Wings";
    public List<string> AccessoryWhitelist { get; set; } = [];
    public bool ShowCharacterInfo { get; set; } = true;
    public bool InfoEnabled { get; set; } = false;
    public bool AdventurePlateEnabled { get; set; } = false;
    public bool ShowPrivateHouseLocation { get; set; } = false;
    public bool ShowFcHouseLocation { get; set; } = false;
    public LoginInfoDisplay LoginInfoDisplay { get; set; } = LoginInfoDisplay.Echo;
    // Order of the 5 info items: 0=Character, 1=SearchInfo, 2=PrivateHouse, 3=FreeCompany, 4=FcHouse
    public List<int> LoginInfoOrder { get; set; } = [0, 1, 2, 3, 4];
    public bool CharactersDbEnabled { get; set; } = false;
    // Visibility of the 9 Characters table columns: LastSeen, Name, World, DC, FC, SearchInfo, PrivateHouse, FcHouse, Gil
    public bool[] CharactersColumns { get; set; } = [true, true, true, true, true, true, true, true, true];
    public int LastSelectedSection { get; set; } = 0;

    // Server info section
    public bool ServerInfoPingEnabled { get; set; } = false;
    public PingDisplay ServerInfoPingDisplay { get; set; } = PingDisplay.Last;
    public bool ServerInfoFpsEnabled { get; set; } = false;

    // Repair section
    public bool RepairLowEnabled { get; set; } = false;
    public float RepairLowThreshold { get; set; } = 50f;
    public bool RepairCriticalEnabled { get; set; } = true;
    public float RepairCriticalThreshold { get; set; } = 25f;

    // Client section
    public string ClientTitlePrefix { get; set; } = "";
    public bool ClientAppendNameOnLogin { get; set; } = false;
    public bool ClientFlashOnTell { get; set; } = false;
    public bool ClientFlashOnReadyCheck { get; set; } = false;

    // NoKill section
    public bool NoKillEnabled { get; set; } = true;
    public bool NoKillDisablePopup { get; set; } = false;

    // Physics section
    public bool PhysicsEnabled { get; set; } = false;
    public float PhysicsTargetFps { get; set; } = 60f;

    private IDalamudPluginInterface pluginInterface = null!;

    public void Initialize(IDalamudPluginInterface pi)
    {
        pluginInterface = pi;

        // Ensure order list is valid (covers upgrades from old saves)
        var expected = Enumerable.Range(0, 5).ToList();
        if (LoginInfoOrder.Count != 5 || !expected.All(LoginInfoOrder.Contains))
            LoginInfoOrder = expected;

        if (CharactersColumns == null || CharactersColumns.Length != 9)
            CharactersColumns = [true, true, true, true, true, true, true, true, true];
    }

    public void Save()
    {
        pluginInterface.SavePluginConfig(this);
    }
}
