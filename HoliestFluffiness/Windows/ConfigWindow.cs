using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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
    private readonly FastMouseClickFixHandler fastMouseClickFixHandler;
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
    private bool cachedSearchDbEnabled;
    private List<SettingEntry> cachedSearchMatches = [];
    private int searchBoxGeneration;
    private string? pendingJumpKey;
    private int pendingJumpFramesLeft;
    private string? flashKey;
    private double flashEndTime;

    public ConfigWindow(Configuration configuration, LoginInfoHandler loginInfoHandler, AccessoryHandler accessoryHandler, RepairHandler repairHandler, NoKillHandler noKillHandler, PhysicsHandler physicsHandler, AntiAfkHandler antiAfkHandler, FastMouseClickFixHandler fastMouseClickFixHandler, ReadyCheckHandler readyCheckHandler, IObjectTable objectTable, IDalamudPluginInterface pluginInterface, CharacterDb characterDb, IClientState clientState, Action<string, string> onSwitchCharacter, Action<CharacterRecord, HousingBidRecord> onGoToBid, Action onClientSettingsChanged)
        : base($"The Holiest Fluffiness##Config")
    {
        this.configuration = configuration;
        this.loginInfoHandler = loginInfoHandler;
        this.accessoryHandler = accessoryHandler;
        this.repairHandler = repairHandler;
        this.noKillHandler = noKillHandler;
        this.physicsHandler = physicsHandler;
        this.antiAfkHandler = antiAfkHandler;
        this.fastMouseClickFixHandler = fastMouseClickFixHandler;
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

    // key null means "just open the section", used by whole-section search results.
    private void JumpTo(ConfigSection section, string? key)
    {
        NavigateTo(section);
        pendingJumpKey = key;
        pendingJumpFramesLeft = key != null ? 3 : 0;
        flashKey = key;
        flashEndTime = ImGui.GetTime() + 1.2;
        ExitSearchMode();
    }

    // Sticky: search mode is only cleared here, never by focus loss (a result-row click drops the
    // input's focus on the same frame). Bumping searchBoxGeneration makes ImGui drop keyboard focus too.
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

    // Draws every section once into a hidden, input-blocked child so every Config*/Anchor call
    // registers into SearchIndex before the user opens a tab. Runs once per session on first draw.
    private static bool searchIndexWarmed;

    private void WarmSearchIndex()
    {
        var savedSection = currentDrawSection;

        ImGui.SetCursorPos(Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0f);
        // Must use a real size: a zero-area child gets SkipItems'd, so nothing inside registers.
        // Alpha 0 + NoInputs already make it invisible and unclickable.
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

    // Refresh table data when reopening on a persisted data-table section (NavigateTo isn't called).
    public override void OnOpen()
    {
        if (selectedSection == ConfigSection.Characters) LoadCharacters();
        if (selectedSection == ConfigSection.Bids) LoadBids();
    }

    // ── Sidebar ───────────────────────────────────────────────────────────────

    private void DrawSidebar(float width, float height)
    {
        if (Theme.UseCustom)
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg,              Theme.Fade(Theme.ColPrimary));
            ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,          Theme.Fade(Theme.ColHighlight));
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab,        Theme.ColGoldSub);
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Theme.ColGoldMid);
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive,  Theme.ColGold);
        }
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
            // DB disabled: no separator, just the toggle
            SidebarItem("Database", ConfigSection.Database);
        }

        ImGui.Dummy(new Vector2(0, 4));
        SidebarSeparator();
        SidebarItem("About", ConfigSection.About);

        ImGui.EndChild();
        ImGui.PopStyleVar();
        if (Theme.UseCustom) ImGui.PopStyleColor(5);
    }

    private void SidebarSeparator()
    {
        if (Theme.UseCustom)
        {
            var x     = ImGui.GetCursorScreenPos().X + 8f;
            var y     = ImGui.GetCursorScreenPos().Y;
            var width = ImGui.GetContentRegionAvail().X - 16f;
            ImGui.GetWindowDrawList().AddLine(new Vector2(x, y), new Vector2(x + width, y), ImGui.GetColorU32(Theme.ColGoldMid), 1f);
        }
        ImGui.Dummy(new Vector2(0, 4));
    }

    private void DrawSearchBox()
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 6f);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 6f);
        PushInput();
        ImGui.InputTextWithHint($"##settingssearch{searchBoxGeneration}", "Search settings...", ref searchQuery, 64);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Searches section names, group headers, and individual settings.");
        // Only ever latches search mode ON here; it's turned off exclusively by ExitSearchMode().
        if (ImGui.IsItemActive() || ImGui.IsItemFocused())
            searchModeActive = true;
        PopInput();
    }

    private bool SidebarItem(string label, ConfigSection index)
    {
        bool active = selectedSection == index;

        if (!Theme.UseCustom)
        {
            // Default theme: mark the selected row with the user's own Header accent (never gold),
            // leave the rest as plain transparent buttons with default text.
            var styleCols = ImGui.GetStyle().Colors;
            ImGui.PushStyleColor(ImGuiCol.Button,        active ? styleCols[(int)ImGuiCol.Header] : Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, styleCols[(int)ImGuiCol.HeaderHovered]);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  styleCols[(int)ImGuiCol.HeaderActive]);
            ImGui.PushStyleColor(ImGuiCol.Text,          styleCols[(int)ImGuiCol.Text]);
        }
        else if (active)
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
        if (Theme.UseCustom)
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg,              Theme.Fade(Theme.ColSecondary));
            ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,          Theme.Fade(Theme.ColHighlight));
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab,        Theme.ColGoldSub);
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Theme.ColGoldMid);
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive,  Theme.ColGold);
        }
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
        if (Theme.UseCustom) ImGui.PopStyleColor(5);
    }

    // ── Search results ────────────────────────────────────────────────────────

    private void DrawSearchResults()
    {
        var query   = searchQuery.Trim();
        var showAll = query.Length == 0;
        var dbOn    = configuration.CharactersDbEnabled;

        if (query != cachedSearchQuery || SearchIndex.Version != cachedSearchVersion || dbOn != cachedSearchDbEnabled)
        {
            cachedSearchQuery     = query;
            cachedSearchVersion   = SearchIndex.Version;
            cachedSearchDbEnabled = dbOn;

            // Hidden sidebar entries stay out of the results, otherwise clicking one would
            // bounce to Database (see NavigateTo).
            var visible = SearchIndex.Entries.Where(e =>
                dbOn || (e.Section != ConfigSection.Characters && e.Section != ConfigSection.Bids));

            var filtered = showAll ? visible : visible.Where(e => Matches(e, query));

            // Kind first so a query like "database" leads with the section itself, then its
            // groups, then the individual settings inside it.
            cachedSearchMatches = filtered
                .OrderBy(e => e.Kind)
                .ThenBy(e => e.Section)
                .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        var matches = cachedSearchMatches;


        ImGui.Dummy(new Vector2(0, 6));
        if (Theme.UseCustom) ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColGold);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);
        using (titleFont?.Push())
            ImGui.TextUnformatted(showAll ? $"Everything ({matches.Count})" : $"Search results ({matches.Count})");
        if (Theme.UseCustom) ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 4));

        if (matches.Count == 0)
        {
            SectionRow();
            Common.DimmedText("Nothing matches your search.");
            return;
        }

        foreach (var entry in matches)
            DrawSearchResultRow(entry);
    }

    // Every whitespace-separated term must appear somewhere in the entry, so word order does not
    // matter ("taskbar flash" finds "Flash taskbar on..."). The section name is part of the
    // haystack, which makes a bare section name list that section and everything under it.
    private static bool Matches(SettingEntry entry, string query)
    {
        var haystack = $"{entry.Title}\n{entry.Desc}\n{entry.Keywords}\n{SearchIndex.DisplayName(entry.Section)}";
        foreach (var term in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (!haystack.Contains(term, StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    private void DrawSearchResultRow(SettingEntry entry)
    {
        SectionRow();
        var width = ImGui.GetContentRegionAvail().X - 8f;
        var height = entry.Desc != null ? 42f : 26f;

        if (Theme.UseCustom)
        {
            ImGui.PushStyleColor(ImGuiCol.Header,        Theme.ColGoldSub);
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Theme.ColGoldMid);
            ImGui.PushStyleColor(ImGuiCol.HeaderActive,  Theme.ColGold);
        }
        bool clicked = ImGui.Selectable($"##searchres_{entry.Section}_{entry.Key}", false,
            ImGuiSelectableFlags.None, new Vector2(width, height));
        if (Theme.UseCustom) ImGui.PopStyleColor(3);

        var min   = ImGui.GetItemRectMin();
        var max   = ImGui.GetItemRectMax();
        var textX = min.X + 6f;

        ImGui.SetCursorScreenPos(new Vector2(textX, min.Y + 4f));
        if (Theme.UseCustom) ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColGold);
        ImGui.TextUnformatted(entry.Title);
        if (Theme.UseCustom) ImGui.PopStyleColor();
        ImGui.SameLine();
        Common.DimmedText(entry.Kind switch
        {
            SearchEntryKind.Section    => "[ Section ]",
            SearchEntryKind.Subsection => $"[ {SearchIndex.DisplayName(entry.Section)} / group ]",
            _                          => $"[ {SearchIndex.DisplayName(entry.Section)} ]",
        });

        if (entry.Desc != null)
        {
            ImGui.SetCursorScreenPos(new Vector2(textX, min.Y + 21f));
            Common.DimmedTextWrapped(entry.Desc);
        }

        // Pin the cursor back to the row's bottom; the SetCursorScreenPos calls above otherwise
        // leave it mid-row, causing subsequent rows to creep up and overlap.
        ImGui.SetCursorScreenPos(new Vector2(min.X, max.Y));

        ImGui.Dummy(new Vector2(0, 2));

        if (clicked)
            JumpTo(entry.Section, entry.Kind == SearchEntryKind.Section ? null : entry.Key);
    }

    // ── Resize grip ───────────────────────────────────────────────────────────

    private void DrawResizeGrip()
    {
        if (!Theme.UseCustom) return; // default theme keeps ImGui's own resize grip
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
        if (Theme.UseCustom) ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColGold);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);
        using (titleFont?.Push())
            ImGui.TextUnformatted(title);
        if (afterTitle != null) { ImGui.SameLine(); afterTitle(); }
        if (Theme.UseCustom) ImGui.PopStyleColor();
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
    // Every searchable control calls Anchor(key, title, desc) after drawing itself: it self-registers
    // into SearchIndex and, after a result click, scrolls to the key and flashes a highlight rect.

    private static string? ExtractKey(string label)
    {
        var idx = label.IndexOf("##", StringComparison.Ordinal);
        return idx >= 0 ? label[(idx + 2)..] : null;
    }

    // Null when the label has no visible text before "##" (not a standalone search result).
    private static string? ExtractTitle(string label)
    {
        var idx = label.IndexOf("##", StringComparison.Ordinal);
        var title = idx >= 0 ? label[..idx] : label;
        return title.Length > 0 ? title : null;
    }

    private void Anchor(string? key, string? title = null, string? desc = null,
        SearchEntryKind kind = SearchEntryKind.Setting)
    {
        if (key == null) return;

        if (title != null)
            SearchIndex.Register(currentDrawSection, key, title, desc, kind);

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();

        if (pendingJumpKey == key)
        {
            // Retried over a few frames: SetScrollHereY no-ops until ImGui has a settled scroll
            // range, which a section's child lacks on its very first frame.
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
        SectionRow();
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
        ImGui.BeginGroup();
        if (Theme.UseCustom) ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColGold);
        ImGui.TextUnformatted(label);
        if (Theme.UseCustom) ImGui.PopStyleColor();
        if (desc != null)
        {
            Common.DimmedTextWrapped(desc);
        }
        ImGui.EndGroup();
        // Group headers are searchable too, keyed off their own text so no call site has to
        // invent an anchor id for them.
        Anchor(SearchIndex.SubsectionKeyPrefix + label, label, desc, SearchEntryKind.Subsection);
        ImGui.Dummy(new Vector2(0, 1));
    }

    // ── Style helpers ─────────────────────────────────────────────────────────

    private void PushGlobalStyle()
    {
        if (Theme.UseCustom)
        {
            ImGui.PushStyleColor(ImGuiCol.Text,                Theme.ColWhite);
            // The sidebar and main children fully cover the window and each paint their own
            // faded ChildBg, so a faded WindowBg here would stack a second translucent layer
            // (making the config window read as more opaque than the opacity knob asks for).
            // Keep the window bg fully transparent and let the children carry the single layer.
            ImGui.PushStyleColor(ImGuiCol.WindowBg,            Vector4.Zero);
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
        }
        else
        {
            // Default theme: still honour the opacity knob by fading only the window
            // background (the content children are transparent, so this is the bg).
            ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.Fade(ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg]));
        }
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,   new Vector2(8, 6));
    }

    private void PopGlobalStyle()
    {
        ImGui.PopStyleColor(Theme.UseCustom ? 12 : 1);
        ImGui.PopStyleVar(2);
    }

    private void PushButton()
    {
        if (Theme.UseCustom)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ColGoldSub);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ColGoldMid);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ColGold);
            ImGui.PushStyleColor(ImGuiCol.Text,          Theme.ColGold);
        }
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
    }

    private void PopButton()
    {
        if (Theme.UseCustom) ImGui.PopStyleColor(4);
        ImGui.PopStyleVar();
    }

    private void PushCheckbox()
    {
        if (Theme.UseCustom)
        {
            ImGui.PushStyleColor(ImGuiCol.CheckMark, Theme.ColGold);
            ImGui.PushStyleColor(ImGuiCol.Border,    Theme.ColGold);
        }
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
    }

    private void PopCheckbox()
    {
        if (Theme.UseCustom) ImGui.PopStyleColor(2);
        ImGui.PopStyleVar();
    }

    private void PushInput()
    {
        if (Theme.UseCustom)
        {
            ImGui.PushStyleColor(ImGuiCol.Border,        Theme.ColGold);
            ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ColGrey);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ColGreyHov);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ColGreyAct);
        }
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
    }

    private void PopInput()
    {
        if (Theme.UseCustom) ImGui.PopStyleColor(4);
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
            // Split display text from ImGui ID so they can be positioned independently
            var sep  = label.IndexOf("##", StringComparison.Ordinal);
            var text = sep >= 0 ? label[..sep] : label;
            var id   = sep >= 0 ? label[sep..] : "##" + label;

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
    // Same convention as above: label is "Display text##anchorkey", auto-registered via Anchor().

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
