using System;
using SQLite;

namespace HoliestFluffiness;

public enum BidType { Private = 0, FC = 1 }

[Table("housing_bids")]
public class HousingBidRecord
{
    [PrimaryKey, AutoIncrement, Column("id")]
    public int Id { get; set; }

    [Column("character_key")]
    public string CharacterKey { get; set; } = "";

    [Column("district")]
    public string District { get; set; } = "";

    [Column("ward")]
    public int Ward { get; set; }

    [Column("plot")]
    public int Plot { get; set; }

    [Column("bid_number")]
    public int BidNumber { get; set; }

    [Column("bid_type")]
    public BidType BidType { get; set; }

    [Column("bid_date")]
    public DateTime BidDate { get; set; }
}
