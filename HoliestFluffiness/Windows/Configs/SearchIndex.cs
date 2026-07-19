using System.Collections.Generic;

namespace HoliestFluffiness.Windows;

// Ordered so search results can group by kind: sections first, then their groups, then settings.
public enum SearchEntryKind
{
    Section    = 0,
    Subsection = 1,
    Setting    = 2,
}

public readonly record struct SettingEntry(
    string Title,
    string? Desc,
    ConfigSection Section,
    string Key,
    SearchEntryKind Kind = SearchEntryKind.Setting,
    string? Keywords = null);

public partial class ConfigWindow
{
    // Populated at draw time by Anchor(), so every call site is the source of truth for its own
    // entry. A section only lands here once it has been drawn at least once this session.
    private static class SearchIndex
    {
        // Sentinel key for a whole-section entry; clicking one just opens the section.
        public const string SectionKey = "__section";

        // Prefix that keeps subsection entries out of the setting key space
        public const string SubsectionKeyPrefix = "__sub_";

        private static readonly Dictionary<(ConfigSection Section, string Key), SettingEntry> Registry = new();

        public static IEnumerable<SettingEntry> Entries => Registry.Values;

        // Bumped on every registration so callers can cheaply detect a changed registry
        public static int Version { get; private set; }

        // The enum name is not always what the user sees
        public static string DisplayName(ConfigSection section) => section switch
        {
            ConfigSection.Client     => "Client",
            ConfigSection.Login      => "Login",
            ConfigSection.Indicators => "Indicators",
            ConfigSection.Social     => "Social",
            ConfigSection.Database   => "Database",
            ConfigSection.Characters => "Characters",
            ConfigSection.Bids       => "House bids",
            ConfigSection.About      => "About",
            _                        => section.ToString(),
        };

        // Characters/Bids/About are never drawn by the search warm-up pass, so seeding them up front
        // is the only way they are findable before first opening them.
        static SearchIndex()
        {
            Seed(ConfigSection.Client, "Settings that change client/application behaviour.",
                "window title taskbar flash physics fps anti-afk no-kill mouse");
            Seed(ConfigSection.Login, "Settings for what happens when you log in with a character.",
                "welcome message fashion accessory glamour");
            Seed(ConfigSection.Indicators, "Settings for in-game indicators and HUD additions.",
                "loot mp bar server info repair food check ready check combat hits");
            Seed(ConfigSection.Social, "Nearby players, targeting tracker, house doorbell, commendation sounds, and nameplate tweaks.",
                "nameplate doorbell commendation targeting");
            Seed(ConfigSection.Database, "Stores character info to a local SQLite database on every login.",
                "sqlite storage backup export");
            Seed(ConfigSection.Characters, "Cached info for every character you've logged into, including gil, MGP, houses, and tracked items.",
                "gil mgp inventory houses submarine alt");
            Seed(ConfigSection.Bids, "Housing lottery bids tracked automatically when you place or confirm a bid.",
                "housing lottery plot ward");
            Seed(ConfigSection.About, "Plugin info, credits, and appearance options.",
                "credits version theme colour color opacity");
        }

        private static void Seed(ConfigSection section, string desc, string keywords) =>
            Register(section, SectionKey, DisplayName(section), desc, SearchEntryKind.Section, keywords);

        public static void Register(ConfigSection section, string key, string title, string? desc,
            SearchEntryKind kind = SearchEntryKind.Setting, string? keywords = null)
        {
            Registry[(section, key)] = new SettingEntry(title, desc, section, key, kind, keywords);
            Version++;
        }
    }
}
