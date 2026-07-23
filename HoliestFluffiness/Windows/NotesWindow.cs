using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using HoliestFluffiness.Handlers;
using Lumina.Excel.Sheets;

namespace HoliestFluffiness.Windows;

public sealed class NotesWindow : Window, IDisposable
{
    private enum Filter { All, Global, Character, Duty }

    private readonly CharacterDb        characterDb;
    private readonly NotesHandler       notesHandler;
    private readonly IDataManager       dataManager;
    private readonly NotePreviewWindow  notePreviewWindow;

    private string searchText = string.Empty;
    private Filter filter     = Filter.All;
    private int?   selectedId;
    // True while editing a brand-new note that has never been written to the database; it only
    // gets a real row (and an id) the first time SaveIfDirty actually has something to persist, so
    // clicking "New Note" repeatedly without typing anything never litters empty rows.
    private bool   isDraft;

    // Edit buffers for the attached panel; loaded from the selected record, written back on Save
    private string editTitle    = "";
    private string editContent  = "";
    private bool   editIsGlobal;
    private bool   editEnabled;
    private bool   editPinned;
    private bool   editHideOnCombat;
    private int?   editDutyId;
    private bool   dirty;

    private string dutyPickerSearch = "";
    private List<(uint Id, string Name)>? allDuties;

    private readonly Dictionary<int, string> dutyNameCache = [];

    // "Delete" needs a second click within this window to actually commit
    private double? deleteConfirmExpiresAt;

