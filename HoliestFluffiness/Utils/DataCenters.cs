using System;
using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace HoliestFluffiness;

// The client's World sheet still carries the launch-era DC layout (one DC per region: Elemental,
// Aether, Chaos, Materia), so World.DataCenter is wrong for roughly half the worlds. The lobby
// server hands out the real layout at runtime; this table mirrors it so we can resolve offline.
// Unknown worlds fall back to the sheet value.
internal static class DataCenters
{
    private static readonly (uint Id, string Name, string[] Worlds)[] Layout =
    [
        (1,  "Elemental", ["Aegis", "Atomos", "Carbuncle", "Garuda", "Gungnir", "Kujata", "Tonberry", "Typhon"]),
        (2,  "Gaia",      ["Alexander", "Bahamut", "Durandal", "Fenrir", "Ifrit", "Ridill", "Tiamat", "Ultima"]),
        (3,  "Mana",      ["Anima", "Asura", "Chocobo", "Hades", "Ixion", "Masamune", "Pandaemonium", "Titan"]),
        (10, "Meteor",    ["Belias", "Mandragora", "Ramuh", "Shinryu", "Unicorn", "Valefor", "Yojimbo", "Zeromus"]),
        (4,  "Aether",    ["Adamantoise", "Cactuar", "Faerie", "Gilgamesh", "Jenova", "Midgardsormr", "Sargatanas", "Siren"]),
        (8,  "Crystal",   ["Balmung", "Brynhildr", "Coeurl", "Diabolos", "Goblin", "Malboro", "Mateus", "Zalera"]),
        (5,  "Primal",    ["Behemoth", "Excalibur", "Exodus", "Famfrit", "Hyperion", "Lamia", "Leviathan", "Ultros"]),
        (11, "Dynamis",   ["Cuchulainn", "Golem", "Halicarnassus", "Kraken", "Maduin", "Marilith", "Rafflesia", "Seraph"]),
        (6,  "Chaos",     ["Cerberus", "Louisoix", "Moogle", "Omega", "Phantom", "Ragnarok", "Sagittarius", "Spriggan"]),
        (7,  "Light",     ["Alpha", "Lich", "Odin", "Phoenix", "Raiden", "Shiva", "Twintania", "Zodiark"]),
        (9,  "Materia",   ["Bismarck", "Ravana", "Sephirot", "Sophia", "Zurvan"]),
    ];

    private static readonly Dictionary<string, (uint Id, string Name)> ByWorld = Build();

    private static Dictionary<string, (uint Id, string Name)> Build()
    {
        var map = new Dictionary<string, (uint, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name, worlds) in Layout)
            foreach (var w in worlds)
                map[w] = (id, name);
        return map;
    }

    internal static string? NameFor(string world) =>
        ByWorld.TryGetValue(world, out var dc) ? dc.Name : null;

    internal static string NameFor(World world) =>
        NameFor(world.Name.ExtractText()) ?? world.DataCenter.ValueNullable?.Name.ExtractText() ?? "";

    internal static uint IdFor(World world) =>
        ByWorld.TryGetValue(world.Name.ExtractText(), out var dc) ? dc.Id : world.DataCenter.RowId;
}
