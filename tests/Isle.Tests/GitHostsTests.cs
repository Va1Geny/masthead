using System.Diagnostics;
using Isle.Modules.Projects;

namespace Isle.Tests;

public class GitHostsTests
{
    [Fact]
    public void Gh_pr_list_is_parsed()
    {
        const string json = """[{"author":{"login":"alex","is_bot":false},"number":7,"repository":{"name":"api","nameWithOwner":"me/api"},"title":"Fix retry","url":"https://github.com/me/api/pull/7"}]""";
        Assert.Equal(new ReviewItem("me/api", "Fix retry", "https://github.com/me/api/pull/7", "alex"), Assert.Single(GitHosts.GhPrs(json)));
        Assert.Empty(GitHosts.GhPrs("[]"));
    }

    [Fact]
    public void GitLab_merge_requests_and_pipelines_are_parsed()
    {
        const string mrs = """[{"iid":12,"title":"Bump image","web_url":"https://git.example.com/team/infra/-/merge_requests/12","author":{"username":"sam"},"references":{"full":"team/sub/infra!12"}}]""";
        Assert.Equal(new ReviewItem("team/sub/infra", "Bump image", "https://git.example.com/team/infra/-/merge_requests/12", "sam"), Assert.Single(GitHosts.GitLabMrs(mrs)));

        Assert.Null(GitHosts.GitLabPipeline("[]"));
        foreach (var (status, state) in new[] { ("success", CiState.Passed), ("failed", CiState.Failed), ("canceled", CiState.Cancelled), ("running", CiState.Running), ("pending", CiState.Running) })
        {
            var run = GitHosts.GitLabPipeline($$"""[{"id":99,"status":"{{status}}","web_url":"https://git.example.com/p/-/pipelines/99","updated_at":"2026-11-21T10:00:00Z"}]""")!;
            Assert.Equal((99L, state), (run.Id, run.State));
        }
    }

    [Theory]
    [InlineData("https://gitlab.com/group/sub/proj.git", "gitlab.com", "group/sub/proj")]
    [InlineData("git@git.example.com:team/infra.git", "git.example.com", "team/infra")]
    [InlineData("ssh://git@git.example.com:2222/team/infra.git", "git.example.com", "team/infra")]
    [InlineData("https://github.com/me/api.git", "gitlab.com", null)]
    [InlineData("https://notgitlab.com/a/b.git", "gitlab.com", null)]
    public void GitLab_remote_is_recognised(string remote, string host, string? expected) => Assert.Equal(expected, GitHosts.GitLabPath(remote, host));

    [Fact]
    public void Glab_token_is_read_per_host()
    {
        const string yaml = "git_protocol: ssh\nhosts:\n    gitlab.com:\n        token: glpat-demo1\n        api_host: gitlab.com\n    git.example.com:\n        token: \"glpat-demo2\"\n    keyring.example.com:\n        token: !!null\n";
        Assert.Equal("glpat-demo1", GitHosts.GlabToken(yaml, "gitlab.com"));
        Assert.Equal("glpat-demo2", GitHosts.GlabToken(yaml, "git.example.com"));
        Assert.Equal("", GitHosts.GlabToken(yaml, "keyring.example.com"));
        Assert.Equal("", GitHosts.GlabToken(yaml, "other.example.com"));
    }

    [Fact]
    public void Pull_plan_allows_only_a_pure_fast_forward()
    {
        Assert.Equal(PullPlan.NoUpstream, GitHosts.Plan(new RepoStatus("x", 0, 0, 0, 0, false)));
        Assert.Equal(PullPlan.UpToDate, GitHosts.Plan(new RepoStatus("x", 2, 0, 0, 0, true)));
        Assert.Equal(PullPlan.Diverged, GitHosts.Plan(new RepoStatus("x", 1, 3, 0, 0, true)));
        Assert.Equal(PullPlan.FastForward, GitHosts.Plan(new RepoStatus("x", 0, 3, 5, 0, true)));
    }

