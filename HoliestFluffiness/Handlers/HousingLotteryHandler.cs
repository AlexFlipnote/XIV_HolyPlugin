using System;
using System.Text.RegularExpressions;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Events.EventDataTypes;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace HoliestFluffiness.Handlers;

public sealed class HousingLotteryHandler : IDisposable
{
    private readonly CharacterDb characterDb;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IAddonEventManager addonEventManager;
    private readonly IObjectTable objectTable;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;

    private IAddonEventHandle? _resultYesHandle;

    private const string AddonName = "ContentsInfoDetail";

    // AtkValues indices for the housing lottery entry block
    private const int IdxStatus   = 119;
    private const int IdxLocation = 121;
    private const int IdxBidNum   = 122;
    private const int IdxBidType  = 123;

    // Deduplication: only process when the key fields change
    private string _lastLocation = string.Empty;
    private string _lastBidNum   = string.Empty;
    private string _lastStatus   = string.Empty;

    // Captured from SelectYesNoTextScroll (bid confirmation dialog) before chat message fires
    private BidType _pendingBidType = BidType.Private;

    private static readonly Regex LocationRx = new(@"Plot (\d+), (\d+)\w+ Ward, ([^(\r\n]+)", RegexOptions.Compiled);
    private static readonly Regex BidNumRx   = new(@"Lottery Number:\s*(\d+)", RegexOptions.Compiled);
    // "submitted a lottery entry for plot 4, ward 7, Shirogane. Your lottery number is 3."
    private static readonly Regex SubmitRx   = new(@"lottery entry for plot (\d+), ward (\d+), ([^.]+)\. Your lottery number is (\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public HousingLotteryHandler(
        CharacterDb characterDb, IAddonLifecycle addonLifecycle, IAddonEventManager addonEventManager,
        IObjectTable objectTable, IChatGui chatGui, IPluginLog log)
    {
        this.characterDb       = characterDb;
        this.addonLifecycle    = addonLifecycle;
        this.addonEventManager = addonEventManager;
        this.objectTable       = objectTable;
        this.chatGui           = chatGui;
        this.log               = log;

        addonLifecycle.RegisterListener(AddonEvent.PostSetup,    AddonName, OnAddon);
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh,  AddonName, OnAddon);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize,  AddonName, OnAddonFinalize);
        addonLifecycle.RegisterListener(AddonEvent.PostSetup,    "SelectYesNoTextScroll", OnBidConfirmSetup);
        addonLifecycle.RegisterListener(AddonEvent.PostSetup,    "SelectYesno", OnResultYesnoSetup);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize,  "SelectYesno", OnResultYesnoFinalize);
        chatGui.ChatMessage += OnChatMessage;
    }

    private unsafe void OnResultYesnoSetup(AddonEvent type, AddonArgs args)
    {
        if (args.Addon.IsNull) return;
        var addon = (AddonSelectYesno*)(void*)args.Addon.Address;
        if (addon == null || addon->AtkValuesCount < 1) return;
        var text = ReadAtkString(&addon->AtkValues[0]);

        bool isResult = text.Contains("winner of this lottery",   StringComparison.OrdinalIgnoreCase)
                     || text.Contains("full refund of your deposit", StringComparison.OrdinalIgnoreCase)
                     || text.Contains("better luck in the future",   StringComparison.OrdinalIgnoreCase);
        if (!isResult) return;

        if (addon->YesButton == null || addon->YesButton->OwnerNode == null) return;
        if (_resultYesHandle != null) { addonEventManager.RemoveEvent(_resultYesHandle); _resultYesHandle = null; }
        _resultYesHandle = addonEventManager.AddEvent(
            (nint)addon, (nint)addon->YesButton->OwnerNode,
            AddonEventType.ButtonClick, OnResultYesClicked);
        log.Debug("[HousingLottery] Result dialog detected, hooked Yes button.");
    }

    private void OnAddonFinalize(AddonEvent type, AddonArgs args)
    {
        _lastLocation = string.Empty;
        _lastBidNum   = string.Empty;
        _lastStatus   = string.Empty;
    }

    private void OnResultYesnoFinalize(AddonEvent type, AddonArgs args)
    {
        if (_resultYesHandle == null) return;
        addonEventManager.RemoveEvent(_resultYesHandle);
        _resultYesHandle = null;
    }

    private void OnResultYesClicked(AddonEventType type, AddonEventData data)
    {
        if (objectTable[0] is not IPlayerCharacter player) return;
        var world = Common.WorldName(player);
        if (string.IsNullOrEmpty(world)) return;
        var charKey = $"{player.Name.TextValue}@{world}";

        var bids = characterDb.GetBidsByCharacter(charKey);
        foreach (var bid in bids)
            characterDb.DeleteBid(bid.Id);

        if (bids.Count > 0)
        {
            Notify("Lottery bid removed.");
            log.Debug("[HousingLottery] Removed bid(s) for {Key} via result dialog Yes.", charKey);
        }
    }

    private unsafe void OnBidConfirmSetup(AddonEvent type, AddonArgs args)
    {
        if (args.Addon.IsNull) return;
        var addon = (AtkUnitBase*)(void*)args.Addon.Address;
        if (addon == null || addon->AtkValuesCount < 1) return;
        var text = ReadAtkString(&addon->AtkValues[0]);
        _pendingBidType = text.Contains("free company", StringComparison.OrdinalIgnoreCase)
            ? BidType.FC : BidType.Private;
        log.Debug("[HousingLottery] Bid confirmation: type={T}", _pendingBidType);
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        if (args.Addon.IsNull) return;
        unsafe
        {
            var addon = (AtkUnitBase*)(void*)args.Addon.Address;
            if (addon == null || addon->AtkValuesCount <= IdxBidType) return;

            var status   = ReadAtkString(&addon->AtkValues[IdxStatus]);
            var location = ReadAtkString(&addon->AtkValues[IdxLocation]);
            var bidNum   = ReadAtkString(&addon->AtkValues[IdxBidNum]);
            var bidType  = ReadAtkString(&addon->AtkValues[IdxBidType]);

            if (string.IsNullOrEmpty(location) || string.IsNullOrEmpty(bidNum)) return;

            if (location == _lastLocation && bidNum == _lastBidNum && status == _lastStatus) return;
            _lastLocation = location;
            _lastBidNum   = bidNum;
            _lastStatus   = status;

            log.Debug("[HousingLottery] status={S} location={L} bidNum={N} type={T}", status, location, bidNum, bidType);
            ProcessEntry(status, location, bidNum, bidType);
        }
    }

    private void ProcessEntry(string status, string location, string bidNumStr, string bidTypeStr)
    {
        if (objectTable[0] is not IPlayerCharacter player) return;

        var world = Common.WorldName(player);
        if (string.IsNullOrEmpty(world)) return;
        var charKey = $"{player.Name.TextValue}@{world}";

        var locMatch    = LocationRx.Match(location);
        var bidNumMatch = BidNumRx.Match(bidNumStr);
        if (!locMatch.Success || !bidNumMatch.Success) return;

        int plot     = int.Parse(locMatch.Groups[1].Value);
        int ward     = int.Parse(locMatch.Groups[2].Value);
        var district = HousingDistricts.Normalize(locMatch.Groups[3].Value.Trim());
        int bidNum   = int.Parse(bidNumMatch.Groups[1].Value);

        // Active statuses mean the bid still exists
        bool isActive = status.Contains("Current Entry",             StringComparison.OrdinalIgnoreCase)
                     || status.Contains("Results period in progress", StringComparison.OrdinalIgnoreCase)
                     || status.Contains("Entry period in progress",   StringComparison.OrdinalIgnoreCase);
        bool isOver = !isActive;

        if (isOver)
        {
            var match = characterDb.GetBidsByCharacter(charKey)
                .Find(b => b.District == district && b.Ward == ward && b.Plot == plot && b.BidNumber == bidNum);
            if (match != null)
            {
                characterDb.DeleteBid(match.Id);
                Notify($"Lottery bid removed ({district} W{ward} P{plot}).");
                log.Debug("[HousingLottery] Removed concluded bid for {Key}", charKey);
            }
            return;
        }

        bool exists = characterDb.GetBidsByCharacter(charKey)
            .Exists(b => b.District == district && b.Ward == ward && b.Plot == plot && b.BidNumber == bidNum);
        if (exists) return;

        var bType = bidTypeStr.Contains("Free Company", StringComparison.OrdinalIgnoreCase)
            ? BidType.FC : BidType.Private;

        characterDb.AddBid(new HousingBidRecord
        {
            CharacterKey = charKey,
            District     = district,
            Ward         = ward,
            Plot         = plot,
            BidNumber    = bidNum,
            BidType      = bType,
            BidDate      = DateTime.UtcNow,
        });
        Notify($"Lottery bid tracked: {district} W{ward} P{plot} #{bidNum}.");
        log.Debug("[HousingLottery] Added bid for {Key}: {D} W{W} P{P} #{N}", charKey, district, ward, plot, bidNum);
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        var text = message.Message.ToString();
        if (!text.Contains("lottery", StringComparison.OrdinalIgnoreCase)) return;

        if (objectTable[0] is not IPlayerCharacter player) return;
        var world = Common.WorldName(player);
        if (string.IsNullOrEmpty(world)) return;
        var charKey = $"{player.Name.TextValue}@{world}";

        // Captured immediately when the player places the bid
        var sub = SubmitRx.Match(text);
        if (sub.Success)
        {
            int plot     = int.Parse(sub.Groups[1].Value);
            int ward     = int.Parse(sub.Groups[2].Value);
            var district = HousingDistricts.Normalize(sub.Groups[3].Value.Trim());
            int bidNum   = int.Parse(sub.Groups[4].Value);

            bool exists = characterDb.GetBidsByCharacter(charKey)
                .Exists(b => b.District == district && b.Ward == ward && b.Plot == plot && b.BidNumber == bidNum);
            if (!exists)
            {
                characterDb.AddBid(new HousingBidRecord
                {
                    CharacterKey = charKey,
                    District     = district,
                    Ward         = ward,
                    Plot         = plot,
                    BidNumber    = bidNum,
                    BidType      = _pendingBidType,
                    BidDate      = DateTime.UtcNow,
                });
                Notify($"Lottery bid tracked: {district} W{ward} P{plot} #{bidNum}.");
                log.Debug("[HousingLottery] Captured from submission message: {D} W{W} P{P} #{N}", district, ward, plot, bidNum);
            }
            return;
        }

        if (!text.Contains("refund",  StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("awarded", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("won",     StringComparison.OrdinalIgnoreCase)) return;

        var bids = characterDb.GetBidsByCharacter(charKey);
        if (bids.Count == 0) return;

        foreach (var bid in bids)
            characterDb.DeleteBid(bid.Id);

        Notify("Lottery concluded, bid(s) removed.");
        log.Debug("[HousingLottery] Removed {Count} bid(s) for {Key} on lottery conclusion message.", bids.Count, charKey);
    }

    private static unsafe string ReadAtkString(AtkValue* val)
    {
        if (val == null) return string.Empty;
        // 8 = String, 33 = String8
        var t = (byte)val->Type;
        if (t != 8 && t != 33) return string.Empty;
        return val->String.ToString(); // CStringPointer.ToString() handles null safely
    }


    private static void Notify(string msg) => Common.ShowToast("Lottery tracker", msg);

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup,   AddonName, OnAddon);
        addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, AddonName, OnAddon);
        addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, AddonName, OnAddonFinalize);
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup,   "SelectYesNoTextScroll", OnBidConfirmSetup);
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup,   "SelectYesno", OnResultYesnoSetup);
        addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "SelectYesno", OnResultYesnoFinalize);
        if (_resultYesHandle != null) { addonEventManager.RemoveEvent(_resultYesHandle); _resultYesHandle = null; }
        chatGui.ChatMessage -= OnChatMessage;
    }
}
