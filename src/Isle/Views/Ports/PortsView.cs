using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Isle.Core;
using Isle.Modules.Ports;

namespace Isle.Views.Ports;

/// <summary>Ports tab: one row per listening port (port, process, local/network), Open and End. Refreshes every 2 s while shown.</summary>
public sealed class PortsView : Grid, ITabView
{
    readonly Func<IReadOnlyList<Listener>> _scan;
    readonly Func<CancellationToken, Task<List<ContainerRow>>>? _containers;
    readonly IslandWindow _island;
    IReadOnlyList<ContainerRow> _boxes = [];
    string? _confirmBox;
    int _ticks;
    readonly StackPanel _rows = new();
    readonly TextBlock _empty;
    DispatcherTimer? _timer;
    IReadOnlyList<Listener> _shown = [];
    (int Port, int Pid)? _confirm;
    bool _busy;

    public static string Url(int port) => $"http://localhost:{port}/";

    public PortsView(Func<IReadOnlyList<Listener>> scan, IslandWindow island, Func<CancellationToken, Task<List<ContainerRow>>>? containers = null)
    {
        _scan = scan;
        _containers = containers;
        _island = island;
        _empty = Ui.Dim(Strings.T("ports.none"), wrap: true);
        _empty.Margin = new Thickness(4, 6, 4, 0);
        Children.Add(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    public void OnShown()
    {
        _timer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
        OnTick(null, EventArgs.Empty);
    }

    public void OnHidden() { _timer?.Stop(); _confirm = null; _confirmBox = null; _ticks = 0; }

    async void OnTick(object? sender, EventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            Show(await Task.Run(_scan)); // ports first: docker/wsl can take seconds to answer
            if (_containers is not null && _ticks++ % 5 == 0) // docker/wsl every 10 s
            {
                _boxes = await _containers(CancellationToken.None);
                Show(_shown);
            }
        }
        catch (Exception ex) { Logger.Error("ports refresh", ex); }
        finally { _busy = false; }
    }

    internal void Show(IReadOnlyList<Listener> listeners)
    {
        _shown = listeners;
        _rows.Children.Clear();
        if (listeners.Count == 0 && _boxes.Count == 0) { _rows.Children.Add(_empty); return; }
        foreach (var l in listeners) _rows.Children.Add(Row(l));
        if (_boxes.Count == 0) return;
        var caption = new TextBlock { Text = Strings.T("ports.containers"), Margin = new Thickness(4, 8, 0, 4) };
        caption.SetResourceReference(StyleProperty, "TextCaption");
        _rows.Children.Add(caption);
        foreach (var b in _boxes) _rows.Children.Add(BoxRow(b));
    }

    internal void ShowContainers(IReadOnlyList<ContainerRow> boxes) { _boxes = boxes; Show(_shown); }

    FrameworkElement BoxRow(ContainerRow b)
    {
        var row = new DockPanel { Margin = new Thickness(4, 0, 0, 2) };
        var kind = new TextBlock { Text = b.Kind == ContainerKind.Docker ? "docker" : "wsl", Width = 62, VerticalAlignment = VerticalAlignment.Center };
        kind.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        kind.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
        DockPanel.SetDock(kind, Dock.Left);
        row.Children.Add(kind);
        var key = b.Kind + b.Id;
        Button stop = _confirmBox == key
            ? Ui.TextButton(Strings.T("ports.stopConfirm"), "AccentButton", new Thickness(8, 1, 8, 1), () => Stop(b), "containers stop")
            : Ui.IconButton(Ui.MakeIcon("Icon.Stop", 11), 26, () => { _confirmBox = key; Show(_shown); }, "containers stop");
        stop.ToolTip = Strings.T("ports.stop", b.Name);
        System.Windows.Automation.AutomationProperties.SetName(stop, Strings.T("ports.stop", b.Name));
        DockPanel.SetDock(stop, Dock.Right);
        row.Children.Add(stop);
        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines.Add(new System.Windows.Documents.Run(b.Name));
        var detail = new System.Windows.Documents.Run("  ·  " + b.Detail);
        detail.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "FgDimBrush");
        text.Inlines.Add(detail);
        text.SetResourceReference(StyleProperty, "Text");
        row.Children.Add(text);
        return row;
    }

    async void Stop(ContainerRow b)
    {
        _confirmBox = null;
        _boxes = _boxes.Where(x => x != b).ToList();
        Show(_shown);
        var (code, _) = await Containers.StopAsync(b);
        _island.ShowToast(Strings.T(code == 0 ? "ports.stopped" : "ports.stopFailed", b.Name));
    }

    FrameworkElement Row(Listener l)
    {
        var row = new DockPanel { Margin = new Thickness(4, 0, 0, 2) };
        var port = new TextBlock { Text = ":" + l.Port, Width = 62, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        port.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        port.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        DockPanel.SetDock(port, Dock.Left);
        row.Children.Add(port);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        if (_confirm == (l.Port, l.Pid))
        {
            buttons.Children.Add(Ui.TextButton(Strings.T("sysmon.endConfirm"), "AccentButton", new Thickness(8, 1, 8, 1), () => End(l), "ports end"));
        }
        else
        {
            var open = Ui.IconButton(Ui.MakeIcon("Icon.Globe", 13), 26, () => ProcessOps.Open(Url(l.Port)), "ports open");
            open.ToolTip = Strings.T("ports.open", Url(l.Port));
            System.Windows.Automation.AutomationProperties.SetName(open, Strings.T("ports.open", Url(l.Port)));
            buttons.Children.Add(open);
            var end = Ui.IconButton(Ui.MakeIcon("Icon.Close", 11), 26, () => { _confirm = (l.Port, l.Pid); Show(_shown); }, "ports end");
            end.ToolTip = Strings.T("sysmon.end", l.Process);
            System.Windows.Automation.AutomationProperties.SetName(end, Strings.T("sysmon.end", l.Process));
            buttons.Children.Add(end);
        }
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);

        var scope = Ui.Dim(Strings.T(l.Local ? "ports.local" : "ports.network"));
        scope.VerticalAlignment = VerticalAlignment.Center;
        scope.Margin = new Thickness(0, 0, 8, 0);
        if (!l.Local) scope.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
        DockPanel.SetDock(scope, Dock.Right);
        row.Children.Add(scope);

        var name = new TextBlock { Text = $"{l.Process}  ·  {l.Pid}", TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(StyleProperty, "Text");
        row.Children.Add(name);
        return row;
    }

    void End(Listener l)
    {
        _confirm = null;
        _island.ShowToast(Strings.T(ProcessOps.TryEnd(l.Pid, l.Process) ? "sysmon.ended" : "sysmon.endFailed", l.Process));
        Show(_shown.Where(x => x.Pid != l.Pid).ToList());
    }
}
