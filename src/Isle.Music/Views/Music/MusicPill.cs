using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Isle.Core;
using Isle.Modules.Media;
using MediaState = Isle.Modules.Media.MediaState;

namespace Isle.Views.Music;

/// <summary>The lyric line at <paramref name="now"/> for the pill ("" between lines), or null when the track has no synced lyrics.</summary>
public delegate string? LyricLine(MediaState track, DateTimeOffset now);

/// <summary>Collapsed-pill music view: art on the left, equalizer on the right, optional current lyric line between.</summary>
public sealed class MusicPill : Grid, IDisposable
{
    public const double PlainWidth = 200, LyricWidth = 340;

    readonly IMediaSource _media;
    readonly Func<LyricLine?> _lyric;
    readonly ArtImage _art = new(22, 6) { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(7, 0, 0, 0) };
    readonly Equalizer _eq = new() { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 13, 0) };
    readonly TextBlock _line = new()
    {
        FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(38, 0, 40, 0),
    };
    readonly List<Equalizer> _compacts = [];
    DispatcherTimer? _lyricTimer;
    readonly Func<DateTimeOffset> _now;

    /// <param name="lyric">Current lyric provider (Home pack), null = no lyric line.</param>
    public MusicPill(IMediaSource media, Func<LyricLine?> lyric, Func<DateTimeOffset>? now = null)
    {
        _media = media;
        _lyric = lyric;
        _now = now ?? (() => DateTimeOffset.Now);
        Children.Add(_art);
        Children.Add(_line);
        Children.Add(_eq);
        IsVisibleChanged += (_, _) => UpdateLyricTimer();
    }

    /// <summary>Pill width: wider when a lyric line is shown.</summary>
    public double PillWidth => _lyric() is null ? PlainWidth : LyricWidth;

    public void Update(MediaState s)
    {
        _art.Set(s);
        _eq.Playing = s.IsPlaying;
        foreach (var c in _compacts) c.Playing = s.IsPlaying;
        UpdateLyric();
    }

    /// <summary>Small equalizer for the pill's side slot when something more important owns the pill.</summary>
    public FrameworkElement CreateCompact()
    {
        var eq = new Equalizer(12, 2.5) { HorizontalAlignment = HorizontalAlignment.Center, Playing = _media.Current?.IsPlaying == true };
        _compacts.Add(eq);
        if (_compacts.Count > 4) _compacts.RemoveAt(0);
        return eq;
    }

    public void UpdateLyric()
    {
        var show = _media.Current is { } cur && _lyric()?.Invoke(cur, _now()) is not null;
        _line.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Tick();
        UpdateLyricTimer();
    }

    // the line only changes every few seconds; 250 ms resolution only while visible, playing and enabled
    void UpdateLyricTimer()
    {
        var needed = IsVisible && _line.Visibility == Visibility.Visible && _media.Current?.IsPlaying == true;
        if (!needed) { _lyricTimer?.Stop(); return; }
        _lyricTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        _lyricTimer.Start();
    }

    void Tick()
    {
        if (_line.Visibility != Visibility.Visible || _media.Current is not { } cur || _lyric()?.Invoke(cur, _now()) is not { } line) return;
        if (_line.Text != line) _line.Text = line;
    }

    public void Dispose() => _lyricTimer?.Stop();
}

/// <summary>
/// Music pill without the Home pack (no Music tab): art, "title — artist" and a play/pause button. Hovering the button
/// does not open the island (see IslandWindow.IsOverButton); the rest of the pill opens it as usual.
/// </summary>
public sealed class MiniPlayerPill : Grid
{
    public const double PillWidth = 250;

    readonly ArtImage _art = new(22, 6) { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(7, 0, 0, 0) };
    readonly TextBlock _text = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(38, 0, 42, 0) };
    readonly Views.Icon _icon = Ui.MakeIcon("Icon.Pause", 13, "FgBrush", filled: true);

    public MiniPlayerPill(IMediaSource media)
    {
        var play = Ui.IconButton(_icon, 28, media.PlayPause, "music play/pause");
        play.HorizontalAlignment = HorizontalAlignment.Right;
        play.Margin = new Thickness(0, 0, 8, 0);
        play.ToolTip = Strings.T("music.playPause");
        AutomationProperties.SetName(play, Strings.T("music.playPause"));
        Children.Add(_art);
        Children.Add(_text);
        Children.Add(play);
    }

    public void Update(MediaState s)
    {
        _art.Set(s);
        _text.Inlines.Clear();
        _text.Inlines.Add(new Run(s.Title.Length > 0 ? s.Title : Strings.T("music.loading", s.Source)));
        if (s.Artist.Length > 0)
        {
            var artist = new Run(" — " + s.Artist);
            artist.SetResourceReference(TextElement.ForegroundProperty, "FgDimBrush");
            _text.Inlines.Add(artist);
        }
        _icon.SetResourceReference(Views.Icon.DataProperty, s.IsPlaying ? "Icon.Pause" : "Icon.Play");
    }
}

/// <summary>Short banner shown on a track change: art, title and artist.</summary>
public sealed class TrackBanner : Grid
{
    public const double BannerWidth = 300, BannerHeight = 48;

    public TrackBanner(MediaState s)
    {
        var art = new ArtImage(34, 9) { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(7, 0, 0, 0) };
        art.Set(s);
        var title = new TextBlock { Text = s.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var artist = new TextBlock { Text = s.Artist, TextTrimming = TextTrimming.CharacterEllipsis };
        artist.SetResourceReference(StyleProperty, "TextDim");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(50, 0, 40, 0) };
        text.Children.Add(title);
        if (s.Artist.Length > 0) text.Children.Add(artist);
        var eq = new Equalizer { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 16, 0), Playing = s.IsPlaying };
        Children.Add(art);
        Children.Add(text);
        Children.Add(eq);
    }
}
