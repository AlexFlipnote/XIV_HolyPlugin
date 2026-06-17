using System;
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

        if (!characterWanted && !fcWanted && !plateWanted && !privateHouseWanted && !fcHouseWanted && !dbEnabled) return;

        // Cross-world check, bail with a warning if visiting another world
        bool differentWorld = false;
        await framework.RunOnFrameworkThread(() =>
        {
            if (objectTable[0] is IPlayerCharacter pc)
                differentWorld = pc.HomeWorld.RowId != pc.CurrentWorld.RowId;
        });

        if (differentWorld)
        {
            await framework.RunOnFrameworkThread(() =>
                chatGui.Print(new XivChatEntry
                {
                    Type    = XivChatType.Echo,
                    Message = new SeStringBuilder().AddText("You are in a different world, cannot show info").Build(),
                }));
            return;
        }

        // When DB is enabled we collect everything regardless of display toggles
        bool needCharacter    = characterWanted    || dbEnabled;
        bool needFc           = fcWanted           || dbEnabled;
        bool needPlate        = plateWanted        || dbEnabled;
        bool needPrivateHouse = privateHouseWanted || dbEnabled;
        bool needFcHouse      = fcHouseWanted      || dbEnabled;

        CharInfo? charInfo    = needCharacter    ? await CollectCharacterAsync(token)    : null;
        string?   privateHouse = needPrivateHouse ? await CollectPrivateHouseAsync(token) : null;
        string?   fcHouse      = needFcHouse      ? await CollectFcHouseAsync(token)      : null;
        long      gil          = dbEnabled         ? await CollectGilAsync(token)          : 0;
        FcData?   fc    = null;
        SeString? plate = null;

        if (needFc) fc = await CollectFcAsync(token, instant); // self-contained retry until definitive

        // Load existing record once — used for cached plate display and as fallback for uncertain values.
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

                    // Still empty after 10s — player cleared their search info
                    var r = await Task.Run(() => characterDb.GetByKey(charInfo!.DbKey), token);
                    if (r != null) { r.SearchInfo = null; await Task.Run(() => characterDb.Upsert(r), token); }
                }, token);
            }
            else
            {
                // No cached value — FC resolution above already took a few seconds, plate likely ready.
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

        // Persist to DB — fall back to existing record values for anything that didn't load confidently
        if (dbEnabled && charInfo != null)
        {
            var record = new CharacterRecord
            {
                Key          = charInfo.DbKey,
                Name         = charInfo.Name,
                World        = charInfo.World,
                DataCenter   = charInfo.Dc,
                FreeCompany  = fc?.Display          ?? existing?.FreeCompany,
                SearchInfo   = plate?.TextValue      ?? existing?.SearchInfo,
                PrivateHouse = privateHouse          ?? existing?.PrivateHouse,
                FcHouse      = fcHouse               ?? existing?.FcHouse,
                Gil          = gil >= 0 ? gil        : existing?.Gil ?? 0,
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

        if (newFc?.Display    != null) existing.FreeCompany  = newFc.Display;
        if (newPrivateHouse   != null) existing.PrivateHouse = newPrivateHouse;
        if (newFcHouse        != null) existing.FcHouse      = newFcHouse;
        if (newGil            >= 0)    existing.Gil          = newGil;
        if (newPlate?.TextValue != null) existing.SearchInfo = newPlate.TextValue;
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

                var existing = await Task.Run(() => characterDb.GetByKey(charInfo.DbKey), token);
                if (existing == null) continue;

                // Only accept confident values; fall back to existing for anything that didn't load
                var newFcDisplay  = newFc?.Display          ?? existing.FreeCompany;
                var newGilValue   = newGil >= 0 ? newGil    : existing.Gil;
                var newPlateText  = newPlate?.TextValue      ?? existing.SearchInfo;
                var newPH         = newPrivateHouse          ?? existing.PrivateHouse;
                var newFcH        = newFcHouse               ?? existing.FcHouse;

                if (existing.FreeCompany  == newFcDisplay  &&
                    existing.PrivateHouse == newPH         &&
                    existing.FcHouse      == newFcH        &&
                    existing.Gil          == newGilValue   &&
                    existing.SearchInfo   == newPlateText)
                    continue;

                existing.FreeCompany  = newFcDisplay;
                existing.PrivateHouse = newPH;
                existing.FcHouse      = newFcH;
                existing.Gil          = newGilValue;
                existing.SearchInfo   = newPlateText;
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

    // Blocks until FC state is definitive: tag present (has FC) or proxy gone (no FC).
    // Pass instant=true to skip the wait (single read, used when data is guaranteed loaded).
    private async Task<FcData?> CollectFcAsync(CancellationToken token, bool instant = false)
    {
        if (!configuration.InfoEnabled && !configuration.CharactersDbEnabled) return null;

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
            if (proxyNull)      return null;                   // definitively no FC

            // proxy present but name still empty — still loading, wait and retry
            if (i < attempts - 1) await Task.Delay(500, token);
        }

        return null; // timed out
    }

    private async Task<SeString?> CollectPlateAsync(CancellationToken token)
    {
        if (!configuration.AdventurePlateEnabled && !configuration.CharactersDbEnabled) return null;

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

        if (bytes == null) return null;
        return SeString.Parse(bytes);
    }

    private async Task<string?> CollectPrivateHouseAsync(CancellationToken token)
    {
        if (!configuration.ShowPrivateHouseLocation && !configuration.CharactersDbEnabled) return null;

        token.ThrowIfCancellationRequested();

        string? result = null;

        await framework.RunOnFrameworkThread(() =>
        {
            unsafe
            {
                var id = HousingManager.GetOwnedHouseId(EstateType.PersonalEstate);
                if (id.Id == 0 || id.TerritoryTypeId == 65535) return;

                var district = GetDistrictName(id.TerritoryTypeId) ?? $"Zone {id.TerritoryTypeId}";
                result = id.IsApartment
                    ? $"{district} Apartment"
                    : $"{district}, Ward {id.WardIndex + 1}, Plot {id.PlotIndex + 1}";
            }
        });

        return result;
    }

    private async Task<string?> CollectFcHouseAsync(CancellationToken token)
    {
        if (!configuration.ShowFcHouseLocation && !configuration.CharactersDbEnabled) return null;

        token.ThrowIfCancellationRequested();

        string? result = null;

        await framework.RunOnFrameworkThread(() =>
        {
            unsafe
            {
                var id = HousingManager.GetOwnedHouseId(EstateType.FreeCompanyEstate);
                if (id.Id == 0 || id.TerritoryTypeId == 65535) return;

                var district = GetDistrictName(id.TerritoryTypeId) ?? $"Zone {id.TerritoryTypeId}";
                result = $"{district}, Ward {id.WardIndex + 1}, Plot {id.PlotIndex + 1}";
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

    private static string? GetDistrictName(ushort territoryTypeId) => territoryTypeId switch
    {
        339 => "Mist",
        340 => "Lavender Beds",
        341 => "The Goblet",
        641 => "Shirogane",
        979 => "Empyreum",
        _   => null,
    };
}
