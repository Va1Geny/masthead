using Isle.Core;
using Isle.Views.Projects;

namespace Isle.Modules.Projects;

/// <summary>--snapshot states for the Projects tab and its pills (fixed data; no git, no network).</summary>
static class ProjectsSnapshots
{
    static readonly DateTimeOffset T = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);

    static readonly Repo[] Demo =
    [
        new(@"C:\Users\me\Projects\island", "island", new RepoStatus("dev-edition", 2, 0, 5, 1, true), "me/island", new CiRun(1, "build", CiState.Running, "", T)),
        new(@"C:\Users\me\Projects\api", "api", new RepoStatus("main", 0, 3, 0, 0, true), "me/api", new CiRun(2, "test", CiState.Failed, "", T)),
        new(@"C:\Users\me\Projects\site", "site", new RepoStatus("feature/hero", 1, 0, 0, 0, false), "me/site", new CiRun(3, "deploy", CiState.Passed, "", T)),
        new(@"C:\Users\me\Projects\notes", "notes", new RepoStatus("main", 0, 0, 0, 0, true), null, null),
    ];

    public static void Register(Dictionary<string, Action<IslandWindow>> states)
    {
        states["projects-tab"] = w => Tab(w, Demo, 2);
        states["projects-empty"] = w => Tab(w, [], null);
        states["projects-build-ok"] = w => Pill(w, ProjectsModule.BuildText(new BuildResult("dotnet test", 0, TimeSpan.FromSeconds(42))), "OkBrush");
        states["projects-build-failed"] = w => Pill(w, ProjectsModule.BuildText(new BuildResult("cargo build", 101, TimeSpan.FromSeconds(19))), "DangerBrush");
    }

    static void Tab(IslandWindow w, IReadOnlyList<Repo> repos, int? reviews)
    {
        var view = new ProjectsView(new ProjectsActions(_ => Task.FromResult(repos), _ => Task.FromResult(reviews), () => reviews is not null, () => { }));
        view.Show(repos);
        view.ShowReviews(reviews);
        w.AddTab(new TabInfo(ProjectsModule.TabId, "tab.projects", "Icon.Branch", () => view, 97));
        w.Expand(ProjectsModule.TabId);
    }

    static void Pill(IslandWindow w, string text, string brush) => w.Pill.Set(new PillItem
    {
        Id = "projects.event", Priority = PillPriority.Transient, Width = Math.Clamp(60 + text.Length * 6.5, 160, 300), Peek = true,
        View = () => ProjectsModule.PillView(text, brush),
    });
}
