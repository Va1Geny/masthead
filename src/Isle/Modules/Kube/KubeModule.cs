using System.Text.RegularExpressions;
using System.Windows.Threading;
using Isle.Core;
using Isle.Views.Live;

namespace Isle.Modules.Kube;

/// <summary>Reads only the top-level "current-context" line of kubeconfig files (they hold credentials).</summary>
public static class KubeConfig
{
    public const string DefaultPattern = "prod|production|prd|live";

    /// <summary>Config files in kubectl order: KUBECONFIG (';'-separated) or the default file under the profile.</summary>
    public static List<string> Paths(string? env, string profile)
    {
        var list = (env ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (list.Count == 0 && profile.Length > 0) list.Add(Path.Combine(profile, ".kube", "config"));
        return list;
    }

    /// <summary>The value of the unindented "current-context:" line, or null when absent or empty.</summary>
    public static string? ParseCurrentContext(IEnumerable<string> lines)
    {
        const string Key = "current-context:";
        foreach (var raw in lines)
        {
            var line = raw.TrimStart('﻿');
            if (!line.StartsWith(Key, StringComparison.Ordinal)) continue;
            var v = line[Key.Length..].Trim();
            if (v.Length > 0 && v[0] is '"' or '\'')
            {
                var end = v.IndexOf(v[0], 1);
                v = end > 0 ? v[1..end] : v[1..];
            }
            else if (v.StartsWith('#')) v = "";
            else { var c = v.IndexOf(" #", StringComparison.Ordinal); if (c >= 0) v = v[..c]; }
            v = v.Trim();
            return v.Length > 0 ? v : null;
        }
        return null;
    }

    /// <summary>kubectl semantics: the first file that sets a current-context wins; unreadable files are skipped.</summary>
    public static string? Resolve(IEnumerable<string> files)
    {
        foreach (var f in files)
        {
            try
            {
                using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(s);
                var ctx = ParseCurrentContext(Lines(r));
                if (ctx is not null) return ctx;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;

        static IEnumerable<string> Lines(StreamReader r) { while (r.ReadLine() is { } l) yield return l; }
    }

    /// <summary>Case-insensitive pattern with a match timeout; an invalid one falls back to the default.</summary>
    public static Regex BuildPattern(string? pattern, out bool fellBack)
    {
        const RegexOptions o = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var t = TimeSpan.FromMilliseconds(50);
        fellBack = false;
        try { return new Regex(string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern, o, t); }
        catch (ArgumentException) { fellBack = true; return new Regex(DefaultPattern, o, t); }
    }

    public static bool IsProd(string? context, Regex pattern)
    {
        if (context is null) return false;
        try { return pattern.IsMatch(context); }
        catch (RegexMatchTimeoutException) { return false; }
    }
}

/// <summary>
/// Kubernetes context warning (module id "kube"): a persistent pill indicator while the active kubectl context
/// matches "kube.prodPattern", plus a short banner when the context switches to such a context.
/// </summary>
public sealed class KubeModule(IslandWindow island) : IModule, IDisposable
{
    const string IndicatorId = "kube.prod", BannerId = "kube.banner";
    readonly List<FileSystemWatcher> _watchers = [];
    List<string> _files = [];
    Regex _pattern = KubeConfig.BuildPattern(null, out _);
    DispatcherTimer? _debounce, _bannerTimer;
    string? _context;

    public string Id => "kube";

    public void Start()
    {
        _pattern = KubeConfig.BuildPattern(Settings.Current.Opt("kube.prodPattern"), out var fellBack);
        if (fellBack) Logger.Warn("kube: invalid prod pattern, using default");
        _files = KubeConfig.Paths(Environment.GetEnvironmentVariable("KUBECONFIG"), Environment.GetEnvironmentVariable("USERPROFILE") ?? "");
        var debounce = _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
        debounce.Tick += (_, _) => { debounce.Stop(); Recheck(initial: false); };
        void Kick() => island.Dispatcher.BeginInvoke(() => { debounce.Stop(); debounce.Start(); });
        foreach (var f in _files)
        {
            var dir = Path.GetDirectoryName(f);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            try
            {
                var w = new FileSystemWatcher(dir, Path.GetFileName(f)) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                w.Created += (_, _) => Kick();
                w.Changed += (_, _) => Kick();
                w.Deleted += (_, _) => Kick();
                w.Renamed += (_, _) => Kick();
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { Logger.Warn("kube: cannot watch config directory"); }
        }
        if (_watchers.Count > 0) Recheck(initial: true);
    }

    void Recheck(bool initial)
    {
        if (_watchers.Count == 0) return; // stopped: a watcher event queued before Stop() must not re-add the pill
        var ctx = KubeConfig.Resolve(_files);
        if (ctx == _context) return;
        _context = ctx;
        if (!initial) Logger.Info("kube context changed");
        if (!KubeConfig.IsProd(ctx, _pattern)) { island.Pill.Remove(IndicatorId); island.Pill.Remove(BannerId); return; }
        island.Pill.Set(Indicator(ctx!));
        if (!initial) Banner(ctx!);
    }

    internal static PillItem Indicator(string ctx) => new()
    {
        Id = IndicatorId, Priority = PillPriority.MicCamera - 1, // just below the mic/camera privacy indicator, which must win
        Width = 240,
        View = () => new IndicatorPill("Icon.Server", ctx, Strings.T("kube.prod"), "DangerBrush"),
        Compact = () => new IndicatorPill("Icon.Server", "", "", "DangerBrush", center: true), CompactWidth = 40,
    };

    internal static PillItem BannerItem(string ctx) => new()
    {
        Id = BannerId, Priority = PillPriority.Transient, Width = IndicatorPill.MessageWidth(Strings.T("kube.banner", ctx)) + 30, Peek = true,
        View = () => new IndicatorPill("Icon.Server", Strings.T("kube.banner", ctx), "", "DangerBrush"),
    };

    void Banner(string ctx)
    {
        island.Pill.Set(BannerItem(ctx));
        if (_bannerTimer is null)
        {
            _bannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _bannerTimer.Tick += HideBanner;
        }
        _bannerTimer.Stop();
        _bannerTimer.Start();
    }

    void HideBanner(object? s, EventArgs e) { _bannerTimer?.Stop(); island.Pill.Remove(BannerId); }

    public void Stop()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        _debounce?.Stop();
        _bannerTimer?.Stop();
        island.Pill.Remove(IndicatorId);
        island.Pill.Remove(BannerId);
        _context = null;
    }

    public void Dispose() => Stop();
}

static class KubeSnapshots
{
    public static void Register(Dictionary<string, Action<IslandWindow>> states)
    {
        states["kube-banner"] = w => w.Pill.Set(KubeModule.BannerItem("prod-eu-west"));
        states["kube-indicator"] = w => w.Pill.Set(KubeModule.Indicator("prod-eu-west"));
    }
}
