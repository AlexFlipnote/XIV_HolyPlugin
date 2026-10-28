using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Dalamud.Plugin.Services;

namespace HoliestFluffiness.Handlers;

public enum MediaStatus { Closed = 0, Opened = 1, Changing = 2, Stopped = 3, Playing = 4, Paused = 5 }

public enum MediaAction { TogglePlayPause, Play, Pause, Next, Previous, Seek }

public sealed record MediaSnapshot(
    string AppName,
    string Title,
    string Artist,
    MediaStatus Status,
    TimeSpan Position,
    TimeSpan Duration,
    DateTime PositionUpdatedUtc,
    bool CanSeek,
    byte[]? Artwork)
{
    public bool IsPlaying => Status == MediaStatus.Playing;
    public bool HasTimeline => Duration > TimeSpan.Zero;

    // Apps only report the position when it jumps, so extrapolate from the last report while playing
    public TimeSpan LivePosition
    {
        get
        {
            if (!IsPlaying) return Position;
            var live = Position + (DateTime.UtcNow - PositionUpdatedUtc);
            if (live < TimeSpan.Zero) return TimeSpan.Zero;
            return HasTimeline && live > Duration ? Duration : live;
        }
    }

    public float Progress => HasTimeline ? (float)Math.Clamp(LivePosition / Duration, 0d, 1d) : 0f;

    public string Display => Artist.Length > 0 ? $"{Title} - {Artist}" : Title;

    public string TimeText => HasTimeline ? $"{FormatTime(LivePosition)} / {FormatTime(Duration)}" : "";

    public static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}

// Reads whatever Spotify, foobar2000, a browser, etc. is playing through Windows' global media session
// manager (the same source as the media flyout), and sends play/pause/skip back to it. All WinRT work
// runs on one MTA background thread; the game thread only reads the latest snapshot.
public sealed class MediaHandler : IDisposable
{
    private const string ManagerClass = "Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager";
    private static readonly Guid ManagerStaticsId = new("2050c4ee-11a0-57de-aed7-c97c70338245");

    // GlobalSystemMediaTransportControlsSessionManager(+Statics)
    private const int RequestManagerSlot = 6;
    private const int CurrentSessionSlot = 6;
    private const int SessionsSlot       = 7;
    // IVectorView<Session>
    private const int VectorGetAtSlot = 6;
    private const int VectorSizeSlot  = 7;
    // GlobalSystemMediaTransportControlsSession
    private const int SessionAppIdSlot       = 6;
    private const int SessionPropertiesSlot  = 7;
    private const int SessionTimelineSlot    = 8;
    private const int SessionPlaybackSlot    = 9;
    private const int SessionPlaySlot        = 10;
    private const int SessionPauseSlot       = 11;
    private const int SessionNextSlot        = 16;
    private const int SessionPreviousSlot    = 17;
    private const int SessionToggleSlot      = 20;
    private const int SessionSeekSlot        = 24;
    // ...MediaProperties
    private const int TitleSlot       = 6;
    private const int AlbumArtistSlot = 8;
    private const int ArtistSlot      = 9;
    private const int ThumbnailSlot   = 15;
    // ...TimelineProperties
    private const int TimelineStartSlot    = 6;
    private const int TimelineEndSlot      = 7;
    private const int TimelinePositionSlot = 10;
    private const int TimelineUpdatedSlot  = 11;
    // ...PlaybackInfo / PlaybackControls
    private const int PlaybackControlsSlot = 6;
    private const int PlaybackStatusSlot   = 7;
    private const int ControlsToggleSlot   = 16;
    private const int ControlsSeekSlot     = 20;

    private const int PollMs            = 1000;
    private const int AfterCommandMs    = 150;
    private const int DisabledMs        = 2000;
    private const int RequestTimeoutMs  = 5000;
    private const int PropsTimeoutMs    = 2000;
    private const int MaxSessions       = 16;
    private const int ArtworkTimeoutMs  = 3000;
    private const int MaxArtworkBytes   = 4 * 1024 * 1024;
    private const int ArtworkConfirmMs  = 2000;
    private const int ArtworkRetryMs    = 1500;
    private const int ArtworkMaxRetries = 3;

