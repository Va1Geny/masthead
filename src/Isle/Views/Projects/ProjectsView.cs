using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Isle.Core;
using Isle.Modules.Projects;

namespace Isle.Views.Projects;

public sealed record ProjectsActions(
    Func<CancellationToken, Task<IReadOnlyList<Repo>>> Refresh,
    Func<CancellationToken, Task<int?>> Reviews,
    Func<bool> HasToken,
    Action Configure);

/// <summary>
/// Projects tab: one row per watched repository — name, branch, local changes, ahead/behind, latest CI run —
/// with Open folder / Open in VS Code. Refreshes on show and every 15 s while shown.
/// </summary>
public sealed class ProjectsView : Grid, ITabView
{
    readonly ProjectsActions _a;
    readonly StackPanel _rows = new();
    readonly TextBlock _reviews = new() { Margin = new Thickness(4, 0, 0, 4), Visibility = Visibility.Collapsed, Cursor = System.Windows.Input.Cursors.Hand };
    DispatcherTimer? _timer;
    CancellationTokenSource? _cts;

    public ProjectsView(ProjectsActions actions)
    {
        _a = actions;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _reviews.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
        _reviews.MouseLeftButtonUp += (_, _) => ProcessOps.Open("https://github.com/pulls/review-requested");
        Children.Add(_reviews);
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
            var reviews = _a.HasToken() ? await _a.Reviews(cts.Token) : null;
            if (!cts.IsCancellationRequested) ShowReviews(reviews);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.Error("projects refresh", ex); }
    }

    internal void ShowReviews(int? count)
    {
        _reviews.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (count > 0) _reviews.Text = Strings.T("projects.reviews", count);
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
    }

    static FrameworkElement Row(Repo r)
    {
        var row = new DockPanel { Margin = new Thickness(4, 0, 0, 4) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(Action("Icon.Folder", Strings.T("projects.openFolder"), () => ProcessOps.Open(r.Path)));
        buttons.Children.Add(Action("Icon.Code", Strings.T("projects.openCode"), () => ProcessOps.Open("vscode://file/" + r.Path.Replace('\\', '/'))));
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);

        if (r.GitHub is not null)
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
        var url = r.Ci?.Url is { Length: > 0 } u ? u : $"https://github.com/{r.GitHub}/actions";
        var b = Ui.IconButton(t, 28, () => ProcessOps.Open(url), "projects ci");
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
