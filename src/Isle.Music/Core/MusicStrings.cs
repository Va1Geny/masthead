namespace Isle.Music;

/// <summary>UI text of the Music pack, merged into <see cref="Core.Strings"/> by <see cref="Pack.Register"/>.</summary>
static class MusicStrings
{
    internal static readonly Dictionary<string, string> En = new()
    {
        ["mod.media"] = "Music",
        ["mod.media.hint"] = "Now playing in the pill, album colour",
        ["music.playPause"] = "Play / pause",
        ["music.loading"] = "{0} — loading…",
    };

    internal static readonly Dictionary<string, string> Uk = new()
    {
        ["mod.media"] = "Музика",
        ["mod.media.hint"] = "Що грає — у пілюлі, колір обкладинки",
        ["music.playPause"] = "Відтворити / пауза",
        ["music.loading"] = "{0} — завантаження…",
    };
}