    private readonly Configuration config;
    private readonly IPluginLog log;
    private readonly string ownExe;
    private readonly ConcurrentQueue<(MediaAction Command, long Arg)> commands = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly Thread thread;
    private volatile bool stopping;
    private volatile MediaSnapshot? current;

    // Worker-thread only
    private nint manager;
    private long timelineStartTicks;
    private TimeSpan lastPosition;
    private DateTime lastPositionAt;
    private byte[]?  artwork;
    private string   artworkTrackKey = "";
    private int      artworkRetries;
    private bool     artworkConfirmed;
    private long     artworkNextFetch;

    public MediaHandler(Configuration config, IPluginLog log)
    {
        this.config = config;
        this.log    = log;
        ownExe      = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "ffxiv_dx11");

        thread = new Thread(Run) { IsBackground = true, Name = "HF media sessions" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    // False once Windows refused the session manager (Wine, stripped-down installs); stays false
    public bool IsAvailable { get; private set; } = true;

    // Null when disabled or nothing has an open media session
    public MediaSnapshot? Current => current;

    public void Send(MediaAction command, long arg = 0)
    {
        if (!config.MediaEnabled || !IsAvailable) return;
        commands.Enqueue((command, arg));
        wake.Set();
    }

    public void Seek(TimeSpan position) => Send(MediaAction.Seek, Math.Max(0, position.Ticks));

    private void Run()
    {
        var entered = WinRt.EnterMta();
        try
        {
            var wait = 0;
            while (!stopping)
            {
                if (wait > 0) wake.WaitOne(wait);
                if (stopping) break;
                try
                {
                    wait = Tick();
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "[HF] Media: poll failed");
                    wait = 5000;
                }
            }
        }
        finally
        {
            WinRt.Release(ref manager);
            if (entered) WinRt.LeaveMta();
        }
    }

    private int Tick()
    {
        if (!config.MediaEnabled || !IsAvailable)
        {
            commands.Clear();
            current = null;
            return DisabledMs;
        }

        if (manager == 0 && !TryCreateManager())
        {
            IsAvailable = false;
            current     = null;
            return DisabledMs;
        }

        var session = PickSession();
        if (session == 0) commands.Clear();
        try
        {
            var commanded = session != 0 && RunCommands(session);
            current = session == 0 ? null : Read(session);
            return commanded ? AfterCommandMs : PollMs;
        }
        finally
        {
            WinRt.Release(session);
        }
    }

    private bool TryCreateManager()
    {
        var hr = WinRt.GetActivationFactory(ManagerClass, ManagerStaticsId, out var statics);
        if (!WinRt.Succeeded(hr))
        {
            log.Info($"[HF] Media: session manager unavailable (0x{hr:X8}), media controls disabled.");
            return false;
        }

        nint operation = 0;
        try
        {
            hr = WinRt.GetPointer(statics, RequestManagerSlot, out operation);
            if (WinRt.Succeeded(hr) && WinRt.Await(operation, RequestTimeoutMs, out manager) && manager != 0)
                return true;
            log.Info($"[HF] Media: session manager request failed (0x{hr:X8}), media controls disabled.");
            return false;
        }
        finally
        {
            WinRt.Release(operation);
            WinRt.Release(statics);
        }
    }

    // Prefers a session that is actually playing, then Windows' own pick, then anything open.
    // The game itself is skipped so a phone/music plugin publishing as ffxiv_dx11 never wins.
    private nint PickSession()
    {
        if (!WinRt.Succeeded(WinRt.GetPointer(manager, SessionsSlot, out var list)) || list == 0) return 0;
        try
        {
            if (!WinRt.Succeeded(WinRt.GetInt32(list, VectorSizeSlot, out var size))) return 0;

            nint playing = 0, fallback = 0;
            var count = Math.Min(size, MaxSessions);
            for (var i = 0; i < count && playing == 0; i++)
            {
                if (!WinRt.Succeeded(WinRt.GetPointerAt(list, VectorGetAtSlot, (uint)i, out var session)) || session == 0)
                    continue;

                if (IsOwnProcess(WinRt.GetString(session, SessionAppIdSlot)))
                {
                    WinRt.Release(session);
                }
                else if (ReadStatus(session) == MediaStatus.Playing)
                {
                    playing = session;
                }
                else if (fallback == 0)
                {
                    fallback = session;
                }
                else
                {
                    WinRt.Release(session);
                }
            }

            if (playing != 0)
            {
                WinRt.Release(fallback);
                return playing;
            }

            var preferred = CurrentSession();
            if (preferred != 0)
            {
                WinRt.Release(fallback);
                return preferred;
            }
            return fallback;
        }
        finally
        {
            WinRt.Release(list);
        }
    }

