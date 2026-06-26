using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using HoliestFluffiness.Windows;

namespace HoliestFluffiness;

public class LoginInfoHandler(Configuration configuration, IChatGui chatGui, IFramework framework, IObjectTable objectTable, LoginInfoWindow loginInfoWindow, CharacterDb characterDb, IPluginLog log, INotificationManager notificationManager)
{
    public static readonly Dictionary<uint, string> TrackedItems = new()
    {
        { 10155u, "Ceruleum" },
        { 10373u, "Magitek" },
    };
    private record FcData(string Tag, string Name)
    {
        public string Display => $"«{Tag}» {Name}";
    }

    private record CharInfo(string Name, string World, string Dc)
    {
        public string Display => Dc.Length > 0 ? $"{Name} @ {World} ({Dc})" : $"{Name} @ {World}";
        public string DbKey   => $"{Name}@{World}";
    }

    public event Action? OnInfoReady;

    // Called on login, retries every second for up to 10s waiting for data to load.
    public async Task RunAsync(CancellationToken token, bool instant = false)
    {
        bool characterWanted    = configuration.ShowCharacterInfo;
        bool fcWanted           = configuration.InfoEnabled;
        bool plateWanted        = configuration.AdventurePlateEnabled;
        bool privateHouseWanted = configuration.ShowPrivateHouseLocation;
        bool fcHouseWanted      = configuration.ShowFcHouseLocation;
        bool dbEnabled          = configuration.CharactersDbEnabled;

        if (!characterWanted && !fcWanted && !plateWanted && !privateHouseWanted && !fcHouseWanted && !dbEnabled && !configuration.AccessoryEnabled) return;

        // Cross-world check, bail with a warning if visiting another world
        if (await IsOnDifferentWorldAsync())
        {
            if (characterWanted || fcWanted || plateWanted || privateHouseWanted || fcHouseWanted)
                await framework.RunOnFrameworkThread(() =>
                    chatGui.Print(new XivChatEntry
                    {
                        Type    = XivChatType.Echo,
                        Message = new SeStringBuilder().AddText("You are in a different world, cannot show info").Build(),
                    }));

            // Gil, search info, and last seen are still accurate cross-world, save them if we have a record
            if (dbEnabled)
            {
                var xwChar = await CollectCharacterAsync(token);
                if (xwChar != null)
                {
                    var xwExisting = await Task.Run(() => characterDb.GetByKey(xwChar.DbKey), token);
                    if (xwExisting != null)
                    {
                        var xwGil   = await CollectGilAsync(token);
                        var xwPlate = await CollectPlateAsync(token, retry: true);
                        if (xwGil >= 0)                     xwExisting.Gil        = xwGil;
                        if (xwPlate?.TextValue != null)     xwExisting.SearchInfo = xwPlate.TextValue;
                        xwExisting.LastSeen = DateTime.UtcNow;
                        await Task.Run(() => characterDb.Upsert(xwExisting), token);
                    }
                }
            }

            return;
        }

        // When DB is enabled we collect everything regardless of display toggles.
        // FC is always fetched, it's a reliable signal that the character has fully loaded in.
        bool needCharacter    = characterWanted    || dbEnabled;
        bool needFc           = true;
        bool needPlate        = plateWanted        || dbEnabled;
        bool needPrivateHouse = privateHouseWanted || dbEnabled;
        bool needFcHouse      = fcHouseWanted      || dbEnabled;

        CharInfo? charInfo    = needCharacter    ? await CollectCharacterAsync(token)    : null;
        string?   privateHouse = needPrivateHouse ? await CollectPrivateHouseAsync(token) : null;
        string?   fcHouse      = needFcHouse      ? await CollectFcHouseAsync(token)      : null;
        long      gil          = dbEnabled         ? await CollectGilAsync(token)          : 0;
        string?   inventory    = dbEnabled         ? await CollectInventoryAsync(token)    : null;
        FcData?   fc    = null;
        SeString? plate = null;

        if (needFc) fc = await CollectFcAsync(token, instant); // self-contained retry until definitive

        // Load existing record once ,  used for cached plate display and as fallback for uncertain values.
        CharacterRecord? existing = (dbEnabled && charInfo != null)
            ? await Task.Run(() => characterDb.GetByKey(charInfo.DbKey), token)
            : null;

        if (needPlate && !instant)
        {
            // If we have a cached value in the DB, show it immediately and verify live in background.
            // Otherwise fall through to a normal live retry.
            string? cachedPlate = existing?.SearchInfo;

            if (cachedPlate != null)
            {
                plate = new SeStringBuilder().AddText(cachedPlate).Build();

                _ = Task.Run(async () =>
                {
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (DateTime.UtcNow < deadline)
                    {
                        token.ThrowIfCancellationRequested();
                        var live = await CollectPlateAsync(token);
                        var liveText = live?.TextValue;

                        if (!string.IsNullOrEmpty(liveText))
                        {
                            if (liveText == cachedPlate) return; // unchanged, nothing to do
                            var rec = await Task.Run(() => characterDb.GetByKey(charInfo!.DbKey), token);
                            if (rec != null) { rec.SearchInfo = liveText; await Task.Run(() => characterDb.Upsert(rec), token); }
                            return;
                        }

                        await Task.Delay(500, token);
                    }

                    // Still empty after 10s ,  player cleared their search info
                    var r = await Task.Run(() => characterDb.GetByKey(charInfo!.DbKey), token);
                    if (r != null) { r.SearchInfo = null; await Task.Run(() => characterDb.Upsert(r), token); }
                }, token);
            }
            else
            {
                // No cached value ,  FC resolution above already took a few seconds, plate likely ready.
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    plate = await CollectPlateAsync(token);
                    if (plate != null) break;
                    await Task.Delay(500, token);
                }
            }
        }
        else if (needPlate)
        {
            plate = await CollectPlateAsync(token);
        }

