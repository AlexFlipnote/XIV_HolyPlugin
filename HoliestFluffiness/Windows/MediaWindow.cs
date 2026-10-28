using System;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using HoliestFluffiness.Handlers;

namespace HoliestFluffiness.Windows;

public class MediaWindow : Window, IDisposable
{
    private const float BtnW         = 44f;
    private const float Width        = 260f;
    private const float ArtMaxHeight = 64f;
    private const float ArtMaxWidth  = ArtMaxHeight * 16f / 9f;
    private const float ArtGap       = 10f;
    private const float ArtRounding  = 4f;

    // How long a released scrub keeps showing its target, so the bar does not snap back
    // to the old position before the player reports the new one
    private static readonly TimeSpan SeekHold = TimeSpan.FromSeconds(1.5);

    private readonly MediaHandler     media;
    private readonly ITextureProvider textures;

    private bool     scrubbing;
    private float    scrubFraction;
    private DateTime seekShownUntil;

    // The cover on screen stays until the next one has finished loading, so skipping never flashes empty
    private byte[]?                    artSource;
    private Task<IDalamudTextureWrap>? artLoading;
    private IDalamudTextureWrap?       art;

    public MediaWindow(MediaHandler media, ITextureProvider textures)
        : base("Media###MediaPopup",
               ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse)
    {
        this.media         = media;
        this.textures      = textures;
        RespectCloseHotkey = true;
    }

