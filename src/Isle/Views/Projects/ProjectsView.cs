using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Isle.Core;
using Isle.Modules.Projects;

namespace Isle.Views.Projects;

public sealed record ProjectsActions(
    Func<CancellationToken, Task<IReadOnlyList<Repo>>> Refresh,
    Func<CancellationToken, Task<IReadOnlyList<ReviewItem>?>> Reviews,
    Func<IReadOnlyList<(string Label, IReadOnlyList<BuildRun> Runs)>> Builds,
    Func<string, CancellationToken, Task<bool>> Fetch,
    Func<string, CancellationToken, Task<PullResult>> Pull,
    Action Configure);

/// <summary>
/// Projects tab: one row per watched repository — name, branch, local changes, ahead/behind, latest CI run —
/// with Open folder / Open in VS Code. Refreshes on show and every 15 s while shown.
/// </summary>
public sealed class ProjectsView : Grid, ITabView
{
    readonly ProjectsActions _a;
    readonly StackPanel _rows = new();
    readonly StackPanel _top = new();
    readonly TextBlock _msg = new() { Margin = new Thickness(4, 0, 0, 4), Visibility = Visibility.Collapsed, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11.5 };
    DispatcherTimer? _timer;
    CancellationTokenSource? _cts;

    public ProjectsView(ProjectsActions actions)
    {
        _a = actions;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _msg.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        _top.Children.Add(_msg);
        Children.Add(_top);
        var scroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SetRow(scroll, 1);
        Children.Add(scroll);
    }

    public void OnShown()
    {
        _timer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
        OnTick(null, EventArgs.Empty);
    }

    public void OnHidden()
    {
        _timer?.Stop();
        _cts?.Cancel();
    }

