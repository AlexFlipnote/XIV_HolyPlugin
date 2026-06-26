using System;
using System.Collections.Generic;

namespace HoliestFluffiness;

internal static class HousingDistricts
{
    internal static readonly Dictionary<string, ushort> TerritoryIds = new()
    {
        ["Mist"]          = 339,
        ["Lavender Beds"] = 340,
        ["The Goblet"]    = 341,
        ["Shirogane"]     = 641,
        ["Empyreum"]      = 979,
    };

    internal static string? FromTerritoryId(ushort id) => id switch
    {
        339 => "Mist",
        340 => "Lavender Beds",
        341 => "The Goblet",
        641 => "Shirogane",
        979 => "Empyreum",
        _   => null,
    };

    // Byte index 1–5 from AgentContentsTimer memory layout
    internal static string FromAgentIndex(byte index) => index switch
    {
        1 => "Mist",
        2 => "Lavender Beds",
        3 => "The Goblet",
        4 => "Shirogane",
        5 => "Empyreum",
        _ => $"District{index}",
    };

    // Fuzzy match from in-game location strings (e.g. "Lavender Beds (Ward 3)")
    internal static string Normalize(string raw) => raw switch
    {
        var s when s.Contains("Mist",      StringComparison.OrdinalIgnoreCase) => "Mist",
        var s when s.Contains("Lavender",  StringComparison.OrdinalIgnoreCase) => "Lavender Beds",
        var s when s.Contains("Goblet",    StringComparison.OrdinalIgnoreCase) => "The Goblet",
        var s when s.Contains("Shirogane", StringComparison.OrdinalIgnoreCase) => "Shirogane",
        var s when s.Contains("Empyreum",  StringComparison.OrdinalIgnoreCase) => "Empyreum",
        _                                                                       => raw,
    };
}
