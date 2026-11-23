using System.Windows;
using System.Windows.Controls;
using Isle.Core;
using Isle.Modules.Servers;

namespace Isle.Views.Servers;

/// <summary>Servers tab: ssh hosts (optional reachability dot, open in terminal), cloud accounts, watch-list results.</summary>
public sealed class ServersView : Grid, ITabView
{
    readonly Func<ServersData> _load;
    readonly IslandWindow _island;
    readonly StackPanel _rows = new();
    bool _busy;

    public ServersView(Func<ServersData> load, IslandWindow island)
    {
        _load = load;
        _island = island;
        Children.Add(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    public void OnShown() => Refresh();
    public void OnHidden() { }

    async void Refresh()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var data = await Task.Run(_load);
            var reach = new Dictionary<string, bool>();
            Show(data, ServersModule.Results, reach);
            var s = Settings.Current;
            var global = s.OptBool("servers.reach");
            var wanted = data.Hosts.Where(h => ServerInfo.ReachWanted(global, s.Opt("servers.reachHosts"), h.Alias)).ToList();
            if (wanted.Count == 0) return;
            var ok = await Task.WhenAll(wanted.Select(h => ServerInfo.Reachable(h.HostName, h.Port)));
            for (var i = 0; i < wanted.Count; i++) reach[wanted[i].Alias] = ok[i];
            Show(data, ServersModule.Results, reach);
        }
        catch (Exception ex) { Logger.Error("servers refresh", ex); }
        finally { _busy = false; }
    }

    internal void Show(ServersData d, IReadOnlyList<(WatchItem Item, List<string>? Bad)> results, IReadOnlyDictionary<string, bool> reach)
    {
        _rows.Children.Clear();
        if (d.Hosts.Count == 0 && d.Aws.Count == 0 && d.Accounts.Count == 0 && results.Count == 0)
        {
            var empty = Ui.Dim(Strings.T("servers.none"), wrap: true);
            empty.Margin = new Thickness(4, 6, 4, 0);
            _rows.Children.Add(empty);
            return;
        }
        if (d.Hosts.Count > 0) { Caption("servers.hosts"); foreach (var h in d.Hosts) _rows.Children.Add(HostRow(h, reach)); }
        if (d.Aws.Count > 0 || d.Accounts.Count > 0)
        {
            Caption("servers.cloud");
            if (d.Aws.Count > 0)
                _rows.Children.Add(Line("AWS", d.Aws.Count + " " + Strings.T("servers.profiles"),
                    d.SsoExpiry is { } e ? "SSO " + ServersModule.Left(e, DateTimeOffset.Now) : null, d.SsoExpiry is { } x && x <= DateTimeOffset.Now ? "DangerBrush" : null));
            foreach (var a in d.Accounts) _rows.Children.Add(Line(a.Tool, a.Name, a.Who, null));
        }
        if (results.Count > 0)
        {
            Caption("servers.watch");
            foreach (var (item, bad) in results)
                _rows.Children.Add(Line(bad is null ? "…" : bad.Count == 0 ? "OK" : "!", item.Target, bad is { Count: > 0 } ? string.Join(", ", bad) : null, bad is { Count: > 0 } ? "DangerBrush" : null));
        }
    }

    void Caption(string key)
    {
        var c = new TextBlock { Text = Strings.T(key), Margin = new Thickness(4, 8, 0, 4) };
        c.SetResourceReference(StyleProperty, "TextCaption");
        _rows.Children.Add(c);
    }

    static FrameworkElement Line(string tag, string text, string? detail, string? brush)
    {
        var row = new DockPanel { Margin = new Thickness(4, 0, 0, 2) };
        var t = new TextBlock { Text = tag, Width = 62, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        t.SetResourceReference(TextBlock.ForegroundProperty, brush ?? "FgDimBrush");
        DockPanel.SetDock(t, Dock.Left);
        row.Children.Add(t);
        row.Children.Add(Label(text, detail, brush));
        return row;
    }

    static TextBlock Label(string text, string? detail, string? brush)
    {
        var l = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        l.Inlines.Add(new System.Windows.Documents.Run(text));
        if (detail is not null)
        {
            var r = new System.Windows.Documents.Run("  ·  " + detail);
            r.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, brush ?? "FgDimBrush");
            l.Inlines.Add(r);
        }
        l.SetResourceReference(StyleProperty, "Text");
        return l;
    }

    FrameworkElement HostRow(SshHost h, IReadOnlyDictionary<string, bool> reach)
    {
        var row = new DockPanel { Margin = new Thickness(4, 0, 0, 2) };
        var open = Ui.IconButton(Ui.MakeIcon("Icon.Play", 12), 26, () => ServersModule.OpenSsh(h.Alias), "servers ssh");
        open.ToolTip = Strings.T("servers.open", h.Alias);
        System.Windows.Automation.AutomationProperties.SetName(open, Strings.T("servers.open", h.Alias));
        DockPanel.SetDock(open, Dock.Right);
        row.Children.Add(open);
        var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, reach.TryGetValue(h.Alias, out var up) ? (up ? "OkBrush" : "DangerBrush") : "FgDimBrush");
        dot.Opacity = reach.ContainsKey(h.Alias) ? 1 : 0.25;
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);
        var detail = (h.User is null ? "" : h.User + "@") + h.HostName + (h.Port == 22 ? "" : ":" + h.Port);
        row.Children.Add(Label(h.Alias, detail, null));
        return row;
    }
}
