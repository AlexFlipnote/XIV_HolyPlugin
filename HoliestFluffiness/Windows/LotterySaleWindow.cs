using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace HoliestFluffiness.Windows;

// Docks to the right of the native "For Sale" placard (HousingSignBoard) while it shows a plot that
// is selling via lottery: entry count, odds of winning, and a "Copy sale" button for sharing the
// plot as a one-liner. Everything is read passively from the placard's own text, nothing is
// requested from the server. Gated on the same "Show ward info panel" setting as WardInfoWindow.
public sealed class LotterySaleWindow : Window
{
    private readonly Configuration config;
    private readonly CharacterDb characterDb;
    private readonly IGameGui gameGui;
    private readonly IObjectTable objectTable;

    private const string AddonName  = "HousingSignBoard";
    private const float WindowWidth = 260f;

    private static readonly Regex LocationRx     = new(@"Plot (\d+), (\d+)\w+ Ward, ([^(\r\n]+)", RegexOptions.Compiled);
    private static readonly Regex ParticipantsRx = new(@"Current participants:\s*([\d,]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DeadlineRx     = new(@"Accepting entries until (.+?)\.?\r?$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex PriceRx        = new(@"([\d,]{5,})\s*Gil", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SizeRx         = new(@"^(Small|Medium|Large)\r?$", RegexOptions.Compiled | RegexOptions.Multiline);

    private sealed record SaleInfo(
        string District, int Ward, int Plot, int Entries,
        string? Deadline, string? Price, string? Size, bool FcAllowed, bool PrivateAllowed);

    private SaleInfo? sale;
    private string? world;

    // The player's own tracked bid on this exact plot (if any), so the odds can account for the
    // fact that an entered player is already counted in the participant total. Re-queried when the
    // placard changes plots and at most once a second otherwise, not every frame.
    private HousingBidRecord? ownBid;
    private string ownBidKey = string.Empty;
    private DateTime ownBidCheckedAt = DateTime.MinValue;

    public LotterySaleWindow(Configuration config, CharacterDb characterDb, IGameGui gameGui, IObjectTable objectTable)
        : base("Lottery##HFLotterySale", ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings)
    {
        this.config      = config;
        this.characterDb = characterDb;
        this.gameGui     = gameGui;
        this.objectTable = objectTable;

        ShowCloseButton    = false;
        AllowPinning       = false;
        AllowClickthrough  = false;
        RespectCloseHotkey = false;
    }

    public override unsafe void PreOpenCheck()
    {
        sale = null;
        var addon = config.WardInfoWindowEnabled ? GetAddon() : null;
        if (addon != null) sale = Parse(ReadAllText(addon));

        if (sale != null && Common.TryGetLocalPlayer(objectTable, out var player))
        {
            world = player.CurrentWorld.ValueNullable?.Name.ExtractText();
            RefreshOwnBid(Common.CharacterKey(player), sale);
        }

        IsOpen = sale != null;
    }

    public override unsafe void PreDraw()
    {
        Common.PushWindowTheme();

        var addon = GetAddon();
        if (addon == null || addon->WindowNode == null) return;

        var win = &addon->WindowNode->AtkResNode;
        var topLeft = Common.GetNodePosition(win);
        ImGui.SetNextWindowPos(new Vector2(topLeft.X + win->Width * addon->Scale, topLeft.Y), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(WindowWidth, win->Height * addon->Scale), ImGuiCond.Always);
    }

    public override void PostDraw() => Common.PopWindowTheme();

    public override void Draw()
    {
        if (sale == null) return;

        Common.GoldText($"{sale.District} W{sale.Ward} P{sale.Plot}");
        if (!string.IsNullOrEmpty(world))
        {
            ImGui.SameLine();
            Common.DimmedText(world);
        }

        ImGui.Separator();

        ImGui.TextUnformatted($"Currently {sale.Entries} {(sale.Entries == 1 ? "entry" : "entries")}");

        // An entered player is already part of the participant count; otherwise entering adds one.
        var pool = ownBid != null ? Math.Max(sale.Entries, 1) : sale.Entries + 1;
        var chance = 100.0 / pool;
        if (ownBid != null)
        {
            Common.GreenText($"Entered (#{ownBid.BidNumber}, {(ownBid.BidType == BidType.FC ? "FC" : "Private")})");
            ImGui.TextUnformatted($"Chance of winning: {FormatChance(chance)}");
        }
        else
        {
            ImGui.TextUnformatted($"Chance if you enter: {FormatChance(chance)}");
        }
        Common.DimmedText($"1 in {pool}");

        ImGui.Dummy(new Vector2(0, 4));

        if (sale.Size  != null) Row("Size",  sale.Size);
        if (sale.Price != null) Row("Price", $"{sale.Price} gil");
        Row("Buyers", (sale.FcAllowed, sale.PrivateAllowed) switch
        {
            (true, true)  => "FC + Private",
            (true, false) => "FC only",
            (false, true) => "Private only",
            _             => "?",
        });
        if (sale.Deadline != null) Row("Ends", sale.Deadline);

        // Pinned to the bottom of the docked panel so it lines up near the native buttons.
        var buttonHeight = ImGui.GetFrameHeight();
        var bottomY = ImGui.GetWindowHeight() - buttonHeight - ImGui.GetStyle().WindowPadding.Y;
        if (ImGui.GetCursorPosY() < bottomY) ImGui.SetCursorPosY(bottomY);

        Common.PushGoldButton();
        if (ImGui.Button("Copy sale##hflotterycopy", new Vector2(-1, 0)))
        {
            ImGui.SetClipboardText(CopyText(sale));
            Common.ShowToast("Lottery", "Sale copied to clipboard.");
        }
        Common.PopGoldButton();
    }

    // "[Phantom] Mist W17 P45 M -> 44 (16,000,000)". Readable without the plugin or site, and the
    // holy.ffxiv.watch sketch book parses it; any part the placard didn't show is left out.
    private string CopyText(SaleInfo s)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(world)) sb.Append($"[{world}] ");
        sb.Append($"{ShortDistrict(s.District)} W{s.Ward} P{s.Plot}");
        if (s.Size != null) sb.Append($" {s.Size[0]}");
        sb.Append($" -> {s.Entries}");
        if (s.Price != null) sb.Append($" ({s.Price})");
        return sb.ToString();
    }