    public NotesWindow(
        CharacterDb characterDb, NotesHandler notesHandler,
        IDataManager dataManager, NotePreviewWindow notePreviewWindow)
        : base("Notes##HFNotes")
    {
        this.characterDb       = characterDb;
        this.notesHandler      = notesHandler;
        this.dataManager       = dataManager;
        this.notePreviewWindow = notePreviewWindow;

        Size          = new Vector2(520, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        Common.PushWindowTheme();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
    }

    public override void PostDraw()
    {
        Common.PopWindowTheme();
        ImGui.PopStyleVar(2);
    }

    public override void Draw()
    {
        var visibleNotes = GetFilteredNotes();
        CloseEditPanelIfEditedNoteHidden(visibleNotes);

        DrawList(visibleNotes);
        DrawFooter();

        if (selectedId != null || isDraft)
            DrawAttachedEditPanel();
    }

    // A draft has no row in the list yet regardless of filter/search, so it's exempt - this is
    // only for a real note that's being edited and has quietly stopped matching the current
    // filter/search (or was deleted elsewhere): saves whatever's pending, then closes the panel,
    // since there's no point leaving it open on something you can no longer see or reach.
    private void CloseEditPanelIfEditedNoteHidden(List<NoteRecord> visibleNotes)
    {
        if (isDraft || selectedId is not int id) return;
        if (visibleNotes.Any(n => n.Id == id)) return;

        SaveIfDirty();
        selectedId = null;
        dirty      = false;
        deleteConfirmExpiresAt = null;
    }

    // ── List ──────────────────────────────────────────────────────────────────

    private List<NoteRecord> GetAllNotesForCharacter()
    {
        var key = notesHandler.CurrentCharacterKey;
        return key != null ? characterDb.GetNotesForCharacter(key) : [.. characterDb.GetAllNotes().Where(n => n.IsGlobal)];
    }

    private List<NoteRecord> GetFilteredNotes()
    {
        var all = GetAllNotesForCharacter();

        IEnumerable<NoteRecord> filtered = filter switch
        {
            Filter.Global    => all.Where(n => n.IsGlobal),
            Filter.Character => all.Where(n => !n.IsGlobal),
            Filter.Duty      => all.Where(n => n.DutyId != null),
            _                => all,
        };

        if (!string.IsNullOrEmpty(searchText))
            filtered = filtered.Where(n =>
                n.Title.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                n.Content.Contains(searchText, StringComparison.OrdinalIgnoreCase));

        return [.. SortNotes(filtered)];
    }

    // By title then creation date, not last-edited - re-sorting a note to the top just because you
    // tweaked it is disorienting.
    private static IEnumerable<NoteRecord> SortNotes(IEnumerable<NoteRecord> notes) => notes
        .OrderByDescending(n => n.Pinned)
        .ThenBy(n => n.Title, StringComparer.OrdinalIgnoreCase)
        .ThenBy(n => n.CreatedAt);

    // Popout pages through this note's "peers": other notes bound to the same duty, or - if this
    // one is a general note - other general notes. Not the main list's active filter/search.
    private List<NoteRecord> GetPopoutPeers(NoteRecord n) =>
        [.. SortNotes(GetAllNotesForCharacter().Where(x => x.DutyId == n.DutyId))];

    private void DrawList(List<NoteRecord> notes)
    {
        var footerH = ImGui.GetStyle().ItemSpacing.Y + ImGui.GetFrameHeightWithSpacing() + 4f;
        var listH   = ImGui.GetContentRegionAvail().Y - footerH;

        if (notes.Count == 0)
        {
            DrawEmptyState(listH);
            return;
        }

        ImGui.BeginChild("##noteslist", new Vector2(0, listH));
        foreach (var n in notes)
            DrawNoteRow(n);
        ImGui.EndChild();
    }

    // Draws one invisible, full-block Selectable spanning the title, scope, and (if present) duty
    // lines (so the highlight/hover covers them as a single unit) then overlays the text lines on
    // top of it - otherwise the title and its dimmed lines below it read as separate list rows.
    private void DrawNoteRow(NoteRecord n)
    {
        var isSelected  = selectedId == n.Id;
        var lineH       = ImGui.GetTextLineHeight();
        const float topPad = 3f, lineGap = 2f, bottomPad = 3f, indent = 8f;
        var hasDutyLine = n.DutyId != null;
        var lineCount   = hasDutyLine ? 3 : 2;
        var rowH = topPad + lineH * lineCount + lineGap * (lineCount - 1) + bottomPad;

        var rowStart = ImGui.GetCursorPos();

        ImGui.PushStyleColor(ImGuiCol.Header,        isSelected ? Theme.ColGoldMid : Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Theme.ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive,  Theme.ColGoldMid);
        // No SpanAllColumns here - that flag is meant for Selectables inside an actual table and
        // misbehaves (including scroll-wheel hit-testing) without one; this list is a plain child.
        var clicked = ImGui.Selectable($"##noterow{n.Id}", isSelected, ImGuiSelectableFlags.None, new Vector2(0, rowH));
        ImGui.PopStyleColor(3);

        if (clicked)
            SelectNote(isSelected ? null : n.Id);
        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup($"##notectx{n.Id}");
        DrawContextMenu(n);

        var rowEnd = ImGui.GetCursorPos();

        // Gold = will auto-show in popouts ("Popout" setting on), plain white = won't
        ImGui.SetCursorPos(new Vector2(rowStart.X + indent, rowStart.Y + topPad));
        ImGui.PushStyleColor(ImGuiCol.Text, n.Enabled ? Theme.ColGold : Theme.ColWhite);
        ImGui.TextUnformatted(n.Pinned ? $"* {n.Title}" : n.Title);
        ImGui.PopStyleColor();

        var scopeY = rowStart.Y + topPad + lineH + lineGap;
        ImGui.SetCursorPos(new Vector2(rowStart.X + indent, scopeY));
        Common.DimmedText(NoteScopeLine(n));

        if (hasDutyLine)
        {
            // Muted gold instead of the plain dimmed white the scope line uses, so it reads as a
            // distinct "duty" tag rather than more of the same line above it.
            ImGui.SetCursorPos(new Vector2(rowStart.X + indent, scopeY + lineH + lineGap));
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColWhiteDim);
            ImGui.TextUnformatted($"- {DutyName(n.DutyId!.Value)}");
            ImGui.PopStyleColor();
        }

        ImGui.SetCursorPos(rowEnd);
        ImGui.Spacing();
    }

