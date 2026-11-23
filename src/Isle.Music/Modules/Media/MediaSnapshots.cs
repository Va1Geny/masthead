using Isle.Core;
using Isle.Views.Music;

namespace Isle.Modules.Media;

/// <summary>--snapshot states for the music pill (fake source, fixed clock). The Home pack adds the Music tab states.</summary>
static class MediaSnapshots
{
    internal static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public static void Register(Dictionary<string, Action<IslandWindow>> states)
    {
        states["music-pill"] = w => Pill(w, Demo(), () => null);
        states["music-mini"] = w => Mini(w, Demo());
        states["music-mini-short"] = w => Mini(w, Demo() with { Title = "Lua", Artist = "Bright Eyes", IsPlaying = false });
        states["music-mini-loading"] = w => Mini(w, new MediaState { Title = "", Source = "Spotify", IsPlaying = true });
        states["music-banner"] = w =>
        {
            var s = Demo();
            w.Pill.Set(new PillItem { Id = "b", Priority = PillPriority.Transient, View = () => new TrackBanner(s), Width = TrackBanner.BannerWidth, Height = TrackBanner.BannerHeight, IsBanner = true });
        };
    }

    internal static MediaState Demo() => FakeMediaSource.Demo(Now);

    static void Mini(IslandWindow w, MediaState s)
    {
        Theme.SetAccent(s.Accent);
        var pill = new MiniPlayerPill(new FakeMediaSource { Current = s });
        pill.Update(s);
        w.Pill.Set(new PillItem { Id = "music", Priority = PillPriority.Music, View = () => pill, Width = MiniPlayerPill.PillWidth });
    }

    internal static void Pill(IslandWindow w, MediaState s, Func<LyricLine?> lyric)
    {
        Theme.SetAccent(s.Accent);
        var pill = new MusicPill(new FakeMediaSource { Current = s }, lyric, () => Now);
        pill.Update(s);
        w.Pill.Set(new PillItem { Id = "music", Priority = PillPriority.Music, View = () => pill, Width = pill.PillWidth });
    }
}
