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
    Dictionary<uint, long> InventoryTotals,
    long TotalFcPoints
);

public sealed class CharacterDb : IDisposable
{
    private readonly SQLiteConnection db;

    // SQLiteConnection is not thread-safe and this DB is touched from the draw thread, the framework
    // thread and the thread pool, so every db.* access is serialized behind this lock. The Changed
    // event is always raised outside it so subscribers cannot re-enter and deadlock.
    private readonly object dbLock = new();

    public CharacterDb(string path)
    {
        // SQLite-net caches TableMappings statically by type name, so after a hot-reload the stale
        // entry points at the old load context's type and throws InvalidCastException.
        ClearStaleMapping<CharacterRecord>();
        ClearStaleMapping<HousingBidRecord>();
        db = new SQLiteConnection(path);
        db.CreateTable<CharacterRecord>();
        db.CreateTable<HousingBidRecord>();
        AddColumnIfMissing("slot", "INTEGER");
        AddColumnIfMissing("inventory", "TEXT");
        AddColumnIfMissing("mgp", "INTEGER");
        AddColumnIfMissing("fc_points", "INTEGER");
        AddColumnIfMissing("fc_leader", "INTEGER");
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

    private sealed class StatsCache { public CharacterDbStats Value; }
    private volatile StatsCache? statsCache;

    // One pass over the table rather than ~9 separate full-table reads
    public CharacterDbStats GetStats()
    {
        var cache = statsCache;
        if (cache != null) return cache.Value;
        cache = new StatsCache { Value = ComputeStats() };
        statsCache = cache;
        return cache.Value;
    }

    private CharacterDbStats ComputeStats()
    {
        List<CharacterRecord> all;
        lock (dbLock) all = db.Table<CharacterRecord>().ToList();

        var uniqueFc      = new HashSet<string>();
        var uniqueFcHouse = new HashSet<string>();
        var invTotals     = new Dictionary<uint, long>();
        var fcPointsByFc  = new Dictionary<string, (long Points, DateTime LastSeen)>();

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

            // FC points are company-wide, so keep one reading per FC or members get summed twice
            if (!string.IsNullOrEmpty(r.FreeCompany) && r.FcPoints >= 0 &&
                (!fcPointsByFc.TryGetValue(r.FreeCompany, out var seen) || r.LastSeen > seen.LastSeen))
                fcPointsByFc[r.FreeCompany] = (r.FcPoints, r.LastSeen);

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
            richest, gilCount == 0 ? 0 : gilSum / gilCount, invTotals,
            fcPointsByFc.Values.Sum(v => v.Points));
    }

    public event Action? Changed;

    // Every character-table write goes through here so the cached stats drop in lockstep
    private void RaiseChanged()
    {
        statsCache = null;
        Changed?.Invoke();
    }

    public void Upsert(CharacterRecord record)
    {
        lock (dbLock) db.InsertOrReplace(record);
        RaiseChanged();
    }

    public void UpsertPreservingSlot(CharacterRecord record)
    {
        lock (dbLock)
        {
            if (record.Slot == 0)
                record.Slot = db.Find<CharacterRecord>(record.Key)?.Slot ?? 0;
            db.InsertOrReplace(record);
        }
        RaiseChanged();
    }

    public void UpsertSlot(string key, string name, string world, string dc, int slot)
    {
        lock (dbLock)
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
        }
        RaiseChanged();
    }

    public CharacterRecord? GetByKey(string key)
    {
        lock (dbLock) return db.Find<CharacterRecord>(key);
    }

    public CharacterRecord? GetByWorldAndSlot(string world, int slot)
    {
        List<CharacterRecord> all;
        lock (dbLock) all = db.Table<CharacterRecord>().ToList();
        return all.FirstOrDefault(r => string.Equals(r.World, world, StringComparison.OrdinalIgnoreCase) && r.Slot == slot);
    }

    public List<CharacterRecord> GetByWorld(string world)
    {
        List<CharacterRecord> all;
        lock (dbLock) all = db.Table<CharacterRecord>().ToList();
        return [.. all
              .Where(r => string.Equals(r.World, world, StringComparison.OrdinalIgnoreCase))
              .OrderBy(r => r.Slot == 0 ? int.MaxValue : r.Slot)];
    }

    public List<CharacterRecord> GetAll()
    {
        lock (dbLock) return [.. db.Table<CharacterRecord>()];
    }

    public void Delete(string key)
    {
        lock (dbLock) db.Delete<CharacterRecord>(key);
        RaiseChanged();
    }

    public void Reset(string key)
    {
        lock (dbLock)
        {
            var rec = db.Find<CharacterRecord>(key);
            if (rec == null) return;
            rec.FreeCompany  = null;
            rec.SearchInfo   = null;
            rec.PrivateHouse = null;
            rec.FcHouse      = null;
            rec.Gil          = -1;
            rec.Mgp          = -1;
            rec.FcPoints     = -1;
            rec.Inventory    = null;
            db.Update(rec);
        }
        RaiseChanged();
    }

    public List<HousingBidRecord> GetAllBids()
    {
        lock (dbLock) return [.. db.Table<HousingBidRecord>()];
    }

    public List<HousingBidRecord> GetBidsByCharacter(string key)
    {
        lock (dbLock) return [.. db.Table<HousingBidRecord>().Where(b => b.CharacterKey == key)];
    }

    public void AddBid(HousingBidRecord bid)
    {
        lock (dbLock) db.Insert(bid);
    }

    public void DeleteBid(int id)
    {
        lock (dbLock) db.Delete<HousingBidRecord>(id);
    }

    public void Dispose()
    {
        lock (dbLock) db.Close();
    }
}
