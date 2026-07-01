using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using HoliestFluffiness.Handlers;

namespace HoliestFluffiness.Windows;

public partial class ConfigWindow : Window
{
    private readonly Configuration configuration;
    private readonly LoginInfoHandler loginInfoHandler;
    private readonly AccessoryHandler accessoryHandler;
    private readonly RepairHandler repairHandler;
    private readonly NoKillHandler noKillHandler;
    private readonly PhysicsHandler physicsHandler;
    private readonly AntiAfkHandler antiAfkHandler;
    private readonly ReadyCheckHandler readyCheckHandler;
    private FoodCheckHandler? foodCheckHandler;
    private readonly IObjectTable objectTable;
    private readonly CharacterDb characterDb;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IClientState clientState;
    private readonly Action<string, string> onSwitchCharacter;
    private readonly Action<CharacterRecord, HousingBidRecord> onGoToBid;
    private readonly Action onClientSettingsChanged;
    private readonly FileDialogManager fileDialogManager = new() { AddedWindowFlags = ImGuiWindowFlags.NoCollapse };

    private IFontHandle? titleFont;
    internal void SetTitleFont(IFontHandle font) => titleFont = font;

    private ConfigSection selectedSection;
    private ConfigSection currentDrawSection;
    private string searchQuery = "";
    private bool searchModeActive;
    private string cachedSearchQuery = "\0"; // sentinel, never a real trimmed query, forces first compute
    private int cachedSearchVersion = -1;
    private List<SettingEntry> cachedSearchMatches = [];
    private int searchBoxGeneration;
    private string? pendingJumpKey;
    private int pendingJumpFramesLeft;
    private string? flashKey;
    private double flashEndTime;