    private nint CurrentSession()
    {
        if (!WinRt.Succeeded(WinRt.GetPointer(manager, CurrentSessionSlot, out var session)) || session == 0) return 0;
        if (!IsOwnProcess(WinRt.GetString(session, SessionAppIdSlot))) return session;
        WinRt.Release(session);
        return 0;
    }

    private bool IsOwnProcess(string appId) =>
        appId.Length > 0 && string.Equals(Path.GetFileNameWithoutExtension(appId), ownExe, StringComparison.OrdinalIgnoreCase);

    private bool RunCommands(nint session)
    {
        var ran = false;
        while (commands.TryDequeue(out var entry))
        {
            ran |= entry.Command switch
            {
                MediaAction.Play     => WinRt.Fire(session, SessionPlaySlot),
                MediaAction.Pause    => WinRt.Fire(session, SessionPauseSlot),
                MediaAction.Next     => WinRt.Fire(session, SessionNextSlot),
                MediaAction.Previous => WinRt.Fire(session, SessionPreviousSlot),
                MediaAction.Seek     => FireSeek(session, entry.Arg),
                _                     => TogglePlayPause(session),
            };
        }
        return ran;
    }

    // Some players only advertise separate play/pause, so fall back to whichever matches the state
    private static bool TogglePlayPause(nint session)
    {
        if (ReadControl(session, ControlsToggleSlot)) return WinRt.Fire(session, SessionToggleSlot);
        return WinRt.Fire(session, ReadStatus(session) == MediaStatus.Playing ? SessionPauseSlot : SessionPlaySlot);
    }

    private bool FireSeek(nint session, long ticks)
    {
        var hr = WinRt.GetPointerWithInt64(session, SessionSeekSlot, ticks + timelineStartTicks, out var operation);
        WinRt.Release(operation);
        return WinRt.Succeeded(hr);
    }

    private MediaSnapshot Read(nint session)
    {
        var appId  = WinRt.GetString(session, SessionAppIdSlot);
        var status = ReadStatus(session);
        var canSeek = ReadControl(session, ControlsSeekSlot);

        string title = "", artist = "";
        nint operation = 0, props = 0;
        try
        {
            if (WinRt.Succeeded(WinRt.GetPointer(session, SessionPropertiesSlot, out operation))
                && WinRt.Await(operation, PropsTimeoutMs, out props) && props != 0)
            {
                title  = WinRt.GetString(props, TitleSlot);
                artist = WinRt.GetString(props, ArtistSlot);
                if (artist.Length == 0) artist = WinRt.GetString(props, AlbumArtistSlot);
                RefreshArtwork(props, $"{appId}\n{title}\n{artist}");
            }
        }
        finally
        {
            WinRt.Release(props);
            WinRt.Release(operation);
        }

        var (position, duration, updatedUtc) = ReadTimeline(session);
        return new MediaSnapshot(AppName(appId), title, artist, status, position, duration, updatedUtc, canSeek, artwork);
    }

    // Players often publish the new title before the new cover, so a track change fetches right away,
    // checks once more shortly after, and retries a few times while nothing comes back
    private void RefreshArtwork(nint props, string trackKey)
    {
        var now = Environment.TickCount64;
        if (trackKey != artworkTrackKey)
        {
            artworkTrackKey  = trackKey;
            artworkRetries   = 0;
            artworkConfirmed = false;
            artworkNextFetch = 0;
        }
        if (now < artworkNextFetch || artworkNextFetch == long.MaxValue) return;

        var bytes = FetchArtwork(props);
        if (bytes != null)
        {
            if (artwork == null || !bytes.AsSpan().SequenceEqual(artwork)) artwork = bytes;
        }
        else if (!artworkConfirmed && artworkRetries == 0)
        {
            artwork = null;
        }

        if (!artworkConfirmed)
        {
            artworkConfirmed = true;
            artworkNextFetch = now + ArtworkConfirmMs;
        }
        else if (bytes == null && artworkRetries < ArtworkMaxRetries)
        {
            artworkRetries++;
            artworkNextFetch = now + ArtworkRetryMs;
        }
        else
        {
            artworkNextFetch = long.MaxValue;
        }
    }

