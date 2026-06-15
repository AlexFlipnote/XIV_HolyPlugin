using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace HoliestFluffiness.Windows;

public class ConfigWindow : Window
{
    private readonly Configuration configuration;
    private readonly LoginInfoHandler loginInfoHandler;
    private readonly AccessoryHandler accessoryHandler;
    private readonly IObjectTable objectTable;
    private readonly CharacterDb characterDb;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IClientState clientState;
    private readonly Action<string, string> onSwitchCharacter;

    private CancellationTokenSource? testAllCts;
    private CancellationTokenSource? accessoryCts;
    private CancellationTokenSource? bulkUpdateCts;
    private int bulkUpdateProgress;
    private int bulkUpdateTotal;

    private int selectedSection;

    // Characters section state
    private List<CharacterRecord>? cachedRecords;
    private string charFilter = "";

    private static readonly Vector4 ColBg       = new(30f / 255f,  30f / 255f,  30f / 255f,  1f);
    private static readonly Vector4 ColSidebar  = new(48f / 255f,  48f / 255f,  48f / 255f,  1f);
    private static readonly Vector4 ColContent  = new(24f / 255f,  24f / 255f,  24f / 255f,  1f);
    private static readonly Vector4 ColSection  = new(40f / 255f,  40f / 255f,  40f / 255f,  1f);
    private static readonly Vector4 ColBgDeep   = new(18f / 255f,  18f / 255f,  18f / 255f,  1f);
    private static readonly Vector4 ColWhite    = new(249f / 255f, 248f / 255f, 244f / 255f, 1f);
    private static readonly Vector4 ColWhiteDim = new(249f / 255f, 248f / 255f, 244f / 255f, 0.55f);
    private static readonly Vector4 ColGreen    = new( 80f / 255f, 200f / 255f,  80f / 255f, 1f);
    private static readonly Vector4 ColRed      = new(220f / 255f,  80f / 255f,  80f / 255f, 1f);
    private static readonly Vector4 ColGold     = new(235f / 255f, 230f / 255f, 114f / 255f, 1f);
    private static readonly Vector4 ColGoldSub  = new(235f / 255f, 230f / 255f, 114f / 255f, 0.18f);
    private static readonly Vector4 ColGoldMid  = new(235f / 255f, 230f / 255f, 114f / 255f, 0.35f);
    private static readonly Vector4 ColNone     = new(0f, 0f, 0f, 0f);

