using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Utility;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
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

    internal static string? GetCurrentPlayerKey(IObjectTable objectTable)
    {
        var player = objectTable[0] as IPlayerCharacter;
        return player != null
            ? $"{player.Name.TextValue}@{player.HomeWorld.ValueNullable?.Name.ExtractText()}"
            : null;
    }

    // ── ImGui style helpers ───────────────────────────────────────────────────

    // Full themed window: WindowBg, Text, TitleBar, FrameBg, Scrollbar, ResizeGrip (13 colors)
    internal static void PushWindowTheme()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg,             Theme.ColSecondary);
        ImGui.PushStyleColor(ImGuiCol.Text,                 Theme.ColWhite);
        ImGui.PushStyleColor(ImGuiCol.TitleBg,              Theme.ColHighlight);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive,        Theme.ColHighlight);
        ImGui.PushStyleColor(ImGuiCol.FrameBg,              Theme.ColPrimary);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered,       Theme.ColHighlight);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,          Theme.ColHighlight);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab,        Theme.ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Theme.ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive,  Theme.ColGold);
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip,           Theme.ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered,    Theme.ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive,     Theme.ColGold);
    }
    internal static void PopWindowTheme() => ImGui.PopStyleColor(13);

    // Scrollbar sub-theme (4 colors) use standalone or as part of a manual push block
    internal static void PushScrollbarTheme()
    {
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,          Theme.ColHighlight);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab,        Theme.ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Theme.ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive,  Theme.ColGold);
    }
    internal static void PopScrollbarTheme() => ImGui.PopStyleColor(4);

    // Resize grip sub-theme (3 colors)
    internal static void PushResizeGripTheme()
    {
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip,        Theme.ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, Theme.ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive,  Theme.ColGold);
    }
    internal static void PopResizeGripTheme() => ImGui.PopStyleColor(3);

    // Gold button (4 colors: Button + ButtonHovered + ButtonActive + Text)
    internal static void PushGoldButton()
    {
        ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ColGold);
        ImGui.PushStyleColor(ImGuiCol.Text,          Theme.ColGold);
    }
    internal static void PopGoldButton() => ImGui.PopStyleColor(4);

    // Grey (secondary) button (4 colors: Button + ButtonHovered + ButtonActive + Text)
    internal static void PushGreyButton()
    {
        ImGui.PushStyleColor(ImGuiCol.Button,        Theme.ColGrey);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.ColGreyHov);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.ColGreyAct);
        ImGui.PushStyleColor(ImGuiCol.Text,          Theme.ColWhite);
    }
    internal static void PopGreyButton() => ImGui.PopStyleColor(4);

    // Gold-coloured TextUnformatted (1 color push/pop)
    internal static void GoldText(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColGold);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    // Green-coloured TextUnformatted (1 color push/pop)
    internal static void GreenText(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColGreen);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    // Red-coloured TextUnformatted (1 color push/pop)
    internal static void RedText(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColRed);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    // Table header theme (2 colors: TableHeaderBg + Text)
    internal static void PushTableHeader()
    {
        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, Theme.ColPrimary);
        ImGui.PushStyleColor(ImGuiCol.Text,          Theme.ColGold);
    }
    internal static void PopTableHeader() => ImGui.PopStyleColor(2);

    // Search/filter InputTextWithHint border theme (1 color + 1 style var)
    internal static void PushSearchInput()
    {
        ImGui.PushStyleColor(ImGuiCol.Border, Theme.ColGoldMid);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
    }
    internal static void PopSearchInput()
    {
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    // Horizontally centers the next widget by offsetting CursorPosX
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

    internal static void DrawToasts(Configuration config)
    {
        lock (_toastLock)
        {
            var dt = ImGui.GetIO().DeltaTime;

            for (int i = _toasts.Count - 1; i >= 0; i--)
            {
                var t = _toasts[i];
                // Pause timer while hovered by sliding ExpiresAt forward
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

            // Zero padding on the overlay so cursor pos 0 == screen edge exactly
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            if (!ImGui.Begin("##hf_toasts", Flags)) { ImGui.PopStyleVar(); ImGui.End(); return; }
            ImGui.PopStyleVar();

            float curY = (vp.WorkSize.Y - totalH) * 0.5f;
            for (int i = 0; i < _toasts.Count; i++)
            {
                float w = widths[i];
                float h = heights[i];
                // Right-align within overlay, child right edge == screen right edge
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
        // Explicit fixed width, caller knows best (e.g. content with unmeasurable glyphs)
        if (t.Width > 0f) return t.Width * s;

        // Auto-size: measure each line independently so ImGui's per-glyph measurement is
        // accurate. Toasts with plain-text content (swap character, lottery) shrink to fit.
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

        // ── Title bar (ColHighlight) ───────────────────────────────────────────
        dl.AddRectFilled(pos, new Vector2(pos.X + w, pos.Y + titleBarH), ToastU32(Theme.ColHighlight, a));

        // ── Content area (ColSecondary) ───────────────────────────────────────
        float contentTop = titleBarH;
        float progressH  = ToastProgressH * s;
        if (!string.IsNullOrEmpty(t.Message))
            dl.AddRectFilled(
                new Vector2(pos.X, pos.Y + contentTop),
                new Vector2(pos.X + w, pos.Y + sz.Y - progressH),
                ToastU32(Theme.ColSecondary, a));

        // ── X dismiss button (in title bar, vertically centred) ───────────────
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

        // ── Title text ────────────────────────────────────────────────────────
        ImGui.SetCursorPos(new Vector2(padX, titlePadY));
        ImGui.PushTextWrapPos(w - padX - xArea);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColGold with { W = a });
        ImGui.TextWrapped(t.Title);
        ImGui.PopStyleColor();
        ImGui.PopTextWrapPos();

        // ── Message text ──────────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(t.Message))
        {
            ImGui.SetCursorPos(new Vector2(padX, contentTop + contentPadY));
            ImGui.PushTextWrapPos(w - padX);
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColWhiteDim with { W = a });
            ImGui.TextWrapped(t.Message);
            ImGui.PopStyleColor();
            ImGui.PopTextWrapPos();
        }

        // ── Progress bar ──────────────────────────────────────────────────────
        float totalSec = (float)(t.ExpiresAt - t.CreatedAt).TotalSeconds;
        float elapsed  = (float)(DateTime.UtcNow - t.CreatedAt).TotalSeconds;
        float progress = t.AnimOut ? 0f : Math.Clamp(1f - elapsed / totalSec, 0f, 1f);
        float barY     = pos.Y + sz.Y - progressH;

        dl.AddRectFilled(new Vector2(pos.X, barY), new Vector2(pos.X + w, barY + progressH),
            ToastU32(Theme.ColGold, 0.15f * a));
        if (progress > 0f)
            dl.AddRectFilled(new Vector2(pos.X, barY), new Vector2(pos.X + w * progress, barY + progressH),
                ToastU32(Theme.ColGold, 0.65f * a));

        // Update hover state for next frame (used to pause the timer)
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
}
