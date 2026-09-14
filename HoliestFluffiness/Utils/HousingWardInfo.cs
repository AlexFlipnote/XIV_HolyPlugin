using System;
using System.IO;
using System.Text;

namespace HoliestFluffiness;

// Identifies which land plot table a HousingWardInfo blob is for. Field read order below matches
// the wire layout (LandId, WardNumber, TerritoryTypeId, WorldId) - do not reorder.
public sealed record LandIdent(short LandId, short WardNumber, short TerritoryTypeId, short WorldId);

public sealed class HouseInfoEntry
{
    public uint HousePrice;
    public HousingFlags InfoFlags;
    public sbyte[] HouseAppeals = new sbyte[3];
    public string EstateOwnerName = "";

    public bool IsOwned => (InfoFlags & HousingFlags.PlotOwned) != 0;
}

[Flags]
public enum HousingFlags : byte
{
    PlotOwned        = 1 << 0,
    VisitorsAllowed  = 1 << 1,
    HasSearchComment = 1 << 2,
    HouseBuilt       = 1 << 3,
    OwnedByFC        = 1 << 4,
}

public enum PurchaseType : byte { Unavailable = 0, FCFS = 1, Lottery = 2 }
public enum TenantType : byte { FreeCompany = 1, Personal = 2 }

// Binary layout confirmed against the open-source FFXIV_PaissaHouse plugin's HousingWardInfo
// reader: LandIdent (8 bytes), then 60 fixed-size plot entries, then purchase/tenant type bytes.
// Sent by the server whenever a ward is opened/selected in the "HousingSelectBlock" addon.
internal sealed class HousingWardInfo
{
    public const int WireSize = 2664;
    private const int PlotCount = 60;

    public LandIdent LandIdent = null!;
    public HouseInfoEntry[] HouseInfoEntries = new HouseInfoEntry[PlotCount];
    public PurchaseType PurchaseType;
    public TenantType TenantType;

    public static unsafe HousingWardInfo Read(nint dataPtr)
    {
        var wardInfo = new HousingWardInfo();
        using var stream = new UnmanagedMemoryStream((byte*)dataPtr, WireSize);
        using var reader = new BinaryReader(stream);

        wardInfo.LandIdent = new LandIdent(
            LandId:          reader.ReadInt16(),
            WardNumber:      reader.ReadInt16(),
            TerritoryTypeId: reader.ReadInt16(),
            WorldId:         reader.ReadInt16());

        for (var i = 0; i < PlotCount; i++)
        {
            var entry = new HouseInfoEntry
            {
                HousePrice = reader.ReadUInt32(),
                InfoFlags  = (HousingFlags)reader.ReadByte(),
            };
            for (var j = 0; j < entry.HouseAppeals.Length; j++)
                entry.HouseAppeals[j] = reader.ReadSByte();

            var name = Encoding.UTF8.GetString(reader.ReadBytes(32)).TrimEnd('\0');
            // An unowned plot's owner-name bytes are leftover garbage, not a real name.
            entry.EstateOwnerName = (entry.InfoFlags & HousingFlags.PlotOwned) != 0 ? name : "";

            wardInfo.HouseInfoEntries[i] = entry;
        }

        wardInfo.PurchaseType = (PurchaseType)reader.ReadByte();
        reader.ReadByte(); // padding
        wardInfo.TenantType = (TenantType)reader.ReadByte();
        // Remaining bytes are trailing padding, deliberately left unread.

        return wardInfo;
    }
}
