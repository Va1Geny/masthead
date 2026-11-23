using Isle.Core;
using Isle.Modules.Kube;

namespace Isle.Modules.Setup;

/// <summary>One answer to a "Your tools" question. Detected when <c>Exe</c> is on PATH, <c>Dir</c> exists in the user profile or <c>Path</c> exists.</summary>
public sealed record SetupChoice(string Value, string LabelKey, string? Exe = null, string? Dir = null, string? Path = null);

/// <summary>A "Your tools" question; the last choice is always "none".</summary>
public sealed record SetupQuestion(string Option, string LabelKey, SetupChoice[] Choices);

/// <summary>Something Isle can show: <c>Id</c> is the module id. Items that need internet are never pre-ticked.</summary>
public sealed record SetupItem(string Id, string LabelKey, bool Alert = false, bool Online = false);

/// <summary>Pure rules of the first-run setup (data-driven; Phase 2 adds entries to the two lists).</summary>
public static class SetupModel
{
    public const int Limit = 10;
    public const string None = "none";

    static readonly SetupChoice NoChoice = new(None, "setup.none");

    public static readonly SetupQuestion[] Questions =
    [
        new("agent", "setup.agent", [new("claude", "setup.claude", "claude", ".claude"), new("codex", "setup.codex", "codex", ".codex"), NoChoice]),
        new("git", "setup.git", [new("github", "setup.github", "gh"), NoChoice]),
        new("containers", "setup.containers", [new("docker", "setup.docker", "docker", Path: @"\\.\pipe\docker_engine"), NoChoice]),
    ];

    public static readonly SetupItem[] Items =
    [
        new("sysmon", "tab.sysmon"), new("ports", "tab.ports"), new("projects", "tab.projects"), new("agents", "tab.agents"), new("usage", "tab.usage"),
        new("media", "setup.item.media", Alert: true), new("kube", "setup.item.kube", Alert: true), new("logs", "setup.item.logs", Alert: true),
    ];

    /// <summary>Values found on this PC per option (cheap checks only: no process is started).</summary>
    public static Dictionary<string, HashSet<string>> Detect(Func<string, bool> onPath, Func<string, bool> exists, string profile) =>
        Questions.ToDictionary(q => q.Option, q => q.Choices
            .Where(c => (c.Exe is not null && onPath(c.Exe)) || (c.Dir is not null && profile.Length > 0 && exists(System.IO.Path.Combine(profile, c.Dir))) || (c.Path is not null && exists(c.Path)))
            .Select(c => c.Value).ToHashSet());

    /// <summary>Answers pre-selected from what was found: the first detected choice, else "none".</summary>
    public static Dictionary<string, string> Preselect(IReadOnlyDictionary<string, HashSet<string>> found) =>
        Questions.ToDictionary(q => q.Option, q => q.Choices.FirstOrDefault(c => found[q.Option].Contains(c.Value))?.Value ?? None);

    /// <summary>Agents and Usage only make sense with an AI agent.</summary>
    public static bool Offered(string id, string agent) => agent != None || id is not ("agents" or "usage");

    public static HashSet<string> Defaults(string agent, bool kubeconfigExists) =>
        Items.Where(i => !i.Online && Offered(i.Id, agent) && (i.Id != "kube" || kubeconfigExists)).Select(i => i.Id).ToHashSet();

    public static bool CanTick(ISet<string> ticked, string id, int limit = Limit) => ticked.Contains(id) || ticked.Count < limit;

    public static bool KubeconfigExists() =>
        KubeConfig.Paths(Environment.GetEnvironmentVariable("KUBECONFIG"), Environment.GetEnvironmentVariable("USERPROFILE") ?? "").Any(File.Exists);

    /// <summary>The state to open with: the saved choices when setup was done before, else the detected defaults.</summary>
    public static (Dictionary<string, string> Answers, HashSet<string> Ticked) Initial(Settings s, IReadOnlyDictionary<string, HashSet<string>> found, bool kubeconfig)
    {
        var answers = Preselect(found);
        if (!s.OptBool("setup.done"))
            return (answers, Defaults(answers["agent"], kubeconfig));
        foreach (var q in Questions)
            if (q.Choices.Any(c => c.Value == s.Opt("setup." + q.Option))) answers[q.Option] = s.Opt("setup." + q.Option);
        return (answers, Items.Where(i => Offered(i.Id, answers["agent"]) && s.IsModuleEnabled(i.Id)).Select(i => i.Id).ToHashSet());
    }

    /// <summary>Start: module switches and option answers only; the caller saves and syncs the modules.</summary>
    public static void Apply(Settings s, IReadOnlyDictionary<string, string> answers, ISet<string> ticked)
    {
        foreach (var i in Items) s.Modules[i.Id] = ticked.Contains(i.Id) && Offered(i.Id, answers["agent"]);
        foreach (var q in Questions) s.SetOpt("setup." + q.Option, answers[q.Option]);
        s.SetOpt("setup.done", true);
    }
}
