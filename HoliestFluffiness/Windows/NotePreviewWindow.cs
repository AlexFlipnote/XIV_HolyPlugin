using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace HoliestFluffiness.Windows;

public class NotePreviewWindow : Window
{
    private const string IdSuffix = "###HFNotePreview";

    private List<NoteRecord> notes = [];
    private int pageIndex;
    // The DutyId all current pages share (set from the first note in Show()); lets NotifyCreated
    // decide, without any lookup back into NotesWindow, whether a brand-new note belongs here.
    private int? groupDutyId;
    // True only when UpdateDutyPreview itself emptied the list and closed the window (all its
    // notes got hidden for combat) - lets a later wipe reopen it, without reopening a popout the
    // user closed manually themselves for unrelated reasons.
    private bool autoClosedForCombat;

    public NotePreviewWindow() : base("Notes" + IdSuffix)
    {
        Size               = new Vector2(360, 220);
        SizeCondition      = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = true;
    }

    // Manually invoked (right-click > Popout): always opens/refocuses. startId picks which page
    // to land on within the set; popping out a note a 2nd time (even a different one) just
    // refocuses the window onto that page instead of stacking another popup.
    public void Show(List<NoteRecord> notesToShow, int? startId = null)
    {
        if (notesToShow.Count == 0) return;
        notes       = notesToShow;
        groupDutyId = notesToShow[0].DutyId;
        autoClosedForCombat = false;
        pageIndex   = startId is int id ? Math.Max(0, notes.FindIndex(n => n.Id == id)) : 0;
        UpdateTitle();
        IsOpen = true;
    }

    // Reactive: adds/removes notes from an in-progress duty-notes popout as combat starts/a wipe
    // happens. Only acts if this popout is either already open on that same duty, or was closed by
    // this very method earlier (everything got hidden) - never touches a popout showing something
    // else, and never reopens one the user dismissed manually.
    public void UpdateDutyPreview(List<NoteRecord> notesToShow, int? dutyId)
    {
        if (groupDutyId != dutyId || !(IsOpen || autoClosedForCombat)) return;

        if (notesToShow.Count == 0)
        {
            notes = [];
            IsOpen = false;
            autoClosedForCombat = true;
            return;
        }

        autoClosedForCombat = false;
        var currentId = pageIndex < notes.Count ? notes[pageIndex].Id : (int?)null;
        notes     = notesToShow;
        pageIndex = currentId is int id ? notes.FindIndex(n => n.Id == id) : -1;
        if (pageIndex < 0) pageIndex = 0;
        UpdateTitle();
        IsOpen = true;
    }

    private void UpdateTitle() =>
        WindowName = (pageIndex < notes.Count ? notes[pageIndex].Title : "Notes") + IdSuffix;

    // The three reactive hooks below are not user-invoked: they keep an already-open popout's page
    // set current as notes are created/edited/deleted elsewhere (NotesWindow). None of them ever
    // open the window - only Show() (a deliberate user action) does that.

    // Only appends if the new note actually belongs to the group currently being paged through.
    public void NotifyCreated(NoteRecord created)
    {
        if (!IsOpen || created.DutyId != groupDutyId) return;
        notes.Add(created);
    }

    public void NotifyUpdated(NoteRecord updated)
    {
        var idx = notes.FindIndex(n => n.Id == updated.Id);
        if (idx < 0) return;

        notes[idx] = updated;
        if (idx == pageIndex) UpdateTitle();
    }

    public void NotifyDeleted(int deletedId)
    {
        var idx = notes.FindIndex(n => n.Id == deletedId);
        if (idx < 0) return;

        notes.RemoveAt(idx);
        if (notes.Count == 0) { IsOpen = false; return; }

        pageIndex = Math.Min(pageIndex, notes.Count - 1);
        UpdateTitle();
    }

    public override void PreDraw() => Common.PushChartWindowTheme();

    public override void PostDraw() => Common.PopChartWindowTheme();

    public override void Draw()
    {
        if (notes.Count == 0) { IsOpen = false; return; }
        if (pageIndex >= notes.Count) pageIndex = notes.Count - 1;

        var note        = notes[pageIndex];
        var hasMultiple  = notes.Count > 1;

        if (hasMultiple)
        {
            // Separator + spacing above the paging row (mirrors NotesWindow's edit-panel header),
            // plus a bit of breathing room below it so the buttons aren't flush against the window
            // edge.
            var footerH = ImGui.GetStyle().ItemSpacing.Y * 2 + 1f + ImGui.GetFrameHeightWithSpacing() + 6f;
            ImGui.BeginChild("##notepreviewcontent", new Vector2(0, ImGui.GetContentRegionAvail().Y - footerH));
            DrawContent(note);
            ImGui.EndChild();

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            DrawPagingFooter();
        }
        else
        {
            DrawContent(note);
        }

        if (hasMultiple && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
        {
            if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow))  PrevPage();
            if (ImGui.IsKeyPressed(ImGuiKey.RightArrow)) NextPage();
        }
    }

    private static void DrawContent(NoteRecord note)
    {
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(note.Content);
        ImGui.PopTextWrapPos();
    }

    private void DrawPagingFooter()
    {
        var pageText  = $"{pageIndex + 1} / {notes.Count}";
        var pageTextW = ImGui.CalcTextSize(pageText).X;
        var rowW      = ImGui.GetFrameHeight() * 2 + pageTextW + ImGui.GetStyle().ItemSpacing.X * 2;
        Common.CenterCursorForWidth(rowW);

        Common.PushGoldButton();
        if (ImGui.ArrowButton("##noteprev", ImGuiDir.Left)) PrevPage();
        Common.PopGoldButton();

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(pageText);

        ImGui.SameLine();
        Common.PushGoldButton();
        if (ImGui.ArrowButton("##notenext", ImGuiDir.Right)) NextPage();
        Common.PopGoldButton();
    }

    private void PrevPage()
    {
        pageIndex = pageIndex == 0 ? notes.Count - 1 : pageIndex - 1;
        UpdateTitle();
    }

    private void NextPage()
    {
        pageIndex = (pageIndex + 1) % notes.Count;
        UpdateTitle();
    }
}
