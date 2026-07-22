using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
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

    private readonly record struct CharacterSnapshot(
        FcData?   Fc,
        string?   PrivateHouse,
        string?   FcHouse,
        long      Gil,
        long      Mgp,
        long      FcPoints,
        SeString? Plate,
        string?   Inventory);

    public event Action? OnInfoReady;

    // Once per RunAsync, after the background FC-points task finishes. Lets a caller that switches
    // characters wait out the FC window cycle.
    public event Action? OnFcPointsReady;

    // Retries every second for up to 10s waiting for data to load
    public async Task RunAsync(CancellationToken token, bool instant = false)
    {
        bool characterWanted    = configuration.ShowCharacterInfo;
        bool fcWanted           = configuration.InfoEnabled;
        bool plateWanted        = configuration.AdventurePlateEnabled;
        bool privateHouseWanted = configuration.ShowPrivateHouseLocation;
        bool fcHouseWanted      = configuration.ShowFcHouseLocation;
        bool dbEnabled          = configuration.CharactersDbEnabled;

        if (!characterWanted && !fcWanted && !plateWanted && !privateHouseWanted && !fcHouseWanted && !dbEnabled && !configuration.AccessoryEnabled) return;

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

        // With the DB on, collect everything regardless of display toggles. FC is always fetched: it
        // is a reliable signal that the character has fully loaded in.
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

        // Loaded once: cached plate display, plus fallback for anything that loads uncertainly
        CharacterRecord? existing = (dbEnabled && charInfo != null)
            ? await Task.Run(() => characterDb.GetByKey(charInfo.DbKey), token)
            : null;

        if (needPlate && !instant)
        {
            // A cached value shows immediately and is verified live in the background
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

        string? displayChar = characterWanted    ? charInfo?.Display : null;
        string? displayPH   = privateHouseWanted ? privateHouse      : null;
        string? displayFcH  = fcHouseWanted      ? fcHouse           : null;
        FcData? displayFc   = fcWanted           ? fc                : null;
        SeString? displayPl = plateWanted        ? plate             : null;

        if (displayChar != null || displayFc != null || displayPl != null || displayPH != null || displayFcH != null)
            await ShowData(displayChar, displayFc, displayPl, displayPH, displayFcH);

        // Anything that did not load confidently falls back to the existing record
        if (dbEnabled && charInfo != null)
        {
            var record = new CharacterRecord
            {
                Key        = charInfo.DbKey,
                Name       = charInfo.Name,
                World      = charInfo.World,
                DataCenter = charInfo.Dc,
            };
            // Fc points are not collected here; the -1 sentinel keeps the existing value (refreshed
            // by the background task below). Gil falls back to 0, not -1, when there is no record yet.
            var snapshot = new CharacterSnapshot(fc, privateHouse, fcHouse, gil, mgp, -1, plate, inventory);
            var fallback = existing ?? new CharacterRecord { Gil = 0, Mgp = -1, FcPoints = -1 };
            Merge(record, fallback, snapshot);
            record.LastSeen = DateTime.UtcNow;
            await Task.Run(() => characterDb.UpsertPreservingSlot(record), token);
        }

        OnInfoReady?.Invoke();

        // Only safe to force the FC window open/closed now that login has settled. Always forced:
        // there is no passive "never requested" signal to wait on.
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

        var snapshot = await CollectSnapshotAsync(CancellationToken.None);
        Merge(existing, existing, snapshot);
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

                var snapshot = await CollectSnapshotAsync(token);

                var existing = await Task.Run(() => characterDb.GetByKey(charInfo.DbKey), token);
                if (existing == null) continue;

                if (!Merge(existing, existing, snapshot)) continue;
                existing.LastSeen = DateTime.UtcNow;
                await Task.Run(() => characterDb.Upsert(existing), token);
                log.Debug("Periodic DB update written for {Key}", charInfo.DbKey);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Error(ex, "Periodic DB update failed for {Key}; will retry next cycle.", charInfo.DbKey);
            }
        }
    }

    // The collect-once path shared by QuickSaveAsync and RunPeriodicUpdatesAsync. RunAsync collects
    // its own way (display toggles, cached-plate verification) and only reuses Merge.
    private async Task<CharacterSnapshot> CollectSnapshotAsync(CancellationToken token)
    {
        var fc           = await CollectFcAsync(token, instant: true);
        var privateHouse = await CollectPrivateHouseAsync(token);
        var fcHouse      = await CollectFcHouseAsync(token);
        var gil          = await CollectGilAsync(token);
        var mgp          = await CollectMgpAsync(token);
        var fcPoints     = await CollectFcPointsAsync(token);
        var plate        = await CollectPlateAsync(token);
        var inventory    = await CollectInventoryAsync(token);
        return new CharacterSnapshot(fc, privateHouse, fcHouse, gil, mgp, fcPoints, plate, inventory);
    }

    // Applies the "only accept confident values" rules field by field, writing into target and using
    // fallback for anything the snapshot did not load confidently. target and fallback are the same
    // record for in-place updates; RunAsync passes a fresh target with a separate fallback source.
    // Returns whether any merged field differs from target's prior value (used to skip no-op writes).
    private static bool Merge(CharacterRecord target, CharacterRecord fallback, CharacterSnapshot snap)
    {
        var freeCompany  = snap.Fc?.Display;
        var fcLeader     = snap.Fc?.IsLeader ?? false;
        var gil          = snap.Gil >= 0 ? snap.Gil : fallback.Gil;
        var mgp          = snap.Mgp >= 0 ? snap.Mgp : fallback.Mgp;
        var fcPoints     = snap.Fc == null ? -1 : (snap.FcPoints >= 0 ? snap.FcPoints : fallback.FcPoints);
        var searchInfo   = snap.Plate?.TextValue ?? fallback.SearchInfo;
        var privateHouse = snap.PrivateHouse      ?? fallback.PrivateHouse;
        var fcHouse      = snap.Fc == null ? null : (snap.FcHouse ?? fallback.FcHouse);
        var inventory    = snap.Inventory         ?? fallback.Inventory;

        var changed =
            target.FreeCompany  != freeCompany  ||
            target.FcLeader     != fcLeader     ||
            target.PrivateHouse != privateHouse ||
            target.FcHouse      != fcHouse      ||
            target.Gil          != gil          ||
            target.Mgp          != mgp          ||
            target.FcPoints     != fcPoints     ||
            target.SearchInfo   != searchInfo   ||
            target.Inventory    != inventory;

        target.FreeCompany  = freeCompany;
        target.FcLeader     = fcLeader;
        target.PrivateHouse = privateHouse;
        target.FcHouse      = fcHouse;
        target.Gil          = gil;
        target.Mgp          = mgp;
        target.FcPoints     = fcPoints;
        target.SearchInfo   = searchInfo;
        target.Inventory    = inventory;

        return changed;
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
            var world = Common.WorldName(pc);
            var dc    = pc.HomeWorld.ValueNullable?.DataCenter.ValueNullable?.Name.ExtractText() ?? "";

            if (name.Length == 0 || world == null) return;

            result = new CharInfo(name, world, dc);
        });

        return result;
    }

    // Blocks until FC state is known and complete, or still incomplete after retries. instant=true does
    // a single read, for when the data is guaranteed loaded.
    //
    // Tag and name come from two independent sources that load on different schedules: the tag is read
    // straight off the local player's own game object (populated as soon as that object exists), while
    // the name comes from InfoProxyFreeCompany, a separate module the client fills in only after it gets
    // a server round trip for the local player's FC. Right after login/character-switch the tag can be
    // valid for several hundred ms while the name is still empty, so both must be present before
    // treating an FC read as definitive. Tag empty is only a confirmed "not in FC" once the player
    // object itself has resolved; before that (or while the proxy has not initialized) there is no
    // signal yet, so it keeps retrying rather than concluding "no FC" from an absence of data.
    private async Task<FcData?> CollectFcAsync(CancellationToken token, bool instant = false)
    {
        token.ThrowIfCancellationRequested();

        var attempts = instant ? 1 : 10;
        string tag = string.Empty, name = string.Empty;

        for (var i = 0; i < attempts; i++)
        {
            bool havePc = false;
            string master = string.Empty, playerName = string.Empty;
            tag = name = string.Empty;

            await framework.RunOnFrameworkThread(() =>
            {
                if (objectTable[0] is IPlayerCharacter pc)
                {
                    havePc     = true;
                    tag        = pc.CompanyTag.ToString();
                    playerName = pc.Name.TextValue;
                }

                unsafe
                {
                    var fc = InfoProxyFreeCompany.Instance();
                    if (fc != null)
                    {
                        name   = fc->NameString;
                        master = fc->MasterString;
                    }
                }
            });

            // Player object resolved and tag empty: authoritative "not in an FC", nothing left to wait for
            if (havePc && tag.Length == 0) return null;

            // Master can be transferred at any time, so leadership is never cached
            if (tag.Length > 0 && name.Length > 0)
            {
                var isLeader = master.Length > 0 && playerName.Length > 0 &&
                               string.Equals(master, playerName, StringComparison.Ordinal);
                return new FcData(tag, name, isLeader); // tag and name both confirmed
            }

            // Either the player object has not resolved yet, or the tag is confirmed but the name
            // proxy has not caught up yet; keep waiting either way
            if (i < attempts - 1) await Task.Delay(500, token);
        }

        // Retries exhausted with a tag but no name: report what we have rather than losing the tag
        return tag.Length > 0 ? new FcData(tag, name, false) : null;
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

    // Not exposed by InfoProxyFreeCompany, so read straight off the FreeCompanyCreditShop agent. The
    // offset reads 0 until the game requests credit-shop data, making "never requested"
    // indistinguishable from a genuine zero; allowForceRefresh triggers that request.
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

    // Opens the FC window to trigger the server request, closes it, then polls until the value
    // changes. The round trip can land after the window closes, so waiting on a real change beats a
    // fixed delay. A window the player already had open is left alone.
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

        // null means the inventory manager was unavailable; a non-null result always lists every
        // tracked item (0 when absent), so the UI can tell "never scanned" from "found zero".
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
