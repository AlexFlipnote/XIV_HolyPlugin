namespace HoliestFluffiness.Windows;

public enum ConfigSection
{
    Client     = 0,
    Login      = 1,
    Indicators = 2,
    // 3 intentionally unused (removed section; numbering kept stable for saved LastSelectedSection values)
    Database   = 4,
    Characters = 5,
    Bids       = 6,
    About      = 7,
    Social     = 8,
    // 9 intentionally unused (Inventory merged into Characters; numbering kept stable for saved LastSelectedSection values)
}
