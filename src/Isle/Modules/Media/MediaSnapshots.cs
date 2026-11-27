using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Isle.Core;
using Isle.Views.Music;

namespace Isle.Modules.Media;

/// <summary>--snapshot states for the music pill (demo track).</summary>
static class MediaSnapshots
{
    public static void Register(Dictionary<string, Action<IslandWindow>> states)
    {
        states["music-mini"] = w => Mini(w, Demo());
        states["music-mini-short"] = w => Mini(w, Demo() with { Title = "Lua", Artist = "Bright Eyes", IsPlaying = false });
        states["music-mini-loading"] = w => Mini(w, new MediaState { Title = "", Source = "Spotify", IsPlaying = true });
        states["music-banner"] = w =>
        {
            var s = Demo();
            w.Pill.Set(new PillItem { Id = "b", Priority = PillPriority.Transient, View = () => new TrackBanner(s), Width = TrackBanner.BannerWidth, Height = TrackBanner.BannerHeight, IsBanner = true });
        };
    }

    static MediaState Demo()
    {
        var art = DemoArt();
        return new MediaState
        {
            Title = "Paper Lanterns Over the Harbour",
            Artist = "The Quiet Meridian",
            Album = "Low Tide Hours",
            Source = "Spotify",
            IsPlaying = true,
            Art = art,
            Accent = ArtColors.Dominant(art),
        };
    }

    /// <summary>Original abstract "album art" drawn in code (a sunset over water).</summary>
    static BitmapSource DemoArt()
    {
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0xF2, 0x8C, 0x45), Color.FromRgb(0x8E, 0x2D, 0x5B), 90), null, new Rect(0, 0, 192, 192));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xD3, 0x6E)), null, new Point(96, 110), 42, 42);
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x2B, 0x2E, 0x6B), Color.FromRgb(0x14, 0x15, 0x33), 90), null, new Rect(0, 118, 192, 74));
            for (var i = 0; i < 5; i++)
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(120, 0xFF, 0xD3, 0x6E)), null, new Rect(66 + i * 3, 128 + i * 11, 60 - i * 6, 3));
        }
        var bmp = new RenderTargetBitmap(192, 192, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(v);
        bmp.Freeze();
        return bmp;
    }

    static void Mini(IslandWindow w, MediaState s)
    {
        Theme.SetAccent(s.Accent);
        var pill = new MiniPlayerPill(() => { }, () => { }, () => { });
        pill.Update(s);
        w.Pill.Set(new PillItem { Id = "music", Priority = PillPriority.Music, View = () => pill, Width = MiniPlayerPill.PillWidth });
    }
}
