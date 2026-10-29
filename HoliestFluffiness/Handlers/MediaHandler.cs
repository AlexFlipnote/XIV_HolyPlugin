using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Dalamud.Plugin.Services;
using Microsoft.Win32;

namespace HoliestFluffiness.Handlers;

public enum MediaStatus { Closed = 0, Opened = 1, Changing = 2, Stopped = 3, Playing = 4, Paused = 5 }

public enum MediaAction { TogglePlayPause, Play, Pause, Next, Previous, Seek, Volume }

public sealed record MediaSnapshot(
    string AppName,
    string Title,
    string Artist,
    MediaStatus Status,
    TimeSpan Position,
    TimeSpan Duration,
    DateTime PositionUpdatedUtc,
    bool CanSeek,
    byte[]? Artwork,
    float? Volume)
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
    private const int TimelineSampleMs  = 15;
    private static readonly TimeSpan CachedTimelineGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SeekMarkerWindow    = TimeSpan.FromMilliseconds(500);
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
    // Volume commands carry the level in thousandths
    private const float VolumeScale     = 1000f;

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
    private string   timelineAppId = "";
    private bool     timelineMissing;
    private (TimeSpan Position, TimeSpan Duration, DateTime UpdatedUtc, bool Playing)? lastTimeline;
    private byte[]?  artwork;
    private string   artworkTrackKey = "";
    private int      artworkRetries;
    private bool     artworkConfirmed;
    private long     artworkNextFetch;
    private readonly Dictionary<string, string?> mozillaProducts = new();
    private readonly AppVolume appVolume = new();
    private string   verifiedAppId = "";
    private (string Title, string Artist, byte[]? Artwork)? verifiedTrack;

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

    // 0..1 of the playing app's own volume, as in the Windows Volume Mixer
    public void SetVolume(float volume) =>
        Send(MediaAction.Volume, (long)MathF.Round(Math.Clamp(volume, 0f, 1f) * VolumeScale));

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
            appVolume.Dispose();
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
            if (commanded) return AfterCommandMs;
            if (!timelineMissing) return PollMs;
            WatchTimeline(session);
            return 0;
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

    // Firefox only holds a real timeline for 30-100ms (on track start, pause/play and seek) before clearing
    // it again, so while it is missing, sample just the timeline every TimelineSampleMs for one poll
    // interval instead of sleeping. Returns early when a command wakes us.
    private void WatchTimeline(nint session)
    {
        var until = Environment.TickCount64 + PollMs;
        while (Environment.TickCount64 < until)
        {
            if (wake.WaitOne(TimelineSampleMs) || stopping) return;
            if (current is not { } snap) return;

            var status = ReadStatus(session);
            var (position, duration, updatedUtc) = StableTimeline(ReadTimeline(session), status, timelineAppId);
            if (status != snap.Status || position != snap.Position || duration != snap.Duration)
                current = snap with { Status = status, Position = position, Duration = duration, PositionUpdatedUtc = updatedUtc };
        }
    }

    private bool IsOwnProcess(string appId) =>
        appId.Length > 0 && string.Equals(Path.GetFileNameWithoutExtension(appId), ownExe, StringComparison.OrdinalIgnoreCase);

    private bool RunCommands(nint session)
    {
        var ran = false;
        while (commands.TryDequeue(out var entry))
        {
            // A drag queues a volume per step; only the last one still waiting matters
            if (entry.Command == MediaAction.Volume && commands.TryPeek(out var next) && next.Command == MediaAction.Volume)
                continue;

            ran |= entry.Command switch
            {
                MediaAction.Play     => WinRt.Fire(session, SessionPlaySlot),
                MediaAction.Pause    => WinRt.Fire(session, SessionPauseSlot),
                MediaAction.Next     => WinRt.Fire(session, SessionNextSlot),
                MediaAction.Previous => WinRt.Fire(session, SessionPreviousSlot),
                MediaAction.Seek     => FireSeek(session, entry.Arg),
                MediaAction.Volume   => appVolume.Write(ProcessName(WinRt.GetString(session, SessionAppIdSlot)), entry.Arg / VolumeScale),
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

        var (position, duration, updatedUtc) = StableTimeline(ReadTimeline(session), status, appId);
        var (shownTitle, shownArtist, shownArtwork) = VerifiedTrack(appId, title, artist);
        var volume = appVolume.Read(ProcessName(appId));
        return new MediaSnapshot(AppName(appId), shownTitle, shownArtist, status, position, duration, updatedUtc, canSeek, shownArtwork, volume);
    }

    // The process behind a media session: "Spotify.exe" and Spotify's store id both give "Spotify",
    // Firefox's install hash gives "Firefox", and process names compare without case
    private string ProcessName(string appId)
    {
        var name = BaseName(appId);
        return MozillaProduct(name) ?? name;
    }

    // YouTube's hover previews rewrite Firefox's title, artist and cover, and never put the playing video
    // back afterwards. The window title still names the real tab, so for Mozilla apps only trust metadata
    // that matches an open window and otherwise keep showing the last one that did.
    private (string Title, string Artist, byte[]? Artwork) VerifiedTrack(string appId, string title, string artist)
    {
        if (appId != verifiedAppId)
        {
            verifiedAppId = appId;
            verifiedTrack = null;
        }
        if (MozillaProduct(BaseName(appId)) == null) return (title, artist, artwork);

        if (title.Length > 0 && MozillaWindowTitles().Any(w => w.Contains(title, StringComparison.Ordinal)))
        {
            verifiedTrack = (title, artist, artwork);
            return (title, artist, artwork);
        }
        return verifiedTrack ?? (title, artist, artwork);
    }

    private static List<string> MozillaWindowTitles()
    {
        var titles = new List<string>();
        var buffer = new StringBuilder(512);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (GetClassName(hwnd, buffer, buffer.Capacity) == 0 || buffer.ToString() != "MozillaWindowClass") return true;
            if (GetWindowText(hwnd, buffer, buffer.Capacity) > 0) titles.Add(buffer.ToString());
            return true;
        }, 0);
        return titles;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);

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

    // Firefox clears the timeline to 0/0 30-100ms after every real one (track start, pause/play, seek), so
    // keep extrapolating the last good one. Keyed on the app, not the title: YouTube's hover previews
    // rewrite Firefox's title/artist without touching the playing video. A play/pause flip while on the
    // cached copy re-anchors it at its live position so the bar freezes or resumes in place, and it is
    // dropped once it runs past the end without a fresh report.
    private (TimeSpan Position, TimeSpan Duration, DateTime UpdatedUtc) StableTimeline(
        (TimeSpan Position, TimeSpan Duration, DateTime UpdatedUtc) read, MediaStatus status, string appId)
    {
        var playing = status == MediaStatus.Playing;
        if (appId != timelineAppId)
        {
            timelineAppId = appId;
            lastTimeline  = null;
        }

        timelineMissing = read.Duration <= TimeSpan.Zero && (playing || status == MediaStatus.Paused);
        if (!timelineMissing)
        {
            lastTimeline = read.Duration > TimeSpan.Zero ? (read.Position, read.Duration, read.UpdatedUtc, playing) : null;
            return read;
        }
        if (lastTimeline is not { } cached) return read;

        var now  = DateTime.UtcNow;
        var live = cached.Playing ? cached.Position + (now - cached.UpdatedUtc) : cached.Position;
        if (live > cached.Duration + CachedTimelineGrace)
        {
            lastTimeline = null;
            return read;
        }
        if (cached.Playing != playing)
        {
            // Firefox announces a seek as Paused at the old position, then Playing at the new one for
            // ~30ms, and sometimes skips that second report. Paused flipping straight back to an empty
            // Playing means the position is unknown until the next real report, so show none.
            if (playing && now - cached.UpdatedUtc < SeekMarkerWindow)
            {
                lastTimeline = null;
                return read;
            }
            cached       = (live > cached.Duration ? cached.Duration : live, cached.Duration, now, playing);
            lastTimeline = cached;
        }
        return (cached.Position, cached.Duration, cached.UpdatedUtc);
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
            if (end > start) timelineStartTicks = start;

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

    // "Spotify.exe" -> "Spotify", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" -> "Spotify",
    // "308046B0AF4A39CB" -> "Firefox"
    private string AppName(string appId)
    {
        var name = BaseName(appId);
        name = MozillaProduct(name) ?? name;
        return name switch
        {
            "" => "Media",
            "MSEdge" => "Edge",
            _ when name.Length > 1 && char.IsLower(name[0]) => char.ToUpperInvariant(name[0]) + name[1..],
            _ => name,
        };
    }

    private static string BaseName(string appId)
    {
        var bang = appId.LastIndexOf('!');
        return bang >= 0 ? appId[(bang + 1)..] : Path.GetFileNameWithoutExtension(appId);
    }

    // Product name ("Firefox") when the id belongs to a Mozilla app, else null; cached per id
    private string? MozillaProduct(string name)
    {
        if (!mozillaProducts.TryGetValue(name, out var product))
            mozillaProducts[name] = product = MozillaAppName(name);
        return product;
    }

    // Firefox (and Thunderbird, forks) publish a hash of the install path as their app id and list it
    // under SOFTWARE\Mozilla\<Product>\TaskBarIDs as "<install dir>" = "<hash>"
    private static string? MozillaAppName(string appId)
    {
        if (appId.Length != 16 || !appId.All(Uri.IsHexDigit)) return null;
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var mozilla = root.OpenSubKey(@"SOFTWARE\Mozilla");
                if (mozilla == null) continue;
                foreach (var product in mozilla.GetSubKeyNames())
                {
                    using var ids = mozilla.OpenSubKey($@"{product}\TaskBarIDs");
                    if (ids == null) continue;
                    foreach (var dir in ids.GetValueNames())
                    {
                        if (string.Equals(ids.GetValue(dir) as string, appId, StringComparison.OrdinalIgnoreCase))
                            return product;
                    }
                }
            }
            catch (Exception)
            {
                // Unreadable hive; fall back to the raw id
            }
        }
        return null;
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
