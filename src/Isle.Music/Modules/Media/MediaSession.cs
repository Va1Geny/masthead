using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Isle.Core;
using Windows.Media.Control;
using GsmtcManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using GsmtcSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace Isle.Modules.Media;

/// <summary>What views need from the media source (real: <see cref="MediaSession"/>; fakes for snapshots/tests).</summary>
public interface IMediaSource
{
    /// <summary>Current track, or null when nothing is available. UI thread.</summary>
    MediaState? Current { get; }
    /// <summary>Raised on the UI thread when <see cref="Current"/> changes.</summary>
    event Action? Changed;
    void PlayPause();
    void Next();
    void Previous();
    void Seek(TimeSpan position);
}

/// <summary>
/// Windows Global System Media Transport Controls, fully event-driven: manager (current session, sessions list) and
/// the chosen session (media properties, playback info, timeline). All WinRT callbacks are marshalled to the UI thread.
/// </summary>
public sealed class MediaSession : IMediaSource, IDisposable
{
    readonly Dispatcher _ui;
    GsmtcManager? _manager;
    GsmtcSession? _session;
    readonly Dictionary<string, DateTimeOffset> _lastActive = [];
    MediaState? _current;
    string _artKey = "";
    BitmapSource? _art;
    System.Windows.Media.Color _artAccent;
    int _refreshVersion;
    bool _disposed;

    public MediaState? Current => _current;
    /// <summary>Publish an empty-title state while a session has no track yet (the mini player shows "App — loading…").</summary>
    public bool ShowLoading { get; set; }
    public event Action? Changed;

    public MediaSession(Dispatcher ui) => _ui = ui;

    public async Task StartAsync()
    {
        _manager = await GsmtcManager.RequestAsync();
        if (_disposed) { _manager = null; return; }
        Logger.Info("media: session manager ready");
        _manager.CurrentSessionChanged += OnManagerChanged;
        _manager.SessionsChanged += OnManagerChanged;
        Choose();
    }

    void OnManagerChanged(GsmtcManager sender, object args) => _ui.BeginInvoke(Choose);

    /// <summary>Picks the session to follow and (re)subscribes to it. UI thread.</summary>
    void Choose()
    {
        if (_disposed || _manager is null) return;
        try
        {
            var sessions = _manager.GetSessions();
            var current = _manager.GetCurrentSession();
            var infos = new List<(bool, bool, DateTimeOffset)>(sessions.Count);
            foreach (var s in sessions)
            {
                var playing = IsPlaying(s);
                var id = s.SourceAppUserModelId ?? "";
                if (playing || !_lastActive.ContainsKey(id)) _lastActive[id] = DateTimeOffset.Now;
                infos.Add((playing, current is not null && s.SourceAppUserModelId == current.SourceAppUserModelId, _lastActive[id]));
            }
            var index = PlaybackMath.Choose(infos);
            var chosen = index >= 0 ? sessions[index] : null;
            if (!SameSession(chosen, _session))
            {
                Unsubscribe();
                _session = chosen;
                Logger.Info($"media: following {(chosen is null ? "nothing" : PlaybackMath.FriendlySource(chosen.SourceAppUserModelId))} ({sessions.Count} session(s))");
                if (_session is not null)
                {
                    _session.MediaPropertiesChanged += OnSessionChanged;
                    _session.PlaybackInfoChanged += OnSessionChanged;
                    _session.TimelinePropertiesChanged += OnSessionChanged;
                }
            }
            Refresh();
        }
        catch (Exception ex) { Logger.Error("media choose session", ex); }
    }

    static bool SameSession(GsmtcSession? a, GsmtcSession? b) =>
        ReferenceEquals(a, b);

