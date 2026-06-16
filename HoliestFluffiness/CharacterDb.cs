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

    public long TotalGil() => db.Table<CharacterRecord>().ToList().Where(r => r.Gil >= 0).Sum(r => r.Gil);

    public int CountWithFc() => db.Table<CharacterRecord>().ToList().Count(r => !string.IsNullOrEmpty(r.FreeCompany));

    public int CountUniqueFc() => db.Table<CharacterRecord>().ToList()
        .Where(r => !string.IsNullOrEmpty(r.FreeCompany)).Select(r => r.FreeCompany).Distinct().Count();

    public int CountWithPrivateHouse() => db.Table<CharacterRecord>().ToList().Count(r => !string.IsNullOrEmpty(r.PrivateHouse));

    public int CountUniqueFcHouse() => db.Table<CharacterRecord>().ToList()
        .Where(r => !string.IsNullOrEmpty(r.FcHouse)).Select(r => r.FcHouse).Distinct().Count();

    public int CountWithSearchInfo() => db.Table<CharacterRecord>().ToList().Count(r => !string.IsNullOrEmpty(r.SearchInfo));

    public CharacterRecord? RichestCharacter() => db.Table<CharacterRecord>().ToList()
        .Where(r => r.Gil >= 0).OrderByDescending(r => r.Gil).FirstOrDefault();

    public long AverageGil()
    {
        var list = db.Table<CharacterRecord>().ToList().Where(r => r.Gil >= 0).ToList();
        return list.Count == 0 ? 0 : (long)list.Average(r => r.Gil);
    }

    public event Action? Changed;

    public void Upsert(CharacterRecord record) { db.InsertOrReplace(record); Changed?.Invoke(); }

    public void UpsertPreservingSlot(CharacterRecord record)
    {
        if (record.Slot == 0)
            record.Slot = db.Find<CharacterRecord>(record.Key)?.Slot ?? 0;
        db.InsertOrReplace(record);
        Changed?.Invoke();
    }

    public void UpsertSlot(string key, string name, string world, string dc, int slot)
    {
        var existing = db.Find<CharacterRecord>(key);
        if (existing != null)
        {
            existing.Slot = slot;
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
        Changed?.Invoke();
    }

    public CharacterRecord? GetByKey(string key) => db.Find<CharacterRecord>(key);

    public CharacterRecord? GetByWorldAndSlot(string world, int slot) =>
        db.Table<CharacterRecord>().ToList()
          .FirstOrDefault(r => string.Equals(r.World, world, StringComparison.OrdinalIgnoreCase) && r.Slot == slot);

    public List<CharacterRecord> GetByWorld(string world) =>
        [.. db.Table<CharacterRecord>().ToList()
              .Where(r => string.Equals(r.World, world, StringComparison.OrdinalIgnoreCase))
              .OrderBy(r => r.Slot == 0 ? int.MaxValue : r.Slot)];

    public List<CharacterRecord> GetAll() => [.. db.Table<CharacterRecord>()];

    public void Delete(string key) { db.Delete<CharacterRecord>(key); Changed?.Invoke(); }

    public void Reset(string key)
    {
        var rec = db.Find<CharacterRecord>(key);
        if (rec == null) return;
        rec.FreeCompany  = null;
        rec.SearchInfo   = null;
        rec.PrivateHouse = null;
        rec.FcHouse      = null;
        rec.Gil          = -1;
        db.Update(rec);
        Changed?.Invoke();
    }

    public void Dispose() => db.Close();
}
