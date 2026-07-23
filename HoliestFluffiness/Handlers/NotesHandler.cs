using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace HoliestFluffiness.Handlers;

public class NotesHandler : IDisposable
{
    private readonly CharacterDb characterDb;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly IDataManager dataManager;
    private readonly IFramework framework;
    private readonly IDutyState dutyState;

    private bool wasInDuty;
    private bool wasInCombat;
    // Delays the duty-entered check a couple seconds past the BoundByDuty edge: right on that
    // frame the local player object / TerritoryType / ContentFinderCondition lookup can still be
    // mid-transition, so checking immediately can silently miss the popup.
    private int dutyCheckFramesLeft;
    private const int DutyCheckDelayFrames = 120;

    public NotesHandler(
        CharacterDb characterDb, IClientState clientState, ICondition condition,
        IObjectTable objectTable, IDataManager dataManager, IFramework framework, IDutyState dutyState)
    {
        this.characterDb   = characterDb;
        this.clientState   = clientState;
        this.condition     = condition;
        this.objectTable   = objectTable;
        this.dataManager   = dataManager;
        this.framework     = framework;
        this.dutyState     = dutyState;

        clientState.Login   += OnLogin;
        framework.Update     += OnFrameworkUpdate;
        dutyState.DutyWiped += OnDutyWiped;
    }

    // Fires with the notes that should be shown in the preview popup
    public event Action<List<NoteRecord>>? RequestShowPreview;

    // Fires to add/remove notes from an already-open duty-notes popout as combat starts/ends -
    // dutyId is the duty these notes belong to, so the popout can ignore this if it's showing
    // something unrelated (or not open at all). See NotePreviewWindow.UpdateDutyPreview.
    public event Action<List<NoteRecord>, int?>? RequestUpdateDutyPreview;

    public bool IsInDuty =>
        condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56] || condition[ConditionFlag.BoundByDuty95];

    public int? CurrentDutyId
    {
        get
        {
            var territory = dataManager.GetExcelSheet<TerritoryType>()?.GetRow(clientState.TerritoryType);
            var cfc       = territory?.ContentFinderCondition.ValueNullable;
            return cfc is { RowId: > 0 } ? (int)cfc.Value.RowId : null;
        }
    }

    public string? CurrentCharacterKey => Common.GetCurrentPlayerKey(objectTable);

    // Notes visible right now: enabled, applicable to this character, and duty-bound notes only
    // count while actually in that duty (they're hidden otherwise, not just deprioritized).
    public List<NoteRecord> GetApplicableNotes()
    {
        var key = CurrentCharacterKey;
        if (key == null) return [];

        var dutyId = IsInDuty ? CurrentDutyId : null;
        return [.. characterDb.GetNotesForCharacter(key)
            .Where(n => n.Enabled && (n.DutyId == null || n.DutyId == dutyId))
            .OrderByDescending(n => n.Pinned)
            .ThenBy(n => n.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n.CreatedAt)];
    }

    private List<NoteRecord> GetCurrentDutyNotes() => GetApplicableNotes().Where(n => n.DutyId != null).ToList();

    // "Popout automatically" is a per-note setting (NoteRecord.Enabled), already filtered into
    // GetApplicableNotes() - no separate global gate needed here.
    private void OnLogin()
    {
        var notes = GetApplicableNotes();
        if (notes.Count > 0) RequestShowPreview?.Invoke(notes);
    }

    private void OnFrameworkUpdate(IFramework fw)
    {
        var inDuty = IsInDuty;
        if (inDuty && !wasInDuty)
            dutyCheckFramesLeft = DutyCheckDelayFrames;
        wasInDuty = inDuty;

        if (dutyCheckFramesLeft > 0)
        {
            // Left the duty again before the delay elapsed; nothing to check anymore.
            if (!inDuty) dutyCheckFramesLeft = 0;
            else if (--dutyCheckFramesLeft == 0) OnDutyEntered();
        }

        var inCombat = condition[ConditionFlag.InCombat];
        if (inDuty && inCombat && !wasInCombat)
            OnCombatStartedInDuty();
        wasInCombat = inCombat;
    }

    private void OnDutyEntered()
    {
        var notes = GetCurrentDutyNotes();
        if (notes.Count > 0) RequestShowPreview?.Invoke(notes);
    }

    // Pulls any note marked "hide on combat" out of the current duty's notes; harmless no-op if
    // none are marked that way, or if no duty-notes popout is even open right now.
    private void OnCombatStartedInDuty()
    {
        var dutyId = CurrentDutyId;
        if (dutyId == null) return;
        var remaining = GetCurrentDutyNotes().Where(n => !n.HideOnCombat).ToList();
        RequestUpdateDutyPreview?.Invoke(remaining, dutyId);
    }

    // The party wiped and the screen faded to black - bring every duty note back (including any
    // "hide on combat" ones pulled out at combat start) as a reminder before the next pull.
    private void OnDutyWiped(IDutyStateEventArgs args)
    {
        var dutyId = CurrentDutyId;
        if (dutyId == null) return;
        RequestUpdateDutyPreview?.Invoke(GetCurrentDutyNotes(), dutyId);
    }

    public void Dispose()
    {
        clientState.Login   -= OnLogin;
        framework.Update     -= OnFrameworkUpdate;
        dutyState.DutyWiped -= OnDutyWiped;
    }
}
