using System.Text.Json.Nodes;
using Isle.Agents;
using Isle.Modules.Agents;

namespace Isle.Tests;

public class AgentSwitchTests
{
    static readonly string Exe = Path.Combine(AppContext.BaseDirectory, HookInstaller.ExeName);
    const string UserHook = "{\"hooks\":{\"Stop\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"echo mine\"}]}]},\"theme\":\"dark\"}";

    static string Config(TempDir dir, string agent, string text, string name)
    {
        var path = Path.Combine(dir.Path, "." + agent, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var root = JsonNode.Parse(text)!.AsObject();
        HookInstaller.Merge(root, agent, Exe, 60);
        File.WriteAllText(path, root.ToJsonString());
        return path;
    }

    [Fact]
    public void Switching_removes_only_isle_entries_from_the_other_agents()
    {
        using var dir = new TempDir();
        var claude = new HookTarget("claude", Config(dir, "claude", UserHook, "settings.json"));
        var codex = new HookTarget("codex", Config(dir, "codex", "{}", "hooks.json"));
        var gemini = new HookTarget("gemini", Config(dir, "gemini", UserHook, "settings.json"));

        var cleaned = HookInstaller.RemoveFromOthers("codex", [claude, codex, gemini], DateTime.Now);

        Assert.Equal(["claude", "gemini"], cleaned);
        Assert.True(HookInstaller.IsInstalled(codex));
        Assert.False(HookInstaller.IsInstalled(claude));
        Assert.False(HookInstaller.IsInstalled(gemini));
        var left = JsonNode.Parse(File.ReadAllText(claude.ConfigPath))!;
        Assert.Equal("echo mine", (string?)left["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]);
        Assert.Equal("dark", (string?)left["theme"]);
        Assert.Single(left["hooks"]!.AsObject());
    }

    [Fact]
    public void None_removes_everywhere_and_a_broken_config_is_left_alone()
    {
        using var dir = new TempDir();
        var codex = new HookTarget("codex", Config(dir, "codex", "{}", "hooks.json"));
        var bad = new HookTarget("claude", Path.Combine(dir.Path, "bad.json"));
        File.WriteAllText(bad.ConfigPath, "{ not json");
        HookInstaller.RemoveFromOthers("none", [bad, codex], DateTime.Now);
        Assert.False(HookInstaller.IsInstalled(codex));
        Assert.Equal("{ not json", File.ReadAllText(bad.ConfigPath));
    }

    [Fact]
    public void Gemini_entries_use_its_event_names_and_millisecond_timeout()
    {
        var root = new JsonObject();
        HookInstaller.Merge(root, "gemini", @"C:\Program Files\Isle\Isle.Hook.exe", 60);
        var hooks = root["hooks"]!.AsObject();
        Assert.Equal(["SessionStart", "BeforeAgent", "BeforeTool", "AfterAgent", "SessionEnd"], hooks.Select(p => p.Key));
        var h = hooks["BeforeTool"]![0]!["hooks"]![0]!;
        Assert.Equal(@"""C:\Program Files\Isle\Isle.Hook.exe"" gemini --async", (string?)h["command"]);
        Assert.Equal(10_000, (int?)h["timeout"]);
    }

    [Fact]
    public void Gemini_events_map_to_the_internal_ones()
    {
        string Event(string name)
        {
            var line = HookProtocol.Request("gemini", false, 60, new JsonObject { ["session_id"] = "g1", ["cwd"] = @"C:\Code\weather-api", ["hook_event_name"] = name });
            return HookMessage.Parse(line)!.Event;
        }
        Assert.Equal("UserPromptSubmit", Event("BeforeAgent"));
        Assert.Equal("PreToolUse", Event("BeforeTool"));
        Assert.Equal("Stop", Event("AfterAgent"));
        Assert.Equal("SessionStart", Event("SessionStart"));
        var book = new AgentBook(() => DateTimeOffset.Now);
        book.Apply(HookMessage.Parse(HookProtocol.Request("gemini", false, 60, new JsonObject { ["session_id"] = "g1", ["cwd"] = @"C:\Code\weather-api", ["hook_event_name"] = "BeforeTool", ["tool_name"] = "run_shell_command" }))!);
    }
}
