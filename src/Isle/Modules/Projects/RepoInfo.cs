using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Isle.Modules.Projects;

/// <summary>Working-tree state from <c>git status --porcelain=v2 --branch</c>.</summary>
public sealed record RepoStatus(string Branch, int Ahead, int Behind, int Changed, int Untracked, bool HasUpstream);

public enum CiState { None, Running, Passed, Failed, Cancelled }

/// <summary>Latest GitHub Actions run for a branch.</summary>
public sealed record CiRun(long Id, string Name, CiState State, string Url, DateTimeOffset Updated);

/// <summary>One watched repository as the Projects tab shows it.</summary>
public sealed record Repo(string Path, string Name, RepoStatus? Status, string? GitHub, CiRun? Ci);

/// <summary>Parsers for git and the GitHub REST API (pure; unit tested).</summary>
public static partial class RepoParse
{
    public static RepoStatus? Status(string porcelain)
    {
        string? head = null;
        int ahead = 0, behind = 0, changed = 0, untracked = 0;
        var upstream = false;
        foreach (var raw in porcelain.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("# branch.head ", StringComparison.Ordinal)) head = line[14..];
            else if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal)) upstream = true;
            else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var parts = line[12..].Split(' ');
                if (parts.Length == 2)
                {
                    int.TryParse(parts[0].TrimStart('+'), NumberStyles.Integer, CultureInfo.InvariantCulture, out ahead);
                    int.TryParse(parts[1].TrimStart('-'), NumberStyles.Integer, CultureInfo.InvariantCulture, out behind);
                }
            }
            else if (line.StartsWith("? ", StringComparison.Ordinal)) untracked++;
            else if (line.Length > 2 && line[0] is '1' or '2' or 'u' && line[1] == ' ') changed++;
        }
        if (head is null) return null;
        return new RepoStatus(head == "(detached)" ? "detached" : head, ahead, behind, changed, untracked, upstream);
    }

    [GeneratedRegex(@"github\.com[:/](?<owner>[\w.-]+)/(?<repo>[\w.-]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubRemote();

    /// <summary>"owner/repo" from an https or ssh GitHub remote URL, otherwise null.</summary>
    public static string? GitHub(string remoteUrl)
    {
        var m = GitHubRemote().Match(remoteUrl.Trim());
        return m.Success ? $"{m.Groups["owner"].Value}/{m.Groups["repo"].Value}" : null;
    }

    /// <summary>The newest run from <c>GET /repos/{o}/{r}/actions/runs</c>.</summary>
    public static CiRun? LatestRun(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("workflow_runs", out var runs) || runs.GetArrayLength() == 0) return null;
        var r = runs[0];
        var status = r.GetProperty("status").GetString();
        var conclusion = r.TryGetProperty("conclusion", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var state = status != "completed" ? CiState.Running : conclusion switch
        {
            "success" or "neutral" or "skipped" => CiState.Passed,
            "cancelled" => CiState.Cancelled,
            _ => CiState.Failed,
        };
        var updated = r.TryGetProperty("updated_at", out var u) && u.TryGetDateTimeOffset(out var when) ? when : DateTimeOffset.MinValue;
        return new CiRun(r.GetProperty("id").GetInt64(), r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", state, r.GetProperty("html_url").GetString() ?? "", updated);
    }

    /// <summary>total_count from <c>GET /search/issues</c>.</summary>
    public static int SearchCount(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("total_count", out var t) ? t.GetInt32() : 0;
    }

    /// <summary>
    /// A run that finished since the previous poll: the same run went from running to done, or a newer run is
    /// already done. Nothing on the first poll (no "before"), so starting Isle does not replay old results.
    /// </summary>
    public static CiRun? JustFinished(CiRun? before, CiRun? now)
    {
        if (before is null || now is null || now.State is CiState.Running or CiState.None) return null;
        return before.Id == now.Id ? (before.State == CiState.Running ? now : null) : (now.Updated > before.Updated ? now : null);
    }
}