    private static void Row(string label, string value)
    {
        Common.DimmedText($"{label}:");
        ImGui.SameLine(60f * ImGuiHelpers.GlobalScale);
        ImGui.TextUnformatted(value);
    }

    private static string FormatChance(double pct) =>
        pct.ToString(pct >= 10 ? "0.#" : "0.##", CultureInfo.InvariantCulture) + "%";

    private static string ShortDistrict(string district) => district switch
    {
        "The Lavender Beds" => "Lavender",
        "The Goblet"        => "Goblet",
        _                   => district,
    };

    private void RefreshOwnBid(string charKey, SaleInfo s)
    {
        var key = $"{charKey}|{s.District}|{s.Ward}|{s.Plot}";
        if (key == ownBidKey && DateTime.UtcNow - ownBidCheckedAt < TimeSpan.FromSeconds(1)) return;

        ownBidKey       = key;
        ownBidCheckedAt = DateTime.UtcNow;
        ownBid = characterDb.GetBidsByCharacter(charKey)
            .Find(b => HousingDistricts.Normalize(b.District) == s.District && b.Ward == s.Ward && b.Plot == s.Plot);
    }

    private static SaleInfo? Parse(string text)
    {
        if (!text.Contains("Selling via lottery", StringComparison.OrdinalIgnoreCase)) return null;

        var loc = LocationRx.Match(text);
        var participants = ParticipantsRx.Match(text);
        if (!loc.Success || !participants.Success) return null;
        if (!int.TryParse(participants.Groups[1].Value.Replace(",", ""), out var entries)) return null;

        var deadline = DeadlineRx.Match(text);
        var price    = PriceRx.Match(text);
        var size     = SizeRx.Match(text);

        return new SaleInfo(
            HousingDistricts.Normalize(loc.Groups[3].Value.Trim()),
            int.Parse(loc.Groups[2].Value),
            int.Parse(loc.Groups[1].Value),
            entries,
            deadline.Success ? deadline.Groups[1].Value.Trim() : null,
            price.Success ? price.Groups[1].Value : null,
            size.Success ? size.Groups[1].Value : null,
            text.Contains("Available to free companies", StringComparison.OrdinalIgnoreCase),
            text.Contains("Available to private buyers", StringComparison.OrdinalIgnoreCase));
    }

    // The placard's AtkValue layout isn't documented, so rather than hardcode indices this gathers
    // every string the addon holds (AtkValues plus visible text nodes) and lets the regexes find
    // what they need. Each piece goes on its own line so the line-anchored patterns work.
    private static unsafe string ReadAllText(AtkUnitBase* addon)
    {
        var parts = new List<string>();

        for (var i = 0; i < addon->AtkValuesCount; i++)
        {
            var val = &addon->AtkValues[i];
            var t = (byte)val->Type;
            if (t != 8 && t != 33) continue; // 8 = String, 33 = String8
            var s = val->String.ToString();
            if (!string.IsNullOrEmpty(s)) parts.Add(s);
        }

        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || node->Type != NodeType.Text) continue;
            var s = ((AtkTextNode*)node)->NodeText.ToString();
            if (!string.IsNullOrEmpty(s)) parts.Add(s);
        }

        var sb = new StringBuilder();
        foreach (var p in parts) sb.Append(p).Append('\n');
        return sb.ToString();
    }

    private unsafe AtkUnitBase* GetAddon()
    {
        var addon = (AtkUnitBase*)gameGui.GetAddonByName(AddonName).Address;
        return addon != null && Common.IsAddonVisible(addon) ? addon : null;
    }
}
