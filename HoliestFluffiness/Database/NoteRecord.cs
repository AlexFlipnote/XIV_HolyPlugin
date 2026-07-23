using System;
using SQLite;

namespace HoliestFluffiness;

[Table("notes")]
public class NoteRecord
{
    [PrimaryKey, AutoIncrement, Column("id")]
    public int Id { get; set; }

    [Column("author")]
    public string Author { get; set; } = "";

    [Column("title")]
    public string Title { get; set; } = "";

    [Column("content")]
    public string Content { get; set; } = "";

    [Column("is_global")]
    public bool IsGlobal { get; set; }

    // ContentFinderCondition RowId; null = general note, not tied to a duty
    [Column("duty_id")]
    public int? DutyId { get; set; }

    [Column("enabled")]
    public bool Enabled { get; set; } = true;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    [Column("pinned")]
    public bool Pinned { get; set; }

    // If true, this note's popout page is pulled out of an open duty-notes popup the moment
    // combat starts in its duty, and put back if the party wipes.
    [Column("hide_on_combat")]
    public bool HideOnCombat { get; set; }
}
