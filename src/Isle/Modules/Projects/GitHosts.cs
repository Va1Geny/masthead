using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Isle.Core;

namespace Isle.Modules.Projects;

/// <summary>A pull/merge request waiting on the user.</summary>
public sealed record ReviewItem(string Repo, string Title, string Url, string Author);

public enum PullPlan { NoUpstream, UpToDate, Diverged, FastForward }
public enum PullResult { Updated, UpToDate, Diverged, NoUpstream, Failed }

/// <summary>Parsers and rules for GitHub (via the gh CLI), GitLab (REST) and ff-only pulls. Pure; unit tested.</summary>
public static partial class GitHosts
{
    /// <summary>The chosen git host ("github" when never asked, so existing setups keep working). "none" keeps every network feature off.</summary>
    public static string Provider() => Settings.Current.Opt("setup.git", "github");
    public static string GitLabHost() => Settings.Current.Opt("gitlab.host", "gitlab.com").Trim().TrimEnd('/');

    /// <summary>Output of <c>gh search prs --json number,title,url,repository,author</c>.</summary>
    public static List<ReviewItem> GhPrs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(p => new ReviewItem(
            Str(p, "repository", "nameWithOwner"), Str(p, "title"), Str(p, "url"), Str(p, "author", "login"))).ToList();
    }

    /// <summary>Output of <c>GET /api/v4/merge_requests</c>; the project path comes from <c>references.full</c> ("group/project!12").</summary>
    public static List<ReviewItem> GitLabMrs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(m =>
        {
            var full = Str(m, "references", "full");
            var bang = full.LastIndexOf('!');
            return new ReviewItem(bang > 0 ? full[..bang] : "", Str(m, "title"), Str(m, "web_url"), Str(m, "author", "username"));
        }).ToList();
    }

    /// <summary>The newest pipeline from <c>GET /projects/:id/pipelines?per_page=1</c>.</summary>
    public static CiRun? GitLabPipeline(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.GetArrayLength() == 0) return null;
        var p = doc.RootElement[0];
        var state = Str(p, "status") switch
        {
            "success" or "skipped" => CiState.Passed,
            "failed" => CiState.Failed,
            "canceled" or "canceling" or "manual" => CiState.Cancelled,
            _ => CiState.Running, // created, pending, running, preparing, scheduled, waiting_for_resource
        };
        var updated = p.TryGetProperty("updated_at", out var u) && u.TryGetDateTimeOffset(out var t) ? t : DateTimeOffset.MinValue;
        return new CiRun(p.GetProperty("id").GetInt64(), "pipeline", state, Str(p, "web_url"), updated);
    }

    static string Str(JsonElement e, string a, string? b = null)
    {
        if (!e.TryGetProperty(a, out var x)) return "";
        if (b is not null && (x.ValueKind != JsonValueKind.Object || !x.TryGetProperty(b, out x))) return "";
        return x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";
    }

    /// <summary>"group/sub/project" when <paramref name="remote"/> points at <paramref name="host"/> (https, ssh or scp style), otherwise null.</summary>
    public static string? GitLabPath(string remote, string host)
    {
        var m = Regex.Match(remote.Trim(), @"(?:^|[@/])" + Regex.Escape(host) + @"(?::\d+)?[:/](?<p>.+?)(?:\.git)?/?$", RegexOptions.IgnoreCase);
        return m.Success && m.Groups["p"].Value.Contains('/') ? m.Groups["p"].Value : null;
    }

    /// <summary>The token a <c>glab auth login</c> stored for <paramref name="host"/> in config.yml, or "" (also when it lives in the OS keyring).</summary>
    public static string GlabToken(string yaml, string host)
    {
        var inHost = false;
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var t = line.Trim();
            if (t.Length == 0 || t[0] == '#') continue;

            if (t.EndsWith(':')) inHost = string.Equals(t.TrimEnd(':').Trim('"', '\''), host, StringComparison.OrdinalIgnoreCase);
            else if (inHost && t.StartsWith("token:", StringComparison.Ordinal))
            {
                var v = t[6..].Trim().Trim('"', '\'');
                return v.StartsWith('!') ? "" : v;
            }
        }
        return "";
    }

    /// <summary>Read-only look at glab's config; "" when absent.</summary>
    public static string GlabConfigToken(string host)
    {
        var dir = Environment.GetEnvironmentVariable("GLAB_CONFIG_DIR");
        if (string.IsNullOrEmpty(dir)) dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "glab-cli");
        var file = System.IO.Path.Combine(dir, "config.yml");
        try { return File.Exists(file) ? GlabToken(File.ReadAllText(file), host) : ""; }
        catch (IOException) { return ""; }
    }

    /// <summary>What a pull may do given the status after a fetch. Only a pure fast-forward is ever allowed.</summary>
    public static PullPlan Plan(RepoStatus s) =>
        !s.HasUpstream ? PullPlan.NoUpstream : s.Behind == 0 ? PullPlan.UpToDate : s.Ahead > 0 ? PullPlan.Diverged : PullPlan.FastForward;

    public static async Task<bool> FetchAsync(string path, CancellationToken ct) =>
        (await Cli.RunAsync("git", ["-C", path, "fetch"], null, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false)).Code == 0;

    /// <summary>Fetch, then <c>pull --ff-only</c> only when that is a fast-forward. Never merges or rebases.</summary>
    public static async Task<PullResult> PullAsync(string path, CancellationToken ct)
    {
        if (!await FetchAsync(path, ct).ConfigureAwait(false)) return PullResult.Failed;
        var (code, text) = await Cli.RunAsync("git", ["-C", path, "status", "--porcelain=v2", "--branch"], null, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        if (code != 0 || RepoParse.Status(text) is not { } s) return PullResult.Failed;
        switch (Plan(s))
        {
            case PullPlan.NoUpstream: return PullResult.NoUpstream;
            case PullPlan.UpToDate: return PullResult.UpToDate;
            case PullPlan.Diverged: return PullResult.Diverged;
        }
        var (pc, _) = await Cli.RunAsync("git", ["-C", path, "pull", "--ff-only"], null, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return pc == 0 ? PullResult.Updated : PullResult.Failed;
    }
}

/// <summary>One finished build or test run.</summary>
public sealed record BuildRun(double Seconds, bool Ok);

/// <summary>Last runs per build label ("dotnet test"), kept in builds.json next to the settings.</summary>
public static class BuildHistory
{
    public const int Keep = 10;

    public static void Add(Dictionary<string, List<BuildRun>> h, string label, BuildRun run)
    {
        if (!h.TryGetValue(label, out var l)) h[label] = l = [];
        l.Add(run);
        if (l.Count > Keep) l.RemoveRange(0, l.Count - Keep);
    }

    /// <summary>A sparkline of the durations, oldest first ("▁▃█").</summary>
    public static string Spark(IReadOnlyList<BuildRun> runs)
    {
        if (runs.Count == 0) return "";
        double lo = runs.Min(r => r.Seconds), hi = runs.Max(r => r.Seconds);
        return string.Concat(runs.Select(r => "▁▂▃▄▅▆▇█"[hi - lo < 0.5 ? 0 : (int)Math.Round((r.Seconds - lo) / (hi - lo) * 7)]));
    }

    public static string Secs(double s) => s < 90 ? $"{s.ToString("0", CultureInfo.InvariantCulture)} s" : $"{(int)(s / 60)} min {(int)(s % 60)} s";

    public static Dictionary<string, List<BuildRun>> Load(string file)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, List<BuildRun>>>(File.ReadAllText(file)) ?? []; }
        catch (Exception ex) when (ex is IOException or JsonException) { return []; }
    }

    public static void Save(string file, Dictionary<string, List<BuildRun>> h)
    {
        try { File.WriteAllText(file, JsonSerializer.Serialize(h)); }
        catch (IOException) { }
    }
}