        // Display (filtered by per-toggle settings)
        string? displayChar = characterWanted    ? charInfo?.Display : null;
        string? displayPH   = privateHouseWanted ? privateHouse      : null;
        string? displayFcH  = fcHouseWanted      ? fcHouse           : null;
        FcData? displayFc   = fcWanted           ? fc                : null;
        SeString? displayPl = plateWanted        ? plate             : null;

        if (displayChar != null || displayFc != null || displayPl != null || displayPH != null || displayFcH != null)
            await ShowData(displayChar, displayFc, displayPl, displayPH, displayFcH);

        // Persist to DB ,  fall back to existing record values for anything that didn't load confidently
        if (dbEnabled && charInfo != null)
        {
            var record = new CharacterRecord
            {
                Key          = charInfo.DbKey,
                Name         = charInfo.Name,
                World        = charInfo.World,
                DataCenter   = charInfo.Dc,
                FreeCompany  = fc?.Display,
                SearchInfo   = plate?.TextValue      ?? existing?.SearchInfo,
                PrivateHouse = privateHouse          ?? existing?.PrivateHouse,
                FcHouse      = fc == null ? null     : (fcHouse ?? existing?.FcHouse),
                Gil          = gil >= 0 ? gil                    : existing?.Gil ?? 0,
                Inventory    = inventory                         ?? existing?.Inventory,
                LastSeen     = DateTime.UtcNow,
            };
            await Task.Run(() => characterDb.UpsertPreservingSlot(record), token);
        }

