using System;
using System.Text.RegularExpressions;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Events.EventDataTypes;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace HoliestFluffiness.Handlers;

public sealed class HousingLotteryHandler : IDisposable
{
    private readonly CharacterDb characterDb;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IAddonEventManager addonEventManager;
    private readonly IObjectTable objectTable;
    private readonly IChatGui chatGui;
    private readonly INotificationManager notificationManager;
    private readonly IPluginLog log;

    private IAddonEventHandle? _resultYesHandle;

    private const string AddonName = "ContentsInfoDetail";

    // AtkValues indices for the housing lottery entry block
    private const int IdxStatus   = 119;
    private const int IdxLocation = 121;
    private const int IdxBidNum   = 122;
    private const int IdxBidType  = 123;

    // Deduplicate: only process when the key fields change
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
        IObjectTable objectTable, IChatGui chatGui,
        INotificationManager notificationManager, IPluginLog log)
    {
        this.characterDb         = characterDb;
        this.addonLifecycle      = addonLifecycle;
        this.addonEventManager   = addonEventManager;
        this.objectTable         = objectTable;
        this.chatGui             = chatGui;
        this.notificationManager = notificationManager;
        this.log                 = log;

        addonLifecycle.RegisterListener(AddonEvent.PostSetup,    AddonName, OnAddon);
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh,  AddonName, OnAddon);
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
        _resultYesHandle = addonEventManager.AddEvent(
            (nint)addon, (nint)addon->YesButton->OwnerNode,
            AddonEventType.ButtonClick, OnResultYesClicked);
        log.Debug("[HousingLottery] Result dialog detected, hooked Yes button.");
    }

    private void OnResultYesnoFinalize(AddonEvent type, AddonArgs args)
    {
        if (_resultYesHandle == null) return;
        addonEventManager.RemoveEvent(_resultYesHandle);
        _resultYesHandle = null;
    }

    private void OnResultYesClicked(AddonEventType type, AddonEventData data)
    {
        var player = objectTable[0] as IPlayerCharacter;
        if (player == null) return;
        var world = player.HomeWorld.ValueNullable?.Name.ExtractText();
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

            // Skip if nothing changed since last time
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
        var player = objectTable[0] as IPlayerCharacter;
        if (player == null) return;

        var world = player.HomeWorld.ValueNullable?.Name.ExtractText();
        if (string.IsNullOrEmpty(world)) return;
        var charKey = $"{player.Name.TextValue}@{world}";

        var locMatch    = LocationRx.Match(location);
        var bidNumMatch = BidNumRx.Match(bidNumStr);
        if (!locMatch.Success || !bidNumMatch.Success) return;

        int plot     = int.Parse(locMatch.Groups[1].Value);
        int ward     = int.Parse(locMatch.Groups[2].Value);
        var district = NormalizeDistrict(locMatch.Groups[3].Value.Trim());
        int bidNum   = int.Parse(bidNumMatch.Groups[1].Value);

        // Active statuses ,  bid still exists, keep tracking
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

        // Active ,  add if not already tracked
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

        // Bid submission message ,  capture immediately when the player places the bid
        var sub = SubmitRx.Match(text);
        if (sub.Success)
        {
            var p = objectTable[0] as IPlayerCharacter;
            if (p == null) return;
            var w = p.HomeWorld.ValueNullable?.Name.ExtractText();
            if (string.IsNullOrEmpty(w)) return;
            var ck = $"{p.Name.TextValue}@{w}";

            int plot      = int.Parse(sub.Groups[1].Value);
            int ward      = int.Parse(sub.Groups[2].Value);
            var district  = NormalizeDistrict(sub.Groups[3].Value.Trim());
            int bidNum    = int.Parse(sub.Groups[4].Value);

            bool exists = characterDb.GetBidsByCharacter(ck)
                .Exists(b => b.District == district && b.Ward == ward && b.Plot == plot && b.BidNumber == bidNum);
            if (!exists)
            {
                characterDb.AddBid(new HousingBidRecord
                {
                    CharacterKey = ck,
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

        var player = objectTable[0] as IPlayerCharacter;
        if (player == null) return;
        var world = player.HomeWorld.ValueNullable?.Name.ExtractText();
        if (string.IsNullOrEmpty(world)) return;
        var charKey = $"{player.Name.TextValue}@{world}";

        var bids = characterDb.GetBidsByCharacter(charKey);
        if (bids.Count == 0) return;

        foreach (var bid in bids)
            characterDb.DeleteBid(bid.Id);

        Notify("Lottery concluded ,  bid(s) removed.");
        log.Debug("[HousingLottery] Removed {Count} bid(s) for {Key} on lottery conclusion message.", bids.Count, charKey);
    }

    private static unsafe string ReadAtkString(AtkValue* val)
    {
        if (val == null) return string.Empty;
        // 8 = String, 33 = String8 ,  avoid System.ValueType namespace collision
        var t = (byte)val->Type;
        if (t != 8 && t != 33) return string.Empty;
        return val->String.ToString(); // CStringPointer.ToString() handles null safely
    }

    private static string NormalizeDistrict(string raw) => raw switch
    {
        var s when s.Contains("Mist",      StringComparison.OrdinalIgnoreCase) => "Mist",
        var s when s.Contains("Lavender",  StringComparison.OrdinalIgnoreCase) => "Lavender Beds",
        var s when s.Contains("Goblet",    StringComparison.OrdinalIgnoreCase) => "The Goblet",
        var s when s.Contains("Shirogane", StringComparison.OrdinalIgnoreCase) => "Shirogane",
        var s when s.Contains("Empyreum",  StringComparison.OrdinalIgnoreCase) => "Empyreum",
        _                                                                       => raw,
    };

    // AgentContentsTimer memory layout (discovered via CE + plugin scanning):
    //   agent->0x10 points to a data block; within that block:
    //   [typeMarker:0x27] [ward:u8] [plot0:u8] [??:u8] [??:u8] [district:u8] [lotteryNum:u8] [bidType:u8] [status:u8]
    //   district: 1=Mist 2=Lavender 3=Goblet 4=Shirogane 5=Empyreum
    //   status non-zero = active bid; plot0 is 0-indexed (add 1 for display)
    public unsafe void TryReadFromAgent(string charKey)
    {
        var mod = AgentModule.Instance();
        if (mod == null) return;

        var timer = mod->GetAgentByInternalId(AgentId.ContentsTimer);
        if (timer == null) return;

        var dataPtr = *(byte**)((byte*)timer + 0x10);
        if (dataPtr == null) return;

        for (int i = 1; i < 8192 - 9; i++)
        {
            if (dataPtr[i - 1] != 0x27) continue;   // housing lottery type marker

            byte ward      = dataPtr[i];
            byte plotIdx   = dataPtr[i + 1];
            byte district  = dataPtr[i + 4];
            byte lotteryNo = dataPtr[i + 5];
            byte bidTyp    = dataPtr[i + 6];
            byte status    = dataPtr[i + 7];

            if (ward == 0 || ward > 24)          continue;
            if (plotIdx > 59)                    continue;
            if (district == 0 || district > 5)  continue;
            if (lotteryNo == 0)                  continue;
            if (status == 0)                     continue;  // no active bid

            var districtName = district switch
            {
                1 => "Mist",
                2 => "Lavender Beds",
                3 => "The Goblet",
                4 => "Shirogane",
                5 => "Empyreum",
                _ => $"District{district}",
            };
            int plot    = plotIdx + 1;
            var bType   = bidTyp == 2 ? BidType.FC : BidType.Private;

            log.Debug("[HousingLottery] Agent proactive read: {D} W{W} P{P} #{N} type={T} status={S}",
                districtName, ward, plot, lotteryNo, bType, status);

            bool exists = characterDb.GetBidsByCharacter(charKey)
                .Exists(b => b.District == districtName && b.Ward == ward && b.Plot == plot && b.BidNumber == lotteryNo);
            if (exists) return;

            characterDb.AddBid(new HousingBidRecord
            {
                CharacterKey = charKey,
                District     = districtName,
                Ward         = ward,
                Plot         = plot,
                BidNumber    = lotteryNo,
                BidType      = bType,
                    BidDate      = DateTime.UtcNow,
            });
            Notify($"Lottery bid tracked (login): {districtName} W{ward} P{plot} #{lotteryNo}.");
            return;
        }
    }

    private void Notify(string msg) =>
        notificationManager.AddNotification(new Notification
        {
            Content         = $"[HF] {msg}",
            Type            = NotificationType.Info,
            InitialDuration = TimeSpan.FromSeconds(6),
            Minimized       = false,
        });

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup,   AddonName, OnAddon);
        addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, AddonName, OnAddon);
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup,   "SelectYesNoTextScroll", OnBidConfirmSetup);
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup,   "SelectYesno", OnResultYesnoSetup);
        addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "SelectYesno", OnResultYesnoFinalize);
        if (_resultYesHandle != null) { addonEventManager.RemoveEvent(_resultYesHandle); _resultYesHandle = null; }
        chatGui.ChatMessage -= OnChatMessage;
    }
}
