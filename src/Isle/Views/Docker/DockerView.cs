using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Isle.Core;
using Isle.Modules.Docker;

namespace Isle.Views.Docker;

/// <summary>Docker tab: containers grouped by compose project, start/stop/restart, log tail, compose up/restart/down, disk usage and prune.</summary>
public sealed class DockerView : Grid, ITabView
{
    readonly DockerModule _m;
    readonly IslandWindow _island;
    readonly StackPanel _rows = new();
    readonly TextBlock _disk = Ui.Dim("");
    readonly Border _overlay = new() { Visibility = Visibility.Collapsed };
    readonly Button _prune;
    DiskUsage? _df;
    string? _confirm; // "stop:<id>" or "down:<project>": the second click does it
    const int MaxLogLines = 500;
    readonly List<string> _log = [];
    CancellationTokenSource? _logCts;
    TextBlock? _logText;
    ScrollViewer? _logScroll;

    public DockerView(DockerModule m, IslandWindow island)
    {
        _m = m;
        _island = island;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var foot = new DockPanel { Margin = new Thickness(4, 4, 0, 0) };
        var prune = _prune = Ui.IconAction("Icon.Trash", "docker.prune", () => ShowPrune(_df), "docker prune", "FgBrush");
        DockPanel.SetDock(prune, Dock.Right);
        foot.Children.Add(prune);
        _disk.VerticalAlignment = VerticalAlignment.Center;
        foot.Children.Add(_disk);
        SetRow(foot, 1);
        Children.Add(foot);

        _overlay.SetResourceReference(Border.BackgroundProperty, "BgBrush");
        SetRowSpan(_overlay, 2);
        Children.Add(_overlay);
        Unloaded += (_, _) => StopLogs();
    }

    public void OnShown()
    {
        _m.Changed += OnChanged;
        OnChanged();
        _ = LoadDf();
    }

    public void OnHidden() { _m.Changed -= OnChanged; _confirm = null; Hide(); }

    void OnChanged() => Render(_df);

    async Task LoadDf()
    {
        try
        {
            if (_m.Api is null || !_m.Up) return;
            _df = await _m.Api.DfAsync();
            Render(_df);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { }
    }

    internal void Render(DiskUsage? df)
    {
        if (!_m.Up) { df = null; if (_logCts is not null) Hide(); } // engine down: the log stream is dead, close its view
        _df = df;
        _prune.Visibility = _m.Up ? Visibility.Visible : Visibility.Collapsed;
        _disk.Text = df is null ? "" : Strings.T("docker.disk", DockerParse.Size(df.Images), DockerParse.Size(df.Volumes), DockerParse.Size(df.BuildCache));
        _rows.Children.Clear();
        if (!_m.Up || _m.Boxes.Count == 0)
        {
            var empty = Ui.Dim(Strings.T(_m.Up ? "docker.none" : "docker.engineDown", Engine), wrap: true);
            empty.Margin = new Thickness(4, 6, 4, 0);
            _rows.Children.Add(empty);
            return;
        }
        foreach (var (project, boxes) in DockerParse.Group(_m.Boxes))
        {
            _rows.Children.Add(Header(project, boxes));
            foreach (var b in boxes) _rows.Children.Add(Row(b));
        }
    }

    string Engine => _m.Engine == "podman" ? "Podman" : "Docker";

    FrameworkElement Header(string project, List<Box> boxes)
    {
        var head = new DockPanel { Margin = new Thickness(4, 8, 0, 3) };
        // compose actions only from labels that pass ComposeArgs' checks (any image can set them)
        var first = boxes.FirstOrDefault(b => (b.WorkDir ?? b.ConfigFiles) is not null && DockerParse.ComposeArgs(project, b.WorkDir, b.ConfigFiles) is not null);
        if (first is not null)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(Act("Icon.Play", Strings.T("docker.up", project), () => Compose(project, first, "up", "-d")));
            buttons.Children.Add(Act("Icon.Reset", Strings.T("docker.restartAll", project), () => Compose(project, first, "restart")));
            buttons.Children.Add(_confirm == "down:" + project
                ? Ui.TextButton(Strings.T("docker.downConfirm"), "AccentButton", new Thickness(8, 1, 8, 1), () => { _confirm = null; Compose(project, first, "down"); }, "docker down")
                : Act("Icon.Stop", Strings.T("docker.down.tip", project), () => { _confirm = "down:" + project; Render(_df); }));
            DockPanel.SetDock(buttons, Dock.Right);
            head.Children.Add(buttons);
        }
        var caption = new TextBlock { Text = project.Length > 0 ? project : Strings.T("docker.standalone"), VerticalAlignment = VerticalAlignment.Center };
        caption.SetResourceReference(StyleProperty, "TextCaption");
        head.Children.Add(caption);
        return head;
    }

    FrameworkElement Row(Box b)
    {
        var row = new DockPanel { Margin = new Thickness(4, 0, 0, 2) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Act("Icon.Code", Strings.T("docker.logs", b.Name), () => _ = Logs(b)));
        if (b.Running)
        {
            buttons.Children.Add(Act("Icon.Reset", Strings.T("docker.restart", b.Name), () => _ = DoAct(b, "restart")));
            buttons.Children.Add(_confirm == "stop:" + b.Id
                ? Ui.TextButton(Strings.T("docker.stopConfirm"), "AccentButton", new Thickness(8, 1, 8, 1), () => { _confirm = null; _ = DoAct(b, "stop"); }, "docker stop")
                : Act("Icon.Stop", Strings.T("docker.stop", b.Name), () => { _confirm = "stop:" + b.Id; Render(_df); }));
        }
        else buttons.Children.Add(Act("Icon.Play", Strings.T("docker.start", b.Name), () => _ = DoAct(b, "start")));
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);

