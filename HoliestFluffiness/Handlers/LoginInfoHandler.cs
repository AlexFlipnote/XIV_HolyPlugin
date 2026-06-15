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

    private record CharInfo(string Name, string World, string Dc, int? Slot = null)
    {
        private string WorldSlot => Slot.HasValue ? $"{World}/{Slot}" : World;
        public string Display => Dc.Length > 0 ? $"{Name} @ {WorldSlot} ({Dc})" : $"{Name} @ {WorldSlot}";
        public string DbKey   => $"{Name}@{World}";
    }

    public event Action? OnInfoReady;

    // Called on login — retries every second for up to 10s waiting for data to load.
    public async Task RunAsync(CancellationToken token, bool instant = false)
    {
        bool characterWanted    = configuration.ShowCharacterInfo;
        bool fcWanted           = configuration.InfoEnabled;
        bool plateWanted        = configuration.AdventurePlateEnabled;
        bool privateHouseWanted = configuration.ShowPrivateHouseLocation;
        bool fcHouseWanted      = configuration.ShowFcHouseLocation;
        bool dbEnabled          = configuration.CharactersDbEnabled;

        if (!characterWanted && !fcWanted && !plateWanted && !privateHouseWanted && !fcHouseWanted && !dbEnabled) return;

        // Cross-world check — bail with a warning if visiting another world
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
        FcData?   fc           = null;
        SeString? plate        = null;

        if (needFc)
        {
            int attempts = instant ? 1 : 10;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                fc = await CollectFcAsync(token);
                if (fc != null) break;
                if (attempt < attempts - 1) await Task.Delay(1000, token);
            }
        }

        if (needPlate)
        {
            if (!instant) await Task.Delay(3000, token);
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

        // Persist to DB
        if (dbEnabled && charInfo != null)
        {
            var record = new CharacterRecord
            {
                Key          = charInfo.DbKey,
                Name         = charInfo.Name,
                World        = charInfo.World,
                DataCenter   = charInfo.Dc,
                FreeCompany  = fc?.Display,
                SearchInfo   = plate?.TextValue,
                PrivateHouse = privateHouse,
                FcHouse      = fcHouse,
                Gil          = gil,
                LastSeen     = DateTime.UtcNow,
            };
            await Task.Run(() => characterDb.UpsertPreservingSlot(record), token);
        }

        OnInfoReady?.Invoke();
    }

    public async Task QuickSaveAsync()
    {
        if (!configuration.CharactersDbEnabled) return;

        var charInfo = await CollectCharacterAsync(CancellationToken.None);
        if (charInfo == null) return;

        var existing = await Task.Run(() => characterDb.GetByKey(charInfo.DbKey));
        if (existing == null) return;

        var newFc           = await CollectFcAsync(CancellationToken.None);
        var newPrivateHouse = await CollectPrivateHouseAsync(CancellationToken.None);
        var newFcHouse      = await CollectFcHouseAsync(CancellationToken.None);
        var newGil          = await CollectGilAsync(CancellationToken.None);
        var newPlate        = await CollectPlateAsync(CancellationToken.None);

        existing.FreeCompany  = newFc?.Display;
        existing.PrivateHouse = newPrivateHouse;
        existing.FcHouse      = newFcHouse;
        existing.Gil          = newGil;
        existing.SearchInfo   = newPlate?.TextValue;
        existing.LastSeen     = DateTime.UtcNow;
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
                var newFc           = await CollectFcAsync(token);
                var newPrivateHouse = await CollectPrivateHouseAsync(token);
                var newFcHouse      = await CollectFcHouseAsync(token);
                var newGil          = await CollectGilAsync(token);
                var newPlate        = await CollectPlateAsync(token);

                var existing = await Task.Run(() => characterDb.GetByKey(charInfo.DbKey), token);
                if (existing == null) continue;

                var newFcDisplay = newFc?.Display;
                var newPlateText = newPlate?.TextValue;

                if (existing.FreeCompany  == newFcDisplay   &&
                    existing.PrivateHouse == newPrivateHouse &&
                    existing.FcHouse      == newFcHouse      &&
                    existing.Gil          == newGil          &&
                    existing.SearchInfo   == newPlateText)
                    continue;

                existing.FreeCompany  = newFcDisplay;
                existing.PrivateHouse = newPrivateHouse;
                existing.FcHouse      = newFcHouse;
                existing.Gil          = newGil;
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

        if (result != null)
        {
            var slot = await Task.Run(() => characterDb.GetByKey(result.DbKey)?.Slot, token);
            result = result with { Slot = slot };
        }

        return result;
    }

    private async Task<FcData?> CollectFcAsync(CancellationToken token)
    {
        if (!configuration.InfoEnabled && !configuration.CharactersDbEnabled) return null;

        token.ThrowIfCancellationRequested();

        string tag  = string.Empty;
        string name = string.Empty;

        await framework.RunOnFrameworkThread(() =>
        {
            if (objectTable[0] is IPlayerCharacter pc)
                tag = pc.CompanyTag.ToString();

            unsafe
            {
                var fc = InfoProxyFreeCompany.Instance();
                if (fc != null) name = fc->NameString;
            }
        });

        if (string.IsNullOrEmpty(tag)) return null;
        return new FcData(tag, name);
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