    private static byte[]? FetchArtwork(nint props)
    {
        if (!WinRt.Succeeded(WinRt.GetPointer(props, ThumbnailSlot, out var thumbnail)) || thumbnail == 0) return null;
        try
        {
            return WinRt.ReadStreamReference(thumbnail, ArtworkTimeoutMs, MaxArtworkBytes);
        }
        finally
        {
            WinRt.Release(thumbnail);
        }
    }

    private (TimeSpan Position, TimeSpan Duration, DateTime UpdatedUtc) ReadTimeline(nint session)
    {
        if (!WinRt.Succeeded(WinRt.GetPointer(session, SessionTimelineSlot, out var timeline)) || timeline == 0)
            return (TimeSpan.Zero, TimeSpan.Zero, DateTime.UtcNow);

        try
        {
            WinRt.GetInt64(timeline, TimelineStartSlot,    out var start);
            WinRt.GetInt64(timeline, TimelineEndSlot,      out var end);
            WinRt.GetInt64(timeline, TimelinePositionSlot, out var position);
            WinRt.GetInt64(timeline, TimelineUpdatedSlot,  out var updated);
            timelineStartTicks = start;

            var relative = TimeSpan.FromTicks(Math.Max(0, position - start));
            var duration = TimeSpan.FromTicks(Math.Max(0, end - start));

            // Windows.Foundation.DateTime is a FILETIME; some apps leave it at zero, so stamp the
            // moment we first saw this position instead
            DateTime updatedUtc;
            if (updated > 0)
            {
                updatedUtc = DateTime.FromFileTimeUtc(updated);
            }
            else
            {
                if (relative != lastPosition) lastPositionAt = DateTime.UtcNow;
                updatedUtc = lastPositionAt;
            }
            lastPosition = relative;
            return (relative, duration, updatedUtc);
        }
        finally
        {
            WinRt.Release(timeline);
        }
    }

    private static MediaStatus ReadStatus(nint session)
    {
        if (!WinRt.Succeeded(WinRt.GetPointer(session, SessionPlaybackSlot, out var info)) || info == 0)
            return MediaStatus.Closed;
        try
        {
            return WinRt.Succeeded(WinRt.GetInt32(info, PlaybackStatusSlot, out var status))
                ? (MediaStatus)status
                : MediaStatus.Closed;
        }
        finally
        {
            WinRt.Release(info);
        }
    }

    private static bool ReadControl(nint session, int slot)
    {
        if (!WinRt.Succeeded(WinRt.GetPointer(session, SessionPlaybackSlot, out var info)) || info == 0) return false;
        nint controls = 0;
        try
        {
            return WinRt.Succeeded(WinRt.GetPointer(info, PlaybackControlsSlot, out controls))
                && controls != 0
                && WinRt.GetBool(controls, slot);
        }
        finally
        {
            WinRt.Release(controls);
            WinRt.Release(info);
        }
    }

    // "Spotify.exe" -> "Spotify", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" -> "Spotify"
    private static string AppName(string appId)
    {
        var bang = appId.LastIndexOf('!');
        var name = bang >= 0 ? appId[(bang + 1)..] : Path.GetFileNameWithoutExtension(appId);
        return name switch
        {
            "" => "Media",
            "MSEdge" => "Edge",
            _ when name.Length > 1 && char.IsLower(name[0]) => char.ToUpperInvariant(name[0]) + name[1..],
            _ => name,
        };
    }

    public void Dispose()
    {
        stopping = true;
        wake.Set();
        if (thread.Join(3000))
            wake.Dispose();
        else
            log.Warning("[HF] Media: worker did not stop within 3s");
    }
}
