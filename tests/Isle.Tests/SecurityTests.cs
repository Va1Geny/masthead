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
}