    static readonly string[] Repos = [@"C:\Work\app", @"C:\Work\app\tools\cli", @"D:\x\"];

    [Theory]
    [InlineData(@"c:\work\APP\src", null, @"C:\Work\app")]                        // case-insensitive, cwd inside
    [InlineData(@"C:\Work\app\tools\cli\bin", null, @"C:\Work\app\tools\cli")]   // deepest wins
    [InlineData(@"C:\Work\app\", null, @"C:\Work\app")]                           // trailing slash on the hint
    [InlineData(@"d:/x/sub", null, @"D:\x\")]                                     // forward slashes; repo with trailing slash
    [InlineData(@"C:\Work\application", null, null)]                              // longer sibling name is not inside
    [InlineData(@"C:\Other", null, null)]
    [InlineData(@"""C:\Tools\dotnet.exe"" build ""C:\Work\app\app.sln""", @"C:\Other", @"C:\Work\app")] // command line wins over cwd
    [InlineData("dotnet build", @"C:\Work\app\tools", @"C:\Work\app")]            // falls back to cwd
    public void Build_maps_to_the_deepest_watched_project(string first, string? cwd, string? expected) =>
        Assert.Equal(expected, BuildHistory.Match(Repos, first, cwd));

    [Fact]
    public void Old_label_keyed_history_loads_and_the_cwd_of_a_live_process_is_read()
    {
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllText(f, """{"dotnet test":[{"Seconds":5,"Ok":true}]}""");
            Assert.Single(BuildHistory.Load(f)["dotnet test"]);
            File.WriteAllText(f, "not json");
            Assert.Empty(BuildHistory.Load(f));
        }
        finally { File.Delete(f); }
        var dir = Directory.CreateTempSubdirectory("isle-cwd-");
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c ping -n 4 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir.FullName })!;
            Assert.Equal(dir.FullName, BuildWatcher.CurrentDirectory(p.Id)?.TrimEnd('\\'), ignoreCase: true);
            p.Kill(true); // whole tree: the ping child also holds the cwd
            p.WaitForExit();
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Build_history_is_capped_and_trends()
    {
        var h = new Dictionary<string, List<BuildRun>>();
        for (var i = 0; i < 15; i++) BuildHistory.Add(h, "dotnet test", new BuildRun(10 + i, true));
        Assert.Equal(BuildHistory.Keep, h["dotnet test"].Count);
        Assert.Equal(15, h["dotnet test"][0].Seconds);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], BuildHistory.Levels([new(1, true), new(2, true), new(3, true), new(4, true), new(5, true), new(6, true), new(7, true), new(8, true)]));
        Assert.Equal([0, 0], BuildHistory.Levels([new(5, true), new(5.2, true)]));
        Assert.Equal("42 s", BuildHistory.Secs(42));
        Assert.Equal("2 min 5 s", BuildHistory.Secs(125));
    }

    [Fact]
    public async Task Pull_is_ff_only_on_throwaway_repos()
    {
        var root = Path.Combine(Path.GetTempPath(), "isle-git-" + Guid.NewGuid().ToString("N"));
        try
        {
            var origin = Path.Combine(root, "origin");
            var clone = Path.Combine(root, "clone");
            var other = Path.Combine(root, "other");
            Directory.CreateDirectory(origin);
            Git(origin, "init", "-q", "-b", "main");
            Commit(origin, "a");
            Git(root, "clone", "-q", origin, clone);
            Git(root, "clone", "-q", origin, other);

            Assert.Equal(PullResult.UpToDate, await GitHosts.PullAsync(clone, default));

            Commit(origin, "b");
            Assert.Equal(PullResult.Updated, await GitHosts.PullAsync(clone, default));
            Assert.True(File.Exists(Path.Combine(clone, "b.txt")));

            // both sides move on: no merge, no rebase, nothing changes
            Commit(origin, "c");
            Commit(clone, "local");
            var before = Git(clone, "rev-parse", "HEAD");
            Assert.Equal(PullResult.Diverged, await GitHosts.PullAsync(clone, default));
            Assert.Equal(before, Git(clone, "rev-parse", "HEAD"));
            Assert.Equal("0", Git(clone, "rev-list", "--merges", "--count", "HEAD").Trim());
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    static void Commit(string dir, string name)
    {
        File.WriteAllText(Path.Combine(dir, name + ".txt"), name);
        Git(dir, "add", ".");
        Git(dir, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", name);
    }

    static string Git(string dir, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, string.Join(' ', args) + ": " + p.StandardError.ReadToEnd());
        return o.Trim();
    }
}
