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
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using HoliestFluffiness.Windows;

namespace HoliestFluffiness;

public class LoginInfoHandler(Configuration configuration, IChatGui chatGui, IFramework framework, IObjectTable objectTable, LoginInfoWindow loginInfoWindow, CharacterDb characterDb, IPluginLog log)
{
    public static readonly Dictionary<uint, string> TrackedItems = new()
    {
        { 10155u, "Ceruleum" },
        { 10373u, "Magitek" },
    };
    private record FcData(string Tag, string Name, bool IsLeader)
    {
        public string Display => $"«{Tag}» {Name}";
    }

    private record CharInfo(string Name, string World, string Dc)
    {
        public string Display => Dc.Length > 0 ? $"{Name} @ {World} ({Dc})" : $"{Name} @ {World}";
        public string DbKey   => $"{Name}@{World}";
    }

    public event Action? OnInfoReady;

    // Fires once per RunAsync call, after the background FC-points task finishes (or immediately if
    // there was nothing to refresh). Lets callers that switch characters wait out the FC window cycle.
    public event Action? OnFcPointsReady;

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

            // Gil, search info, and last seen stay accurate cross-world; save them if we have a record
            if (dbEnabled)
            {
                var xwChar = await CollectCharacterAsync(token);
                if (xwChar != null)
                {
                    var xwExisting = await Task.Run(() => characterDb.GetByKey(xwChar.DbKey), token);
                    if (xwExisting != null)
                    {
                        var xwGil   = await CollectGilAsync(token);
                        var xwMgp   = await CollectMgpAsync(token);
                        var xwPlate = await CollectPlateAsync(token, retry: true);
                        if (xwGil >= 0)                     xwExisting.Gil        = xwGil;
                        if (xwMgp >= 0)                     xwExisting.Mgp        = xwMgp;
                        if (xwPlate?.TextValue != null)     xwExisting.SearchInfo = xwPlate.TextValue;
                        xwExisting.LastSeen = DateTime.UtcNow;
                        await Task.Run(() => characterDb.Upsert(xwExisting), token);
                    }
                }
            }

