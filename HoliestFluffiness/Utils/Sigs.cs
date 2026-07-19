namespace HoliestFluffiness;

// Single source of truth for every byte signature the plugin scans for. SigTracker parses this
// file directly (see SigTracker/index.py) and keys its per-patch history off the field names, so
// a sig that lives anywhere else is a sig that silently rots when a patch lands. Add sigs here
// and nowhere else; `make check` fails the build if a sig literal appears in any other file.
//
// Renaming a field orphans its SigTracker/sigs/<name>.json history. Rename the JSON to match.
//
// Before adding a sig, check whether FFXIVClientStructs already resolves the function; anything it
// owns is one less pattern to re-find by hand every patch. Physics lived here until it turned out
// to be BoneSimulator::Update, which PhysicsHandler now reads straight off ClientStructs.
public static class Sigs
{
    public const string AddToScreenLog      = "E8 ?? ?? ?? ?? BF ?? ?? ?? ?? EB 39";
    public const string CountdownTimer      = "40 53 48 83 EC 40 80 79 38 00";
    public const string LoadIconByID        = "E8 ?? ?? ?? ?? 41 8D 45 3E";
    public const string LobbyError          = "40 53 48 83 EC 30 48 8B D9 49 8B C8 E8 ?? ?? ?? ?? 8B D0";
    public const string MouseClickDelay     = "EB 3F B8 ?? ?? ?? ?? 48 8B D7";
    public const string ReceiveEvent        = "44 0F B7 C2 4D 8B D1";
}