    static bool IsPlaying(GsmtcSession s)
    {
        try { return s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch { return false; }
    }

    void OnSessionChanged(GsmtcSession sender, object args) => _ui.BeginInvoke(() =>
    {
        // another session may have started playing: re-evaluate the choice on play-state changes
        if (args is PlaybackInfoChangedEventArgs) Choose();
        else Refresh();
    });

    void Unsubscribe()
    {
        if (_session is null) return;
        try
        {
            _session.MediaPropertiesChanged -= OnSessionChanged;
            _session.PlaybackInfoChanged -= OnSessionChanged;
            _session.TimelinePropertiesChanged -= OnSessionChanged;
        }
        catch (Exception ex) { Logger.Error("media unsubscribe", ex); }
        _session = null;
    }

    /// <summary>Reads the session into a new <see cref="MediaState"/>. Async (media properties), latest call wins.</summary>
    async void Refresh()
    {
        var version = ++_refreshVersion;
        var s = _session;
        if (s is null) { Publish(null); return; }
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            if (version != _refreshVersion || _disposed) return;
            var playback = s.GetPlaybackInfo();
            var timeline = s.GetTimelineProperties();
            var title = props?.Title ?? "";
            if (title.Length == 0)
            {
                Logger.Info("media: session has no title yet");
                Publish(ShowLoading ? new MediaState { Title = "", Source = PlaybackMath.FriendlySource(s.SourceAppUserModelId) } : null);
                return;
            }
            var artist = props?.Artist ?? "";
            if (artist.Length == 0) artist = props?.AlbumArtist ?? "";
            var album = props?.AlbumTitle ?? "";

            var artKey = $"{s.SourceAppUserModelId}|{artist}|{title}|{album}";
            if (artKey != _artKey)
            {
                _artKey = artKey;
                _art = await LoadArtAsync(props?.Thumbnail);
                if (version != _refreshVersion || _disposed) return;
                _artAccent = _art is not null ? ArtColors.Dominant(_art) : ArtColors.Readable(ArtColors.PlaceholderColors(title).A);
            }

            var duration = timeline is null ? TimeSpan.Zero : timeline.EndTime - timeline.StartTime;
            var controls = playback?.Controls;
            Publish(new MediaState
            {
                Title = title,
                Artist = artist,
                Album = album,
                Source = PlaybackMath.FriendlySource(s.SourceAppUserModelId),
                IsPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                CanSeek = controls?.IsPlaybackPositionEnabled == true && duration > TimeSpan.Zero,
                CanNext = controls?.IsNextEnabled ?? true,
                CanPrev = controls?.IsPreviousEnabled ?? true,
                Duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration,
                Position = timeline is null ? TimeSpan.Zero : timeline.Position - timeline.StartTime,
                PositionStamp = timeline?.LastUpdatedTime ?? DateTimeOffset.Now,
                Rate = playback?.PlaybackRate ?? 1.0,
                Art = _art,
                Accent = _artAccent,
            });
        }
        catch (Exception ex)
        {
            Logger.Error("media refresh", ex);
        }
    }

    static async Task<BitmapSource?> LoadArtAsync(Windows.Storage.Streams.IRandomAccessStreamReference? thumb)
    {
        if (thumb is null) return null;
        try
        {
            using var ras = await thumb.OpenReadAsync();
            using var stream = ras.AsStreamForRead();
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = 192; // enough for the 104-px art at 175% scale; keeps memory small
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception ex)
        {
            Logger.Warn($"album art unavailable: {ex.GetType().Name}");
            return null;
        }
    }

    void Publish(MediaState? state)
    {
        if (Equals(state, _current)) return;
        _current = state;
        try { Changed?.Invoke(); }
        catch (Exception ex) { Logger.Error("media changed handler", ex); }
    }

    // ---- commands (fire and forget; failures are logged) ----

    public void PlayPause() => Run(s => s.TryTogglePlayPauseAsync().AsTask(), "play/pause");
    public void Next() => Run(s => s.TrySkipNextAsync().AsTask(), "next");
    public void Previous() => Run(s => s.TrySkipPreviousAsync().AsTask(), "previous");

    public void Seek(TimeSpan position)
    {
        if (_current is not null)
        {
            // show the new position immediately; the app's timeline event confirms it later
            Publish(_current with { Position = position, PositionStamp = DateTimeOffset.Now });
        }
        Run(s =>
        {
            var start = s.GetTimelineProperties()?.StartTime ?? TimeSpan.Zero;
            return s.TryChangePlaybackPositionAsync((start + position).Ticks).AsTask();
        }, "seek");
    }

    async void Run(Func<GsmtcSession, Task<bool>> action, string what)
    {
        var s = _session;
        if (s is null) return;
        try { await action(s); }
        catch (Exception ex) { Logger.Error($"media {what}", ex); }
    }

    public void Dispose()
    {
        _disposed = true;
        Unsubscribe();
        if (_manager is not null)
        {
            try
            {
                _manager.CurrentSessionChanged -= OnManagerChanged;
                _manager.SessionsChanged -= OnManagerChanged;
            }
            catch { /* shutting down */ }
            _manager = null;
        }
        _current = null;
        _art = null;
    }
}
