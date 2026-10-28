using System;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;

namespace HoliestFluffiness.Handlers;

// The current song as a server info bar entry, next to ping and FPS
public sealed class MediaDtrHandler : IDisposable
{
    private const int MaxLength = 60;

    private readonly Configuration config;
    private readonly MediaHandler  media;
    private readonly IFramework    framework;
    private readonly IClientState  clientState;
    private readonly IDtrBarEntry  entry;

    private string lastText    = "";
    private string lastTooltip = "";

    public MediaDtrHandler(Configuration config, MediaHandler media, IDtrBar dtrBar, IFramework framework, IClientState clientState)
    {
        this.config      = config;
        this.media       = media;
        this.framework   = framework;
        this.clientState = clientState;

        entry       = dtrBar.Get("[HF] Media");
        entry.Shown = false;

        framework.Update += OnUpdate;
    }

    public void SetClickAction(Action action) => entry.OnClick = _ => action();

    private void OnUpdate(IFramework fw)
    {
        var song  = media.Current;
        var shown = config.MediaEnabled && config.MediaDtrEnabled && clientState.IsLoggedIn && song is { Title.Length: > 0 };
        if (entry.Shown != shown) entry.Shown = shown;
        if (!shown || song == null) return;

        // Note and double bar are both in the game font, so they render like the rest of the bar
        var display = Format(config.MediaDtrFormat, song);
        if (display.Length > MaxLength) display = display[..(MaxLength - 3)].TrimEnd() + "...";
        var text = $"{(song.IsPlaying ? "♪" : "II")} {display}";
        if (text != lastText)
        {
            entry.Text = text;
            lastText   = text;
        }

        var time    = song.HasTimeline ? $"\n{song.TimeText}" : "";
        var tooltip = $"{song.Display}\n{(song.IsPlaying ? "Playing" : "Paused")} in {song.AppName}{time}";
        if (tooltip != lastTooltip)
        {
            entry.Tooltip = tooltip;
            lastTooltip   = tooltip;
        }
    }

    // Fills the user's template; separators left dangling by an empty token (no artist, no timeline)
    // are trimmed, and an empty result falls back to the song name
    internal static string Format(string template, MediaSnapshot song)
    {
        if (string.IsNullOrWhiteSpace(template)) template = Configuration.DefaultMediaDtrFormat;
        var text = template
            .Replace("<song>",   song.Title,    StringComparison.OrdinalIgnoreCase)
            .Replace("<artist>", song.Artist,   StringComparison.OrdinalIgnoreCase)
            .Replace("<app>",    song.AppName,  StringComparison.OrdinalIgnoreCase)
            .Replace("<time>",   song.TimeText, StringComparison.OrdinalIgnoreCase)
            .Trim(' ', '-', '|', ',', ':', '/');
        return text.Length > 0 ? text : song.Title;
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        entry.Remove();
    }
}