    private void DrawContextMenu(NoteRecord n)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 6));
        var open = ImGui.BeginPopup($"##notectx{n.Id}");
        ImGui.PopStyleVar();
        if (!open) return;

        ImGui.TextDisabled(n.Title);
        ImGui.Separator();

        // Pages through this note's peers - other notes on the same duty, or other general notes
        // if this one has none - not the main list's active filter/search. Popping out a 2nd time
        // (even a different note) just refocuses that page.
        if (ImGui.MenuItem("Popout"))
            notePreviewWindow.Show(GetPopoutPeers(n), n.Id);

        if (ImGui.MenuItem("Duplicate"))
        {
            var copy = new NoteRecord
            {
                Author    = n.Author,
                Title     = $"{n.Title} (Copy)",
                Content   = n.Content,
                IsGlobal  = n.IsGlobal,
                Enabled   = n.Enabled,
                Pinned    = n.Pinned,
                DutyId    = n.DutyId,
                CreatedAt = DateTime.UtcNow,
            };
            characterDb.AddNote(copy);
            notePreviewWindow.NotifyCreated(copy);
        }

        if (ImGui.MenuItem("Delete"))
        {
            characterDb.DeleteNote(n.Id);
            if (selectedId == n.Id) { selectedId = null; dirty = false; }
            notePreviewWindow.NotifyDeleted(n.Id);
        }

        ImGui.EndPopup();
    }

    private static string NoteScopeLine(NoteRecord n)
    {
        return n.IsGlobal ? $"Global ({n.Author})" : $"{n.Author} (This character)";
    }

    private string DutyName(int dutyId)
    {
        if (dutyNameCache.TryGetValue(dutyId, out var cached)) return cached;
        var name = dataManager.GetExcelSheet<ContentFinderCondition>()?.GetRowOrDefault((uint)dutyId)?.Name.ExtractText();
        var resolved = string.IsNullOrEmpty(name) ? $"Duty #{dutyId}" : name;
        dutyNameCache[dutyId] = resolved;
        return resolved;
    }

    private List<(uint Id, string Name)> GetAllDuties()
    {
        if (allDuties != null) return allDuties;
        var sheet = dataManager.GetExcelSheet<ContentFinderCondition>();
        allDuties = sheet == null
            ? []
            : [.. sheet
                .Select(c => (c.RowId, Name: c.Name.ExtractText()))
                .Where(c => !string.IsNullOrEmpty(c.Name))
                .OrderBy(c => c.Name)];
        return allDuties;
    }

    // Reserves the same height the list would have used, so the footer never shifts
    private static void DrawEmptyState(float height)
    {
        ImGui.BeginChild("##notesempty", new Vector2(0, height));
        const string text = "No notes yet.";
        var avail = ImGui.GetContentRegionAvail();
        var size  = ImGui.CalcTextSize(text);
        ImGui.SetCursorPos(new Vector2(Math.Max(0, (avail.X - size.X) / 2f), Math.Max(0, (avail.Y - size.Y) / 2f)));
        Common.DimmedText(text);
        ImGui.EndChild();
    }

    // ── Footer ────────────────────────────────────────────────────────────────

    private void DrawFooter()
    {
        const string newNoteLabel = "New Note";
        const float  margin       = 8f;

        ImGui.SetCursorPosX(margin);
        Common.PushGoldButton();
        if (ImGui.Button(newNoteLabel))
            CreateNote();
        Common.PopGoldButton();

        ImGui.SameLine();
        Common.PushSearchInput();
        var searchW = ImGui.GetContentRegionAvail().X - margin;
        ImGui.SetNextItemWidth(searchW);
        ImGui.InputTextWithHint("##notessearch", SearchHint(), ref searchText, 64);
        Common.PopSearchInput();

        // The filter dropdown used to sit here too, but that made the footer feel cramped for a
        // 3rd control; right-clicking the search box picks a filter instead, and the hint text
        // reflects whichever one is active so it's never hidden state.
        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup("##notesfilterpopup");
        DrawFilterPopup();
    }

    private static string FilterLabel(Filter f) => f switch
    {
        Filter.Global    => "Global",
        Filter.Character => "This Character",
        Filter.Duty      => "Duty-bound",
        _                => "All Notes",
    };

    private string SearchHint() => filter switch
    {
        Filter.Global    => "Search global notes...",
        Filter.Character => "Search this character's notes...",
        Filter.Duty      => "Search duty-bound notes...",
        _                => "Search... (right-click to filter)",
    };

    private void DrawFilterPopup()
    {
        Common.PushGoldCombo();
        if (ImGui.BeginPopup("##notesfilterpopup"))
        {
            foreach (var tab in Enum.GetValues<Filter>())
                if (ImGui.Selectable(FilterLabel(tab), filter == tab))
                    filter = tab;
            ImGui.EndPopup();
        }
        Common.PopGoldCombo();
    }

    private void CreateNote()
    {
        SaveIfDirty();

        selectedId   = null;
        isDraft      = true;
        dirty        = false;
        editTitle    = "New note";
        editContent  = "";
        editIsGlobal = false;
        editEnabled  = false;
        editPinned   = false;
        editHideOnCombat = false;
        editDutyId   = null;
        deleteConfirmExpiresAt = null;
    }

    // ── Selection / edit buffers ─────────────────────────────────────────────

    private void SelectNote(int? id)
    {
        SaveIfDirty();

        selectedId = id;
        isDraft    = false;
        dirty      = false;

        if (id == null) return;

        var note = characterDb.GetAllNotes().FirstOrDefault(n => n.Id == id);
        if (note == null) { selectedId = null; return; }

        editTitle    = note.Title;
        editContent  = note.Content;
        editIsGlobal = note.IsGlobal;
        editEnabled  = note.Enabled;
        editPinned   = note.Pinned;
        editHideOnCombat = note.HideOnCombat;
        editDutyId   = note.DutyId;
        deleteConfirmExpiresAt = null;
    }

    // Handles both paths: a dirty draft is only ever written to the database here, on its first
    // save, and a dirty existing note is written back to its row. An untouched draft is dropped.
    private void SaveIfDirty()
    {
        if (!dirty)
        {
            isDraft = false;
            return;
        }

        if (isDraft)
        {
            var note = new NoteRecord
            {
                Author       = notesHandler.CurrentCharacterKey ?? "",
                Title        = editTitle,
                Content      = editContent,
                IsGlobal     = editIsGlobal,
                Enabled      = editEnabled,
                Pinned       = editPinned,
                HideOnCombat = editHideOnCombat,
                DutyId       = editDutyId,
                CreatedAt    = DateTime.UtcNow,
            };
            characterDb.AddNote(note);
            selectedId = note.Id;
            isDraft    = false;
            dirty      = false;
            notePreviewWindow.NotifyCreated(note);
            return;
        }

        if (selectedId is not int id) return;

        var existing = characterDb.GetAllNotes().FirstOrDefault(n => n.Id == id);
        if (existing == null) return;

        existing.Title        = editTitle;
        existing.Content      = editContent;
        existing.IsGlobal     = editIsGlobal;
        existing.Enabled      = editEnabled;
        existing.Pinned       = editPinned;
        existing.HideOnCombat = editHideOnCombat;
        existing.DutyId       = editDutyId;
        existing.UpdatedAt    = DateTime.UtcNow;
        characterDb.UpdateNote(existing);
        notePreviewWindow.NotifyUpdated(existing);
        dirty = false;
    }

    private void DeleteSelected()
    {
        if (isDraft)
        {
            isDraft    = false;
            selectedId = null;
            dirty      = false;
            return;
        }

        if (selectedId is not int id) return;
        characterDb.DeleteNote(id);
        selectedId = null;
        dirty      = false;
        notePreviewWindow.NotifyDeleted(id);
    }

    // ── Attached edit panel ───────────────────────────────────────────────────

    private void DrawAttachedEditPanel()
    {
        var mainPos  = ImGui.GetWindowPos();
        var mainSize = ImGui.GetWindowSize();

        ImGui.SetNextWindowPos(new Vector2(mainPos.X + mainSize.X + 2, mainPos.Y), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(320, mainSize.Y), ImGuiCond.Always);

        if (Theme.UseCustom)
        {
            ImGui.PushStyleColor(ImGuiCol.WindowBg,      Theme.Fade(Theme.ColSecondary));
            ImGui.PushStyleColor(ImGuiCol.Text,          Theme.ColWhite);
            ImGui.PushStyleColor(ImGuiCol.FrameBg,       Theme.ColPrimary);
            ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,   Theme.Fade(Theme.ColHighlight));
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, Theme.ColGoldSub);
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.Fade(ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg]));
        }
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 10));

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar     |
            ImGuiWindowFlags.NoResize       |
            ImGuiWindowFlags.NoMove         |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing;

        if (ImGui.Begin("##notesEditPanel", flags))
            DrawEditPanelContent();
        ImGui.End();

        ImGui.PopStyleColor(Theme.UseCustom ? 5 : 1);
        ImGui.PopStyleVar(1);
    }

    private void DrawEditPanelContent()
    {
        DrawEditHeader();

        ImGui.SetNextItemWidth(-1);
        dirty |= ImGui.InputText("##notetitle", ref editTitle, 128);
        ImGui.Spacing();

        // Only the bottom action row is left below the content box, so this reserve is exact.
        var footerH  = ImGui.GetFrameHeightWithSpacing();
        var contentH = Math.Max(80f, ImGui.GetContentRegionAvail().Y - footerH);
        dirty |= ImGui.InputTextMultiline("##notecontent", ref editContent, 4000, new Vector2(-1, contentH));

        DrawBottomRow();
    }

    // Title on the left, duty button pinned top-right - one button now that "clear" lives inside
    // the picker popup instead (see DrawDutyPickerPopup), so it's compact enough to sit up here.
    private void DrawEditHeader()
    {
        ImGui.AlignTextToFramePadding();
        Common.GoldText(isDraft ? "Create Note" : "Edit Note");

        var dutyLabel = editDutyId is int boundId ? DutyName(boundId) : "General note";
        var dutyBtnW  = ImGui.CalcTextSize(dutyLabel).X + ImGui.GetStyle().FramePadding.X * 2;

        ImGui.SameLine();
        var avail = ImGui.GetContentRegionAvail().X;
        if (avail > dutyBtnW)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - dutyBtnW);

        Common.PushGoldButton();
        if (ImGui.Button($"{dutyLabel}##dutypicker", new Vector2(dutyBtnW, 0)))
        {
            dutyPickerSearch = "";
            ImGui.OpenPopup("##dutypickerpopup");
        }
        Common.PopGoldButton();

        DrawDutyPickerPopup();

        ImGui.Separator();
        ImGui.Spacing();
    }

    // Save + Settings bottom-left, Delete pinned bottom-right.
    private void DrawBottomRow()
    {
        const float saveW = 70f, settingsW = 80f, gap = 8f;

        Common.PushGoldButton();
        if (ImGui.Button("Save", new Vector2(saveW, 0)))
            SaveIfDirty();
        Common.PopGoldButton();

        ImGui.SameLine(0, gap);
        Common.PushGreyButton();
        if (ImGui.Button("Settings##notesettings", new Vector2(settingsW, 0)))
            ImGui.OpenPopup("##notesettingspopup");
        Common.PopGreyButton();
        DrawSettingsPopup();

        DrawDeleteButton();
    }

    // Two clicks to actually delete: the first swaps the label to "Are you sure?" for 4 seconds
    // and reverts on its own if nothing follows, so a stray click can't nuke a note outright.
    private void DrawDeleteButton()
    {
        // A draft has nothing saved yet, so discarding it costs nothing - skip the confirm step.
        var confirming = !isDraft && deleteConfirmExpiresAt is double expires && ImGui.GetTime() < expires;
        if (deleteConfirmExpiresAt.HasValue && !confirming) deleteConfirmExpiresAt = null;

        var label = confirming ? "Are you sure?" : "Delete";
        var width = Math.Max(70f, ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2);

        ImGui.SameLine();
        var avail = ImGui.GetContentRegionAvail().X;
        if (avail > width)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - width);

        ImGui.PushStyleColor(ImGuiCol.Button,        confirming ? Theme.ColRed : Theme.ColGrey);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered,  Theme.ColRed);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,   Theme.ColRed);
        ImGui.PushStyleColor(ImGuiCol.Text,           Theme.ColWhite);
        if (ImGui.Button(label, new Vector2(width, 0)))
        {
            if (isDraft || confirming) { deleteConfirmExpiresAt = null; DeleteSelected(); }
            else deleteConfirmExpiresAt = ImGui.GetTime() + 4.0;
        }
        ImGui.PopStyleColor(4);
    }

    // These live behind popups (Settings, duty picker) rather than an explicit Save, so every
    // change here commits immediately instead of waiting for the bottom Save button.
    private void DrawSettingsPopup()
    {
        Common.PushGoldCombo();
        if (ImGui.BeginPopup("##notesettingspopup"))
        {
            Common.PushGoldCheckbox();
            var popoutLabel = editDutyId != null ? "Popout on duty##enabled" : "Popout on login##enabled";
            var changed  = ImGui.Checkbox(popoutLabel, ref editEnabled);
            changed |= ImGui.Checkbox("Global note##global", ref editIsGlobal);
            changed |= ImGui.Checkbox("Pinned note##pinned", ref editPinned);

            // Always visible so it's discoverable, just disabled until there's a duty to hide for
            ImGui.BeginDisabled(editDutyId == null);
            changed |= ImGui.Checkbox("Hide on combat, remind on wipe##hideoncombat", ref editHideOnCombat);
            ImGui.EndDisabled();
            Common.PopGoldCheckbox();

            if (changed) { dirty = true; SaveIfDirty(); }

            ImGui.EndPopup();
        }
        Common.PopGoldCombo();
    }

    private void DrawDutyPickerPopup()
    {
        ImGui.SetNextWindowSize(new Vector2(260, 300), ImGuiCond.Appearing);
        Common.PushGoldCombo();
        if (ImGui.BeginPopup("##dutypickerpopup"))
        {
            if (ImGui.Selectable("No duty (General note)"))
            {
                editDutyId       = null;
                editHideOnCombat = false;
                dirty            = true;
                SaveIfDirty();
                ImGui.CloseCurrentPopup();
            }
            ImGui.Separator();

            if (notesHandler.IsInDuty && notesHandler.CurrentDutyId is int currentDuty)
            {
                if (ImGui.Selectable($"Use current duty ({DutyName(currentDuty)})"))
                {
                    editDutyId = currentDuty;
                    dirty      = true;
                    SaveIfDirty();
                    ImGui.CloseCurrentPopup();
                }
                ImGui.Separator();
            }

            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##dutypickersearch", "Search duties...", ref dutyPickerSearch, 64);

            ImGui.BeginChild("##dutypickerlist", new Vector2(0, 220));
            foreach (var duty in GetAllDuties())
            {
                if (!string.IsNullOrEmpty(dutyPickerSearch) &&
                    !duty.Name.Contains(dutyPickerSearch, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (ImGui.Selectable($"{duty.Name}##duty{duty.Id}"))
                {
                    editDutyId = (int)duty.Id;
                    dirty      = true;
                    dutyNameCache[(int)duty.Id] = duty.Name;
                    SaveIfDirty();
                    ImGui.CloseCurrentPopup();
                }
            }
            ImGui.EndChild();

            ImGui.EndPopup();
        }
        Common.PopGoldCombo();
    }
}
