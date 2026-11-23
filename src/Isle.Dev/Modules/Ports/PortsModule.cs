using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Isle.Core;
using Isle.Views.Ports;

namespace Isle.Modules.Ports;

/// <summary>
/// Ports (module id "ports"): the Ports tab listing what developers' processes listen on, and a short pill when
/// a new one starts listening ("node · :5173", click opens it in the browser). The table is read every 5 s.
/// </summary>
public sealed class PortsModule(IslandWindow island) : IModule, IDisposable
{
    public const string TabId = "ports";
    const string PillId = "ports.new";

    DispatcherTimer? _watch, _pillTimer;
    List<Listener>? _known;

    public string Id => "ports";

    public void Start()
    {
        island.AddTab(new TabInfo(TabId, "tab.ports", "Icon.Server", () => new PortsView(() => PortScanner.Scan(), island, ct => Containers.ListAsync(ct)), 96));
        if (!Settings.Current.OptBool("ports.notify", true)) return;
        _watch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _watch.Tick += async (_, _) => await WatchAsync();
        _watch.Start();
        _ = WatchAsync();
    }

    async Task WatchAsync()
    {
        try
        {
            var now = await Task.Run(() => PortScanner.Scan());
            if (_known is not null)
                foreach (var l in PortFilter.Added(_known, now).Take(1)) Announce(l);
            _known = now;
        }
        catch (Exception ex) { Logger.Error("ports watch", ex); }
    }

    void Announce(Listener l)
    {
        island.Pill.Set(new PillItem
        {
            Id = PillId, Priority = PillPriority.Transient, Width = 220, Peek = true,
            View = () => PillView(l),
            Click = PortFilter.DesktopApp(l.Process) is null ? () => ProcessOps.Open(PortsView.Url(l.Port)) : null,
        });
        _pillTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _pillTimer.Tick -= HidePill;
        _pillTimer.Tick += HidePill;
        _pillTimer.Stop();
        _pillTimer.Start();
    }

    void HidePill(object? sender, EventArgs e) { _pillTimer?.Stop(); island.Pill.Remove(PillId); }

    internal static FrameworkElement PillView(Listener l)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var icon = Views.Ui.MakeIcon("Icon.Server", 14, "OkBrush");
        icon.Margin = new Thickness(0, 0, 6, 0);
        row.Children.Add(icon);
        var t = new TextBlock { Text = PortFilter.DesktopApp(l.Process) is { } app ? Strings.T("ports.appLoading", app) : $"{l.Process} · :{l.Port}", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        row.Children.Add(t);
        return row;
    }

    public void Stop()
    {
        _watch?.Stop();
        _watch = null;
        _pillTimer?.Stop();
        island.Pill.Remove(PillId);
        island.RemoveTab(TabId);
        _known = null;
    }

    public void Dispose() => Stop();
}
