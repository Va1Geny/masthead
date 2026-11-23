using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Isle.Core;
using Isle.Modules.Media;

namespace Isle.Tests;

public class PlaybackTests
{
    static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Extrapolates_only_while_playing_and_clamps()
    {
        var d = TimeSpan.FromSeconds(100);
        Assert.Equal(TimeSpan.FromSeconds(15), PlaybackMath.Extrapolate(TimeSpan.FromSeconds(10), T0, T0.AddSeconds(5), true, 1, d));
        Assert.Equal(TimeSpan.FromSeconds(10), PlaybackMath.Extrapolate(TimeSpan.FromSeconds(10), T0, T0.AddSeconds(5), false, 1, d));
        Assert.Equal(TimeSpan.FromSeconds(20), PlaybackMath.Extrapolate(TimeSpan.FromSeconds(10), T0, T0.AddSeconds(5), true, 2, d));
        Assert.Equal(d, PlaybackMath.Extrapolate(TimeSpan.FromSeconds(99), T0, T0.AddSeconds(30), true, 1, d));
        Assert.Equal(TimeSpan.FromSeconds(10), PlaybackMath.Extrapolate(TimeSpan.FromSeconds(10), T0, T0.AddSeconds(-3), true, 1, d)); // clock skew
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(65, "1:05")]
    [InlineData(3723, "1:02:03")]
    public void Formats_time(int seconds, string expected) => Assert.Equal(expected, PlaybackMath.Format(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "Media Player")]
    [InlineData("MSEdge", "Edge")]
    [InlineData("308046B0AF4A39CB", "308046B0AF4A39CB")]
    [InlineData(@"C:\Apps\coolplayer.exe", "Coolplayer")]
    [InlineData("Company.App_abc123!App", "App")]
    [InlineData("", "")]
    public void Friendly_source_names(string id, string expected) => Assert.Equal(expected, PlaybackMath.FriendlySource(id));

    [Fact]
    public void Chooses_playing_then_current_then_recent()
    {
        Assert.Equal(-1, PlaybackMath.Choose([]));
        Assert.Equal(1, PlaybackMath.Choose([(false, true, T0), (true, false, T0)]));
        Assert.Equal(0, PlaybackMath.Choose([(false, true, T0), (false, false, T0.AddMinutes(5))]));
        Assert.Equal(1, PlaybackMath.Choose([(false, false, T0), (false, false, T0.AddMinutes(5))]));
        Assert.Equal(1, PlaybackMath.Choose([(true, false, T0), (true, true, T0)]));
    }
}

public class ArtColorTests
{
    static byte[] Pixels(params (byte R, byte G, byte B, int Count)[] groups)
    {
        var list = new List<byte>();
        foreach (var (r, g, b, n) in groups)
            for (var i = 0; i < n; i++) list.AddRange([b, g, r, 255]);
        return [.. list];
    }

    [Fact]
    public void Saturated_colour_beats_large_grey_area()
    {
        var c = ArtColors.Dominant(Pixels((128, 128, 128, 200), (20, 20, 20, 30), (220, 40, 40, 26)));
        var (h, s, _) = ArtColors.ToHsl(c);
        Assert.True(h < 15 || h > 345, $"hue {h}");
        Assert.True(s > 0.45);
    }

    [Fact]
    public void Greyscale_art_falls_back_to_default_accent()
    {
        Assert.Equal(ArtColors.Fallback, ArtColors.Dominant(Pixels((128, 128, 128, 100), (250, 250, 250, 100))));
        Assert.Equal(ArtColors.Fallback, ArtColors.Dominant(Array.Empty<byte>()));
    }

    [Fact]
    public void Readable_keeps_lightness_in_band()
    {
        foreach (var c in new[] { Colors.Black, Colors.White, Color.FromRgb(10, 0, 40), Color.FromRgb(255, 250, 200) })
        {
            var (_, s, l) = ArtColors.ToHsl(ArtColors.Readable(c));
            Assert.InRange(l, 0.44, 0.66);
        }
    }

    [Fact]
    public void Placeholder_is_deterministic_and_varies()
    {
        Assert.Equal(ArtColors.PlaceholderColors("abc"), ArtColors.PlaceholderColors("abc"));
        Assert.NotEqual(ArtColors.PlaceholderColors("abc"), ArtColors.PlaceholderColors("abd"));
    }

    [Fact]
    public void Hsl_roundtrip()
    {
        var c = Color.FromRgb(0x5A, 0xA9, 0xFF);
        var (h, s, l) = ArtColors.ToHsl(c);
        var back = ArtColors.FromHsl(h, s, l);
        Assert.InRange(Math.Abs(back.R - c.R) + Math.Abs(back.G - c.G) + Math.Abs(back.B - c.B), 0, 3);
    }
}
