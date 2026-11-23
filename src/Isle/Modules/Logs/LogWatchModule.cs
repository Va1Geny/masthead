using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Isle.Core;

namespace Isle.Modules.Logs;

/// <summary>
/// Follows one log file: remembers where it stopped and returns the first new line matching the pattern.
/// A file that shrank (rotated or truncated) is read again from the start. At most 256 KB is read per call.
/// </summary>
public sealed class LogTail(string path, Regex pattern)
{
    const int MaxRead = 256 * 1024;
    long _position = -1;

    public string Path => path;

    /// <summary>The first matching new line, or null. The first call only remembers the current end of the file.</summary>
    public string? Poll()
    {
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        using (stream)
        {
            var length = stream.Length;
            if (_position < 0 || length < _position) { var first = _position < 0; _position = first ? length : 0; if (first) return null; }
            if (length == _position) return null;
            var start = Math.Max(_position, length - MaxRead);
            stream.Position = start;
            var buffer = new byte[length - start];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            _position = start + read;
            return FirstMatch(Encoding.UTF8.GetString(buffer, 0, read), pattern);
        }
    }

    public static string? FirstMatch(string text, Regex pattern)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0 && pattern.IsMatch(line)) return line;
        }
        return null;
    }
}

/// <summary>
/// Log watcher (module id "logs"): checks the files in "logs.files" every 2 s and shows a pill with the first new
/// error line ("logs.pattern", default ERROR / FATAL / Exception); a click opens the file.
/// </summary>
public sealed class LogWatchModule(IslandWindow island) : IModule, IDisposable
{
    const string PillId = "logs.match";
    public const string DefaultPattern = @"\b(?:ERROR|FATAL|CRITICAL|Unhandled|panic)\b|Exception\b";

    readonly List<LogTail> _tails = [];
    DispatcherTimer? _timer, _pillTimer;
    DateTime _quietUntil;

    public string Id => "logs";

    public void Start()
    {
        Regex pattern;
        try { pattern = new Regex(Settings.Current.Opt("logs.pattern", DefaultPattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
        catch (ArgumentException) { pattern = new Regex(DefaultPattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)); }
        foreach (var f in Settings.Current.Opt("logs.files").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            _tails.Add(new LogTail(f, pattern));
        if (_tails.Count == 0) return;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        _ = PollAsync();
    }

    async Task PollAsync()
    {
        try
        {
            var hits = await Task.Run(() => _tails.Select(t => (t.Path, Line: t.Poll())).Where(x => x.Line is not null).ToList());
            if (hits.Count == 0 || DateTime.Now < _quietUntil) return;
            var (path, line) = hits[0];
            _quietUntil = DateTime.Now.AddSeconds(15); // a burst of errors is one pill, not twenty
            Announce(path, line!);
        }
        catch (Exception ex) when (ex is RegexMatchTimeoutException) { Logger.Warn("logs: pattern too slow"); }
        catch (Exception ex) { Logger.Error("logs poll", ex); }
    }

    void Announce(string path, string line)
    {
        island.Pill.Set(new PillItem
        {
            Id = PillId, Priority = PillPriority.Transient, Width = 300, Peek = true,
            View = () => PillView(System.IO.Path.GetFileName(path), line),
            Click = () => ProcessOps.Open(path),
        });
        _pillTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _pillTimer.Tick -= HidePill;
        _pillTimer.Tick += HidePill;
        _pillTimer.Stop();
        _pillTimer.Start();
    }

    void HidePill(object? sender, EventArgs e) { _pillTimer?.Stop(); island.Pill.Remove(PillId); }

    internal static FrameworkElement PillView(string file, string line)
    {
        var t = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(14, 0, 14, 0) };
        var name = new System.Windows.Documents.Run(file + "  ");
        name.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "WarnBrush");
        t.Inlines.Add(name);
        var text = new System.Windows.Documents.Run(line);
        text.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "FgBrush");
        t.Inlines.Add(text);
        return t;
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
        _pillTimer?.Stop();
        island.Pill.Remove(PillId);
        _tails.Clear();
    }

    public void Dispose() => Stop();
}
