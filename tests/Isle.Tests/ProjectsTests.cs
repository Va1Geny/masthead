using Isle.Modules.Projects;

namespace Isle.Tests;

public class ProjectsTests
{
    [Fact]
    public void Git_status_porcelain_v2_is_parsed()
    {
        const string text = "# branch.oid 1a2b3c\n# branch.head dev-edition\n# branch.upstream origin/dev-edition\n# branch.ab +2 -1\n"
            + "1 .M N... 100644 100644 100644 aaa bbb src/a.cs\n2 R. N... 100644 100644 100644 aaa bbb R100 b.cs\ta.cs\n"
            + "u UU N... 100644 100644 100644 100644 a b c conflict.cs\n? new.txt\n? other.txt\n";
        var s = RepoParse.Status(text)!;
        Assert.Equal("dev-edition", s.Branch);
        Assert.Equal((2, 1, 3, 2, true), (s.Ahead, s.Behind, s.Changed, s.Untracked, s.HasUpstream));
        var fresh = RepoParse.Status("# branch.oid (initial)\r\n# branch.head main\r\n")!;
        Assert.Equal(("main", 0, false), (fresh.Branch, fresh.Changed, fresh.HasUpstream));
        Assert.Equal("detached", RepoParse.Status("# branch.head (detached)\n")!.Branch);
        Assert.Null(RepoParse.Status("fatal: not a git repository"));
    }

    [Theory]
    [InlineData("https://github.com/someone/island.git", "someone/island")]
    [InlineData("https://github.com/owner/repo", "owner/repo")]
    [InlineData("git@github.com:owner/my.repo.git\n", "owner/my.repo")]
    [InlineData("ssh://git@github.com/owner/repo.git", "owner/repo")]
    [InlineData("https://gitlab.com/owner/repo.git", null)]
    public void GitHub_remote_is_recognised(string url, string? expected) => Assert.Equal(expected, RepoParse.GitHub(url));

    [Theory]
    [InlineData("in_progress", null, CiState.Running)]
    [InlineData("queued", null, CiState.Running)]
    [InlineData("completed", "success", CiState.Passed)]
    [InlineData("completed", "failure", CiState.Failed)]
    [InlineData("completed", "timed_out", CiState.Failed)]
    [InlineData("completed", "cancelled", CiState.Cancelled)]
    public void Latest_actions_run_is_parsed(string status, string? conclusion, CiState expected)
    {
        var json = $$"""[{"databaseId":42,"name":"build","status":"{{status}}","conclusion":"{{conclusion}}","url":"https://github.com/o/r/actions/runs/42","updatedAt":"2026-11-21T10:00:00Z"}]""";
        var run = RepoParse.LatestRun(json)!;
        Assert.Equal((42L, "build", expected), (run.Id, run.Name, run.State));
        Assert.Null(RepoParse.LatestRun("[]"));
    }

    [Fact]
    public void Ci_finish_is_reported_once_and_never_on_the_first_poll()
    {
        var t = DateTimeOffset.UnixEpoch;
        var running = new CiRun(1, "b", CiState.Running, "", t);
        var passed = running with { State = CiState.Passed, Updated = t.AddMinutes(3) };
        Assert.Null(RepoParse.JustFinished(null, passed));                // first poll
        Assert.Equal(passed, RepoParse.JustFinished(running, passed));     // finished
        Assert.Null(RepoParse.JustFinished(passed, passed));               // already reported
        var newer = new CiRun(2, "b", CiState.Failed, "", t.AddMinutes(9)); // started and failed between polls
        Assert.Equal(newer, RepoParse.JustFinished(passed, newer));
        Assert.Null(RepoParse.JustFinished(passed, new CiRun(3, "b", CiState.Running, "", t.AddMinutes(10))));
    }

    [Theory]
    [InlineData("dotnet", "\"C:\\Program Files\\dotnet\\dotnet.exe\" test Isle.sln -c Release", "dotnet test")]
    [InlineData("dotnet", "dotnet build", "dotnet build")]
    [InlineData("dotnet", "dotnet run", null)]
    [InlineData("dotnet", "dotnet watch run", null)]
    [InlineData("dotnet", "\"C:\\Program Files\\dotnet\\dotnet.exe\" \"C:\\Program Files\\dotnet\\sdk\\10.0.100\\MSBuild.dll\" -nodemode:1", null)]
    [InlineData("node", "\"C:\\Program Files\\nodejs\\node.exe\" \"C:\\Program Files\\nodejs\\node_modules\\npm\\bin\\npm-cli.js\" run build", "npm run build")]
    [InlineData("node", "node C:\\Users\\me\\AppData\\Roaming\\npm\\node_modules\\pnpm\\bin\\pnpm.cjs test", "pnpm test")]
    [InlineData("node", "node npm-cli.js run dev", null)]
    [InlineData("node", "node server.js", null)]
    [InlineData("cargo", "cargo build --release", "cargo build")]
    [InlineData("cargo", "cargo run", null)]
    [InlineData("go", "go test ./...", "go test")]
    [InlineData("java", "java -classpath gradle-wrapper.jar org.gradle.wrapper.GradleWrapperMain assemble", "gradle assemble")]
    [InlineData("python", "python -m pytest -q", "pytest")]
    [InlineData("python", "python manage.py runserver", null)]
    [InlineData("cmake", "cmake --build build", "cmake --build")]
    public void Build_commands_are_recognised(string process, string commandLine, string? expected) =>
        Assert.Equal(expected, BuildCommand.Label(process, commandLine));

    [Fact]
    public void Build_pill_text()
    {
        Assert.Equal("✓ dotnet test · 42 s", ProjectsModule.BuildText(new BuildResult("dotnet test", 0, TimeSpan.FromSeconds(42))));
        Assert.Equal("✓ npm run build · 2 min 5 s", ProjectsModule.BuildText(new BuildResult("npm run build", null, TimeSpan.FromSeconds(125))));
        Assert.StartsWith("✗ cargo build · ", ProjectsModule.BuildText(new BuildResult("cargo build", 101, TimeSpan.FromSeconds(9))));
    }

    [Fact]
    public void Command_line_of_this_process_is_readable()
    {
        var cmd = BuildWatcher.CommandLine(Environment.ProcessId);
        Assert.False(string.IsNullOrEmpty(cmd));
        Assert.Contains("testhost", cmd, StringComparison.OrdinalIgnoreCase);
    }
}
