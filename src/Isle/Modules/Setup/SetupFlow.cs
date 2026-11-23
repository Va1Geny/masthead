using Isle.Core;
using Isle.Modules.Agents;
using Isle.Views.Setup;

namespace Isle.Modules.Setup;

/// <summary>Shows the setup overlay: automatically on the first expand while "setup.done" is unset, and from the settings row.</summary>
public static class SetupFlow
{
    /// <summary>Hooks the first expand of this session (does nothing once setup is done). Unhooks itself after showing.</summary>
    public static void ShowOnFirstExpand(IslandWindow w)
    {
        if (Settings.Current.OptBool("setup.done")) return;
        void OnExpanded(bool expanded)
        {
            if (!expanded) return;
            w.ExpandedChanged -= OnExpanded;
            w.Dispatcher.BeginInvoke(() => Show(w), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        w.ExpandedChanged += OnExpanded;
    }

    public static void Show(IslandWindow w)
    {
        try
        {
            var found = SetupModel.Detect(OnPath, p => File.Exists(p) || Directory.Exists(p), Environment.GetEnvironmentVariable("USERPROFILE") ?? "");
            var kube = SetupModel.KubeconfigExists();
            var (answers, ticked) = SetupModel.Initial(Settings.Current, found, kube);
            w.ShowOverlay(new SetupView(found, kube, answers, ticked, (a, t) => Start(w, a, t), () => Close(w)));
        }
        catch (Exception ex) { Logger.Error("setup", ex); }
    }

    /// <summary>Closing with X counts as done: it never shows again by itself ("Run setup again" still opens it).</summary>
    static void Close(IslandWindow w)
    {
        Settings.Current.SetOpt("setup.done", true);
        Settings.Current.Save("Options");
        w.HideOverlay();
    }

    static void Start(IslandWindow w, IReadOnlyDictionary<string, string> answers, ISet<string> ticked)
    {
        SetupModel.Apply(Settings.Current, answers, ticked);
        HookInstaller.RemoveFromOthers(answers["agent"], HookTarget.All(), DateTime.Now); // only Isle's own entries
        Settings.Current.Save("Modules");
        App.Host.Sync();
        w.HideOverlay();
    }

    /// <summary>True when <paramref name="exe"/> (.exe/.cmd/.bat) is in a PATH folder.</summary>
    static bool OnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var ext in new[] { ".exe", ".cmd", ".bat" })
                try { if (File.Exists(Path.Combine(dir, exe + ext))) return true; }
                catch (ArgumentException) { }
        return false;
    }
}

static class SetupSnapshots
{
    public static void Register(Dictionary<string, Action<IslandWindow>> states)
    {
        // fixed data: a PC with Claude Code and Docker; nothing is read from the machine
        var found = SetupModel.Questions.ToDictionary(q => q.Option, q => q.Choices.Where(c => c.Value is "claude" or "docker").Select(c => c.Value).ToHashSet());
        void Show(IslandWindow w, int step)
        {
            var answers = SetupModel.Preselect(found);
            w.ShowOverlay(new SetupView(found, true, answers, SetupModel.Defaults(answers["agent"], true), (_, _) => { }, () => { }, step));
        }
        states["setup-tools"] = w => Show(w, 0);
        states["setup-shows"] = w => Show(w, 1);
    }
}
