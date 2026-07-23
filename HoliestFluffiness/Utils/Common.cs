using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Utility;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace HoliestFluffiness;

internal static class Common
{
    internal static void DimmedText(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColWhiteDim);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    internal static void DimmedTextWrapped(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColWhiteDim);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    internal static bool IsPluginLoaded(IDalamudPluginInterface pluginInterface, string name) =>
        pluginInterface.InstalledPlugins.Any(p => p.InternalName == name && p.IsLoaded);

    // Base classes included; anything unlisted (crafters, gatherers) falls through to "other"
    private static readonly HashSet<string> TankJobs =
        new(StringComparer.OrdinalIgnoreCase) { "PLD", "WAR", "DRK", "GNB", "GLA", "MRD" };
    private static readonly HashSet<string> HealerJobs =
        new(StringComparer.OrdinalIgnoreCase) { "WHM", "SCH", "AST", "SGE", "CNJ" };
    private static readonly HashSet<string> DpsJobs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "MNK", "DRG", "NIN", "SAM", "RPR", "VPR", "PGL", "LNC", "ROG",  // melee
            "BRD", "MCH", "DNC", "ARC",                                     // physical ranged
            "BLM", "SMN", "RDM", "PCT", "BLU", "THM", "ACN",                // casters
        };

    internal static Vector4 JobRoleColor(string jobAbbr) =>
          TankJobs.Contains(jobAbbr)   ? Theme.ColRoleTank
        : HealerJobs.Contains(jobAbbr) ? Theme.ColRoleHealer
        : DpsJobs.Contains(jobAbbr)    ? Theme.ColRoleDps
        : Theme.ColRoleOther;

    private static readonly string[] ShortenNumberSuffixes = ["", "K", "M", "B", "T"];

    // 100000 -> "100K", 1200000 -> "1.2M"; a trailing ".0" is dropped
    internal static string ShortenNumber(long num)
    {
        var sign = num < 0 ? "-" : "";
        double value = Math.Abs(num);
        var    tier  = 0;

        while (value >= 1000 && tier < ShortenNumberSuffixes.Length - 1)
        {
            value /= 1000;
            tier++;
        }

        if (tier == 0) return num.ToString("N0");

        value = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        if (value >= 1000 && tier < ShortenNumberSuffixes.Length - 1)
        {
            value = Math.Round(value / 1000, 1, MidpointRounding.AwayFromZero);
            tier++;
        }

        var formatted = value % 1 == 0 ? $"{(long)value}" : $"{value:0.0}";
        return $"{sign}{formatted}{ShortenNumberSuffixes[tier]}";
    }

    // Non-positive slots are ping timeouts or unfilled entries, and are skipped
    internal static (int Samples, int Avg, int Min, int Max) ComputeSampleStats(float[] data)
    {
        var samples = 0;
        var sum     = 0f;
        var min     = float.MaxValue;
        var max     = float.MinValue;

        foreach (var v in data)
        {
            if (v <= 0) continue;
            samples++;
            sum += v;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        return samples > 0
            ? (samples, (int)(sum / samples), (int)min, (int)max)
            : (0, 0, 0, 0);
    }

    // Null while the world sheet row is unresolvable, which happens briefly during zone/login
    internal static string? WorldName(IPlayerCharacter player) =>
        player.HomeWorld.ValueNullable?.Name.ExtractText();

    // Canonical "Name@World" identity used as the key for every per-character store.
    internal static string CharacterKey(IPlayerCharacter player) =>
        $"{player.Name.TextValue}@{WorldName(player)}";

    internal static string? GetCurrentPlayerKey(IObjectTable objectTable) =>
        TryGetLocalPlayer(objectTable, out var player) ? CharacterKey(player) : null;

    // Local player is always slot 0; false means not logged in or mid-transition
    internal static bool TryGetLocalPlayer(IObjectTable objectTable, [NotNullWhen(true)] out IPlayerCharacter? player)
    {
        player = objectTable[0] as IPlayerCharacter;
        return player != null;
    }

    // ── Native hook helpers ───────────────────────────────────────────────────

    // Returns null instead of throwing: a patch that moves one signature should disable that
    // one feature, not take down the whole plugin from Plugin's constructor.
    internal static Hook<T>? TryCreateHook<T>(nint address, T detour, IGameInteropProvider gameInterop,
        IPluginLog log, string failureMessage, bool enable = true) where T : Delegate
    {
        try
        {
            var hook = gameInterop.HookFromAddress(address, detour);
            if (enable) hook.Enable();
            return hook;
        }
        catch (Exception ex)
        {
            log.Warning(ex, failureMessage);
            return null;
        }
    }

    // Same contract as TryCreateHook, for addresses coming from Sigs.cs rather than ClientStructs
    internal static Hook<T>? TryCreateHookFromSignature<T>(string signature, T detour, IGameInteropProvider gameInterop,
        IPluginLog log, string failureMessage, bool enable = true) where T : Delegate
    {
        try
        {
            var hook = gameInterop.HookFromSignature(signature, detour);
            if (enable) hook.Enable();
            return hook;
        }
        catch (Exception ex)
        {
            log.Warning(ex, failureMessage);
            return null;
        }
    }

    // ── ImGui style helpers ───────────────────────────────────────────────────
    //
    // Each Push*/Pop* pair ALWAYS moves the same fixed colour count, so callers can pop with a raw
    // ImGui.PopStyleColor(N) and never desync the stack. With the custom theme off we push the
    // ambient Dalamud colour, a visual no-op.

    private static Vector4 Amb(ImGuiCol slot) => ImGui.GetStyle().Colors[(int)slot];

    private static Vector4 T(Vector4 custom, ImGuiCol slot) => Theme.UseCustom ? custom : Amb(slot);

    // T plus the opacity knob, so backgrounds fade with the window body instead of sitting on top
    private static Vector4 FadeT(Vector4 custom, ImGuiCol slot) => Theme.Fade(Theme.UseCustom ? custom : Amb(slot));

    internal static void PushWindowTheme()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg,             FadeT(Theme.ColSecondary, ImGuiCol.WindowBg));
        ImGui.PushStyleColor(ImGuiCol.Text,                 T(Theme.ColWhite,     ImGuiCol.Text));
        ImGui.PushStyleColor(ImGuiCol.TitleBg,              T(Theme.ColHighlight, ImGuiCol.TitleBg));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive,        T(Theme.ColHighlight, ImGuiCol.TitleBgActive));
        ImGui.PushStyleColor(ImGuiCol.FrameBg,              T(Theme.ColPrimary,   ImGuiCol.FrameBg));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered,       T(Theme.ColHighlight, ImGuiCol.FrameBgHovered));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,          FadeT(Theme.ColHighlight, ImGuiCol.ScrollbarBg));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab,        T(Theme.ColGoldSub,   ImGuiCol.ScrollbarGrab));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, T(Theme.ColGoldMid,   ImGuiCol.ScrollbarGrabHovered));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive,  T(Theme.ColGold,      ImGuiCol.ScrollbarGrabActive));
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip,           T(Theme.ColGoldSub,   ImGuiCol.ResizeGrip));
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered,    T(Theme.ColGoldMid,   ImGuiCol.ResizeGripHovered));
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive,     T(Theme.ColGold,      ImGuiCol.ResizeGripActive));
    }
    internal static void PopWindowTheme() => ImGui.PopStyleColor(13);

    internal static void PushPopupTheme()
    {
        ImGui.PushStyleColor(ImGuiCol.Text,          T(Theme.ColWhite,     ImGuiCol.Text));
        ImGui.PushStyleColor(ImGuiCol.WindowBg,      FadeT(Theme.ColSecondary, ImGuiCol.WindowBg));
        ImGui.PushStyleColor(ImGuiCol.TitleBg,       T(Theme.ColHighlight, ImGuiCol.TitleBg));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, T(Theme.ColHighlight, ImGuiCol.TitleBgActive));
    }
    internal static void PopPopupTheme() => ImGui.PopStyleColor(4);

    internal static void PushTablePopupTheme()
    {
        PushPopupTheme();
        ImGui.PushStyleColor(ImGuiCol.TableBorderLight,  T(Theme.ColGoldMid, ImGuiCol.TableBorderLight));
        ImGui.PushStyleColor(ImGuiCol.TableBorderStrong, T(Theme.ColGold,    ImGuiCol.TableBorderStrong));
    }
    internal static void PopTablePopupTheme() => ImGui.PopStyleColor(6);

    // The idle resize grip is invisible on purpose so it does not sit on top of the plot
    internal static void PushChartWindowTheme()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg,          FadeT(Theme.ColSecondary, ImGuiCol.WindowBg));
        ImGui.PushStyleColor(ImGuiCol.Text,              T(Theme.ColWhite,     ImGuiCol.Text));
        ImGui.PushStyleColor(ImGuiCol.TitleBg,           T(Theme.ColHighlight, ImGuiCol.TitleBg));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive,     T(Theme.ColHighlight, ImGuiCol.TitleBgActive));
        ImGui.PushStyleColor(ImGuiCol.FrameBg,           T(Theme.ColPrimary,   ImGuiCol.FrameBg));
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip,        T(Vector4.Zero,       ImGuiCol.ResizeGrip));
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, T(Theme.ColGoldMid,   ImGuiCol.ResizeGripHovered));
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive,  T(Theme.ColGold,      ImGuiCol.ResizeGripActive));
    }
    internal static void PopChartWindowTheme() => ImGui.PopStyleColor(8);

    internal static void PushGoldButton()
    {
        ImGui.PushStyleColor(ImGuiCol.Button,        T(Theme.ColGoldSub, ImGuiCol.Button));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, T(Theme.ColGoldMid, ImGuiCol.ButtonHovered));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  T(Theme.ColGold,    ImGuiCol.ButtonActive));
        ImGui.PushStyleColor(ImGuiCol.Text,          T(Theme.ColGold,    ImGuiCol.Text));
    }
    internal static void PopGoldButton() => ImGui.PopStyleColor(4);

    internal static void PushGreyButton()
    {
        ImGui.PushStyleColor(ImGuiCol.Button,        T(Theme.ColGrey,    ImGuiCol.Button));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, T(Theme.ColGreyHov, ImGuiCol.ButtonHovered));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  T(Theme.ColGreyAct, ImGuiCol.ButtonActive));
        ImGui.PushStyleColor(ImGuiCol.Text,          T(Theme.ColWhite,   ImGuiCol.Text));
    }
    internal static void PopGreyButton() => ImGui.PopStyleColor(4);

    internal static void GoldText(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, T(Theme.ColGold, ImGuiCol.Text));
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    // Green and red stay themed in both modes; they are semantic, not decorative
    internal static void GreenText(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColGreen);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    internal static void RedText(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColRed);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    internal static void PushTableHeader()
    {
        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, FadeT(Theme.ColPrimary, ImGuiCol.TableHeaderBg));
        ImGui.PushStyleColor(ImGuiCol.Text,          T(Theme.ColGold,    ImGuiCol.Text));
    }
    internal static void PopTableHeader() => ImGui.PopStyleColor(2);

    internal static void PushSearchInput()
    {
        ImGui.PushStyleColor(ImGuiCol.Border, T(Theme.ColGoldMid, ImGuiCol.Border));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
    }
    internal static void PopSearchInput()
    {
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    // Combo box (and its dropdown popup) with a gold accent border and gold-highlighted rows when
    // hovered/selected, but plain white text - an all-gold combo reads as overdone.
    internal static void PushGoldCombo()
    {
        ImGui.PushStyleColor(ImGuiCol.Text,          T(Theme.ColWhite,   ImGuiCol.Text));
        ImGui.PushStyleColor(ImGuiCol.Border,        T(Theme.ColGoldMid, ImGuiCol.Border));
        ImGui.PushStyleColor(ImGuiCol.PopupBg,       FadeT(Theme.ColSecondary, ImGuiCol.PopupBg));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, T(Theme.ColGoldMid, ImGuiCol.HeaderHovered));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive,  T(Theme.ColGold,    ImGuiCol.HeaderActive));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
    }
    internal static void PopGoldCombo()
    {
        ImGui.PopStyleColor(5);
        ImGui.PopStyleVar();
    }

    // Same gold checkmark/border convention ConfigWindow's private PushCheckbox uses, shared here
    // since NotesWindow isn't part of that partial class.
    internal static void PushGoldCheckbox()
    {
        ImGui.PushStyleColor(ImGuiCol.CheckMark, T(Theme.ColGold, ImGuiCol.CheckMark));
        ImGui.PushStyleColor(ImGuiCol.Border,    T(Theme.ColGold, ImGuiCol.Border));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
    }
    internal static void PopGoldCheckbox()
    {
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar();
    }

    internal static void CenterCursorForWidth(float width) =>
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - width) * 0.5f);

    // ── Toast notifications ───────────────────────────────────────────────────

    private const float ToastMaxWidth    = 440f;
    private const float ToastMinWidth    = 160f;
    private const float ToastPadX       = 10f;
    private const float ToastTitlePadY  = 6f;
    private const float ToastContentPadY = 8f;
    private const float ToastGap        = 8f;
    private const float ToastAnimSpeed  = 5f;
    private const float ToastXBtnSize   = 17f;
    private const float ToastXBtnPad    = 8f;
    private const float ToastProgressH  = 3f;

    private sealed class HfToast
    {
        public string   Title     = string.Empty;
        public string   Message   = string.Empty;
        public float    Width     = 0f;   // 0 = auto-size per-line; >0 = fixed exact width
        public float    Alpha;
        public bool     AnimOut;
        public bool     Hovered;
        public DateTime CreatedAt = DateTime.UtcNow;
        public DateTime ExpiresAt;
    }

    private static readonly List<HfToast> _toasts    = [];
    private static readonly object        _toastLock = new();

    internal static void ShowToast(string title, string message = "", float durationSec = 6f, float width = 0f)
    {
        lock (_toastLock)
            _toasts.Add(new HfToast
            {
                Title     = title,
                Message   = message,
                Width     = width,
                ExpiresAt = DateTime.UtcNow.AddSeconds(durationSec),
            });
    }

    internal static void DrawToasts()
    {
        lock (_toastLock)
        {
            var dt = ImGui.GetIO().DeltaTime;

            for (int i = _toasts.Count - 1; i >= 0; i--)
            {
                var t = _toasts[i];
                // Hovering pauses the timer by sliding ExpiresAt forward
                if (t.Hovered && !t.AnimOut)
                    t.ExpiresAt += TimeSpan.FromSeconds(dt);
                if (!t.AnimOut && DateTime.UtcNow >= t.ExpiresAt) t.AnimOut = true;
                t.Alpha = Math.Clamp(t.Alpha + (t.AnimOut ? -1f : 1f) * dt * ToastAnimSpeed, 0f, 1f);
                if (t.AnimOut && t.Alpha <= 0f) _toasts.RemoveAt(i);
            }

            if (_toasts.Count == 0) return;

            var   s    = ImGuiHelpers.GlobalScale;
            var   vp   = ImGui.GetMainViewport();
            float padX = ToastPadX * s;
            float gap  = ToastGap  * s;

            var   widths  = new float[_toasts.Count];
            var   heights = new float[_toasts.Count];
            for (int i = 0; i < _toasts.Count; i++)
                widths[i] = ToastCalcWidth(_toasts[i], s, padX);

            float totalH = (_toasts.Count - 1) * gap;
            for (int i = 0; i < _toasts.Count; i++)
            {
                heights[i] = ToastCalcHeight(_toasts[i], widths[i], s);
                totalH += heights[i];
            }

            float maxW = widths.Max();

            ImGui.SetNextWindowPos(new Vector2(vp.WorkPos.X + vp.WorkSize.X - maxW, vp.WorkPos.Y), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(maxW, vp.WorkSize.Y), ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(0f);

            const ImGuiWindowFlags Flags =
                ImGuiWindowFlags.NoDecoration       |
                ImGuiWindowFlags.NoMove             |
                ImGuiWindowFlags.NoResize           |
                ImGuiWindowFlags.NoSavedSettings    |
                ImGuiWindowFlags.NoFocusOnAppearing |
                ImGuiWindowFlags.NoNav              |
                ImGuiWindowFlags.NoBackground       |
                ImGuiWindowFlags.NoScrollbar        |
                ImGuiWindowFlags.NoInputs;

            // Zero padding so cursor pos 0 is exactly the screen edge
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            if (!ImGui.Begin("##hf_toasts", Flags)) { ImGui.PopStyleVar(); ImGui.End(); return; }
            ImGui.PopStyleVar();

            float curY = (vp.WorkSize.Y - totalH) * 0.5f;
            for (int i = 0; i < _toasts.Count; i++)
            {
                float w = widths[i];
                float h = heights[i];
                ImGui.SetCursorPos(new Vector2(maxW - w, curY));
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
                bool ok = ImGui.BeginChild($"##ht{i}", new Vector2(w, h), false,
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoBackground);
                ImGui.PopStyleVar();
                if (ok) ToastDrawChild(_toasts[i], w, s);
                ImGui.EndChild();
                curY += h + gap;
            }

            ImGui.End();
        }
    }

    private static float ToastCalcWidth(HfToast t, float s, float padX)
    {
        // Explicit width wins; the caller may have glyphs ImGui cannot measure
        if (t.Width > 0f) return t.Width * s;

        // Measure each line independently so per-glyph measurement stays accurate
        float xArea = (ToastXBtnSize + ToastXBtnPad * 2f) * s;
        float best  = ImGui.CalcTextSize(t.Title).X + padX + xArea;

        if (!string.IsNullOrEmpty(t.Message))
        {
            foreach (var line in t.Message.Split('\n'))
            {
                float lw = ImGui.CalcTextSize(line.TrimEnd('\r')).X + padX * 2f;
                if (lw > best) best = lw;
            }
        }

        return Math.Clamp(best, ToastMinWidth * s, ToastMaxWidth * s);
    }

    private static float ToastCalcHeight(HfToast t, float w, float s)
    {
        float padX       = ToastPadX        * s;
        float titlePadY  = ToastTitlePadY   * s;
        float contentPadY = ToastContentPadY * s;
        float xArea      = (ToastXBtnSize + ToastXBtnPad * 2f) * s;

        float titleBarH = titlePadY + ImGui.CalcTextSize(t.Title, false, w - padX - xArea).Y + titlePadY;
        float h         = titleBarH;
        if (!string.IsNullOrEmpty(t.Message))
            h += contentPadY + ImGui.CalcTextSize(t.Message, false, w - padX * 2f).Y + contentPadY;
        h += ToastProgressH * s;
        return h;
    }

    private static void ToastDrawChild(HfToast t, float w, float s)
    {
        var   dl  = ImGui.GetWindowDrawList();
        var   pos = ImGui.GetWindowPos();
        var   sz  = ImGui.GetWindowSize();
        float a   = t.Alpha;

        float padX        = ToastPadX        * s;
        float titlePadY   = ToastTitlePadY   * s;
        float contentPadY = ToastContentPadY * s;
        float xArea       = (ToastXBtnSize + ToastXBtnPad * 2f) * s;
        float xBtnS       = ToastXBtnSize * s;
        float xBtnPad     = ToastXBtnPad  * s;

        float titleTextH = ImGui.CalcTextSize(t.Title, false, w - padX - xArea).Y;
        float titleBarH  = titlePadY + titleTextH + titlePadY;

        // Title bar
        dl.AddRectFilled(pos, new Vector2(pos.X + w, pos.Y + titleBarH), ToastU32(Theme.ColHighlight, a));

        // Content area
        float contentTop = titleBarH;
        float progressH  = ToastProgressH * s;
        if (!string.IsNullOrEmpty(t.Message))
            dl.AddRectFilled(
                new Vector2(pos.X, pos.Y + contentTop),
                new Vector2(pos.X + w, pos.Y + sz.Y - progressH),
                ToastU32(Theme.ColSecondary, a));

        // X dismiss button, vertically centred in the title bar
        var  xMin     = new Vector2(pos.X + w - xBtnPad - xBtnS, pos.Y + (titleBarH - xBtnS) * 0.5f);
        var  xMax     = new Vector2(xMin.X + xBtnS, xMin.Y + xBtnS);
        var  mouse    = ImGui.GetIO().MousePos;
        bool xHovered = mouse.X >= xMin.X && mouse.X <= xMax.X && mouse.Y >= xMin.Y && mouse.Y <= xMax.Y;

        if (xHovered && ImGui.GetIO().MouseClicked[0] && !t.AnimOut) t.AnimOut = true;
        if (xHovered) dl.AddRectFilled(xMin, xMax, ToastU32(Theme.ColGold, 0.2f * a));

        float xi  = 4f * s;
        uint  xc  = ToastU32(xHovered ? Theme.ColGold : Theme.ColGoldMid, a);
        dl.AddLine(new Vector2(xMin.X + xi, xMin.Y + xi), new Vector2(xMax.X - xi, xMax.Y - xi), xc, 1.5f * s);
        dl.AddLine(new Vector2(xMax.X - xi, xMin.Y + xi), new Vector2(xMin.X + xi, xMax.Y - xi), xc, 1.5f * s);

        ImGui.SetCursorPos(new Vector2(padX, titlePadY));
        ImGui.PushTextWrapPos(w - padX - xArea);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColGold with { W = a });
        ImGui.TextWrapped(t.Title);
        ImGui.PopStyleColor();
        ImGui.PopTextWrapPos();

        if (!string.IsNullOrEmpty(t.Message))
        {
            ImGui.SetCursorPos(new Vector2(padX, contentTop + contentPadY));
            ImGui.PushTextWrapPos(w - padX);
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColWhiteDim with { W = a });
            ImGui.TextWrapped(t.Message);
            ImGui.PopStyleColor();
            ImGui.PopTextWrapPos();
        }

        // Progress bar
        float totalSec = (float)(t.ExpiresAt - t.CreatedAt).TotalSeconds;
        float elapsed  = (float)(DateTime.UtcNow - t.CreatedAt).TotalSeconds;
        float progress = t.AnimOut ? 0f : Math.Clamp(1f - elapsed / totalSec, 0f, 1f);
        float barY     = pos.Y + sz.Y - progressH;

        dl.AddRectFilled(new Vector2(pos.X, barY), new Vector2(pos.X + w, barY + progressH),
            ToastU32(Theme.ColGold, 0.15f * a));
        if (progress > 0f)
            dl.AddRectFilled(new Vector2(pos.X, barY), new Vector2(pos.X + w * progress, barY + progressH),
                ToastU32(Theme.ColGold, 0.65f * a));

        t.Hovered = mouse.X >= pos.X && mouse.X <= pos.X + sz.X
                 && mouse.Y >= pos.Y && mouse.Y <= pos.Y + sz.Y;
        if (t.Hovered && ImGui.GetIO().MouseClicked[2] && !t.AnimOut) t.AnimOut = true;
    }

    private static uint ToastU32(Vector4 c, float a) =>
        ImGui.ColorConvertFloat4ToU32(c with { W = Math.Clamp(a, 0f, 1f) });

    // ── Overlay draw helpers ──────────────────────────────────────────────────

    internal static (float expand, float alpha) CalcPulse(
        float maxPx = 15f, float period = 2f, float active = 0.7f, float maxAlpha = 0.75f)
    {
        var phase = (float)(ImGui.GetTime() % period) / period;
        if (phase >= active) return (0f, 0f);
        var t = phase / active;
        return (t * maxPx, maxAlpha * (1f - t));
    }

    internal static void DrawHighlightRect(ImDrawListPtr dl, Vector2 min, Vector2 max,
        float rounding, Vector4 color, string? text = null, bool pulse = true, float scale = 1f)
    {
        dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(color with { W = 0.25f }), rounding);
        dl.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(color), rounding);

        if (pulse)
        {
            var (expand, baseAlpha) = CalcPulse();
            if (baseAlpha > 0.005f)
            {
                for (var i = 0; i <= 8; i++)
                {
                    var frac  = (float)i / 8;
                    var size  = expand * frac * scale;
                    var alpha = baseAlpha * (1f - frac);
                    if (alpha < 0.005f) continue;
                    dl.AddRect(
                        min - new Vector2(size),
                        max + new Vector2(size),
                        ImGui.ColorConvertFloat4ToU32(color with { W = alpha }),
                        rounding + size);
                }
            }
        }

        if (text != null)
        {
            var textSize = ImGui.CalcTextSize(text);
            var textPos  = new Vector2(
                max.X - 5f - textSize.X,
                min.Y + ((max.Y - min.Y) - textSize.Y) / 2f);
            DrawTextShadowed(dl, text, textPos);
        }
    }

    internal static void DrawTextShadowed(ImDrawListPtr dl, string text, Vector2 pos)
    {
        var shadow = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 1f));
        var white  = ImGui.ColorConvertFloat4ToU32(Theme.ColWhite);
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
            if (dx != 0 || dy != 0)
                dl.AddText(pos + new Vector2(dx, dy), shadow, text);
        dl.AddText(pos, white, text);
    }

    internal static unsafe Vector2 GetNodePosition(AtkResNode* node)
    {
        var pos = new Vector2(node->X, node->Y);
        var par = node->ParentNode;
        while (par != null)
        {
            pos *= new Vector2(par->ScaleX, par->ScaleY);
            pos += new Vector2(par->X,      par->Y);
            par  = par->ParentNode;
        }
        return pos;
    }

    internal static unsafe bool IsAddonVisible(AtkUnitBase* addon)
    {
        if (!addon->IsVisible || addon->RootNode is null || !addon->RootNode->IsVisible()) return false;
        if ((addon->VisibilityFlags & 5) is not 0) return false;
        return true;
    }

    internal static unsafe void ExecuteCommand(string command)
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null) return;
        var shellModule = uiModule->GetRaptureShellModule();
        if (shellModule == null) return;
        var str = Utf8String.FromString(command);
        shellModule->ExecuteCommandInner(str, uiModule);
        str->Dtor(true);
    }
}