    public ConfigWindow(Configuration configuration, LoginInfoHandler loginInfoHandler, AccessoryHandler accessoryHandler, RepairHandler repairHandler, NoKillHandler noKillHandler, PhysicsHandler physicsHandler, AntiAfkHandler antiAfkHandler, ReadyCheckHandler readyCheckHandler, IObjectTable objectTable, IDalamudPluginInterface pluginInterface, CharacterDb characterDb, IClientState clientState, Action<string, string> onSwitchCharacter, Action<CharacterRecord, HousingBidRecord> onGoToBid, Action onClientSettingsChanged)
        : base($"The Holiest Fluffiness##Config")
    {
        this.configuration = configuration;
        this.loginInfoHandler = loginInfoHandler;
        this.accessoryHandler = accessoryHandler;
        this.repairHandler = repairHandler;
        this.noKillHandler = noKillHandler;
        this.physicsHandler = physicsHandler;
        this.antiAfkHandler = antiAfkHandler;
        this.readyCheckHandler = readyCheckHandler;
        this.objectTable = objectTable;
        this.characterDb = characterDb;
        this.pluginInterface = pluginInterface;
        this.clientState = clientState;
        this.onSwitchCharacter = onSwitchCharacter;
        this.onGoToBid = onGoToBid;
        this.onClientSettingsChanged = onClientSettingsChanged;
        selectedSection = (ConfigSection)configuration.LastSelectedSection;
        if (!configuration.CharactersDbEnabled && (selectedSection == ConfigSection.Characters || selectedSection == ConfigSection.Bids))
            selectedSection = ConfigSection.Database;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 250),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        Size          = new Vector2(600, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void SetFoodCheckHandler(FoodCheckHandler h) => foodCheckHandler = h;

    public void NavigateTo(ConfigSection section)
    {
        if (!configuration.CharactersDbEnabled && (section == ConfigSection.Characters || section == ConfigSection.Bids))
            section = ConfigSection.Database;

        selectedSection = section;
        configuration.LastSelectedSection = (int)section;
        configuration.Save();
        if (section == ConfigSection.Characters) LoadCharacters();
        if (section == ConfigSection.Bids) LoadBids();
    }

    private void JumpTo(ConfigSection section, string key)
    {
        NavigateTo(section);
        pendingJumpKey = key;
        pendingJumpFramesLeft = 3;
        flashKey = key;
        flashEndTime = ImGui.GetTime() + 1.2;
        ExitSearchMode();
    }

    // searchModeActive is deliberately sticky (only cleared here, never by focus loss alone).
    // A click on a result row can cause the search InputText to lose ImGui focus on that very
    // same frame, before the row itself is even processed, if entering/staying in search mode
    // depended on re-reading IsItemFocused() every frame, that same click would collapse the
    // results list back to the previous section before the row's own click got a chance to fire.
    // Changing the widget's ID (searchBoxGeneration) additionally forces ImGui to drop any
    // lingering keyboard focus so the box visually deactivates too.
    private void ExitSearchMode()
    {
        searchQuery = "";
        searchModeActive = false;
        searchBoxGeneration++;
    }

    public override void PreDraw()
    {
        SizeConstraints = (selectedSection == ConfigSection.Characters || selectedSection == ConfigSection.Bids)
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
        if (!searchIndexWarmed)
        {
            WarmSearchIndex();
            searchIndexWarmed = true;
        }

        var avail = ImGui.GetContentRegionAvail();
        const float sidebarWidth = 180f;

        DrawSidebar(sidebarWidth, avail.Y);
        ImGui.SameLine(0, 0);
        DrawMain(avail.Y);

        DrawResizeGrip();
        fileDialogManager.Draw();
    }

    // Silently draws every settings-bearing section once, into a clipped 1x1 child with all
    // input blocked, purely so every Config*/Anchor call registers into SearchIndex before the
    // user ever opens a tab. Runs once per game session on the first time this window draws.
    private static bool searchIndexWarmed;

    private void WarmSearchIndex()
    {
        var savedSection = currentDrawSection;

        ImGui.SetCursorPos(Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0f);
        // Deliberately NOT sized down to near-zero: a fully clipped/zero-area child gets marked
        // SkipItems by ImGui, which makes every widget inside it (Checkbox, SliderInt, ...) bail
        // out immediately without registering, exactly the bug this warmup exists to avoid. Alpha
        // 0 + NoInputs already make it invisible and unclickable, so a real, unclipped size is safe.
        ImGui.BeginChild("##searchwarmup", ImGui.GetContentRegionAvail(), false,
            ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoNav);

        foreach (var section in new[]
                 {
                     ConfigSection.Client, ConfigSection.Login, ConfigSection.Indicators,
                     ConfigSection.Social, ConfigSection.Database,
                 })
        {
            currentDrawSection = section;
            switch (section)
            {
                case ConfigSection.Client:     DrawClientSection();     break;
                case ConfigSection.Login:      DrawLoginSection();      break;
                case ConfigSection.Indicators: DrawIndicatorsSection(); break;
                case ConfigSection.Social:     DrawSocialSection();     break;
                case ConfigSection.Database:   DrawDatabaseSection();   break;
            }
        }

        ImGui.EndChild();
        ImGui.PopStyleVar();

        currentDrawSection = savedSection;
    }

    public override void OnClose()
    {
        testAllCts?.Cancel();
        accessoryCts?.Cancel();
        bulkUpdateCts?.Cancel();
        repairHandler.TestPct = null;
        foodCheckHandler?.Invalidate();
    }

    // Reopening the window while a data-table section was already selected (persisted from a
    // previous session) skips NavigateTo/SidebarItem entirely, so without this the data would
    // go stale until the user manually clicked Refresh or switched tabs and back.
    public override void OnOpen()
    {
        if (selectedSection == ConfigSection.Characters) LoadCharacters();
        if (selectedSection == ConfigSection.Bids) LoadBids();
    }

    // ── Sidebar ───────────────────────────────────────────────────────────────

    private void DrawSidebar(float width, float height)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg,              Theme.ColPrimary);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,          Theme.ColHighlight);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab,        Theme.ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Theme.ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive,  Theme.ColGold);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6, 6));
        ImGui.BeginChild("##sidebar", new Vector2(width, height), false);

        ImGui.Dummy(new Vector2(0, 4));
        DrawSearchBox();

        SidebarItem("Client", ConfigSection.Client);
        SidebarItem("Login", ConfigSection.Login);
        SidebarItem("Indicators", ConfigSection.Indicators);
        SidebarItem("Social", ConfigSection.Social);

        if (configuration.CharactersDbEnabled)
        {
            ImGui.Dummy(new Vector2(0, 4));
            SidebarSeparator();
            SidebarItem("Database", ConfigSection.Database);
            if (SidebarItem("Characters", ConfigSection.Characters))
                LoadCharacters();
            if (SidebarItem("House bids", ConfigSection.Bids))
                LoadBids();
        } else
        {
            // If the database ain't enabled, don't bother seperating and making bulk
            SidebarItem("Database", ConfigSection.Database);
        }

        ImGui.Dummy(new Vector2(0, 4));
        SidebarSeparator();
        SidebarItem("About", ConfigSection.About);

        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(5);
    }

    private void SidebarSeparator()
    {
        var x     = ImGui.GetCursorScreenPos().X + 8f;
        var y     = ImGui.GetCursorScreenPos().Y;
        var width = ImGui.GetContentRegionAvail().X - 16f;
        ImGui.GetWindowDrawList().AddLine(new Vector2(x, y), new Vector2(x + width, y), ImGui.GetColorU32(Theme.ColGoldMid), 1f);
        ImGui.Dummy(new Vector2(0, 4));
    }

    private void DrawSearchBox()
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 6f);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 6f);
        PushInput();
        ImGui.InputTextWithHint($"##settingssearch{searchBoxGeneration}", "Search settings...", ref searchQuery, 64);
        // Only ever latches search mode ON here; it's turned off exclusively by ExitSearchMode().
        if (ImGui.IsItemActive() || ImGui.IsItemFocused())
            searchModeActive = true;
        PopInput();
    }

    private bool SidebarItem(string label, ConfigSection index)
    {
        bool active = selectedSection == index;

        if (active)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ColGold);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ColGold);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ColGold);
            ImGui.PushStyleColor(ImGuiCol.Text,          Theme.ColHighlight);
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ColGoldSub);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ColGoldMid);
            ImGui.PushStyleColor(ImGuiCol.Text,          Theme.ColWhite);
        }

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f);
        ImGui.PushStyleVar(ImGuiStyleVar.ButtonTextAlign, new Vector2(0f, 0.5f));
        bool clicked = ImGui.Button(label, new Vector2(ImGui.GetContentRegionAvail().X - 6f, 30));
        if (clicked)
        {
            selectedSection = index;
            configuration.LastSelectedSection = (int)index;
            configuration.Save();
            ExitSearchMode();
        }
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(4);
        return clicked;
    }

    // ── Main content ──────────────────────────────────────────────────────────

    private void DrawMain(float height)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg,              Theme.ColSecondary);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,          Theme.ColHighlight);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab,        Theme.ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Theme.ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive,  Theme.ColGold);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 0));
        ImGui.BeginChild("##main", new Vector2(0, height), false);

        if (searchModeActive)
        {
            DrawSearchResults();
        }
        else
        {
            currentDrawSection = selectedSection;
            switch (selectedSection)
            {
                case ConfigSection.Client:     DrawClientSection();     break;
                case ConfigSection.Login:      DrawLoginSection();      break;
                case ConfigSection.Indicators: DrawIndicatorsSection(); break;
                case ConfigSection.Database:   DrawDatabaseSection();   break;
                case ConfigSection.Characters: DrawCharactersSection(); break;
                case ConfigSection.Bids:       DrawBidsSection();       break;
                case ConfigSection.About:      DrawAboutSection();      break;
                case ConfigSection.Social:     DrawSocialSection();     break;
            }
        }

        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(5);
    }

    // ── Search results ────────────────────────────────────────────────────────

    private void DrawSearchResults()
    {
        var query   = searchQuery.Trim();
        var showAll = query.Length == 0;

        if (query != cachedSearchQuery || SearchIndex.Version != cachedSearchVersion)
        {
            cachedSearchQuery   = query;
            cachedSearchVersion = SearchIndex.Version;

            var filtered = showAll
                ? SearchIndex.Entries
                : SearchIndex.Entries.Where(e =>
                    e.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    (e.Desc?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
            cachedSearchMatches = filtered.OrderBy(e => e.Section).ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }
        var matches = cachedSearchMatches;

        ImGui.Dummy(new Vector2(0, 6));
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColGold);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);
        using (titleFont?.Push())
            ImGui.TextUnformatted(showAll ? $"All settings ({matches.Count})" : $"Search results ({matches.Count})");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 4));

        if (matches.Count == 0)
        {
            SectionRow();
            Common.DimmedText("No settings match your search.");
            return;
        }

        foreach (var entry in matches)
            DrawSearchResultRow(entry);
    }

    private void DrawSearchResultRow(SettingEntry entry)
    {
        SectionRow();
        var width = ImGui.GetContentRegionAvail().X - 8f;
        var height = entry.Desc != null ? 42f : 26f;

        ImGui.PushStyleColor(ImGuiCol.Header,        Theme.ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Theme.ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive,  Theme.ColGold);
        bool clicked = ImGui.Selectable($"##searchres_{entry.Section}_{entry.Key}", false,
            ImGuiSelectableFlags.None, new Vector2(width, height));
        ImGui.PopStyleColor(3);

        var min   = ImGui.GetItemRectMin();
        var max   = ImGui.GetItemRectMax();
        var textX = min.X + 6f;

        ImGui.SetCursorScreenPos(new Vector2(textX, min.Y + 4f));
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColGold);
        ImGui.TextUnformatted(entry.Title);
        ImGui.PopStyleColor();
        ImGui.SameLine();
        Common.DimmedText($"[ {entry.Section} ]");

        if (entry.Desc != null)
        {
            ImGui.SetCursorScreenPos(new Vector2(textX, min.Y + 21f));
            Common.DimmedTextWrapped(entry.Desc);
        }

        // The manual SetCursorScreenPos calls above leave the cursor wherever the overlay text
        // ended, not at the bottom of the row, without pinning it back to the Selectable's own
        // rect, rows creep upward and overlap after enough of them, and clicks then resolve to
        // whichever overlapping row's Selectable ends up on top instead of the one you can see.
        ImGui.SetCursorScreenPos(new Vector2(min.X, max.Y));

        ImGui.Dummy(new Vector2(0, 2));

        if (clicked)
            JumpTo(entry.Section, entry.Key);
    }

    // ── Resize grip ───────────────────────────────────────────────────────────

    private void DrawResizeGrip()
    {
        const float gripSize = 15f;
        var winPos  = ImGui.GetWindowPos();
        var winSize = ImGui.GetWindowSize();
        var corner  = winPos + winSize;
        var mouse   = ImGui.GetMousePos();

        bool hovered = mouse.X >= corner.X - gripSize && mouse.X <= corner.X &&
                       mouse.Y >= corner.Y - gripSize && mouse.Y <= corner.Y;
        bool active  = hovered && ImGui.IsMouseDown(ImGuiMouseButton.Left);
        var col = ImGui.GetColorU32(active ? Theme.ColGold : hovered ? Theme.ColGoldMid : Theme.ColGoldSub);

        var p1 = corner;
        var p2 = corner with { X = corner.X - gripSize };
        var p3 = corner with { Y = corner.Y - gripSize };

        ImGui.GetForegroundDrawList().AddTriangleFilled(p1, p2, p3, col);
    }

    // ── Section helpers ───────────────────────────────────────────────────────

    private void BeginSection(string title, string? desc = null, Action? afterTitle = null)
    {
        ImGui.BeginChild(title + "##sec", new Vector2(0, 0), false);

        ImGui.Dummy(new Vector2(0, 6));
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColGold);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);
        using (titleFont?.Push())
            ImGui.TextUnformatted(title);
        if (afterTitle != null) { ImGui.SameLine(); afterTitle(); }
        ImGui.PopStyleColor();
        if (desc != null)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);
            Common.DimmedTextWrapped(desc);
        }
        ImGui.Dummy(new Vector2(0, 2));
    }

    private void EndSection(float bottomPadding = 0)
    {
        if (bottomPadding > 0)
            ImGui.Dummy(new Vector2(0, bottomPadding));
        ImGui.EndChild();
    }

    private static void SectionRow() =>
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);

    private static void RowGap(float gap = 4)
    {
        ImGui.Dummy(new Vector2(0, gap));
        SectionRow();
    }

    // ── Search anchoring ──────────────────────────────────────────────────────
    // Every searchable control (or BeginGroup'd cluster of controls) calls Anchor(key, title, desc)
    // right after drawing itself. This both (a) self-registers into SearchIndex for the current
    // section, so the search box needs no separately-maintained list, and (b) scrolls the key into
    // view once, right after a search-result click sets pendingJumpKey, and draws a fading
    // highlight rect while flashKey == key. title/desc are omitted for controls that don't carry
    // a meaningful standalone label (e.g. a bare checkbox glued to a slider via SameLine).

    private static string? ExtractKey(string label)
    {
        var idx = label.IndexOf("##", StringComparison.Ordinal);
        return idx >= 0 ? label[(idx + 2)..] : null;
    }

    // Null when the label has no visible text before "##" (e.g. a bare checkbox glued to a
    // sibling slider) such controls aren't meaningful standalone search results on their own.
    private static string? ExtractTitle(string label)
    {
        var idx = label.IndexOf("##", StringComparison.Ordinal);
        var title = idx >= 0 ? label[..idx] : label;
        return title.Length > 0 ? title : null;
    }

    private void Anchor(string? key, string? title = null, string? desc = null)
    {
        if (key == null) return;

        if (title != null)
            SearchIndex.Register(currentDrawSection, key, title, desc);

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();

        if (pendingJumpKey == key)
        {
            // Retried for a few frames rather than consumed on the first hit: the target
            // section's "##sec" child can be appearing for the first time this session (you
            // never browsed there normally), and ImGui hasn't got a settled scroll range for a
            // window on its very first frame, SetScrollHereY silently no-ops until it does.
            ImGui.SetScrollHereY(0.3f);
            if (--pendingJumpFramesLeft <= 0)
                pendingJumpKey = null;
        }

        if (flashKey != key) return;

        var remaining = flashEndTime - ImGui.GetTime();
        if (remaining <= 0)
        {
            flashKey = null;
            return;
        }

        var alpha = (float)Math.Clamp(remaining / 1.2, 0, 1);
        Common.DrawHighlightRect(ImGui.GetWindowDrawList(), min - new Vector2(4, 4), max + new Vector2(4, 4),
            4f, Theme.ColGold with { W = alpha }, pulse: false);
    }

    private void ConfigSliderInt(string label, int current, int min, int max, Action<int> setter,
        float width = 220, string? hint = null, Action? onChange = null, bool padding = true, string? desc = null)
    {
        if (padding) SectionRow();
        ImGui.BeginGroup();
        ImGui.SetNextItemWidth(width);
        PushInput();
        if (ImGui.SliderInt(label, ref current, min, max))
        {
            setter(current);
            onChange?.Invoke();
        }
        if (ImGui.IsItemDeactivatedAfterEdit()) configuration.Save();
        PopInput();
        if (hint != null) { ImGui.SameLine(); Common.DimmedText(hint); }
        ImGui.EndGroup();
        Anchor(ExtractKey(label), ExtractTitle(label), desc);
    }

    private void ConfigSliderFloat(string label, float current, float min, float max, Action<float> setter,
        float width = 220, string? format = null, string? hint = null, string? desc = null)
    {
        ImGui.BeginGroup();
        ImGui.SetNextItemWidth(width);
        PushInput();
        bool changed = format != null
            ? ImGui.SliderFloat(label, ref current, min, max, format)
            : ImGui.SliderFloat(label, ref current, min, max);
        if (changed) setter(current);
        if (ImGui.IsItemDeactivatedAfterEdit()) configuration.Save();
        PopInput();
        if (hint != null) { ImGui.SameLine(); Common.DimmedText(hint); }
        ImGui.EndGroup();
        Anchor(ExtractKey(label), ExtractTitle(label), desc);
    }

    private void SubsectionLabel(string label, string? desc = null)
    {
        ImGui.Dummy(new Vector2(0, 8));
        SectionRow();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColGold);
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();
        if (desc != null)
        {
            SectionRow();
            Common.DimmedTextWrapped(desc);
        }
        ImGui.Dummy(new Vector2(0, 1));
    }

    // ── Style helpers ─────────────────────────────────────────────────────────

    private void PushGlobalStyle()
    {
        ImGui.PushStyleColor(ImGuiCol.Text,                Theme.ColWhite);
        ImGui.PushStyleColor(ImGuiCol.WindowBg,            Theme.ColSecondary);
        ImGui.PushStyleColor(ImGuiCol.FrameBg,             Theme.ColPrimary);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered,      Theme.ColHighlight);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive,       Theme.ColHighlight);
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip,          Theme.ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered,   Theme.ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive,    Theme.ColGold);
        ImGui.PushStyleColor(ImGuiCol.TitleBg,             Theme.ColHighlight);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive,       Theme.ColHighlight);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab,          Theme.ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive,    Theme.ColGoldMid);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,   new Vector2(8, 6));
    }

    private void PopGlobalStyle()
    {
        ImGui.PopStyleColor(12);
        ImGui.PopStyleVar(2);
    }

    private void PushButton()
    {
        ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ColGold);
        ImGui.PushStyleColor(ImGuiCol.Text,          Theme.ColGold);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
    }

    private void PopButton()
    {
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar();
    }

    private void PushCheckbox()
    {
        ImGui.PushStyleColor(ImGuiCol.CheckMark, Theme.ColGold);
        ImGui.PushStyleColor(ImGuiCol.Border,    Theme.ColGold);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
    }

    private void PopCheckbox()
    {
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar();
    }

    private void PushInput()
    {
        ImGui.PushStyleColor(ImGuiCol.Border,        Theme.ColGold);
        ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ColGrey);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ColGreyHov);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ColGreyAct);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
    }

    private void PopInput()
    {
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar();
    }

    private void ConfigCheckbox(string label, bool current, Action<bool> setter, string? desc = null)
    {
        SectionRow();
        ImGui.BeginGroup();

        if (desc == null)
        {
            PushCheckbox();
            if (ImGui.Checkbox(label, ref current))
            {
                setter(current);
                configuration.Save();
            }
            PopCheckbox();
        }
        else
        {
            // Separate display text from ImGui ID so we can position them independently
            var sep  = label.IndexOf("##", StringComparison.Ordinal);
            var text = sep >= 0 ? label[..sep] : label;
            var id   = sep >= 0 ? label[sep..] : "##" + label;

            // Draw the checkbox box only (no visible label)
            PushCheckbox();
            if (ImGui.Checkbox(id, ref current))
            {
                setter(current);
                configuration.Save();
            }
            PopCheckbox();

            var boxMin = ImGui.GetItemRectMin();
            var boxMax = ImGui.GetItemRectMax();
            var textX  = boxMax.X + ImGui.GetStyle().ItemInnerSpacing.X + 2f;

            ImGui.SetCursorScreenPos(new Vector2(textX, boxMin.Y - 5f));
            ImGui.TextUnformatted(text);

            ImGui.SetCursorScreenPos(new Vector2(textX, boxMin.Y + 10f));
            Common.DimmedTextWrapped(desc);
        }

        ImGui.EndGroup();
        Anchor(ExtractKey(label), ExtractTitle(label), desc);
    }

    // ── New Config* helpers (combo/color/text/int) ───────────────────────────
    // Follow the same convention as ConfigCheckbox/ConfigSliderInt/ConfigSliderFloat above:
    // label carries a "Display text##anchorkey" suffix used both as the ImGui ID and the
    // search-anchor key, and the group is auto-registered with Anchor() for search jump/flash.

    private void ConfigCombo(string label, int currentIndex, string[] items, Action<int> setter,
        float width = 180, string? hint = null, bool padding = true, string? desc = null, string? title = null)
    {
        if (padding) SectionRow();
        ImGui.BeginGroup();
        ImGui.SetNextItemWidth(width);
        PushInput();
        if (ImGui.Combo(label, ref currentIndex, items, items.Length))
        {
            setter(currentIndex);
            configuration.Save();
        }
        PopInput();
        if (hint != null) { ImGui.SameLine(); Common.DimmedText(hint); }
        ImGui.EndGroup();
        Anchor(ExtractKey(label), title ?? ExtractTitle(label), desc);
    }

    private void ConfigColorEdit4(string label, Vector4 current, Action<Vector4> setter,
        ImGuiColorEditFlags flags = ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar, string? desc = null, string? title = null)
    {
        ImGui.BeginGroup();
        if (ImGui.ColorEdit4(label, ref current, flags))
        {
            setter(current);
        }
        if (ImGui.IsItemDeactivatedAfterEdit()) configuration.Save();
        ImGui.EndGroup();
        Anchor(ExtractKey(label), title ?? ExtractTitle(label), desc);
    }

    private void ConfigInputText(string label, string current, Action<string> setter,
        int maxLength = 128, float width = 220, string? hint = null, Action? onChange = null, string? desc = null)
    {
        SectionRow();
        ImGui.BeginGroup();
        ImGui.SetNextItemWidth(width);
        PushInput();
        if (ImGui.InputText(label, ref current, maxLength))
        {
            setter(current);
            configuration.Save();
            onChange?.Invoke();
        }
        PopInput();
        if (hint != null) { ImGui.SameLine(); Common.DimmedText(hint); }
        ImGui.EndGroup();
        Anchor(ExtractKey(label), ExtractTitle(label), desc);
    }

    private void ConfigInputInt(string label, int current, int min, int max, Action<int> setter,
        int step = 1, int stepFast = 10, float width = 90, string? hint = null, bool padding = true, string? desc = null)
    {
        if (padding) SectionRow();
        ImGui.BeginGroup();
        ImGui.SetNextItemWidth(width);
        PushInput();
        if (ImGui.InputInt(label, ref current, step, stepFast))
        {
            setter(Math.Clamp(current, min, max));
            configuration.Save();
        }
        PopInput();
        if (hint != null) { ImGui.SameLine(); Common.DimmedText(hint); }
        ImGui.EndGroup();
        Anchor(ExtractKey(label), ExtractTitle(label), desc);
    }
}
