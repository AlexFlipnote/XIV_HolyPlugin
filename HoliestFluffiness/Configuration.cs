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
    // Exposed so Social.cs's "Set to default" button can reuse them
    public static readonly Vector4 DefaultNearbyColParty   = new(100/255f, 180/255f, 255/255f, 1f);
    public static readonly Vector4 DefaultNearbyColFriend  = new(1f, 127/255f, 0f, 1f);
    public static readonly Vector4 DefaultNearbyColLocalFc = new(220/255f, 200/255f, 80/255f, 1f);

    public int Version { get; set; } = 1;

    // Unlocked from the About page
    public bool  ThemeDisableCustom { get; set; } = false;
    public float ThemeOpacity       { get; set; } = 1f;

    public bool AccessoryEnabled { get; set; } = false;
    public int AccessoryInventory { get; set; } = 0;
    public int AccessoryInventoryMin { get; set; } = 0;
    public string AccessoryName { get; set; } = "Angel Wings";
    public List<string> AccessoryWhitelist { get; set; } = [];
    public bool ShowCharacterInfo { get; set; } = false;
    public bool InfoEnabled { get; set; } = false;
    public bool AdventurePlateEnabled { get; set; } = false;
    public bool ShowPrivateHouseLocation { get; set; } = false;
    public bool ShowFcHouseLocation { get; set; } = false;
    public LoginInfoDisplay LoginInfoDisplay { get; set; } = LoginInfoDisplay.Echo;
    // Order of the 5 info items: 0=Character, 1=SearchInfo, 2=PrivateHouse, 3=FreeCompany, 4=FcHouse
    public List<int> LoginInfoOrder { get; set; } = [0, 1, 2, 3, 4];
    public bool CharactersDbEnabled { get; set; } = false;
    public bool CharactersDbShortenNumbers { get; set; } = false;
    public bool FcPointsTrackingEnabled { get; set; } = false;
    public int LastSelectedSection { get; set; } = 0;

    public bool ServerInfoPingEnabled { get; set; } = false;
    public PingDisplay ServerInfoPingDisplay { get; set; } = PingDisplay.Last;
    public bool ServerInfoFpsEnabled { get; set; } = false;

    public bool RepairLowEnabled { get; set; } = false;
    public float RepairLowThreshold { get; set; } = 50f;
    public bool RepairCriticalEnabled { get; set; } = false;
    public float RepairCriticalThreshold { get; set; } = 25f;

    public bool AlwaysYesEnabled { get; set; } = false;
    public bool AltF4ExitEnabled { get; set; } = false;
    public string ClientTitlePrefix { get; set; } = "";
    public bool ClientAppendNameOnLogin { get; set; } = false;
    public bool ClientFlashOnTell      { get; set; } = false;
    public bool ClientFlashOnReadyCheck { get; set; } = false;
    public bool ClientFlashOnAlarm     { get; set; } = false;
    public bool ClientFlashOnCombat    { get; set; } = false;
    public bool ClientFlashOnCountdown { get; set; } = false;
    public bool TitleMovieDisabled { get; set; } = false;
    public bool HotbarLockHidden { get; set; } = false;
    public bool FastMouseClickFixEnabled { get; set; } = false;
    public bool DrawSheatheEmoteEnabled { get; set; } = false;
    public bool CharacterPickerOnMainMenu { get; set; } = false;

    public bool NoKillEnabled { get; set; } = false;
    public bool NoKillDisablePopup { get; set; } = false;

    public bool PhysicsEnabled { get; set; } = false;
    public float PhysicsTargetFps { get; set; } = 60f;

    public bool AntiAfkEnabled { get; set; } = false;
    public int AntiAfkTimerLimit { get; set; } = 30;
    public bool AntiAfkRespectManualAfk { get; set; } = false;

    public bool DutyTimerEnabled       { get; set; } = false;
    public bool CastBarAetheryteEnabled { get; set; } = false;

    public bool  LootFadeEnabled { get; set; } = false;
    public float LootFadePercent { get; set; } = 0.5f;

    public bool HideMpBarsPartyList   { get; set; } = false;
    public bool HideMpBarsParamWidget { get; set; } = false;

    public bool ReadyCheckShowNames { get; set; } = false;
    public bool ReadyCheckDrawOverlay { get; set; } = false;
    public int ReadyCheckClearAfterSeconds { get; set; } = 10;

    public bool    NearbyDtrEnabled          { get; set; } = false;
    public bool    NearbyShowTargeters       { get; set; } = false;
    public bool    NearbyTargeterTrackSelf   { get; set; } = false;
    public bool    NearbyHideInCombat        { get; set; } = false;
    public bool    NearbyHideInDuty          { get; set; } = false;
    public bool    NearbyFilterAfk           { get; set; } = false;
    public bool    NearbyFilterLowLevel      { get; set; } = false;
    public bool    NearbyDebugSelf           { get; set; } = false;
    public int     NearbyDebugSelfAs         { get; set; } = 0; // 0=Normal 1=Friend 2=FC 3=Party 4=TargetingYou
    public Vector4 NearbyColParty            { get; set; } = DefaultNearbyColParty;
    public Vector4 NearbyColFriend           { get; set; } = DefaultNearbyColFriend;
    public Vector4 NearbyColLocalFc          { get; set; } = DefaultNearbyColLocalFc;
    public bool    NearbyColorJobs           { get; set; } = false;
    public bool    NearbyMarkTargeting        { get; set; } = false;
    public Vector4 NearbyMarkTargetingColour { get; set; } = new(235/255f, 130/255f, 80/255f, 1f);
    public int     NearbyMarkTargetingSize   { get; set; } = 5;
    public bool    NearbyTargeterSound       { get; set; } = false;
    public string  NearbyTargeterSoundPath   { get; set; } = "";
    public float   NearbyTargeterSoundVolume { get; set; } = 0.5f;

    public bool   CommendationEnabled           { get; set; } = false;
    public string CommendationOneThirdPath      { get; set; } = "";
    public float  CommendationOneThirdVolume    { get; set; } = 0.5f;
    public string CommendationTwoThirdsPath     { get; set; } = "";
    public float  CommendationTwoThirdsVolume   { get; set; } = 0.5f;
    public string CommendationThreeThirdsPath   { get; set; } = "";
    public float  CommendationThreeThirdsVolume { get; set; } = 0.5f;
    public string CommendationAllSevenPath      { get; set; } = "";
    public float  CommendationAllSevenVolume    { get; set; } = 0.5f;

    public bool   DoorbellEnterChat          { get; set; } = false;
    public bool   DoorbellEnterSound         { get; set; } = false;
    public string DoorbellEnterSoundPath     { get; set; } = "";
    public float  DoorbellEnterSoundVolume   { get; set; } = 1.0f;
    public bool   DoorbellLeaveChat          { get; set; } = false;
    public bool   DoorbellLeaveSound         { get; set; } = false;
    public string DoorbellLeaveSoundPath     { get; set; } = "";
    public float  DoorbellLeaveSoundVolume   { get; set; } = 1.0f;
    public bool   DoorbellAlreadyHereChat    { get; set; } = false;
    public bool   DoorbellAlreadyHereSound   { get; set; } = false;
    public string DoorbellAlreadyHereSoundPath   { get; set; } = "";
    public float  DoorbellAlreadyHereSoundVolume { get; set; } = 1.0f;

    // "<player>" becomes a clickable player link when printed
    public const string DefaultDoorbellEnterText       = "<player> has come inside.";
    public const string DefaultDoorbellLeaveText       = "<player> has left the house.";
    public const string DefaultDoorbellAlreadyHereText = "<player> was here when you arrived.";
    public string DoorbellEnterText       { get; set; } = DefaultDoorbellEnterText;
    public string DoorbellLeaveText       { get; set; } = DefaultDoorbellLeaveText;
    public string DoorbellAlreadyHereText { get; set; } = DefaultDoorbellAlreadyHereText;

    public bool DynamicTravelerEnabled    { get; set; } = false;

    public bool LoginSkipLogo    { get; set; } = false;
    public bool PreloadTerritory { get; set; } = false;

    public bool   FoodCheckEcho         { get; set; } = false;
    public bool   FoodCheckHighlight    { get; set; } = false;
    public bool   FoodCheckSound        { get; set; } = false;
    public string FoodCheckSoundPath    { get; set; } = "";
    public float  FoodCheckSoundVolume  { get; set; } = 0.5f;
    public int    FoodCheckThreshold    { get; set; } = 15;
    public bool FoodCheckScopeHighEnd  { get; set; } = false;
    public bool FoodCheckScopeSavage   { get; set; } = false;
    public bool FoodCheckScopeExtreme  { get; set; } = false;
    public bool FoodCheckScopeAny      { get; set; } = false;

    // DC - Direct Critical Damage
    public bool   CombatDcEnabled   { get; set; } = false;
    public string CombatDcSound     { get; set; } = "";
    public float  CombatDcVol       { get; set; } = 0.5f;
    public bool   CombatDcShowText  { get; set; } = false;
    public string CombatDcText      { get; set; } = "DIRECT CRITICAL HIT!";
    // D - Direct Damage
    public bool   CombatDEnabled    { get; set; } = false;
    public string CombatDSound      { get; set; } = "";
    public float  CombatDVol        { get; set; } = 0.5f;
    public bool   CombatDShowText   { get; set; } = false;
    public string CombatDText       { get; set; } = "Mini crit!";
    // C - Critical Damage
    public bool   CombatCEnabled    { get; set; } = false;
    public string CombatCSound      { get; set; } = "";
    public float  CombatCVol        { get; set; } = 0.5f;
    public bool   CombatCShowText   { get; set; } = false;
    public string CombatCText       { get; set; } = "CRITICAL HIT!";
    // CHO - Critical Heal (own + own fairy)
    public bool   CombatChoEnabled  { get; set; } = false;
    public string CombatChoSound    { get; set; } = "";
    public float  CombatChoVol      { get; set; } = 0.5f;
    public bool   CombatChoShowText { get; set; } = false;
    public string CombatChoText     { get; set; } = "CRITICAL HEAL!";
    // CHT - Critical Heal (others + their fairies)
    public bool   CombatChtEnabled  { get; set; } = false;
    public string CombatChtSound    { get; set; } = "";
    public float  CombatChtVol      { get; set; } = 0.5f;
    public bool   CombatChtShowText { get; set; } = false;
    public string CombatChtText     { get; set; } = "THANK YOUR HEALER!";

    private IDalamudPluginInterface pluginInterface = null!;

    public void Initialize(IDalamudPluginInterface pi)
    {
        pluginInterface = pi;

        // Covers upgrades from old saves
        var expected = Enumerable.Range(0, 5).ToList();
        if (LoginInfoOrder.Count != 5 || !expected.All(LoginInfoOrder.Contains))
            LoginInfoOrder = expected;
    }

    public void Save()
    {
        pluginInterface.SavePluginConfig(this);
    }
}
