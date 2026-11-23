using System.Text.Json.Nodes;
using Isle.Core;
using Isle.Views.Agents;

namespace Isle.Modules.Agents;

/// <summary>--snapshot states for the Agents tab and pill (demo sessions, no pipe, config files in a temp folder).</summary>
static class AgentSnapshots
{
    public static void Register(Dictionary<string, Action<IslandWindow>> states)
    {
        states["agents-tab"] = w => Tab(w, Demo(), true);
        states["agents-activity"] = w => Tab(w, Activity(), true);
        states["agents-progress"] = w =>
        {
            var book = Activity();
            Tab(w, book, true, v => v.ShowWorkers(book.Sessions[0]));
        };
        states["agents-diff"] = w =>
        {
            var book = Activity();
            Tab(w, book, true, v => v.ShowDiff(book.Sessions[0].LatestChange!));
        };
        states["agents-long-approval"] = w => Tab(w, Demo(longApproval: true), true);
        states["agents-question"] = w => Tab(w, Question(), true);
        states["agents-empty"] = w => Tab(w, new AgentBook(() => DateTimeOffset.Now), true);
        states["agents-setup"] = w => Tab(w, new AgentBook(() => DateTimeOffset.Now), false);
        states["agents-hooks"] = w => Tab(w, Demo(), true, v => v.ShowHooks());
        states["agents-confirm"] = w => Tab(w, Demo(), true, v => v.Confirm(Targets()[1], install: true));
        states["agents-pill-waiting"] = w => w.Pill.Set(AgentsModule.BuildPill(Demo().Sessions).Item!);
        states["agents-pill-working"] = w =>
        {
            var b = new AgentBook(() => DateTimeOffset.Now);
            Feed(b, "codex", "s2", @"C:\Code\weather-api", "PreToolUse", new JsonObject { ["tool_name"] = "shell", ["tool_input"] = new JsonObject { ["command"] = new JsonArray("npm", "run", "build") } });
            Feed(b, "claude", "s4", @"C:\Code\site", "UserPromptSubmit", new JsonObject { ["prompt"] = "fix the header" });
            w.Pill.Set(AgentsModule.BuildPill(b.Sessions).Item!);
        };
        states["agents-pill-done"] = w => w.Pill.Set(AgentsModule.DonePill(Demo().Sessions.Last()));
    }

    static void Tab(IslandWindow w, AgentBook book, bool anyHooks, Action<AgentsView>? then = null)
    {
        var view = new AgentsView(new AgentsActions(_ => { }, _ => { }, _ => { }, _ => { }, (_, _) => { }, _ => { }, Targets, _ => "", _ => "", () => 60));
        view.Show(book.Sessions, anyHooks);
        then?.Invoke(view);
        w.AddTab(new TabInfo(AgentsModule.TabId, "tab.agents", "Icon.Agents", () => view, 101));
        w.Expand(AgentsModule.TabId);
    }

    /// <summary>Claude "installed", Codex detected but not installed — in a temp folder, never the real configs.</summary>
    static IReadOnlyList<HookTarget> Targets()
    {
        var root = Path.Combine(Path.GetTempPath(), "isle-snap-agents");
        var claude = new HookTarget("claude", Path.Combine(root, ".claude", "settings.json"));
        var codex = new HookTarget("codex", Path.Combine(root, ".codex", "hooks.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(claude.ConfigPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(codex.ConfigPath)!);
        var o = new JsonObject();
        HookInstaller.Merge(o, "claude", @"C:\Users\me\AppData\Roaming\Isle\bin\Isle.Hook.exe", 60);
        File.WriteAllText(claude.ConfigPath, o.ToJsonString());
        if (File.Exists(codex.ConfigPath)) File.Delete(codex.ConfigPath);
        return [claude, codex];
    }

    internal static AgentBook Demo(bool longApproval = false)
    {
        var b = new AgentBook(() => DateTimeOffset.Now);
        Feed(b, "claude", "s3", @"C:\Code\notes-app", "Stop", new JsonObject { ["last_assistant_message"] = "Refactored the store; all 42 tests pass." });
        Feed(b, "codex", "s2", @"C:\Code\weather-api", "PreToolUse", new JsonObject { ["tool_name"] = "shell", ["tool_input"] = new JsonObject { ["command"] = new JsonArray("npm", "run", "build") } });
        Feed(b, "claude", "s1", @"C:\Code\island", "PermissionRequest", new JsonObject
        {
            ["tool_name"] = "Bash",
            ["tool_input"] = new JsonObject { ["command"] = longApproval
                ? string.Join("\n", Enumerable.Repeat("Write-Output 'Verify package metadata'", 18))
                : "dotnet test Isle.sln -c Release --filter Agents", ["description"] = "Run the agent tests" },
        }, wait: true);
        return b;
    }

    static AgentBook Question()
    {
        var b = new AgentBook(() => DateTimeOffset.Now);
        Feed(b, "claude", "s1", @"C:\Code\island", "PreToolUse", new JsonObject
        {
            ["tool_name"] = "AskUserQuestion",
            ["tool_input"] = new JsonObject
            {
                ["questions"] = new JsonArray(
                    new JsonObject
                    {
                        ["question"] = "Which database should the sync service use?", ["header"] = "Database",
                        ["options"] = new JsonArray(
                            new JsonObject { ["label"] = "SQLite", ["description"] = "Single file, no server" },
                            new JsonObject { ["label"] = "PostgreSQL", ["description"] = "Server, more features" },
                            new JsonObject { ["label"] = "Keep JSON files", ["description"] = "What we have now" }),
                        ["multiSelect"] = false,
                    },
                    new JsonObject
                    {
                        ["question"] = "Add tests?", ["header"] = "Tests",
                        ["options"] = new JsonArray(new JsonObject { ["label"] = "Yes" }, new JsonObject { ["label"] = "No" }),
                    }),
            },
        }, wait: true);
        return b;
    }

    static AgentBook Activity()
    {
        var b = new AgentBook(() => DateTimeOffset.Now);
        const string cwd = @"C:\Code\island";
        Feed(b, "codex", "s1", cwd, "SubagentStart", new JsonObject { ["agent_id"] = "worker-b1", ["agent_type"] = "worker" });
        Feed(b, "codex", "s1", cwd, "SubagentStart", new JsonObject { ["agent_id"] = "worker-b2", ["agent_type"] = "explorer" });
        Feed(b, "codex", "s1", cwd, "SubagentStop", new JsonObject
        {
            ["agent_id"] = "worker-b2", ["last_assistant_message"] = Strings.Language == "uk" ? "Перевірено обробку хуків." : "Hook handling reviewed.",
        });
        Feed(b, "codex", "s1", cwd, "PostToolUse", new JsonObject
        {
            ["tool_name"] = "apply_patch", ["agent_id"] = "worker-b1",
            ["tool_input"] = new JsonObject { ["command"] = "*** Begin Patch\n*** Update File: AgentBook.cs\n@@\n-        return false;\n+        return worker.Done;\n*** End Patch" },
            ["tool_response"] = "Exit code: 0\nSuccess. Updated the following files:\nM AgentBook.cs",
        });
        b.Sessions[0].Terminal = new(1000, 1);
        return b;
    }

    static void Feed(AgentBook b, string agent, string session, string cwd, string ev, JsonObject extra, bool wait = false)
    {
        var input = new JsonObject { ["session_id"] = session, ["cwd"] = cwd, ["hook_event_name"] = ev };
        foreach (var (k, v) in extra) input[k] = v?.DeepClone();
        b.Apply(new HookMessage(agent, wait, 60, input));
    }
}
