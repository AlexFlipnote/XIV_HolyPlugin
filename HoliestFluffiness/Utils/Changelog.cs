using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HoliestFluffiness;

internal enum ChangelogLineKind { Paragraph, Bullet, Heading, Gap }

// Notes are pre-split into display lines once at load, so drawing an open entry does no parsing per frame
internal sealed record ChangelogEntry(string Tag, DateTime? Date, IReadOnlyList<(ChangelogLineKind Kind, string Text)> Lines);

// Release notes baked into the DLL at build time (`python build.py changelog`). Read from the embedded
// resource only, so the About page never makes a network request. Empty on local builds that skipped it.
internal static class Changelog
{
    private const string ResourceName = "HoliestFluffiness.changelog.json";

    private static IReadOnlyList<ChangelogEntry>? entries;

    public static IReadOnlyList<ChangelogEntry> Entries => entries ??= Load();

    private static List<ChangelogEntry> Load()
    {
        var list = new List<ChangelogEntry>();
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream == null) return list;

            using var doc = JsonDocument.Parse(stream);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var tag   = el.TryGetProperty("tag", out var t) ? t.GetString() ?? "" : "";
                var notes = el.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "";
                DateTime? date = null;
                if (el.TryGetProperty("date", out var d) &&
                    DateTime.TryParseExact(d.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                    date = parsed;
                if (tag.Length > 0) list.Add(new ChangelogEntry(tag, date, ParseLines(notes)));
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or IOException)
        {
            // A malformed bundle just means no changelog; never worth breaking the settings window over
        }
        return list;
    }

    private static readonly Regex MdLink     = new(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex MdEmphasis = new(@"(\*\*|__|`)", RegexOptions.Compiled);

    // Just enough markdown for release notes: bullets, headings and paragraphs, with links and
    // emphasis reduced to their plain text since ImGui has nowhere to render them
    private static List<(ChangelogLineKind, string)> ParseLines(string notes)
    {
        var lines = new List<(ChangelogLineKind, string)>();
        foreach (var raw in notes.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                // Collapse runs of blank lines into one gap, and never lead with one
                if (lines.Count > 0 && lines[^1].Item1 != ChangelogLineKind.Gap) lines.Add((ChangelogLineKind.Gap, ""));
                continue;
            }

            var kind = ChangelogLineKind.Paragraph;
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                kind = ChangelogLineKind.Bullet;
                line = line[2..];
            }
            else if (line.StartsWith('#'))
            {
                kind = ChangelogLineKind.Heading;
                line = line.TrimStart('#').Trim();
            }

            line = MdEmphasis.Replace(MdLink.Replace(line, "$1"), "");
            lines.Add((kind, line));
        }
        if (lines.Count > 0 && lines[^1].Item1 == ChangelogLineKind.Gap) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    // Tags are plain "0.12.33"; the installed version may carry a trailing ".0"
    public static bool IsVersion(string tag, Version? installed)
    {
        if (installed == null || !Version.TryParse(tag.TrimStart('v'), out var v)) return false;
        return v.Major == installed.Major && v.Minor == installed.Minor && Math.Max(v.Build, 0) == Math.Max(installed.Build, 0);
    }
}
