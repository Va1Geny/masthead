using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Isle.Agents;
using Isle.Core;
using Isle.Modules.Agents;
using Isle.Modules.Docker;
using Isle.Modules.Projects;
using Isle.Modules.Servers;

namespace Isle.Tests;

/// <summary>Trust-boundary checks: what outside data (servers, images, configs, other machines) can make Isle open, run or buffer.</summary>
public class SecurityTests
{
    [Fact]
    public void Only_http_and_https_links_are_opened_and_they_are_re_escaped()
    {
        Assert.Equal("https://gitlab.example.com/g/p/-/merge_requests/1", ProcessOps.WebUrl("https://gitlab.example.com/g/p/-/merge_requests/1"));
        Assert.Equal("http://localhost:5173/", ProcessOps.WebUrl("http://localhost:5173"));
        Assert.Equal("https://example.com/a%20b%22%20--x", ProcessOps.WebUrl("https://example.com/a b\" --x"));
        foreach (var bad in new[] { "file:///C:/Windows/System32/calc.exe", @"\\attacker\share\x.exe", @"C:\Windows\System32\calc.exe", "calc.exe",
                     "ms-msdt:/id PCWDiagnostic", "search-ms:query=x", "javascript:alert(1)", "vscode://file/C:/x", "https://", "" })
            Assert.Null(ProcessOps.WebUrl(bad));
    }

    [Fact]
    public void Compose_actions_refuse_labels_compose_would_not_write()
    {
        Assert.NotNull(DockerParse.ComposeArgs("shop_1-x", @"C:\demo\shop", @"C:\demo\shop\compose.yaml", "up", "-d"));
        Assert.NotNull(DockerParse.ComposeArgs("shop", null, "D:/work/compose.yaml", "down"));
        Assert.Null(DockerParse.ComposeArgs("shop", @"\\attacker\share", null, "up", "-d"));            // UNC project dir
        Assert.Null(DockerParse.ComposeArgs("shop", @"C:\demo", @"C:\demo\a.yaml,\\attacker\s\b.yaml", "up")); // UNC compose file
        Assert.Null(DockerParse.ComposeArgs("shop", @"\\?\C:\demo", null, "up"));                          // device path
        Assert.Null(DockerParse.ComposeArgs("shop", @"..\demo", null, "up"));                              // relative
        Assert.Null(DockerParse.ComposeArgs("shop", "/home/me/demo", null, "up"));                         // not a Windows path
        Assert.Null(DockerParse.ComposeArgs("--file=x", @"C:\demo", null, "up"));                          // not a project name
        Assert.Null(DockerParse.ComposeArgs("", @"C:\demo", null, "up"));
    }

    [Fact]
    public void Log_follow_memory_stays_bounded_whatever_the_container_writes()
    {
        // one endless line is cut at MaxLine
        var d = new LogDemux();
        var lines = d.Push(Encoding.UTF8.GetBytes(new string('x', LogDemux.MaxLine * 2 + 10)));
        Assert.Equal([LogDemux.MaxLine, LogDemux.MaxLine], lines.Select(l => l.Length));
        Assert.Equal(10, d.Flush().Single().Length);

        // TTY output that starts like a frame header with a huge length is passed through, not buffered
        var tty = new LogDemux();
        Assert.Empty(tty.Push([1, 0, 0, 0, 0x7f, 0xff, 0xff, 0xff]));
        Assert.EndsWith("more", tty.Push("more\n"u8).Single());
    }

    [Fact]
    public void Ssh_aliases_that_could_break_out_of_the_command_line_are_skipped()
    {
        foreach (var ok in new[] { "web-1", "prod.db", "deploy@bastion", "host:2222", "сервер" }) Assert.True(ServerInfo.SafeAlias(ok), ok);
        foreach (var bad in new[] { "-oProxyCommand=calc", "web;calc", "a&b", "a|b", "a\"b", "x*", "!neg", "" }) Assert.False(ServerInfo.SafeAlias(bad), bad);

        using var dir = new TempDir();
        File.WriteAllText(dir.File("config"), "Host good web;calc -oProxyCommand=calc\n  HostName example.com\n");
        Assert.Equal(["good"], ServerInfo.ParseSsh(dir.File("config"), dir.Path).Select(h => h.Alias));
    }

    [Fact]
    public void GitLab_token_goes_only_to_the_configured_host()
    {
        Assert.Equal("https://gitlab.example.com/api/v4/user", GitHosts.GitLabApi("gitlab.example.com", "/user")?.AbsoluteUri);
        Assert.Equal("https://gitlab.example.com:8443/api/v4/user", GitHosts.GitLabApi("gitlab.example.com:8443", "/user")?.AbsoluteUri);
        foreach (var bad in new[] { "gitlab.example.com@attacker.example", "attacker.example/x", "attacker.example?", "attacker.example#", @"a\b", "" })
            Assert.Null(GitHosts.GitLabApi(bad, "/user"));
    }

    [Fact]
    public void Shell_hook_commands_refuse_paths_a_shell_would_interpret()
    {
        Assert.Throws<NotSupportedException>(() => HookInstaller.Merge(new JsonObject(), "codex", @"C:\Users\a&b\AppData\Roaming\Isle\bin\Isle.Hook.exe", 60));
        Assert.Throws<NotSupportedException>(() => HookInstaller.Merge(new JsonObject(), "gemini", @"C:\Users\o'neil\Isle\bin\Isle.Hook.exe", 60));
        var plain = new JsonObject();
        HookInstaller.Merge(plain, "codex", @"C:\Users\RUNNER~1\Isle\bin\Isle.Hook.exe", 60);
        Assert.Equal(@"C:\Users\RUNNER~1\Isle\bin\Isle.Hook.exe codex --timeout 60", (string?)plain["hooks"]!["PermissionRequest"]![0]!["hooks"]![0]!["command"]);
        // the exec form (Claude Code) runs no shell, so any path is fine there
        HookInstaller.Merge(new JsonObject(), "claude", @"C:\Users\a&b\AppData\Roaming\Isle\bin\Isle.Hook.exe", 60);
    }

    [Fact]
    public async Task Hook_pipe_drops_clients_from_other_machines()
    {
        var name = "Isle.Agents.test." + Guid.NewGuid().ToString("N");
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new AgentPipeServer(name, (_, _) => { handled.TrySetResult(); return Task.FromResult<string?>(null); });
        server.Start();
        // \\127.0.0.1\pipe\… goes through the SMB server, exactly like a client on another machine signed in as this user.
        using var client = new NamedPipeClientStream("127.0.0.1", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await client.ConnectAsync(3000); }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException) { return; } // no SMB server here: nothing to check
        try
        {
            await HookProtocol.WriteLineAsync(client, HookProtocol.Request("claude", false, 5, new JsonObject { ["hook_event_name"] = "Stop" }), CancellationToken.None);
            Assert.Equal(0, await client.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5))); // closed without a reply
        }
        catch (IOException) { } // or reset before the write
        await Task.Delay(300);
        Assert.False(handled.Task.IsCompleted);
    }
}
