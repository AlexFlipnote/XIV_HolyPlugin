using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace HoliestFluffiness;

public static class WorldResolver
{
    // Returns the canonical world name if input uniquely matches one public world via StartsWith.
    // Returns null if input is ambiguous (0 or 2+ matches) so the caller can fall back.
    public static string? Resolve(string input, IDataManager dataManager)
    {
        var matches = dataManager.GetExcelSheet<World>()
            .Where(w => w.IsPublic && w.Name.ToString().StartsWith(input, StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Name.ToString())
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    // Resolve against an arbitrary string list (e.g. distinct worlds already in the DB).
    public static string? Resolve(string input, IEnumerable<string> candidates) =>
        candidates
            .Where(w => w.StartsWith(input, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList() is [var only] ? only : null;
}
