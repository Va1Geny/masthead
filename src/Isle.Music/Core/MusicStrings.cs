namespace Isle.Music;

/// <summary>UI text of the Music pack, merged into <see cref="Core.Strings"/> by <see cref="Pack.Register"/>.</summary>
static class MusicStrings
{
    internal static readonly Dictionary<string, string> En = new()
    {
        ["music.playPause"] = "Play / pause",
        ["music.loading"] = "{0} — loading…",
    };

    internal static readonly Dictionary<string, string> Uk = new()
    {
        ["music.playPause"] = "Відтворити / пауза",
        ["music.loading"] = "{0} — завантаження…",
    };
}
