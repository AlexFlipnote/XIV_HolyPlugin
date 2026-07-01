using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using SQLite;

namespace HoliestFluffiness;

public readonly record struct CharacterDbStats(
    int Count,
    long TotalGil,
    long TotalMgp,
    int WithFc,
    int UniqueFc,
    int WithPrivateHouse,
    int UniqueFcHouse,
    int WithSearchInfo,
    CharacterRecord? Richest,
    long AverageGil,
    Dictionary<uint, long> InventoryTotals
);

public sealed class CharacterDb : IDisposable
{
    private readonly SQLiteConnection db;

    public CharacterDb(string path)
    {
        // SQLite-net caches TableMappings in a static dict keyed by type full name.
        // After a Dalamud hot-reload the stale entry points to the old load context's
        // type, causing an InvalidCastException. Clear it before opening the connection.
        ClearStaleMapping<CharacterRecord>();
        ClearStaleMapping<HousingBidRecord>();
        db = new SQLiteConnection(path);
        db.CreateTable<CharacterRecord>();
        db.CreateTable<HousingBidRecord>();
        AddColumnIfMissing("slot", "INTEGER");
        AddColumnIfMissing("inventory", "TEXT");
        AddColumnIfMissing("mgp", "INTEGER");
    }

    private void AddColumnIfMissing(string column, string type)
    {
        try { db.Execute($"ALTER TABLE characters ADD COLUMN {column} {type}"); }
        catch { /* column already exists */ }
    }

    private static void ClearStaleMapping<T>()
    {
        try
        {
            var field = typeof(SQLiteConnection).GetField(
                "_mappings", BindingFlags.Static | BindingFlags.NonPublic);
            if (field?.GetValue(null) is IDictionary cache && typeof(T).FullName is { } key)
                cache.Remove(key);
        }
        catch { /* reflection may fail on trimmed/obfuscated builds */ }
    }

    // Single pass over the table instead of the ~9 separate full-table reads the individual
    // Count*/Total*/Richest/Average queries used to do (this is called from Draw() every
    // frame the Database config tab is open, so 9 reads became 1).
    public CharacterDbStats GetStats()
    {
        var all = db.Table<CharacterRecord>().ToList();

        var uniqueFc      = new HashSet<string>();
        var uniqueFcHouse = new HashSet<string>();
        var invTotals     = new Dictionary<uint, long>();

        int withFc = 0, withHouse = 0, withSearch = 0, gilCount = 0;
        long gilSum = 0, mgpSum = 0;
        CharacterRecord? richest = null;

        foreach (var r in all)
        {
            if (!string.IsNullOrEmpty(r.FreeCompany))  { withFc++; uniqueFc.Add(r.FreeCompany); }
            if (!string.IsNullOrEmpty(r.PrivateHouse)) withHouse++;
            if (!string.IsNullOrEmpty(r.FcHouse))      uniqueFcHouse.Add(r.FcHouse);
            if (!string.IsNullOrEmpty(r.SearchInfo))   withSearch++;

            if (r.Gil >= 0)
            {
                gilSum += r.Gil;
                gilCount++;
                if (richest == null || r.Gil > richest.Gil) richest = r;
            }
            if (r.Mgp >= 0) mgpSum += r.Mgp;

            if (r.Inventory != null &&
                JsonSerializer.Deserialize<Dictionary<uint, int>>(r.Inventory) is { } items)
            {
                foreach (var (id, qty) in items)
                    invTotals[id] = invTotals.GetValueOrDefault(id) + qty;
            }
        }

        return new CharacterDbStats(
            all.Count, gilSum, mgpSum,
            withFc, uniqueFc.Count, withHouse, uniqueFcHouse.Count, withSearch,
            richest, gilCount == 0 ? 0 : gilSum / gilCount, invTotals);
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
        rec.Mgp          = -1;
        rec.Inventory    = null;
        db.Update(rec);
        Changed?.Invoke();
    }

    public List<HousingBidRecord> GetAllBids()                  => [.. db.Table<HousingBidRecord>()];
    public List<HousingBidRecord> GetBidsByCharacter(string key) => [.. db.Table<HousingBidRecord>().Where(b => b.CharacterKey == key)];
    public void AddBid(HousingBidRecord bid)                     => db.Insert(bid);
    public void DeleteBid(int id)                                => db.Delete<HousingBidRecord>(id);

    public void Dispose() => db.Close();
}