        var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, b.Running ? "OkBrush" : "FgDimBrush");
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);

        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = b.Status };
        text.Inlines.Add(new System.Windows.Documents.Run(b.Name));
        var detail = new System.Windows.Documents.Run("  ·  " + b.Detail);
        detail.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "FgDimBrush");
        text.Inlines.Add(detail);
        text.SetResourceReference(StyleProperty, "Text");
        row.Children.Add(text);
        return row;
    }

    static Button Act(string icon, string tip, Action click)
    {
        var b = Ui.IconButton(Ui.MakeIcon(icon, 12), 26, click, "docker action");
        b.ToolTip = tip;
        AutomationProperties.SetName(b, tip);
        return b;
    }

    async Task DoAct(Box b, string action)
    {
        try { await _m.Api!.ActAsync(b.Id, action); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        { _island.ShowToast(Strings.T("docker.failed", b.Name)); }
    }

    async void Compose(string project, Box any, params string[] verb)
    {
        _island.ShowToast(Strings.T("docker.composeRunning", project));
        var (code, _) = await Cli.RunAsync(_m.Engine, DockerParse.ComposeArgs(project, any.WorkDir, any.ConfigFiles, verb)!, any.WorkDir, TimeSpan.FromMinutes(5));
        _island.ShowToast(Strings.T(code == 0 ? "docker.composeDone" : "docker.composeFailed", project));
    }

    /// <summary>Opens the log view and follows the container's log until the view closes (see <see cref="StopLogs"/>).</summary>
    async Task Logs(Box b)
    {
        ShowLogs(b.Name, "");
        var cts = _logCts = new CancellationTokenSource();
        try
        {
            await _m.Api!.FollowLogsAsync(b.Id, lines => Dispatcher.BeginInvoke(() => { if (!cts.IsCancellationRequested) AppendLog(lines); }), cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException)
        { if (!cts.IsCancellationRequested && _log.Count == 0) _island.ShowToast(Strings.T("docker.failed", b.Name)); }
    }

    /// <summary>Cancels the followed stream: view closed or replaced, tab hidden, engine down. The module stopping disposes the api, which ends it too.</summary>
    void StopLogs()
    {
        var cts = _logCts;
        _logCts = null;
        _logText = null;
        _logScroll = null;
        cts?.Cancel();
        cts?.Dispose();
    }

    internal void ShowLogs(string name, string text)
    {
        var t = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        var scroll = new ScrollViewer { Content = t, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = true };
        Overlay(Strings.T("docker.logsTitle", name), scroll); // also stops the previous stream
        (_logText, _logScroll) = (t, scroll);
        _log.Clear();
        AppendLog(text.Length == 0 ? [] : text.TrimEnd().Split('\n'));
    }

    /// <summary>UI thread. Keeps the last <see cref="MaxLogLines"/> lines; follows the end only when the view was already at the bottom.</summary>
    internal void AppendLog(IReadOnlyList<string> lines)
    {
        if (_logText is not { } t || _logScroll is not { } scroll) return;
        var atBottom = scroll.VerticalOffset >= scroll.ScrollableHeight - 1;
        _log.AddRange(lines);
        if (_log.Count > MaxLogLines) _log.RemoveRange(0, _log.Count - MaxLogLines);
        t.Text = _log.Count == 0 ? Strings.T("docker.noLogs") : string.Join('\n', _log);
        if (!atBottom) return;
        scroll.UpdateLayout();
        scroll.ScrollToEnd();
    }

    /// <summary>Confirm overlay: prune never touches volumes.</summary>
    internal void ShowPrune(DiskUsage? df)
    {
        var body = new StackPanel();
        var what = Ui.Dim(Strings.T("docker.pruneWhat"), wrap: true);
        what.Margin = new Thickness(0, 0, 0, 8);
        body.Children.Add(what);
        if (df is not null) body.Children.Add(Ui.Dim(Strings.T("docker.disk", DockerParse.Size(df.Images), DockerParse.Size(df.Volumes), DockerParse.Size(df.BuildCache)), wrap: true));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var go = Ui.TextButton(Strings.T("docker.pruneConfirm"), "AccentButton", new Thickness(12, 3, 12, 4), () => _ = Prune(), "docker prune");
        go.Margin = new Thickness(0, 0, 6, 0);
        buttons.Children.Add(go);
        buttons.Children.Add(Ui.TextButton(Strings.T("docker.cancel"), "FlatButton", new Thickness(12, 3, 12, 4), Hide, "docker prune"));
        body.Children.Add(buttons);
        Overlay(Strings.T("docker.pruneTitle"), body);
    }

    async Task Prune()
    {
        Hide();
        try
        {
            var freed = await _m.Api!.PruneAsync();
            _island.ShowToast(Strings.T("docker.pruned", DockerParse.Size(freed)));
            await LoadDf();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        { _island.ShowToast(Strings.T("docker.failed", Engine)); }
    }

    void Overlay(string title, FrameworkElement body)
    {
        StopLogs();
        var close = Ui.IconAction("Icon.Close", "ai.close", Hide, "docker action", "FgBrush");
        _overlay.Child = Ui.OverlayFrame(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis }, body, close, null, 6);
        _overlay.Visibility = Visibility.Visible;
    }

    void Hide() { StopLogs(); _overlay.Visibility = Visibility.Collapsed; _overlay.Child = null; }
}