    public override void PreDraw()
    {
        // The ### suffix keeps the window identity fixed while the visible title follows the player
        var song = media.Current;
        WindowName = song is { Title.Length: > 0 }
            ? $"{(song.IsPlaying ? "Playing" : "Paused")} in {song.AppName}###MediaPopup"
            : "Media###MediaPopup";

        // Like Nearby, ImGui saves the position; centring only applies before anything has been saved
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
        Common.PushPopupTheme();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 12));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
    }

    public override void PostDraw()
    {
        Common.PopPopupTheme();
        ImGui.PopStyleVar(2);
    }

    public override void Draw()
    {
        var song = media.Current;
        if (song == null || song.Title.Length == 0)
        {
            Common.DimmedText("Nothing is playing right now.");
            ImGui.Dummy(new Vector2(Width, 4));
            return;
        }

        DrawHeader(song, GetArt(song.Artwork));

        ImGui.Dummy(new Vector2(Width, 4));

        if (song.HasTimeline)
        {
            DrawTimeline(song);
            ImGui.Dummy(new Vector2(0, 4));
        }

        Common.CenterCursorForWidth(BtnW * 3 + 16f);
        DrawControls(song);
    }

    // Cover on the left, song and artist beside it on one line each, cut with "..." when too long
    private static void DrawHeader(MediaSnapshot song, IDalamudTextureWrap? cover)
    {
        var start  = ImGui.GetCursorScreenPos();
        var lineH  = ImGui.GetTextLineHeightWithSpacing();
        var blockH = song.Artist.Length > 0 ? lineH * 2 : lineH;
        var textX  = 0f;
        var height = blockH;

        if (cover != null)
        {
            var size = ArtSize(cover);
            DrawArt(cover, start, size);
            textX  = size.X + ArtGap;
            height = Math.Max(size.Y, blockH);
        }

        var textWidth = Width - textX;
        var textY     = start.Y + (height - blockH) / 2f;
        ImGui.SetCursorScreenPos(new Vector2(start.X + textX, textY));
        ImGui.TextColored(Theme.ColGold, Ellipsize(song.Title, textWidth));
        if (song.Artist.Length > 0)
        {
            ImGui.SetCursorScreenPos(new Vector2(start.X + textX, textY + lineH));
            ImGui.TextColored(Theme.ColWhiteDim, Ellipsize(song.Artist, textWidth));
        }

        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(Width, height));
    }

    // Capped height keeps the window from jumping in size between square covers and 16:9 thumbnails
    private static Vector2 ArtSize(IDalamudTextureWrap cover)
    {
        var aspect = cover.Width / (float)Math.Max(1, cover.Height);
        return new Vector2(Math.Min(ArtMaxHeight * aspect, ArtMaxWidth), Math.Min(ArtMaxHeight, ArtMaxWidth / aspect));
    }

    private static void DrawArt(IDalamudTextureWrap cover, Vector2 start, Vector2 size) =>
        ImGui.GetWindowDrawList().AddImageRounded(cover.Handle, start, start + size, Vector2.Zero, Vector2.One,
            ImGui.ColorConvertFloat4ToU32(Vector4.One), ArtRounding);

    private static string Ellipsize(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width) return text;
        for (var len = text.Length - 1; len > 0; len--)
        {
            var cut = text[..len].TrimEnd() + "...";
            if (ImGui.CalcTextSize(cut).X <= width) return cut;
        }
        return "...";
    }

    private IDalamudTextureWrap? GetArt(byte[]? bytes)
    {
        if (!ReferenceEquals(bytes, artSource))
        {
            artSource = bytes;
            DropLoading();
            if (bytes == null)
            {
                art?.Dispose();
                art = null;
            }
            else
            {
                artLoading = textures.CreateFromImageAsync(bytes);
            }
        }

        if (artLoading is { IsCompleted: true } done)
        {
            artLoading = null;
            if (done.IsCompletedSuccessfully)
            {
                art?.Dispose();
                art = done.Result;
            }
            else
            {
                _ = done.Exception;
            }
        }
        return art;
    }

    // A load still in flight when the song changes again gets its texture freed once it lands
    private void DropLoading()
    {
        if (artLoading == null) return;
        artLoading.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); });
        artLoading = null;
    }

    public void Dispose()
    {
        DropLoading();
        art?.Dispose();
        art = null;
    }

    // Click or hold-and-drag on the bar to scrub; the seek is sent once the mouse is released
    private void DrawTimeline(MediaSnapshot song)
    {
        var pos      = ImGui.GetCursorScreenPos();
        var size     = new Vector2(Width, ImGui.GetFrameHeight());
        var showSeek = scrubbing || DateTime.UtcNow < seekShownUntil;
        var fraction = showSeek ? scrubFraction : song.Progress;
        var label    = showSeek
            ? $"{MediaSnapshot.FormatTime(song.Duration * scrubFraction)} / {MediaSnapshot.FormatTime(song.Duration)}"
            : song.TimeText;

        // Gold fill to match the rest of the plugin; the time is drawn with an outline on top so it
        // stays readable over both the gold and the dark part of the bar
        if (Theme.UseCustom)
        {
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, Theme.ColGold);
            ImGui.PushStyleColor(ImGuiCol.FrameBg,       Theme.ColGoldSub);
        }
        ImGui.ProgressBar(fraction, size, "");
        if (Theme.UseCustom) ImGui.PopStyleColor(2);

        var labelSize = ImGui.CalcTextSize(label);
        Common.DrawTextShadowed(ImGui.GetWindowDrawList(), label, pos + (size - labelSize) / 2f);

        if (!song.CanSeek) return;

        // An invisible button on top makes the drag ours instead of moving the window
        ImGui.SetCursorScreenPos(pos);
        ImGui.InvisibleButton("##hfmediascrub", size);
        if (ImGui.IsItemActive())
        {
            scrubbing     = true;
            scrubFraction = Math.Clamp((ImGui.GetMousePos().X - pos.X) / size.X, 0f, 1f);
        }
        else if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Click or drag to seek");
        }

        if (scrubbing && ImGui.IsItemDeactivated())
        {
            scrubbing      = false;
            seekShownUntil = DateTime.UtcNow + SeekHold;
            media.Seek(song.Duration * scrubFraction);
        }
    }

    private void DrawControls(MediaSnapshot song)
    {
        var size = new Vector2(BtnW, 0);

        Common.PushGreyButton();
        if (ImGui.Button("<<##hfmediaprev", size))
            media.Send(MediaAction.Previous);
        Common.PopGreyButton();

        ImGui.SameLine(0, 8f);
        // Green while playing, gold while paused, so the state reads at a glance
        if (song.IsPlaying) Common.PushGreenButton(); else Common.PushGoldButton();
        if (ImGui.Button((song.IsPlaying ? "II" : ">") + "##hfmediatoggle", size))
            media.Send(MediaAction.TogglePlayPause);
        if (song.IsPlaying) Common.PopGreenButton(); else Common.PopGoldButton();

        ImGui.SameLine(0, 8f);
        Common.PushGreyButton();
        if (ImGui.Button(">>##hfmedianext", size))
            media.Send(MediaAction.Next);
        Common.PopGreyButton();
    }
}
