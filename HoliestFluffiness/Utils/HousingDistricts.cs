using System;
using System.Collections.Generic;

namespace HoliestFluffiness;

internal static class HousingDistricts
{
    internal static readonly Dictionary<string, ushort> TerritoryIds = new()
    {
        ["Mist"]              = 339,
        ["The Lavender Beds"] = 340,
        ["The Goblet"]        = 341,
        ["Shirogane"]         = 641,
        ["Empyreum"]          = 979,
    };

    // Housing interior territory types mapped to their outdoor district territory, for zone preload.
    internal static readonly Dictionary<uint, uint> InteriorToOutdoor = new()
    {
        // Mist
        [282] = 339, [283] = 339, [284] = 339, [384] = 339, [423] = 339, [573] = 339, [608] = 339,
        // Lavender Beds
        [342] = 340, [343] = 340, [344] = 340, [385] = 340, [425] = 340, [574] = 340, [609] = 340,
        // The Goblet
        [345] = 341, [346] = 341, [347] = 341, [386] = 341, [424] = 341, [575] = 341, [610] = 341,
        // Shirogane
        [649] = 641, [650] = 641, [651] = 641, [652] = 641, [653] = 641, [654] = 641, [655] = 641,
        // Empyreum
        [980] = 979, [981] = 979, [982] = 979, [983] = 979, [984] = 979, [985] = 979, [999] = 979,
    };

    // Housing interior territory types the doorbell watches. Deliberately distinct from
    // InteriorToOutdoor: it omits apartment lobbies and includes the newer Minimalist house zones.
    internal static readonly HashSet<uint> DoorbellTerritories =
    [
        282, 283, 284, 384, 608,  // Mist
        342, 343, 344, 385, 609,  // Lavender Beds
        345, 346, 347, 386, 610,  // Goblet
        649, 650, 651, 652, 655,  // Shirogane
        980, 981, 982, 983, 999,  // Empyreum
        1249, 1250, 1251,          // Minimalist
        1374, 1375, 1376,          // Minimalist Dark (7.5)
    ];

    internal static string? FromTerritoryId(ushort id) => id switch
    {
        339 => "Mist",
        340 => "The Lavender Beds",
        341 => "The Goblet",
        641 => "Shirogane",
        979 => "Empyreum",
        _   => null,
    };

    // Byte index 1-5 from AgentContentsTimer memory layout
    internal static string FromAgentIndex(byte index) => index switch
    {
        1 => "Mist",
        2 => "The Lavender Beds",
        3 => "The Goblet",
        4 => "Shirogane",
        5 => "Empyreum",
        _ => $"District{index}",
    };

    // Fuzzy match from in-game location strings (e.g. "The Lavender Beds (Ward 3)"), and also used to
    // normalize legacy stored districts (pre-fix "Lavender Beds" records lacked the "The" prefix).
    internal static string Normalize(string raw) => raw switch
    {
        var s when s.Contains("Mist",      StringComparison.OrdinalIgnoreCase) => "Mist",
        var s when s.Contains("Lavender",  StringComparison.OrdinalIgnoreCase) => "The Lavender Beds",
        var s when s.Contains("Goblet",    StringComparison.OrdinalIgnoreCase) => "The Goblet",
        var s when s.Contains("Shirogane", StringComparison.OrdinalIgnoreCase) => "Shirogane",
        var s when s.Contains("Empyreum",  StringComparison.OrdinalIgnoreCase) => "Empyreum",
        _                                                                       => raw,
    };
}
