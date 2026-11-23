using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Isle.Core;
using Isle.Views.Projects;

namespace Isle.Modules.Projects;

/// <summary>
/// Projects (module id "projects"): the Projects tab (branch, changes, ahead/behind and the latest GitHub Actions
/// run of each watched repository), a pill when a CI run finishes, and a pill when a local build or test run ends.
/// Repositories come from the "projects.paths" option. CI uses the GitHub REST API; a token is optional
/// (public repositories work without one; with one, private repositories and review requests work too).
/// </summary>
public sealed class ProjectsModule(IslandWindow island) : IModule, IDisposable
{
    public const string TabId = "projects";
    const string PillId = "projects.event";

    BuildWatcher? _builds;
    DispatcherTimer? _buildTimer, _ciTimer, _pillTimer;
    HttpClient? _http;
    readonly Dictionary<string, CiRun?> _lastRuns = [];
    readonly SemaphoreSlim _gate = new(1, 1);

    public string Id => "projects";

    public static IReadOnlyList<string> Paths() =>
        Settings.Current.Opt("projects.paths").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    static string Token() => Secret.Unprotect(Settings.Current.Opt("projects.githubToken"));

    public void Start()
    {
        island.AddTab(new TabInfo(TabId, "tab.projects", "Icon.Branch", () => new ProjectsView(new ProjectsActions(
            Refresh: ct => RefreshAsync(withCi: true, ct),
            Reviews: ReviewCountAsync,
            HasToken: () => Token().Length > 0,
            Configure: () => App.OpenSettings("options"))), 97));

        if (Settings.Current.OptBool("projects.builds", true))
        {
            _builds = new BuildWatcher();
            _builds.Finished += r => island.Dispatcher.BeginInvoke(() => Announce(BuildText(r), r.ExitCode is null or 0 ? "OkBrush" : "DangerBrush", null));
            _buildTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
            _buildTimer.Tick += async (_, _) => await Task.Run(() => { try { _builds?.Poll(); } catch (Exception ex) { Logger.Error("build watch", ex); } });
            _buildTimer.Start();
        }
        if (Paths().Count > 0)
        {
            _ciTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(2) };
            _ciTimer.Tick += async (_, _) => await PollCiAsync();
            _ciTimer.Start();
            _ = PollCiAsync();
        }
    }

    internal static string BuildText(BuildResult r)
    {
        var secs = r.Duration.TotalSeconds < 90 ? $"{r.Duration.TotalSeconds:0} s" : $"{(int)r.Duration.TotalMinutes} min {r.Duration.Seconds} s";
        return r.ExitCode is null or 0 ? $"✓ {r.Label} · {secs}" : $"✗ {r.Label} · {Strings.T("projects.exit", r.ExitCode)}";
    }

    async Task PollCiAsync()
    {
        try
        {
            foreach (var repo in await RefreshAsync(withCi: true, CancellationToken.None))
            {
                if (repo.GitHub is null) continue;
                var key = repo.Path + "|" + repo.Status?.Branch;
                if (_lastRuns.TryGetValue(key, out var before) && RepoParse.JustFinished(before, repo.Ci) is { } done)
                {
                    var ok = done.State == CiState.Passed;
                    Announce($"{(ok ? "✓" : "✗")} CI · {repo.Name} · {repo.Status?.Branch}", ok ? "OkBrush" : done.State == CiState.Cancelled ? "FgDimBrush" : "DangerBrush", done.Url);
                }
                _lastRuns[key] = repo.Ci;
            }
        }
        catch (Exception ex) { Logger.Error("ci poll", ex); }
    }

    /// <summary>Status of every watched repository; with <paramref name="withCi"/> also the latest Actions run.</summary>
    public async Task<IReadOnlyList<Repo>> RefreshAsync(bool withCi, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var repos = new List<Repo>();
            foreach (var path in Paths())
            {
                var (code, porcelain) = await Cli.RunAsync("git", ["-C", path, "status", "--porcelain=v2", "--branch"], null, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                var status = code == 0 ? RepoParse.Status(porcelain) : null;
                var (rc, remote) = await Cli.RunAsync("git", ["-C", path, "remote", "get-url", "origin"], null, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                var github = rc == 0 ? RepoParse.GitHub(remote) : null;
                CiRun? ci = null;
                if (withCi && github is not null && status is { Branch: not "detached" })
                    ci = await LatestRunAsync(github, status.Branch, ct).ConfigureAwait(false);
                repos.Add(new Repo(path, System.IO.Path.GetFileName(path.TrimEnd('\\', '/')), status, github, ci));
            }
            return repos;
        }
        finally { _gate.Release(); }
    }

    async Task<CiRun?> LatestRunAsync(string ownerRepo, string branch, CancellationToken ct)
    {
        var (status, body) = await GetAsync($"https://api.github.com/repos/{ownerRepo}/actions/runs?per_page=1&branch={Uri.EscapeDataString(branch)}", ct).ConfigureAwait(false);
        if (status != 200) return null;
        try { return RepoParse.LatestRun(body); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    async Task<int?> ReviewCountAsync(CancellationToken ct)
    {
        if (Token().Length == 0) return null;
        var (status, body) = await GetAsync("https://api.github.com/search/issues?q=" + Uri.EscapeDataString("is:pr is:open review-requested:@me") + "&per_page=1", ct).ConfigureAwait(false);
        if (status != 200) return null;
        try { return RepoParse.SearchCount(body); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    async Task<(int Status, string Body)> GetAsync(string url, CancellationToken ct)
    {
        _http ??= CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var token = Token();
        if (token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Logger.Warn($"github: {ex.GetType().Name}");
            return (0, "");
        }
    }

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Isle/1.0 (Windows desktop overlay)");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return c;
    }

    void Announce(string text, string brush, string? url)
    {
        island.Pill.Set(new PillItem
        {
            Id = PillId, Priority = PillPriority.Transient, Width = Math.Clamp(60 + text.Length * 6.5, 160, 300), Peek = true,
            View = () => PillView(text, brush),
            Click = url is null ? null : () => ProcessOps.Open(url),
            TabId = url is null ? null : TabId,
        });
        _pillTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _pillTimer.Tick -= HidePill;
        _pillTimer.Tick += HidePill;
        _pillTimer.Stop();
        _pillTimer.Start();
    }

    void HidePill(object? sender, EventArgs e) { _pillTimer?.Stop(); island.Pill.Remove(PillId); }

    internal static FrameworkElement PillView(string text, string brush)
    {
        var t = new TextBlock { Text = text, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(12, 0, 12, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush == "FgDimBrush" ? "FgBrush" : brush);
        return t;
    }

    public void Stop()
    {
        _buildTimer?.Stop();
        _buildTimer = null;
        _ciTimer?.Stop();
        _ciTimer = null;
        _pillTimer?.Stop();
        island.Pill.Remove(PillId);
        island.RemoveTab(TabId);
        _builds?.Dispose();
        _builds = null;
        _http?.Dispose();
        _http = null;
        _lastRuns.Clear();
    }

    public void Dispose() => Stop();
}
