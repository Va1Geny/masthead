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
        new(@"C:\Users\me\Projects\infra", "infra", new RepoStatus("main", 0, 1, 0, 0, true), null, new CiRun(4, "pipeline", CiState.Passed, "", T), "team/infra"),
        new(@"C:\Users\me\Projects\api", "api", new RepoStatus("main", 0, 3, 0, 0, true), "me/api", new CiRun(2, "test", CiState.Failed, "", T)),
        new(@"C:\Users\me\Projects\site", "site", new RepoStatus("feature/hero", 1, 0, 0, 0, false), "me/site", new CiRun(3, "deploy", CiState.Passed, "", T)),
        new(@"C:\Users\me\Projects\notes", "notes", new RepoStatus("main", 0, 0, 0, 0, true), null, null),
    ];

    public static void Register(Dictionary<string, Action<IslandWindow>> states)
    {
        states["projects-tab"] = w => Tab(w, Demo, Reviews, Builds);
        states["projects-empty"] = w => Tab(w, [], null, null);
        states["projects-build-ok"] = w => Pill(w, ProjectsModule.BuildText(new BuildResult("dotnet test", 0, TimeSpan.FromSeconds(42))), "OkBrush");
        states["projects-build-failed"] = w => Pill(w, ProjectsModule.BuildText(new BuildResult("cargo build", 101, TimeSpan.FromSeconds(19))), "DangerBrush");
    }

    static readonly ReviewItem[] Reviews =
    [
        new("me/api", "Add retry to the sync client", "", "alex"),
        new("team/infra", "Bump base image to the new LTS", "", "sam"),
    ];

    static readonly (string, IReadOnlyList<BuildRun>)[] Builds =
    [
        ("dotnet test", [new(48, true), new(52, true), new(61, false), new(44, true), new(42, true)]),
        ("npm run build", [new(19, true), new(21, true), new(18, true)]),
    ];

    static void Tab(IslandWindow w, IReadOnlyList<Repo> repos, IReadOnlyList<ReviewItem>? reviews, IReadOnlyList<(string, IReadOnlyList<BuildRun>)>? builds)
    {
        var view = new ProjectsView(new ProjectsActions(_ => Task.FromResult(repos), _ => Task.FromResult(reviews), () => builds ?? [], (_, _) => Task.FromResult(true), (_, _) => Task.FromResult(PullResult.UpToDate), () => { }));
        view.Show(repos);
        view.ShowReviews(reviews, builds);
        w.AddTab(new TabInfo(ProjectsModule.TabId, "tab.projects", "Icon.Branch", () => view, 97));
        w.Expand(ProjectsModule.TabId);
    }

    static void Pill(IslandWindow w, string text, string brush) => w.Pill.Set(new PillItem
    {
        Id = "projects.event", Priority = PillPriority.Transient, Width = Math.Clamp(60 + text.Length * 6.5, 160, 300), Peek = true,
        View = () => ProjectsModule.PillView(text, brush),
    });
}
