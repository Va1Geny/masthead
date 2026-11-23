using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Isle.Modules.Media;

/// <summary>Immutable snapshot of the chosen media session. Position is extrapolated with <see cref="PositionAt"/>.</summary>
public sealed record MediaState
{
    public required string Title { get; init; }
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    /// <summary>Friendly source name ("Spotify", "Chrome").</summary>
    public string Source { get; init; } = "";
    public bool IsPlaying { get; init; }
    public bool CanSeek { get; init; }
    public bool CanPrev { get; init; } = true;
    public bool CanNext { get; init; } = true;
    public TimeSpan Duration { get; init; }
    /// <summary>Position reported by the app at <see cref="PositionStamp"/>.</summary>
    public TimeSpan Position { get; init; }
    public DateTimeOffset PositionStamp { get; init; }
    public double Rate { get; init; } = 1.0;
    /// <summary>Album art (frozen), or null — views then draw <see cref="ArtColors.Placeholder"/>.</summary>
    public BitmapSource? Art { get; init; }
    /// <summary>Accent colour taken from the art (or from the title hash when there is no art).</summary>
    public Color Accent { get; init; }

    /// <summary>Identity of the track (not of its play state) — changes mean "new track".</summary>
    public string TrackKey => $"{Artist}\u001f{Title}\u001f{Album}";

    public TimeSpan PositionAt(DateTimeOffset now) => PlaybackMath.Extrapolate(Position, PositionStamp, now, IsPlaying, Rate, Duration);
}

public static class PlaybackMath
{
    /// <summary>Local position estimate between the app's (rare) timeline updates, clamped to [0, duration].</summary>
    public static TimeSpan Extrapolate(TimeSpan position, DateTimeOffset stamp, DateTimeOffset now, bool playing, double rate, TimeSpan duration)
    {
        var p = position;
        if (playing && stamp != default)
        {
            var elapsed = now - stamp;
            if (elapsed > TimeSpan.Zero) p += TimeSpan.FromTicks((long)(elapsed.Ticks * (rate <= 0 ? 1 : rate)));
        }
        if (p < TimeSpan.Zero) p = TimeSpan.Zero;
        if (duration > TimeSpan.Zero && p > duration) p = duration;
        return p;
    }

    /// <summary>"3:07", or "1:02:03" for long media.</summary>
    public static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    /// <summary>Friendly app name from an AppUserModelId: "Spotify.exe" → "Spotify", "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" → "Media Player".</summary>
    public static string FriendlySource(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return "";
        var id = appId.Trim();
        var lower = id.ToLowerInvariant();
        foreach (var (needle, name) in KnownApps)
            if (lower.Contains(needle)) return name;
        var bang = id.LastIndexOf('!');
        if (bang >= 0) id = id[(bang + 1)..];
        var slash = id.LastIndexOfAny(['\\', '/']);
        if (slash >= 0) id = id[(slash + 1)..];
        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) id = id[..^4];
        var under = id.IndexOf('_');
        if (under > 0) id = id[..under];
        var dot = id.LastIndexOf('.');
        if (dot >= 0 && dot < id.Length - 1) id = id[(dot + 1)..];
        return id.Length == 0 ? "" : char.ToUpperInvariant(id[0]) + id[1..];
    }

    static readonly (string Needle, string Name)[] KnownApps =
    [
        ("spotify", "Spotify"), ("zunemusic", "Media Player"), ("zunevideo", "Films & TV"), ("msedge", "Edge"),
        ("chrome", "Chrome"), ("firefox", "Firefox"), ("opera", "Opera"), ("brave", "Brave"), ("vivaldi", "Vivaldi"),
        ("vlc", "VLC"), ("foobar2000", "foobar2000"), ("itunes", "iTunes"), ("applemusic", "Apple Music"),
        ("tidal", "TIDAL"), ("deezer", "Deezer"), ("yandex", "Yandex Music"), ("aimp", "AIMP"), ("musicbee", "MusicBee"),
    ];

    /// <summary>Session choice: a playing session wins (the system's current one first), else the system's current,
    /// else the most recently active.</summary>
    public static int Choose(IReadOnlyList<(bool Playing, bool IsCurrent, DateTimeOffset LastActive)> sessions)
    {
        var best = -1;
        for (var i = 0; i < sessions.Count; i++)
        {
            if (best < 0 || Rank(sessions[i]).CompareTo(Rank(sessions[best])) > 0) best = i;
        }
        return best;

        static (int, int, DateTimeOffset) Rank((bool Playing, bool IsCurrent, DateTimeOffset LastActive) s) =>
            (s.Playing ? 1 : 0, s.IsCurrent ? 1 : 0, s.LastActive);
    }
}