        OnInfoReady?.Invoke();
    }

    private async Task<bool> IsOnDifferentWorldAsync()
    {
        bool different = false;
        await framework.RunOnFrameworkThread(() =>
        {
            if (objectTable[0] is IPlayerCharacter pc)
                different = pc.HomeWorld.RowId != pc.CurrentWorld.RowId;
        });
        return different;
    }

    public async Task QuickSaveAsync()
    {
        if (!configuration.CharactersDbEnabled) return;
        if (await IsOnDifferentWorldAsync()) return;

        var charInfo = await CollectCharacterAsync(CancellationToken.None);
        if (charInfo == null) return;

        var existing = await Task.Run(() => characterDb.GetByKey(charInfo.DbKey));
        if (existing == null) return;

        var newFc           = await CollectFcAsync(CancellationToken.None, instant: true);
        var newPrivateHouse = await CollectPrivateHouseAsync(CancellationToken.None);
        var newFcHouse      = await CollectFcHouseAsync(CancellationToken.None);
        var newGil          = await CollectGilAsync(CancellationToken.None);
        var newPlate        = await CollectPlateAsync(CancellationToken.None);
        var newInventory    = await CollectInventoryAsync(CancellationToken.None);

        existing.FreeCompany = newFc?.Display;
        existing.FcHouse     = newFc == null ? null : (newFcHouse ?? existing.FcHouse);
        if (newPrivateHouse   != null) existing.PrivateHouse = newPrivateHouse;
        if (newGil            >= 0)    existing.Gil          = newGil;
        if (newPlate?.TextValue != null) existing.SearchInfo = newPlate.TextValue;
        if (newInventory      != null) existing.Inventory    = newInventory;
        existing.LastSeen = DateTime.UtcNow;
        await Task.Run(() => characterDb.Upsert(existing));
        log.Debug("Quick save written for {Key} before character switch.", charInfo.DbKey);
    }

    public async Task RunPeriodicUpdatesAsync(CancellationToken token)
    {
        if (!configuration.CharactersDbEnabled) return;

        var charInfo = await CollectCharacterAsync(token);
        if (charInfo == null) return;

        while (!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(2), token);
            token.ThrowIfCancellationRequested();

            if (!configuration.CharactersDbEnabled) continue;

            try
            {
                if (await IsOnDifferentWorldAsync()) continue;

                var newFc           = await CollectFcAsync(token, instant: true);
                var newPrivateHouse = await CollectPrivateHouseAsync(token);
                var newFcHouse      = await CollectFcHouseAsync(token);
                var newGil          = await CollectGilAsync(token);
                var newPlate        = await CollectPlateAsync(token);
                var newInventory    = await CollectInventoryAsync(token);

                var existing = await Task.Run(() => characterDb.GetByKey(charInfo.DbKey), token);
                if (existing == null) continue;

                // Only accept confident values; fall back to existing for anything that didn't load
                var newFcDisplay  = newFc?.Display;
                var newGilValue   = newGil >= 0 ? newGil    : existing.Gil;
                var newPlateText  = newPlate?.TextValue      ?? existing.SearchInfo;
                var newPH         = newPrivateHouse          ?? existing.PrivateHouse;
                var newFcH        = newFc == null ? null     : (newFcHouse ?? existing.FcHouse);
                var newInv        = newInventory             ?? existing.Inventory;

                if (existing.FreeCompany  == newFcDisplay  &&
                    existing.PrivateHouse == newPH         &&
                    existing.FcHouse      == newFcH        &&
                    existing.Gil          == newGilValue   &&
                    existing.SearchInfo   == newPlateText  &&
                    existing.Inventory    == newInv)
                    continue;

                existing.FreeCompany  = newFcDisplay;
                existing.PrivateHouse = newPH;
                existing.FcHouse      = newFcH;
                existing.Gil          = newGilValue;
                existing.SearchInfo   = newPlateText;
                existing.Inventory    = newInv;
                existing.LastSeen     = DateTime.UtcNow;
                await Task.Run(() => characterDb.Upsert(existing), token);
                log.Debug("Periodic DB update written for {Key}", charInfo.DbKey);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Error(ex, "Periodic DB update failed for {Key}; will retry next cycle.", charInfo.DbKey);
            }
        }
    }

    private async Task ShowData(string? character, FcData? fc, SeString? plate, string? privateHouse, string? fcHouse)
    {
        switch (configuration.LoginInfoDisplay)
        {
            case LoginInfoDisplay.Popup:
                var order = configuration.LoginInfoOrder;
                await framework.RunOnFrameworkThread(() =>
                    loginInfoWindow.SetData(character, fc?.Display, plate?.ToString(), privateHouse, fcHouse, order));
                break;

            case LoginInfoDisplay.Toast:
                var toastMsg = BuildChatMessage(character, fc, plate, privateHouse, fcHouse);
                if (toastMsg != null)
                    await framework.RunOnFrameworkThread(() =>
                        notificationManager.AddNotification(new Notification
                        {
                            Content         = toastMsg.ToString(),
                            Type            = NotificationType.Info,
                            InitialDuration = TimeSpan.FromSeconds(10),
                            Minimized       = false,
                        }));
                break;

            default: // Echo
                var message = BuildChatMessage(character, fc, plate, privateHouse, fcHouse);
                if (message != null)
                    await framework.RunOnFrameworkThread(() => chatGui.Print(message));
                break;
        }
    }

    private SeString? BuildChatMessage(string? character, FcData? fc, SeString? plate, string? privateHouse, string? fcHouse)
    {
        if (character == null && fc == null && plate == null && privateHouse == null && fcHouse == null) return null;

        var builder = new SeStringBuilder().AddText("Character info loaded.");

        foreach (var slot in configuration.LoginInfoOrder)
        {
            switch (slot)
            {
                case 0 when character != null:
                    builder.AddText($"\n    》 {character}");
                    break;
                case 1 when plate != null:
                    builder.AddText("\n    》 Search info: ");
                    foreach (var payload in plate.Payloads)
                        builder.Add(payload);
                    break;
                case 2 when privateHouse != null:
                    builder.AddText($"\n    》 Private house: {privateHouse}");
                    break;
                case 3 when fc != null:
                    builder.AddText($"\n    》 Free Company: {fc.Display}");
                    break;
                case 4 when fcHouse != null:
                    builder.AddText($"\n    》 FC house: {fcHouse}");
                    break;
            }
        }

        return builder.Build();
    }

    private async Task<CharInfo?> CollectCharacterAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        CharInfo? result = null;

        await framework.RunOnFrameworkThread(() =>
        {
            if (objectTable[0] is not IPlayerCharacter pc) return;

            var name  = pc.Name.TextValue;
            var world = pc.HomeWorld.ValueNullable?.Name.ExtractText();
            var dc    = pc.HomeWorld.ValueNullable?.DataCenter.ValueNullable?.Name.ExtractText() ?? "";

            if (name.Length == 0 || world == null) return;

            result = new CharInfo(name, world, dc);
        });

        return result;
    }

    // Blocks until FC state is known: tag present (has FC) or tag still empty after retries (no FC).
    // Pass instant=true to skip the wait (single read, used when data is guaranteed loaded).
    private async Task<FcData?> CollectFcAsync(CancellationToken token, bool instant = false)
    {
        token.ThrowIfCancellationRequested();

        var attempts = instant ? 1 : 10;
        for (var i = 0; i < attempts; i++)
        {
            string tag       = string.Empty;
            string name      = string.Empty;
            bool   proxyNull = false;

            await framework.RunOnFrameworkThread(() =>
            {
                if (objectTable[0] is IPlayerCharacter pc)
                    tag = pc.CompanyTag.ToString();

                unsafe
                {
                    var fc = InfoProxyFreeCompany.Instance();
                    proxyNull = fc == null;
                    if (fc != null) name = fc->NameString;
                }
            });

            if (tag.Length > 0) return new FcData(tag, name); // has FC
            if (proxyNull)      return null;                  // proxy gone, no FC

            // proxy present but tag still empty ,  still loading, wait and retry
            if (i < attempts - 1) await Task.Delay(500, token);
        }

        return null; // tag still empty after retries, not in FC
    }

    private async Task<SeString?> CollectPlateAsync(CancellationToken token, bool retry = false)
    {
        if (!configuration.AdventurePlateEnabled && !configuration.CharactersDbEnabled) return null;

        var attempts = retry ? 10 : 1;
        for (var i = 0; i < attempts; i++)
        {
            token.ThrowIfCancellationRequested();

            byte[]? bytes = null;
            await framework.RunOnFrameworkThread(() =>
            {
                unsafe
                {
                    var detail = InfoProxyDetail.Instance();
                    if (detail == null) return;
                    var span = detail->UpdateData.SearchComment;
                    if (span.Length <= 0) return;
                    bytes = span.ToArray();
                }
            });

            if (bytes != null) return SeString.Parse(bytes);
            if (i < attempts - 1) await Task.Delay(500, token);
        }

        return null;
    }

    private async Task<string?> CollectPrivateHouseAsync(CancellationToken token)
    {
        if (!configuration.ShowPrivateHouseLocation && !configuration.CharactersDbEnabled) return null;
        return await CollectHouseLocationAsync(token, EstateType.PersonalEstate, allowApartment: true);
    }

    private async Task<string?> CollectFcHouseAsync(CancellationToken token)
    {
        if (!configuration.ShowFcHouseLocation && !configuration.CharactersDbEnabled) return null;
        return await CollectHouseLocationAsync(token, EstateType.FreeCompanyEstate, allowApartment: false);
    }

    private async Task<string?> CollectHouseLocationAsync(CancellationToken token, EstateType type, bool allowApartment)
    {
        token.ThrowIfCancellationRequested();
        string? result = null;
        await framework.RunOnFrameworkThread(() =>
        {
            unsafe
            {
                var id = HousingManager.GetOwnedHouseId(type);
                if (id.Id == 0 || id.TerritoryTypeId == 65535) return;
                var district = HousingDistricts.FromTerritoryId(id.TerritoryTypeId) ?? $"Zone {id.TerritoryTypeId}";
                result = allowApartment && id.IsApartment
                    ? $"{district} Apartment"
                    : $"{district}, Ward {id.WardIndex + 1}, Plot {id.PlotIndex + 1}";
            }
        });
        return result;
    }

    private async Task<long> CollectGilAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        long result = -1; // -1 = not yet cached

        await framework.RunOnFrameworkThread(() =>
        {
            unsafe
            {
                var inv = InventoryManager.Instance();
                if (inv == null) return;
                var container = inv->GetInventoryContainer(InventoryType.Currency);
                if (container == null) return;
                var slot = container->GetInventorySlot(0);
                if (slot == null || slot->ItemId != 1) return; // slot 0 is Gil (item ID 1)
                result = slot->Quantity;
            }
        });

        return result;
    }

    private async Task<string?> CollectInventoryAsync(CancellationToken token)
    {
        if (!configuration.CharactersDbEnabled) return null;
        token.ThrowIfCancellationRequested();

        // null return = inventory manager unavailable (not logged in yet); caller preserves existing value
        // non-null return always includes every tracked item, 0 if not found, so the UI can distinguish
        // "never scanned" (rec.Inventory == null) from "scanned, found zero" (item present with value 0)
        Dictionary<uint, int>? counts = null;
        await framework.RunOnFrameworkThread(() =>
        {
            unsafe
            {
                var inv = InventoryManager.Instance();
                if (inv == null) return;
                counts = TrackedItems.Keys.ToDictionary(id => id, _ => 0);
                foreach (var bag in new[] { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 })
                {
                    var container = inv->GetInventoryContainer(bag);
                    if (container == null) continue;
                    for (int i = 0; i < (int)container->Size; i++)
                    {
                        var slot = container->GetInventorySlot(i);
                        if (slot == null || slot->ItemId == 0) continue;
                        if (counts.ContainsKey(slot->ItemId))
                            counts[slot->ItemId] += (int)slot->Quantity;
                    }
                }
            }
        });

        return counts == null ? null : JsonSerializer.Serialize(counts);
    }

}