    public ConfigWindow(Configuration configuration, LoginInfoHandler loginInfoHandler, AccessoryHandler accessoryHandler, IObjectTable objectTable, IDalamudPluginInterface pluginInterface, CharacterDb characterDb, IClientState clientState, Action<string, string> onSwitchCharacter)
        : base($"The Holiest Fluffiness##Config")
    {
        this.configuration = configuration;
        this.loginInfoHandler = loginInfoHandler;
        this.accessoryHandler = accessoryHandler;
        this.objectTable = objectTable;
        this.characterDb = characterDb;
        this.pluginInterface = pluginInterface;
        this.clientState = clientState;
        this.onSwitchCharacter = onSwitchCharacter;
        selectedSection = configuration.LastSelectedSection;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 250),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        Size          = new Vector2(600, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void NavigateTo(int section)
    {
        selectedSection = section;
        configuration.LastSelectedSection = section;
        configuration.Save();
        if (section == 3) LoadCharacters();
    }

    public override void PreDraw()
    {
        SizeConstraints = selectedSection == 3
            ? new WindowSizeConstraints { MinimumSize = new Vector2(700, 380), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) }
            : new WindowSizeConstraints { MinimumSize = new Vector2(480, 250), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        PushGlobalStyle();
    }

    public override void PostDraw()
    {
        PopGlobalStyle();
        ImGui.PopStyleVar();
    }

    public override void Draw()
    {
        var avail = ImGui.GetContentRegionAvail();
        const float sidebarWidth = 180f;

        DrawSidebar(sidebarWidth, avail.Y);
        ImGui.SameLine(0, 0);
        DrawMain(avail.Y);

        DrawResizeGrip();
    }

    private void DrawResizeGrip()
    {
        const float gripSize = 15f;
        var winPos  = ImGui.GetWindowPos();
        var winSize = ImGui.GetWindowSize();
        var corner  = winPos + winSize;
        var mouse   = ImGui.GetMousePos();

        bool hovered = mouse.X >= corner.X - gripSize && mouse.X <= corner.X &&
                       mouse.Y >= corner.Y - gripSize && mouse.Y <= corner.Y;
        var col = ImGui.GetColorU32(hovered ? ColGold : ColGoldMid);

        var p1 = corner;
        var p2 = corner with { X = corner.X - gripSize };
        var p3 = corner with { Y = corner.Y - gripSize };

        ImGui.GetForegroundDrawList().AddTriangleFilled(p1, p2, p3, col);
    }

    // ── Sidebar ──────────────────────────────────────────────────────────────

    private void DrawSidebar(float width, float height)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, ColSidebar);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6, 6));
        ImGui.BeginChild("##sidebar", new Vector2(width, height), false);

        ImGui.Dummy(new Vector2(0, 4));
        SidebarHeader("SETTINGS");
        SidebarItem("Login info", 0);
        SidebarItem("Fashion accessory", 1);
        SidebarItem("Database", 2);
        if (SidebarItem("Characters", 3))
            LoadCharacters();

        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    private void SidebarHeader(string label)
    {
        var indent = ImGui.GetStyle().FramePadding.X + ImGui.GetContentRegionAvail().X * 0.05f;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + indent);
        ImGui.PushStyleColor(ImGuiCol.Text, ColGold);
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();
    }

    private bool SidebarItem(string label, int index)
    {
        bool active = selectedSection == index;

        if (active)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        ColGold);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColGold);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  ColGold);
            ImGui.PushStyleColor(ImGuiCol.Text,          ColBg);
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        ColNone);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColGoldSub);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  ColGoldMid);
            ImGui.PushStyleColor(ImGuiCol.Text,          ColWhite);
        }

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f);
        ImGui.PushStyleVar(ImGuiStyleVar.ButtonTextAlign, new Vector2(0f, 0.5f));
        bool clicked = ImGui.Button(label, new Vector2(ImGui.GetContentRegionAvail().X - 6f, 30));
        if (clicked)
        {
            selectedSection = index;
            configuration.LastSelectedSection = index;
            configuration.Save();
        }
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(4);
        return clicked;
    }

    // ── Main content ─────────────────────────────────────────────────────────

    private void DrawMain(float height)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg,              ColContent);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,          ColBgDeep);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab,        ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, ColGold);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive,  ColGold);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 0));
        ImGui.BeginChild("##main", new Vector2(0, height), false);

        switch (selectedSection)
        {
            case 0: DrawLoginInfoSection();   break;
            case 1: DrawAccessorySection();   break;
            case 2: DrawDatabaseSection();    break;
            case 3: DrawCharactersSection();  break;
        }

        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(5);
    }

    // ── Section helpers ───────────────────────────────────────────────────────

    private void BeginSection(string title, Action? afterTitle = null)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, ColSection);
        ImGui.BeginChild(title + "##sec", new Vector2(0, 0), false);

        ImGui.Dummy(new Vector2(0, 6));
        ImGui.PushStyleColor(ImGuiCol.Text, ColGold);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 10f);
        ImGui.TextUnformatted(title);
        if (afterTitle != null) { ImGui.SameLine(); afterTitle(); }
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 6));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);
    }

    private void EndSection()
    {
        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private static void SectionRow() =>
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);

    // ── Sections ─────────────────────────────────────────────────────────────

    private void DrawLoginInfoSection()
    {
        BeginSection("Login info");

        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Information that will appear when you login with a character.");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 4));
        SectionRow();

        bool anyEnabled = configuration.ShowCharacterInfo || configuration.InfoEnabled || configuration.AdventurePlateEnabled || configuration.ShowPrivateHouseLocation || configuration.ShowFcHouseLocation;
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Simulate login:");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.BeginDisabled(!anyEnabled);
        PushButton();
        if (ImGui.Button("Test##loginall"))
        {
            testAllCts?.Cancel();
            testAllCts?.Dispose();
            testAllCts = new CancellationTokenSource();
            Task.Run(() => loginInfoHandler.RunAsync(testAllCts.Token, instant: true));
        }
        PopButton();
        ImGui.EndDisabled();

        ImGui.Dummy(new Vector2(0, 4));
        SectionRow();

        PushCheckbox();
        var asPopup = configuration.LoginInfoAsPopup;
        bool popupChanged = ImGui.Checkbox("Show as a popup instead", ref asPopup);
        PopCheckbox();
        if (popupChanged)
        {
            configuration.LoginInfoAsPopup = asPopup;
            configuration.Save();
        }

        ImGui.Dummy(new Vector2(0, 8));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);
        ImGui.PushStyleColor(ImGuiCol.Text, ColGold);
        ImGui.TextUnformatted("What to show");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        PushButton();
        if (ImGui.Button("Reset order##loginorder"))
        {
            configuration.LoginInfoOrder = [0, 1, 2, 3, 4];
            configuration.Save();
        }
        PopButton();

        DrawInfoOrderList();

        EndSection();
    }

    private static readonly string[] InfoItemLabels = ["Character", "Search info", "Private house", "Free Company", "FC house"];
    private static readonly string[] DbColNames     = ["Last Seen", "Character", "World", "DC", "FC", "Search Info", "Private House", "FC House", "Gil"];

    private void DrawInfoOrderList()
    {
        var order  = configuration.LoginInfoOrder;
        var right  = ImGui.GetContentRegionMax().X;
        const float btnW = 22f;

        for (int i = 0; i < order.Count; i++)
        {
            int slot = order[i];

            SectionRow();

            bool enabled = slot switch
            {
                0 => configuration.ShowCharacterInfo,
                1 => configuration.AdventurePlateEnabled,
                2 => configuration.ShowPrivateHouseLocation,
                3 => configuration.InfoEnabled,
                4 => configuration.ShowFcHouseLocation,
                _ => false,
            };
            PushCheckbox();
            bool newEnabled = enabled;
            if (ImGui.Checkbox($"{InfoItemLabels[slot]}##{slot}", ref newEnabled) && newEnabled != enabled)
            {
                switch (slot)
                {
                    case 0: configuration.ShowCharacterInfo          = newEnabled; break;
                    case 1: configuration.AdventurePlateEnabled      = newEnabled; break;
                    case 2: configuration.ShowPrivateHouseLocation   = newEnabled; break;
                    case 3: configuration.InfoEnabled                = newEnabled; break;
                    case 4: configuration.ShowFcHouseLocation        = newEnabled; break;
                }
                configuration.Save();
            }
            PopCheckbox();

            // ▲ / ▼ buttons right-aligned
            ImGui.SameLine(right - btnW * 2 - 4);
            PushButton();
            ImGui.BeginDisabled(i == 0);
            if (ImGui.Button($"^##{slot}u", new Vector2(btnW, 0)))
            {
                (order[i - 1], order[i]) = (order[i], order[i - 1]);
                configuration.Save();
            }
            ImGui.EndDisabled();
            ImGui.SameLine(0, 2);
            ImGui.BeginDisabled(i == order.Count - 1);
            if (ImGui.Button($"v##{slot}d", new Vector2(btnW, 0)))
            {
                (order[i + 1], order[i]) = (order[i], order[i + 1]);
                configuration.Save();
            }
            ImGui.EndDisabled();
            PopButton();
        }

        ImGui.Dummy(new Vector2(0, 4));
    }

    private void LoadCharacters() =>
        cachedRecords = [.. characterDb.GetAll().OrderBy(r => r.World).ThenBy(r => r.Slot ?? int.MaxValue)];

    private void DrawCharacterNameCell(CharacterRecord rec, bool lifestreamOn)
    {
        if (lifestreamOn)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ColGold);
            if (ImGui.Selectable($"{rec.Name}##sel{rec.Key}", false, ImGuiSelectableFlags.None))
                onSwitchCharacter(rec.Name, rec.World);
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
            {
                var slotHint = rec.Slot is { } s ? $" (slot {s})" : "";
                ImGui.SetTooltip($"Click to switch to {rec.Name} on {rec.World}{slotHint}");
            }
        }
        else
        {
            ImGui.TextUnformatted(rec.Name);
        }
    }

    private void DrawDatabaseSection()
    {
        BeginSection("Database");

        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Stores character info to a local SQLite database on every login.");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 6));
        SectionRow();

        PushCheckbox();
        var dbEnabled = configuration.CharactersDbEnabled;
        if (ImGui.Checkbox("Enable character database", ref dbEnabled))
        {
            configuration.CharactersDbEnabled = dbEnabled;
            configuration.Save();
        }
        PopCheckbox();

        ImGui.Dummy(new Vector2(0, 8));
        SectionRow();

        var count = characterDb.Count();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted($"{count:N0} character{(count == 1 ? "" : "s")} indexed");
        ImGui.PopStyleColor();

        ImGui.Dummy(new Vector2(0, 8));
        SectionRow();

        if (bulkUpdateTotal > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
            ImGui.TextUnformatted($"Processing {bulkUpdateProgress}/{bulkUpdateTotal}...");
            ImGui.PopStyleColor();
            ImGui.SameLine();
            PushButton();
            if (ImGui.Button("Cancel##bulkupdate")) bulkUpdateCts?.Cancel();
            PopButton();
        }
        else
        {
            PushButton();
            if (ImGui.Button("Update all characters"))
            {
                bulkUpdateCts?.Cancel();
                bulkUpdateCts?.Dispose();
                bulkUpdateCts = new CancellationTokenSource();
                _ = RunBulkUpdateAsync(bulkUpdateCts.Token);
            }
            PopButton();
        }

        EndSection();
    }

    private async Task RunBulkUpdateAsync(CancellationToken token)
    {
        var chars = characterDb.GetAll()
            .OrderBy(r => r.World).ThenBy(r => r.Slot ?? int.MaxValue)
            .ToList();
        bulkUpdateTotal    = chars.Count;
        bulkUpdateProgress = 0;

        try
        {
            foreach (var rec in chars)
            {
                token.ThrowIfCancellationRequested();
                bulkUpdateProgress++;

                var loginTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var infoTcs  = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnLogin()   => loginTcs.TrySetResult(true);
                void OnInfoReady() => infoTcs.TrySetResult(true);
                clientState.Login              += OnLogin;
                loginInfoHandler.OnInfoReady   += OnInfoReady;
                try
                {
                    onSwitchCharacter(rec.Name, rec.World);
                    await loginTcs.Task.WaitAsync(TimeSpan.FromSeconds(60), token);
                    await infoTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                }
                catch (TimeoutException) { /* character didn't respond in time, skip */ }
                finally
                {
                    clientState.Login            -= OnLogin;
                    loginInfoHandler.OnInfoReady -= OnInfoReady;
                }
            }
        }
        finally
        {
            bulkUpdateTotal    = 0;
            bulkUpdateProgress = 0;
        }
    }

    private void DrawCharactersSection()
    {
        if (cachedRecords == null) LoadCharacters();

        bool lifestreamOn = pluginInterface.InstalledPlugins.Any(p => p.InternalName == "Lifestream" && p.IsLoaded);

        BeginSection("Characters", () =>
        {
            ImGui.PushStyleColor(ImGuiCol.Text, lifestreamOn ? ColGreen : ColRed);
            ImGui.TextUnformatted(lifestreamOn ? "(✓ Lifestream)" : "(✗ Lifestream)");
            ImGui.PopStyleColor();
        });

        var cols = configuration.CharactersColumns;

        SectionRow();
        ImGui.SetNextItemWidth(180f);
        PushInput();
        ImGui.InputText("##charfilter", ref charFilter, 128);
        PopInput();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Filter");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        PushButton();
        if (ImGui.Button("Columns##colvis")) ImGui.OpenPopup("##colpopup");
        PopButton();
        ImGui.SameLine();
        PushButton();
        if (ImGui.Button("Refresh##charrefresh")) LoadCharacters();
        PopButton();

        if (ImGui.BeginPopup("##colpopup"))
        {
            for (int i = 0; i < DbColNames.Length; i++)
            {
                bool vis = cols[i];
                PushCheckbox();
                if (ImGui.Checkbox(DbColNames[i] + "##colchk" + i, ref vis))
                {
                    cols[i] = vis;
                    configuration.Save();
                }
                PopCheckbox();
            }
            ImGui.EndPopup();
        }

        ImGui.Dummy(new Vector2(0, 2));

        int colCount = cols.Count(v => v);
        if (colCount == 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
            ImGui.TextUnformatted("No columns selected.");
            ImGui.PopStyleColor();
        }
        else
        {
            var tableH = Math.Max(50f, ImGui.GetContentRegionAvail().Y - 4f);
            var tableFlags = ImGuiTableFlags.Sortable
                | ImGuiTableFlags.ScrollY
                | ImGuiTableFlags.BordersOuter
                | ImGuiTableFlags.BordersInnerV
                | ImGuiTableFlags.RowBg
                | ImGuiTableFlags.SizingStretchProp;

            if (ImGui.BeginTable("##chardb", colCount, tableFlags, new Vector2(0, tableH)))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                if (cols[0]) ImGui.TableSetupColumn("Last Seen",     ImGuiTableColumnFlags.PreferSortDescending, 0, 0);
                if (cols[1]) ImGui.TableSetupColumn("Character",     ImGuiTableColumnFlags.None, 0, 1);
                if (cols[2]) ImGui.TableSetupColumn("World",         ImGuiTableColumnFlags.DefaultSort, 0, 2);
                if (cols[3]) ImGui.TableSetupColumn("DC",            ImGuiTableColumnFlags.None, 0, 3);
                if (cols[4]) ImGui.TableSetupColumn("FC",            ImGuiTableColumnFlags.None, 0, 4);
                if (cols[5]) ImGui.TableSetupColumn("Search Info",   ImGuiTableColumnFlags.None, 0, 5);
                if (cols[6]) ImGui.TableSetupColumn("Private House", ImGuiTableColumnFlags.None, 0, 6);
                if (cols[7]) ImGui.TableSetupColumn("FC House",      ImGuiTableColumnFlags.None, 0, 7);
                if (cols[8]) ImGui.TableSetupColumn("Gil",           ImGuiTableColumnFlags.None, 0, 8);
                ImGui.TableHeadersRow();

                var sortSpecs = ImGui.TableGetSortSpecs();
                if (sortSpecs.SpecsDirty && sortSpecs.SpecsCount > 0 && cachedRecords != null)
                {
                    var spec = sortSpecs.Specs;
                    bool desc = spec.SortDirection == ImGuiSortDirection.Descending;
                    cachedRecords = (int)spec.ColumnUserID switch
                    {
                        0 => [.. (desc ? cachedRecords.OrderByDescending(r => r.LastSeen).ThenBy(r => r.Slot ?? int.MaxValue)     : cachedRecords.OrderBy(r => r.LastSeen).ThenBy(r => r.Slot ?? int.MaxValue))],
                        1 => [.. (desc ? cachedRecords.OrderByDescending(r => r.Name).ThenBy(r => r.Slot ?? int.MaxValue)         : cachedRecords.OrderBy(r => r.Name).ThenBy(r => r.Slot ?? int.MaxValue))],
                        2 => [.. (desc ? cachedRecords.OrderByDescending(r => r.World).ThenBy(r => r.Slot ?? int.MaxValue)        : cachedRecords.OrderBy(r => r.World).ThenBy(r => r.Slot ?? int.MaxValue))],
                        3 => [.. (desc ? cachedRecords.OrderByDescending(r => r.DataCenter).ThenBy(r => r.Slot ?? int.MaxValue)   : cachedRecords.OrderBy(r => r.DataCenter).ThenBy(r => r.Slot ?? int.MaxValue))],
                        4 => [.. (desc ? cachedRecords.OrderByDescending(r => r.FreeCompany).ThenBy(r => r.Slot ?? int.MaxValue)  : cachedRecords.OrderBy(r => r.FreeCompany).ThenBy(r => r.Slot ?? int.MaxValue))],
                        5 => [.. (desc ? cachedRecords.OrderByDescending(r => r.SearchInfo).ThenBy(r => r.Slot ?? int.MaxValue)   : cachedRecords.OrderBy(r => r.SearchInfo).ThenBy(r => r.Slot ?? int.MaxValue))],
                        6 => [.. (desc ? cachedRecords.OrderByDescending(r => r.PrivateHouse).ThenBy(r => r.Slot ?? int.MaxValue) : cachedRecords.OrderBy(r => r.PrivateHouse).ThenBy(r => r.Slot ?? int.MaxValue))],
                        7 => [.. (desc ? cachedRecords.OrderByDescending(r => r.FcHouse).ThenBy(r => r.Slot ?? int.MaxValue)      : cachedRecords.OrderBy(r => r.FcHouse).ThenBy(r => r.Slot ?? int.MaxValue))],
                        8 => [.. (desc ? cachedRecords.OrderByDescending(r => r.Gil).ThenBy(r => r.Slot ?? int.MaxValue)          : cachedRecords.OrderBy(r => r.Gil).ThenBy(r => r.Slot ?? int.MaxValue))],
                        _ => cachedRecords,
                    };
                    sortSpecs.SpecsDirty = false;
                }

                var filter = charFilter.Trim();
                var worldFilter = WorldResolver.Resolve(filter, cachedRecords!.Select(r => r.World)) ?? filter;
                foreach (var rec in cachedRecords ?? [])
                {
                    if (filter.Length > 0
                        && !rec.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        && !rec.World.Contains(worldFilter, StringComparison.OrdinalIgnoreCase)
                        && !rec.DataCenter.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        && !(rec.FreeCompany ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    ImGui.TableNextRow();
                    int c = 0;
                    if (cols[0]) { ImGui.TableSetColumnIndex(c++); ImGui.TextUnformatted(rec.LastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm")); }
                    if (cols[1])
                    {
                        ImGui.TableSetColumnIndex(c++);
                        DrawCharacterNameCell(rec, lifestreamOn);
                    }
                    if (cols[2]) { ImGui.TableSetColumnIndex(c++); ImGui.TextUnformatted(rec.Slot is { } s ? $"{rec.World}/{s}" : rec.World); }
                    if (cols[3]) { ImGui.TableSetColumnIndex(c++); ImGui.TextUnformatted(rec.DataCenter); }
                    if (cols[4]) { ImGui.TableSetColumnIndex(c++); ImGui.TextUnformatted(rec.FreeCompany ?? ""); }
                    if (cols[5]) { ImGui.TableSetColumnIndex(c++); ImGui.TextUnformatted(rec.SearchInfo ?? ""); }
                    if (cols[6]) { ImGui.TableSetColumnIndex(c++); ImGui.TextUnformatted(rec.PrivateHouse ?? ""); }
                    if (cols[7]) { ImGui.TableSetColumnIndex(c++); ImGui.TextUnformatted(rec.FcHouse ?? ""); }
                    if (cols[8]) { ImGui.TableSetColumnIndex(c++); ImGui.TextUnformatted(rec.Gil == 0 ? "" : rec.Gil.ToString("N0", CultureInfo.InvariantCulture)); }
                }

                ImGui.EndTable();
            }
        }

        EndSection();
    }

    private void DrawAccessorySection()
    {
        BeginSection("Fashion accessory");

        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Simulate login:");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.BeginDisabled(!configuration.AccessoryEnabled);
        PushButton();
        if (ImGui.Button("Test##Fashion accessory"))
        {
            accessoryCts?.Cancel();
            accessoryCts?.Dispose();
            accessoryCts = new CancellationTokenSource();
            Task.Run(() => accessoryHandler.RunAsync(accessoryCts.Token));
        }
        PopButton();
        ImGui.EndDisabled();

        ImGui.Dummy(new Vector2(0, 6));
        SectionRow();

        PushCheckbox();
        var accessoryEnabled = configuration.AccessoryEnabled;
        bool accessoryChanged = ImGui.Checkbox("Equip accessory on login", ref accessoryEnabled);
        PopCheckbox();
        if (accessoryChanged)
        {
            configuration.AccessoryEnabled = accessoryEnabled;
            configuration.Save();
        }

        ImGui.Dummy(new Vector2(0, 4));
        ImGui.BeginDisabled(!accessoryEnabled);

        SectionRow();
        var accessoryName = configuration.AccessoryName;
        ImGui.SetNextItemWidth(200);
        PushInput();
        if (ImGui.InputText("Accessory name", ref accessoryName, 128))
        {
            configuration.AccessoryName = accessoryName;
            configuration.Save();
        }
        PopInput();

        SectionRow();
        var equipDelay = configuration.AccessoryEquipDelay;
        ImGui.SetNextItemWidth(80);
        PushInput();
        if (ImGui.InputInt("Equip delay in seconds (0–10)", ref equipDelay, 1, 5))
        {
            configuration.AccessoryEquipDelay = Math.Clamp(equipDelay, 0, 10);
            configuration.Save();
        }
        PopInput();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("(0 = instant)");
        ImGui.PopStyleColor();

        ImGui.EndDisabled();

        DrawRestrictionsSubsection(accessoryEnabled);

        EndSection();
    }

    private void DrawRestrictionsSubsection(bool enabled)
    {
        ImGui.Dummy(new Vector2(0, 8f));
        SubsectionLabel("Restrictions");

        ImGui.BeginDisabled(!enabled);

        SectionRow();
        var maxFreeSlots = configuration.AccessoryInventory;
        ImGui.SetNextItemWidth(90);
        PushInput();
        if (ImGui.InputInt("Stop if N or fewer free inventory slots (0–140)##max", ref maxFreeSlots, 1, 10))
        {
            configuration.AccessoryInventory = Math.Clamp(maxFreeSlots, 0, 140);
            configuration.Save();
        }
        PopInput();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("(0 = skip check)");
        ImGui.PopStyleColor();

        SectionRow();
        var minFreeSlots = configuration.AccessoryInventoryMin;
        ImGui.SetNextItemWidth(90);
        PushInput();
        if (ImGui.InputInt("Stop if N or more free inventory slots (0–140)##min", ref minFreeSlots, 1, 10))
        {
            configuration.AccessoryInventoryMin = Math.Clamp(minFreeSlots, 0, 140);
            configuration.Save();
        }
        PopInput();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("(0 = skip check)");
        ImGui.PopStyleColor();

        ImGui.EndDisabled();

        ImGui.Dummy(new Vector2(0, 8f));
        SubsectionLabel("Whitelist");

        SectionRow();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Characters listed here will bypass all restrictions.");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 2f));

        ImGui.BeginDisabled(!enabled);

        int removeIdx = -1;
        for (int i = 0; i < configuration.AccessoryWhitelist.Count; i++)
        {
            SectionRow();
            ImGui.TextUnformatted(configuration.AccessoryWhitelist[i]);
            ImGui.SameLine(ImGui.GetContentRegionMax().X - 65f);
            PushButton();
            if (ImGui.Button($" - ##{i}"))
                removeIdx = i;
            PopButton();
        }
        if (removeIdx >= 0)
        {
            configuration.AccessoryWhitelist.RemoveAt(removeIdx);
            configuration.Save();
        }

        SectionRow();
        var player = objectTable[0] as IPlayerCharacter;
        ImGui.BeginDisabled(player == null);
        PushButton();
        if (ImGui.Button("+ Add current character"))
        {
            var key = $"{player!.Name.TextValue}@{player.HomeWorld.ValueNullable?.Name.ExtractText()}";
            if (!configuration.AccessoryWhitelist.Contains(key))
            {
                configuration.AccessoryWhitelist.Add(key);
                configuration.Save();
            }
        }
        PopButton();
        ImGui.EndDisabled();

        ImGui.EndDisabled();
    }

    private void SubsectionLabel(string label)
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);
        ImGui.PushStyleColor(ImGuiCol.Text, ColGold);
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();
    }

    // ── Style helpers ─────────────────────────────────────────────────────────

    private void PushGlobalStyle()
    {
        ImGui.PushStyleColor(ImGuiCol.Text,                ColWhite);
        ImGui.PushStyleColor(ImGuiCol.WindowBg,            ColBg);
        ImGui.PushStyleColor(ImGuiCol.FrameBg,             ColBgDeep);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered,      ColSidebar);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive,       ColSidebar);
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip,          ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered,   ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive,    ColGold);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,   new Vector2(8, 6));
    }

    private void PopGlobalStyle()
    {
        ImGui.PopStyleColor(8);
        ImGui.PopStyleVar(2);
    }

    private void PushButton()
    {
        ImGui.PushStyleColor(ImGuiCol.Button,        ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  ColGold);
        ImGui.PushStyleColor(ImGuiCol.Text,          ColGold);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
    }

    private void PopButton()
    {
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar();
    }

    private void PushCheckbox()
    {
        ImGui.PushStyleColor(ImGuiCol.CheckMark, ColGold);
        ImGui.PushStyleColor(ImGuiCol.Border,    ColGold);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
    }

    private void PopCheckbox()
    {
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar();
    }

    private void PushInput()
    {
        ImGui.PushStyleColor(ImGuiCol.Border, ColGold);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
    }

    private void PopInput()
    {
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    public override void OnClose()
    {
        testAllCts?.Cancel();
        accessoryCts?.Cancel();
        bulkUpdateCts?.Cancel();
    }
}
