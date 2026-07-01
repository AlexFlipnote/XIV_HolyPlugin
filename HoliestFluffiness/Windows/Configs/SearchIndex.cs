using System.Collections.Generic;

namespace HoliestFluffiness.Windows;

public readonly record struct SettingEntry(string Title, string? Desc, ConfigSection Section, string Key);

public partial class ConfigWindow
{
    // Populated at draw time by Anchor(key, title, desc), see ConfigWindow.cs, so every
    // Config*/Anchor call site is the single source of truth for its own search entry. A
    // section only appears here once it has actually been drawn at least once this session.
    private static class SearchIndex
    {
        private static readonly Dictionary<(ConfigSection Section, string Key), SettingEntry> Registry = new();

        public static IEnumerable<SettingEntry> Entries => Registry.Values;

        public static void Register(ConfigSection section, string key, string title, string? desc) =>
            Registry[(section, key)] = new SettingEntry(title, desc, section, key);
    }
}
