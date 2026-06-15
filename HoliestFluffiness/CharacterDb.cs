using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SQLite;

namespace HoliestFluffiness;

public sealed class CharacterDb : IDisposable
{
    private readonly SQLiteConnection db;

    public CharacterDb(string path)
    {
        // SQLite-net caches TableMappings in a static dict keyed by type full name.
        // After a Dalamud hot-reload the stale entry points to the old load context's
        // type, causing an InvalidCastException. Clear it before opening the connection.
        ClearStaleMapping();
        db = new SQLiteConnection(path);
        db.CreateTable<CharacterRecord>();
        AddColumnIfMissing("slot", "INTEGER");
    }

    private void AddColumnIfMissing(string column, string type)
    {
        try { db.Execute($"ALTER TABLE characters ADD COLUMN {column} {type}"); }
        catch { /* column already exists */ }
    }

    private static void ClearStaleMapping()
    {
        try
        {
            var field = typeof(SQLiteConnection).GetField(
                "_mappings", BindingFlags.Static | BindingFlags.NonPublic);
            if (field?.GetValue(null) is IDictionary cache && typeof(CharacterRecord).FullName is { } key)
                cache.Remove(key);
        }
        catch { /* reflection may fail on trimmed/obfuscated builds */ }
    }

    public int Count() => db.Table<CharacterRecord>().Count();

    public void Upsert(CharacterRecord record) => db.InsertOrReplace(record);

    public void UpsertPreservingSlot(CharacterRecord record)
    {
        if (record.Slot == null)
            record.Slot = db.Find<CharacterRecord>(record.Key)?.Slot;
        db.InsertOrReplace(record);
    }

    public void UpsertSlot(string key, string name, string world, string dc, int slot)
    {
        var existing = db.Find<CharacterRecord>(key);
        if (existing != null)
        {
            existing.Slot     = slot;
            existing.LastSeen = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(dc)) existing.DataCenter = dc;
            db.Update(existing);
        }
        else
        {
            db.Insert(new CharacterRecord
            {
                Key        = key,
                Name       = name,
                World      = world,
                DataCenter = dc,
                Slot       = slot,
                LastSeen   = DateTime.UtcNow,
            });
        }
    }

    public CharacterRecord? GetByKey(string key) => db.Find<CharacterRecord>(key);

    public CharacterRecord? GetByWorldAndSlot(string world, int slot) =>
        db.Table<CharacterRecord>().ToList()
          .FirstOrDefault(r => string.Equals(r.World, world, StringComparison.OrdinalIgnoreCase) && r.Slot == slot);

    public List<CharacterRecord> GetByWorld(string world) =>
        [.. db.Table<CharacterRecord>().ToList()
              .Where(r => string.Equals(r.World, world, StringComparison.OrdinalIgnoreCase))
              .OrderBy(r => r.Slot ?? int.MaxValue)];

    public List<CharacterRecord> GetAll() => [.. db.Table<CharacterRecord>()];

    public void Dispose() => db.Close();
}
