using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Isle.Core;
using Isle.Modules.Setup;
using Isle.Views.Projects;

namespace Isle.Modules.Projects;

/// <summary>
/// Projects (module id "projects"): the Projects tab (branch, changes, ahead/behind and the latest GitHub Actions
/// run of each watched repository), a pill when a CI run finishes, and a pill when a local build or test run ends.
/// Repositories come from the "projects.paths" option. CI and review requests go through the gh CLI login
/// (Isle never sees a GitHub token); without gh, or with setup.git not github, GitHub CI shows nothing.
/// </summary>
public sealed class ProjectsModule(IslandWindow island) : IModule, IDisposable
{
    public const string TabId = "projects";
    const string PillId = "projects.event";

    BuildWatcher? _builds;
    DispatcherTimer? _buildTimer, _ciTimer, _pillTimer;
    readonly Dictionary<string, CiRun?> _lastRuns = [];
    readonly SemaphoreSlim _gate = new(1, 1);

    public string Id => "projects";

    public static IReadOnlyList<string> Paths() =>
        Settings.Current.Opt("projects.paths").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    static string GitLabToken() => Secret.Unprotect(Settings.Current.Opt("gitlab.token")) is { Length: > 0 } t ? t : GitHosts.GlabConfigToken(GitHosts.GitLabHost());
    readonly Dictionary<string, List<BuildRun>> _history = BuildHistory.Load(Isle.Core.Paths.File("builds.json"));
    HttpClient? _gl;

    public void Start()
    {
        island.AddTab(new TabInfo(TabId, "tab.projects", "Icon.Branch", () => new ProjectsView(new ProjectsActions(
            Refresh: ct => RefreshAsync(withCi: true, ct),
            Reviews: ReviewsAsync,
            Builds: () => _history.OrderByDescending(h => h.Value.Count).Take(2).Select(h => (h.Key, (IReadOnlyList<BuildRun>)h.Value.ToList())).ToList(),
            Fetch: GitHosts.FetchAsync,
            Pull: GitHosts.PullAsync,
            Configure: () => App.OpenSettings("options"))), 98));

        if (Settings.Current.OptBool("projects.builds", true))
        {
            _builds = new BuildWatcher();
            _builds.Finished += r => island.Dispatcher.BeginInvoke(() =>
            {
                BuildHistory.Add(_history, r.Label, new BuildRun(r.Duration.TotalSeconds, r.ExitCode is null or 0));
                BuildHistory.Save(Isle.Core.Paths.File("builds.json"), _history);
                Announce(BuildText(r), r.ExitCode is null or 0 ? "OkBrush" : "DangerBrush", null);
            });
            _buildTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
            _buildTimer.Tick += async (_, _) => await Task.Run(() => { try { _builds?.Poll(); } catch (Exception ex) { Logger.Error("build watch", ex); } });
            _buildTimer.Start();
        }
        if (Paths().Count > 0 && GitHosts.Provider() != SetupModel.None)
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
                if ((repo.GitHub ?? repo.GitLab) is null) continue;
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
                var gitlab = rc == 0 && GitHosts.Provider() == "gitlab" ? GitHosts.GitLabPath(remote, GitHosts.GitLabHost()) : null;
                CiRun? ci = null;
                if (withCi && status is { Branch: not "detached" })
                {
                    if (github is not null && GitHosts.Provider() == "github") ci = await LatestRunAsync(github, status.Branch, ct).ConfigureAwait(false);
                    else if (gitlab is not null) ci = await GitLabPipelineAsync(gitlab, status.Branch, ct).ConfigureAwait(false);
                }
                repos.Add(new Repo(path, System.IO.Path.GetFileName(path.TrimEnd('\\', '/')), status, github, ci, gitlab));
            }
            return repos;
        }
        finally { _gate.Release(); }
    }

    async Task<CiRun?> LatestRunAsync(string ownerRepo, string branch, CancellationToken ct)
    {
        var (code, json) = await Cli.RunAsync("gh", ["run", "list", "-R", ownerRepo, "-b", branch, "-L", "1", "--json", "databaseId,name,status,conclusion,updatedAt,url"], null, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (code != 0) return null; // gh missing, not logged in or offline: same as "no CI"
        try { return RepoParse.LatestRun(json); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    /// <summary>PRs/MRs waiting on the user; null when no host is chosen or it is unreachable. GitHub goes through the gh CLI login (Isle never sees a token).</summary>
    async Task<IReadOnlyList<ReviewItem>?> ReviewsAsync(CancellationToken ct)
    {
        try
        {
            switch (GitHosts.Provider())
            {
                case "github":
                    var (code, json) = await Cli.RunAsync("gh", ["search", "prs", "--review-requested=@me", "--state=open", "--limit", "20", "--json", "number,title,url,repository,author"], null, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                    return code == 0 ? GitHosts.GhPrs(json) : null;
                case "gitlab":
                    var me = await GitLabGetAsync("/user", ct).ConfigureAwait(false);
                    if (me is null) return null;
                    using (var u = System.Text.Json.JsonDocument.Parse(me))
                    {
                        var name = Uri.EscapeDataString(u.RootElement.GetProperty("username").GetString() ?? "");
                        var items = new List<ReviewItem>();
                        foreach (var q in new[] { "reviewer_username=" + name, "assignee_username=" + name })
                            if (await GitLabGetAsync("/merge_requests?state=opened&scope=all&per_page=20&" + q, ct).ConfigureAwait(false) is { } body)
                                items.AddRange(GitHosts.GitLabMrs(body).Where(i => items.All(x => x.Url != i.Url)));
                        return items;
                    }
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { }
        return null;
    }

    async Task<CiRun?> GitLabPipelineAsync(string project, string branch, CancellationToken ct)
    {
        var body = await GitLabGetAsync($"/projects/{Uri.EscapeDataString(project)}/pipelines?per_page=1&ref={Uri.EscapeDataString(branch)}", ct).ConfigureAwait(false);
        try { return body is null ? null : GitHosts.GitLabPipeline(body); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    async Task<string?> GitLabGetAsync(string api, CancellationToken ct)
    {
        var token = GitLabToken();
        if (token.Length == 0) return null;
        _gl ??= new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{GitHosts.GitLabHost()}/api/v4{api}");
        request.Headers.Add("PRIVATE-TOKEN", token);
        try
        {
            using var response = await _gl.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Logger.Warn($"gitlab: {ex.GetType().Name}");
            return null;
        }
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
        _gl?.Dispose();
        _gl = null;
        _lastRuns.Clear();
    }

    public void Dispose() => Stop();
}
