using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using Isle.Core;
using Isle.Modules.Media;
using MediaState = Isle.Modules.Media.MediaState;

namespace Isle.Views.Music;

/// <summary>
/// The mini player: art, "title — artist" and a play/pause button. Hovering the button
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