    async void OnTick(object? sender, EventArgs e)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        try
        {
            var repos = await _a.Refresh(cts.Token);
            if (cts.IsCancellationRequested) return;
            Show(repos);
            var reviews = await _a.Reviews(cts.Token);
            if (!cts.IsCancellationRequested) ShowReviews(reviews, _a.Builds());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.Error("projects refresh", ex); }
    }

    /// <summary>Header of the tab: PRs/MRs waiting on the user (null/empty = hidden) and the build-time trend per command.</summary>
    internal void ShowReviews(IReadOnlyList<ReviewItem>? reviews, IReadOnlyList<(string Label, IReadOnlyList<BuildRun> Runs)>? builds = null)
    {
        while (_top.Children.Count > 1) _top.Children.RemoveAt(1);
        if (reviews is { Count: > 0 })
        {
            var head = Ui.Dim(Strings.T("projects.reviews", reviews.Count), wrap: false);
            head.Margin = new Thickness(4, 0, 0, 2);
            _top.Children.Add(head);
            foreach (var r in reviews.Take(3))
            {
                var t = new TextBlock { Text = $"{r.Repo}  ·  {r.Title}", Margin = new Thickness(12, 0, 0, 1), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11.5, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = r.Author };
                t.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
                var url = r.Url;
                t.MouseLeftButtonUp += (_, _) => ProcessOps.Open(url);
                _top.Children.Add(t);
            }
        }
        foreach (var (label, runs) in builds ?? [])
        {
            if (runs.Count == 0) continue;
            var t = Ui.Dim($"{label}  {BuildHistory.Spark(runs)}  {BuildHistory.Secs(runs[^1].Seconds)}" + (runs.Count > 1 ? "  ·  " + Strings.T("projects.avg", BuildHistory.Secs(runs.Average(x => x.Seconds))) : ""), wrap: false);
            t.Margin = new Thickness(4, 0, 0, 1);
            _top.Children.Add(t);
        }
        if (_top.Children.Count > 1) ((FrameworkElement)_top.Children[^1]).Margin = new Thickness(4, 0, 0, 6);
    }

    async void Run(Repo r, bool pull)
    {
        var cts = _cts ??= new CancellationTokenSource();
        try
        {
            _msg.Text = r.Name + ": …";
            _msg.Visibility = Visibility.Visible;
            _msg.Text = r.Name + ": " + (pull
                ? Strings.T("projects.pull." + (await _a.Pull(r.Path, cts.Token)).ToString().ToLowerInvariant())
                : Strings.T(await _a.Fetch(r.Path, cts.Token) ? "projects.fetched" : "projects.pull.failed"));
            OnTick(null, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
    }

    internal void Show(IReadOnlyList<Repo> repos)
    {
        _rows.Children.Clear();
        if (repos.Count == 0)
        {
            var hint = Ui.Dim(Strings.T("projects.none"), wrap: true);
            hint.Margin = new Thickness(4, 4, 4, 8);
            _rows.Children.Add(hint);
            var add = Ui.TextButton(Strings.T("projects.add"), "FlatButton", new Thickness(12, 4, 12, 4), _a.Configure, "projects configure");
            add.HorizontalAlignment = HorizontalAlignment.Left;
            add.Margin = new Thickness(4, 0, 0, 0);
            _rows.Children.Add(add);
            return;
        }
        foreach (var r in repos) _rows.Children.Add(Row(r));
        // Row is an instance method so the git buttons can report into the header
    }

    FrameworkElement Row(Repo r)
    {
        var row = new DockPanel { Margin = new Thickness(4, 0, 0, 4) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (r.Status is { HasUpstream: true })
        {
            buttons.Children.Add(Action("Icon.Reset", Strings.T("projects.fetch"), () => Run(r, false)));
            buttons.Children.Add(Action("Icon.Download", Strings.T("projects.pull"), () => Run(r, true)));
        }
        buttons.Children.Add(Action("Icon.Folder", Strings.T("projects.openFolder"), () => ProcessOps.Open(r.Path)));
        buttons.Children.Add(Action("Icon.Code", Strings.T("projects.openCode"), () => ProcessOps.Open("vscode://file/" + r.Path.Replace('\\', '/'))));
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);

        if (r.GitHub is not null || r.GitLab is not null)
        {
            var ci = CiBadge(r);
            DockPanel.SetDock(ci, Dock.Right);
            row.Children.Add(ci);
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = r.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        text.Children.Add(name);
        var detail = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11.5 };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
        if (r.Status is not { } s) detail.Text = Strings.T("projects.notRepo");
        else
        {
            var parts = new List<string> { s.Branch };
            parts.Add(s.Changed + s.Untracked == 0 ? Strings.T("projects.clean") : Strings.T("projects.changes", s.Changed + s.Untracked));
            if (s.Ahead > 0) parts.Add("↑" + s.Ahead);
            if (s.Behind > 0) parts.Add("↓" + s.Behind);
            if (!s.HasUpstream && s.Branch != "detached") parts.Add(Strings.T("projects.noUpstream"));
            detail.Text = string.Join("  ·  ", parts);
        }
        text.Children.Add(detail);
        row.Children.Add(text);
        return row;
    }

    static FrameworkElement CiBadge(Repo r)
    {
        var (glyph, brush, key) = r.Ci?.State switch
        {
            CiState.Passed => ("✓", "OkBrush", "projects.ciPassed"),
            CiState.Failed => ("✗", "DangerBrush", "projects.ciFailed"),
            CiState.Running => ("●", "WarnBrush", "projects.ciRunning"),
            CiState.Cancelled => ("–", "FgDimBrush", "projects.ciCancelled"),
            _ => ("CI", "FgFaintBrush", "projects.ciNone"),
        };
        var t = new TextBlock { Text = glyph, FontSize = 13, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        var url = r.Ci?.Url is { Length: > 0 } u ? u : r.GitLab is not null ? null : $"https://github.com/{r.GitHub}/actions";
        var b = Ui.IconButton(t, 28, () => { if (url is not null) ProcessOps.Open(url); }, "projects ci");
        var tip = Strings.T(key) + (r.Ci is { Name.Length: > 0 } run ? " · " + run.Name : "");
        b.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        b.Margin = new Thickness(0, 0, 4, 0);
        return b;
    }

    static Button Action(string icon, string tip, Action click)
    {
        var b = Ui.IconButton(Ui.MakeIcon(icon, 14), 28, click, "projects open");
        b.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        return b;
    }
}
