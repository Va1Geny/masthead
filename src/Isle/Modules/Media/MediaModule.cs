using System.Windows;
using System.Windows.Threading;
using Isle.Core;
using Isle.Views.Music;

namespace Isle.Modules.Media;

/// <summary>
/// Music (module id "media"): follows the system media session, shows it as the mini player in the pill (title banner
/// on track change) and tints the accent from the album art.
/// </summary>
public sealed class MediaModule(IslandWindow island) : IModule, IDisposable
{
    const string PillId = "music", BannerId = "music.track";
    static readonly TimeSpan BannerTime = TimeSpan.FromSeconds(2.2);

    MediaSession? _session;
    MiniPlayerPill? _mini;
    readonly List<Equalizer> _compacts = [];
    DispatcherTimer? _bannerTimer;
    string? _banneredTrack;
    bool _pillShown;


    public string Id => "media";

    public void Start()
    {
        _session = new MediaSession(island.Dispatcher);
        _session.Changed += OnChanged;
        SetIdleWidth(200); // idle notch narrower than the mini player (kept at 200 when the pill grew for prev/next)
        _ = StartSessionAsync(_session);
    }

    void SetIdleWidth(double w) { island.IdleView.DesiredPillWidth = w; island.Pill.Remove("idle"); island.UpdateIdlePill(); }

    static async Task StartSessionAsync(MediaSession s)
    {
        try { await s.StartAsync(); }
        catch (Exception ex) { Logger.Error("media start (GSMTC unavailable?)", ex); }
    }

    void OnChanged()
    {
        var cur = _session?.Current;
        if (cur is null)
        {
            HidePill();
            island.Pill.Remove(BannerId);
            Theme.SetAccent(null);
            return;
        }

        Theme.SetAccent(cur.Accent);
        ShowPill(cur); // the mini player stays while paused so play/pause can resume
        foreach (var c in _compacts) c.Playing = cur.IsPlaying;
        _mini?.Update(cur);

        if (cur.IsPlaying && cur.Title.Length > 0 && cur.TrackKey != _banneredTrack)
        {
            _banneredTrack = cur.TrackKey; // a track that first shows up paused gets its banner when it starts playing
            ShowBanner(cur);
        }
    }

    void ShowPill(MediaState cur)
    {
        if (_session is null) return;
        _mini ??= new MiniPlayerPill(_session);
        _mini.Update(cur);
        if (_pillShown) return;
        _pillShown = true;
        island.Pill.Set(new PillItem
        {
            Id = PillId,
            Priority = PillPriority.Music,
            View = () => _mini,
            Compact = CreateCompact,
            Width = MiniPlayerPill.PillWidth,
            CompactWidth = 40,
        });
    }

    /// <summary>Small equalizer for the pill's side slot when something more important owns the pill.</summary>
    FrameworkElement CreateCompact()
    {
        var eq = new Equalizer(12, 2.5) { HorizontalAlignment = HorizontalAlignment.Center, Playing = _session?.Current?.IsPlaying == true };
        _compacts.Add(eq);
        if (_compacts.Count > 4) _compacts.RemoveAt(0);
        return eq;
    }

    void HidePill()
    {
        _pillShown = false;
        island.Pill.Remove(PillId);
    }

    void ShowBanner(MediaState cur)
    {
        island.Pill.Set(new PillItem
        {
            Id = BannerId,
            Priority = PillPriority.Transient,
            View = () => new TrackBanner(cur),
            Width = TrackBanner.BannerWidth,
            Height = TrackBanner.BannerHeight,
            IsBanner = true,
            Peek = true,
        });
        _bannerTimer ??= NewTimer(BannerTime, () => island.Pill.Remove(BannerId));
        _bannerTimer.Stop();
        _bannerTimer.Start();
    }

    DispatcherTimer NewTimer(TimeSpan interval, Action tick)
    {
        var t = new DispatcherTimer(DispatcherPriority.Background, island.Dispatcher) { Interval = interval };
        t.Tick += (_, _) => { t.Stop(); tick(); };
        return t;
    }

    public void Stop()
    {
        SetIdleWidth(120);
        _bannerTimer?.Stop();
        island.Pill.Remove(PillId);
        island.Pill.Remove(BannerId);
        if (_session is not null)
        {
            _session.Changed -= OnChanged;
            _session.Dispose();
            _session = null;
        }
        _mini = null;
        _compacts.Clear();
        _pillShown = false;
        Theme.SetAccent(null);
    }

    public void Dispose() { if (_session is not null) Stop(); }
}