            return;
        }

        // DB enabled: collect everything regardless of display toggles. FC is always fetched
        // (a reliable signal that the character has fully loaded in).
        bool needCharacter    = characterWanted    || dbEnabled;
        bool needFc           = true;
        bool needPlate        = plateWanted        || dbEnabled;
        bool needPrivateHouse = privateHouseWanted || dbEnabled;
        bool needFcHouse      = fcHouseWanted      || dbEnabled;

        CharInfo? charInfo    = needCharacter    ? await CollectCharacterAsync(token)    : null;
        string?   privateHouse = needPrivateHouse ? await CollectPrivateHouseAsync(token) : null;
        string?   fcHouse      = needFcHouse      ? await CollectFcHouseAsync(token)      : null;
        long      gil          = dbEnabled         ? await CollectGilAsync(token)          : 0;
        long      mgp          = dbEnabled         ? await CollectMgpAsync(token)          : -1;
        string?   inventory    = dbEnabled         ? await CollectInventoryAsync(token)    : null;
        FcData?   fc    = null;
        SeString? plate = null;

        if (needFc) fc = await CollectFcAsync(token, instant); // self-contained retry until definitive

        // Load existing record once: used for cached plate display and as fallback for uncertain values.
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

                    // Still empty after 10s: player cleared their search info
                    var r = await Task.Run(() => characterDb.GetByKey(charInfo!.DbKey), token);
                    if (r != null) { r.SearchInfo = null; await Task.Run(() => characterDb.Upsert(r), token); }
                }, token);
            }
            else
            {
                // No cached value; FC resolution above already took a few seconds, plate likely ready.
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

        // Persist to DB; fall back to existing record values for anything that didn't load confidently
        if (dbEnabled && charInfo != null)
        {
            var record = new CharacterRecord
            {
                Key          = charInfo.DbKey,
                Name         = charInfo.Name,
                World        = charInfo.World,
                DataCenter   = charInfo.Dc,
                FreeCompany  = fc?.Display,
                FcLeader     = fc?.IsLeader ?? false,
                SearchInfo   = plate?.TextValue      ?? existing?.SearchInfo,
                PrivateHouse = privateHouse          ?? existing?.PrivateHouse,
                FcHouse      = fc == null ? null     : (fcHouse ?? existing?.FcHouse),
                Gil          = gil >= 0 ? gil        : existing?.Gil ?? 0,
                Mgp          = mgp >= 0 ? mgp        : existing?.Mgp ?? -1,
                FcPoints     = fc == null ? -1    : existing?.FcPoints ?? -1,
                Inventory    = inventory               ?? existing?.Inventory,
                LastSeen     = DateTime.UtcNow,
            };
            await Task.Run(() => characterDb.UpsertPreservingSlot(record), token);
        }

        OnInfoReady?.Invoke();

        // Safe to force the FC window open/closed only now that login has fully settled. Runs in the
        // background so it doesn't delay RunAsync. Always forced: there's no passive "never requested"
        // signal (the agent always exists and a stale/zero reading looks like a real one).
        if (dbEnabled && configuration.FcPointsTrackingEnabled && fc != null && charInfo != null)
        {
            var dbKey = charInfo.DbKey;
            _ = Task.Run(async () =>
            {
                try
                {
                    var refreshed = await CollectFcPointsAsync(token, allowForceRefresh: true);
                    if (refreshed < 0) return;

                    var rec = await Task.Run(() => characterDb.GetByKey(dbKey), token);
                    if (rec == null) return;

                    rec.FcPoints = refreshed;
                    await Task.Run(() => characterDb.Upsert(rec), token);
                    log.Debug("FC points refreshed for {Key}: {Points}", dbKey, refreshed);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.Error(ex, "FC points refresh failed for {Key}", dbKey);
                }
                finally
                {
                    OnFcPointsReady?.Invoke();
                }
            }, token);
        }
        else
        {
            OnFcPointsReady?.Invoke();
        }
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
        var newMgp          = await CollectMgpAsync(CancellationToken.None);
        var newFcPoints     = await CollectFcPointsAsync(CancellationToken.None);
        var newPlate        = await CollectPlateAsync(CancellationToken.None);
        var newInventory    = await CollectInventoryAsync(CancellationToken.None);

        existing.FreeCompany = newFc?.Display;
        existing.FcLeader    = newFc?.IsLeader ?? false;
        existing.FcHouse     = newFc == null ? null : (newFcHouse ?? existing.FcHouse);
        existing.FcPoints    = newFc == null ? -1   : (newFcPoints >= 0 ? newFcPoints : existing.FcPoints);
        if (newPrivateHouse   != null) existing.PrivateHouse = newPrivateHouse;
        if (newGil            >= 0)    existing.Gil          = newGil;
        if (newMgp            >= 0)    existing.Mgp          = newMgp;
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
                var newMgp          = await CollectMgpAsync(token);
                var newFcPoints     = await CollectFcPointsAsync(token);
                var newPlate        = await CollectPlateAsync(token);
                var newInventory    = await CollectInventoryAsync(token);

                var existing = await Task.Run(() => characterDb.GetByKey(charInfo.DbKey), token);
                if (existing == null) continue;

                // Only accept confident values; fall back to existing for anything that didn't load
                var newFcDisplay  = newFc?.Display;
                var newFcLeader   = newFc?.IsLeader ?? false;
                var newGilValue   = newGil >= 0 ? newGil    : existing.Gil;
                var newMgpValue   = newMgp >= 0 ? newMgp    : existing.Mgp;
                var newFcPointsValue = newFc == null ? -1   : (newFcPoints >= 0 ? newFcPoints : existing.FcPoints);
                var newPlateText  = newPlate?.TextValue      ?? existing.SearchInfo;
                var newPH         = newPrivateHouse          ?? existing.PrivateHouse;
                var newFcH        = newFc == null ? null     : (newFcHouse ?? existing.FcHouse);
                var newInv        = newInventory             ?? existing.Inventory;

                if (existing.FreeCompany  == newFcDisplay  &&
                    existing.FcLeader     == newFcLeader   &&
                    existing.PrivateHouse == newPH         &&
                    existing.FcHouse      == newFcH        &&
                    existing.Gil          == newGilValue   &&
                    existing.Mgp          == newMgpValue   &&
                    existing.FcPoints     == newFcPointsValue &&
                    existing.SearchInfo   == newPlateText  &&
                    existing.Inventory    == newInv)
                    continue;

                existing.FreeCompany  = newFcDisplay;
                existing.FcLeader     = newFcLeader;
                existing.PrivateHouse = newPH;
                existing.FcHouse      = newFcH;
                existing.Gil          = newGilValue;
                existing.Mgp          = newMgpValue;
                existing.FcPoints     = newFcPointsValue;
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
                        Common.ShowToast(
                            "Character Info", toastMsg.ToString(),
                            durationSec: 10f));
                break;

            default: // Echo
                var message = BuildChatMessage(character, fc, plate, privateHouse, fcHouse, includeHeader: true);
                if (message != null)
                    await framework.RunOnFrameworkThread(() => chatGui.Print(message));
                break;
        }
    }

    private SeString? BuildChatMessage(string? character, FcData? fc, SeString? plate, string? privateHouse, string? fcHouse, bool includeHeader = false)
    {
        if (character == null && fc == null && plate == null && privateHouse == null && fcHouse == null) return null;

        var builder = new SeStringBuilder();
        var spacer = "";
        if (includeHeader) {
            builder.AddText("Character info loaded.\n");
            spacer = "    ";
        }

        foreach (var slot in configuration.LoginInfoOrder)
        {
            switch (slot)
            {
                case 0 when character != null:
                    builder.AddText($"{spacer}》 {character}");
                    break;
                case 1 when plate != null:
                    builder.AddText($"\n{spacer}》 Search info: ");
                    foreach (var payload in plate.Payloads)
                        builder.Add(payload);
                    break;
                case 2 when privateHouse != null:
                    builder.AddText($"\n{spacer}》 Private house: {privateHouse}");
                    break;
                case 3 when fc != null:
                    builder.AddText($"\n{spacer}》 Free Company: {fc.Display}");
                    break;
                case 4 when fcHouse != null:
                    builder.AddText($"\n{spacer}》 FC house: {fcHouse}");
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
            string tag        = string.Empty;
            string name       = string.Empty;
            string master     = string.Empty;
            string playerName = string.Empty;
            bool   proxyNull  = false;

            await framework.RunOnFrameworkThread(() =>
            {
                if (objectTable[0] is IPlayerCharacter pc)
                {
                    tag        = pc.CompanyTag.ToString();
                    playerName = pc.Name.TextValue;
                }

                unsafe
                {
                    var fc = InfoProxyFreeCompany.Instance();
                    proxyNull = fc == null;
                    if (fc != null)
                    {
                        name   = fc->NameString;
                        master = fc->MasterString;
                    }
                }
            });

            // Master ownership can be transferred at any time, so leadership is recomputed from the
            // live proxy on every collection (never cached) — a demoted ex-master flips back to false.
            if (tag.Length > 0)
            {
                var isLeader = master.Length > 0 && playerName.Length > 0 &&
                               string.Equals(master, playerName, StringComparison.Ordinal);
                return new FcData(tag, name, isLeader); // has FC
            }
            if (proxyNull)      return null;                  // proxy gone, no FC

            // proxy present but tag still empty: still loading, wait and retry
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

    private Task<long> CollectGilAsync(CancellationToken token) => CollectCurrencyAsync(token, 1);
    private Task<long> CollectMgpAsync(CancellationToken token) => CollectCurrencyAsync(token, 29);

    private async Task<long> CollectCurrencyAsync(CancellationToken token, uint itemId)
    {
        token.ThrowIfCancellationRequested();

        long result = -1;

        await framework.RunOnFrameworkThread(() =>
        {
            unsafe
            {
                var inv = InventoryManager.Instance();
                if (inv == null) return;
                var container = inv->GetInventoryContainer(InventoryType.Currency);
                if (container == null) return;
                for (int i = 0; i < (int)container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId != itemId) continue;
                    result = slot->Quantity;
                    break;
                }
            }
        });

        return result;
    }

    private const string FcWindowAddonName = "FreeCompany";
    private const string FcWindowOpenCommand = "/freecompanycmd";

    // Not exposed by InfoProxyFreeCompany; read straight off the FreeCompanyCreditShop agent. The agent
    // always exists, and the raw offset reads 0 until the game actually requests credit-shop data, so
    // "never requested" is indistinguishable from "genuinely zero". allowForceRefresh triggers that request.
    private async Task<long> CollectFcPointsAsync(CancellationToken token, bool allowForceRefresh = false)
    {
        if (!configuration.CharactersDbEnabled || !configuration.FcPointsTrackingEnabled) return -1;
        token.ThrowIfCancellationRequested();

        if (!allowForceRefresh) return await ReadFcPointsRawAsync(token);

        return await ForceRefreshFcWindowAsync(token);
    }

    private async Task<long> ReadFcPointsRawAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long result = -1;

        await framework.RunOnFrameworkThread(() =>
        {
            unsafe
            {
                var module = AgentModule.Instance();
                if (module == null) return;
                var agent = module->GetAgentByInternalId(AgentId.FreeCompanyCreditShop);
                if (agent == null) return;
                result = *(int*)((nint)agent + 256);
            }
        });

        return result;
    }

    // Pops the FC window open (triggering the server request), closes it, then polls the raw value
    // until it changes from what it read before. The request is an async round trip that can land
    // after the window closes, so we wait for a real change rather than a fixed delay. If the player
    // already had the window open, we leave it alone and just wait for a value change.
    private async Task<long> ForceRefreshFcWindowAsync(CancellationToken token)
    {
        var before = await ReadFcPointsRawAsync(token);

        bool alreadyOpen = false;
        await framework.RunOnFrameworkThread(() =>
        {
            unsafe
            {
                var addon = GetFcWindowAddon();
                alreadyOpen = addon != null && Common.IsAddonVisible(addon);
            }
        });

        if (!alreadyOpen)
            await framework.RunOnFrameworkThread(() => Common.ExecuteCommand(FcWindowOpenCommand));

        for (var i = 0; i < 20; i++)
        {
            token.ThrowIfCancellationRequested();

            bool ready = false;
            await framework.RunOnFrameworkThread(() =>
            {
                unsafe
                {
                    var addon = GetFcWindowAddon();
                    ready = addon != null && addon->IsReady;
                }
            });
            if (ready) break;

            await Task.Delay(200, token);
        }

        if (!alreadyOpen)
        {
            await framework.RunOnFrameworkThread(() =>
            {
                unsafe
                {
                    var addon = GetFcWindowAddon();
                    if (addon != null) addon->Close(true);
                }
            });
        }

        for (var i = 0; i < 20; i++)
        {
            token.ThrowIfCancellationRequested();
            var current = await ReadFcPointsRawAsync(token);
            if (current != before) return current;
            await Task.Delay(300, token);
        }

        return await ReadFcPointsRawAsync(token);
    }

    private static unsafe AtkUnitBase* GetFcWindowAddon() =>
        (AtkUnitBase*)AtkStage.Instance()->RaptureAtkUnitManager->GetAddonByName(FcWindowAddonName);

    private async Task<string?> CollectInventoryAsync(CancellationToken token)
    {
        if (!configuration.CharactersDbEnabled) return null;
        token.ThrowIfCancellationRequested();

        // null = inventory manager unavailable (caller preserves existing value). Non-null always includes
        // every tracked item (0 if not found), so the UI can tell "never scanned" from "scanned, found zero".
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
