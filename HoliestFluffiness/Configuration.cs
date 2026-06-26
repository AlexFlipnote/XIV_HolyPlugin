using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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

    // Character picker section
    public bool CharacterPickerOnMainMenu { get; set; } = false;

    // NoKill section
    public bool NoKillEnabled { get; set; } = true;
    public bool NoKillDisablePopup { get; set; } = false;

    // Physics section
    public bool PhysicsEnabled { get; set; } = false;
    public float PhysicsTargetFps { get; set; } = 60f;

    // Anti-AFK section
    public bool AntiAfkEnabled { get; set; } = false;
    public int AntiAfkTimerLimit { get; set; } = 30;

    // Party section
    public bool ReadyCheckShowNames { get; set; } = false;
    public bool ReadyCheckDrawOverlay { get; set; } = false;
    public int ReadyCheckClearAfterSeconds { get; set; } = 10;

    // Nearby section
    public bool    NearbyEnabled             { get; set; } = false;
    public bool    NearbyDtrEnabled          { get; set; } = false;
    public bool    NearbyShowTargeters       { get; set; } = true;
    public bool    NearbyTargeterTrackSelf   { get; set; } = false;
    public bool    NearbyHideInCombat        { get; set; } = false;
    public bool    NearbyHideInDuty          { get; set; } = true;
    public bool    NearbyFilterAfk           { get; set; } = false;
    public bool    NearbyFilterLowLevel      { get; set; } = false;
    public bool    NearbyDebugSelf           { get; set; } = false;
    public int     NearbyDebugSelfAs         { get; set; } = 0; // 0=Friend 1=FC 2=Party 3=TargetingYou
    public Vector4 NearbyColParty            { get; set; } = new(100/255f, 180/255f, 255/255f, 1f);
    public Vector4 NearbyColFriend           { get; set; } = new(1f, 127/255f, 0f, 1f);
    public Vector4 NearbyColLocalFc          { get; set; } = new(220/255f, 200/255f,  80/255f, 1f);
    public bool    NearbyMarkTargeting        { get; set; } = false;
    public Vector4 NearbyMarkTargetingColour { get; set; } = new(235/255f, 130/255f, 80/255f, 1f);
    public int     NearbyMarkTargetingSize   { get; set; } = 5;
    public bool    NearbyTargeterSound       { get; set; } = false;
    public string  NearbyTargeterSoundPath   { get; set; } = "";
    public float   NearbyTargeterSoundVolume { get; set; } = 0.5f;

    // Congratulations section
    public bool   CommendationEnabled           { get; set; } = false;
    public string CommendationOneThirdPath      { get; set; } = "";
    public float  CommendationOneThirdVolume    { get; set; } = 0.5f;
    public string CommendationTwoThirdsPath     { get; set; } = "";
    public float  CommendationTwoThirdsVolume   { get; set; } = 0.5f;
    public string CommendationThreeThirdsPath   { get; set; } = "";
    public float  CommendationThreeThirdsVolume { get; set; } = 0.5f;
    public string CommendationAllSevenPath      { get; set; } = "";
    public float  CommendationAllSevenVolume    { get; set; } = 0.5f;

    // Doorbell section
    public bool   DoorbellEnterEnabled       { get; set; } = false;
    public bool   DoorbellEnterChat          { get; set; } = false;
    public bool   DoorbellEnterSound         { get; set; } = false;
    public string DoorbellEnterSoundPath     { get; set; } = "";
    public float  DoorbellEnterSoundVolume   { get; set; } = 0.5f;
    public bool   DoorbellLeaveEnabled       { get; set; } = false;
    public bool   DoorbellLeaveChat          { get; set; } = false;
    public bool   DoorbellLeaveSound         { get; set; } = false;
    public string DoorbellLeaveSoundPath     { get; set; } = "";
    public float  DoorbellLeaveSoundVolume   { get; set; } = 0.5f;
    public bool   DoorbellAlreadyHereEnabled { get; set; } = false;
    public bool   DoorbellAlreadyHereChat    { get; set; } = false;
    public bool   DoorbellAlreadyHereSound   { get; set; } = false;
    public string DoorbellAlreadyHereSoundPath   { get; set; } = "";
    public float  DoorbellAlreadyHereSoundVolume { get; set; } = 0.5f;

    // Combat section
    // DC – Direct Critical Damage
    public bool   CombatDcEnabled   { get; set; } = false;
    public string CombatDcSound     { get; set; } = "";
    public float  CombatDcVol       { get; set; } = 0.5f;
    public bool   CombatDcShowText  { get; set; } = false;
    public string CombatDcText      { get; set; } = "DIRECT CRITICAL HIT!";
    // D – Direct Damage
    public bool   CombatDEnabled    { get; set; } = false;
    public string CombatDSound      { get; set; } = "";
    public float  CombatDVol        { get; set; } = 0.5f;
    public bool   CombatDShowText   { get; set; } = false;
    public string CombatDText       { get; set; } = "Mini crit!";
    // C – Critical Damage
    public bool   CombatCEnabled    { get; set; } = false;
    public string CombatCSound      { get; set; } = "";
    public float  CombatCVol        { get; set; } = 0.5f;
    public bool   CombatCShowText   { get; set; } = false;
    public string CombatCText       { get; set; } = "CRITICAL HIT!";
    // CHO – Critical Heal (own + own fairy)
    public bool   CombatChoEnabled  { get; set; } = false;
    public string CombatChoSound    { get; set; } = "";
    public float  CombatChoVol      { get; set; } = 0.5f;
    public bool   CombatChoShowText { get; set; } = false;
    public string CombatChoText     { get; set; } = "CRITICAL HEAL!";
    // CHT – Critical Heal (others + their fairies)
    public bool   CombatChtEnabled  { get; set; } = false;
    public string CombatChtSound    { get; set; } = "";
    public float  CombatChtVol      { get; set; } = 0.5f;
    public bool   CombatChtShowText { get; set; } = false;
    public string CombatChtText     { get; set; } = "THANK YOUR HEALER!";

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
