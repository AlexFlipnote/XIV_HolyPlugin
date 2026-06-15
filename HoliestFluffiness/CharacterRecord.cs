using System;
using SQLite;

namespace HoliestFluffiness;

[Table("characters")]
public class CharacterRecord
{
    [PrimaryKey, Column("key")]
    public string Key { get; set; } = "";

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("world")]
    public string World { get; set; } = "";

    [Column("data_center")]
    public string DataCenter { get; set; } = "";

    [Column("free_company")]
    public string? FreeCompany { get; set; }

    [Column("search_info")]
    public string? SearchInfo { get; set; }

    [Column("private_house")]
    public string? PrivateHouse { get; set; }

    [Column("fc_house")]
    public string? FcHouse { get; set; }

    [Column("gil")]
    public long Gil { get; set; } = -1;

    [Column("last_seen")]
    public DateTime LastSeen { get; set; }

    [Column("slot")]
    public int? Slot { get; set; }
}
