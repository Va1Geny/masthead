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
    readonly StackPanel _rows = new() { Margin = new Thickness(0, 0, 0, 8) };
    readonly FrameworkElement _add;
    readonly TextBox _addBox = new() { MinHeight = 26, Padding = new Thickness(8, 2, 8, 2), FontSize = 12, VerticalContentAlignment = VerticalAlignment.Center };
    readonly System.Windows.Controls.Primitives.ToggleButton[] _addFlags = [Chip("http"), Chip("tls"), Chip("status")];
    ServersData _data = new([], [], null, []);
    IReadOnlyList<(WatchItem Item, List<string>? Bad)> _results = [];
    IReadOnlyDictionary<string, bool> _reach = new Dictionary<string, bool>();
    string? _confirm; // "watch:<target>": the second click removes it
    bool _busy;

    public ServersView(Func<ServersData> load, IslandWindow island)
    {
        _load = load;
        _island = island;
        _add = AddRow();
        Children.Add(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    public void OnShown() => Refresh();
    public void OnHidden() { _confirm = null; }

    static System.Windows.Controls.Primitives.ToggleButton Chip(string text)
    {
        var c = new System.Windows.Controls.Primitives.ToggleButton { Content = text, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
        c.SetResourceReference(StyleProperty, "Chip");
        return c;
    }

    FrameworkElement AddRow()
    {
        var row = new DockPanel { Margin = new Thickness(4, 2, 0, 2) };
        var add = Ui.TextButton(Strings.T("servers.add"), "AccentButton", new Thickness(10, 2, 10, 3), () => _ = AddWatch(), "servers add");
        add.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(add, Dock.Right);
        row.Children.Add(add);
        foreach (var f in _addFlags.Reverse()) { DockPanel.SetDock(f, Dock.Right); row.Children.Add(f); }
        _addBox.ToolTip = Strings.T("servers.add.tip");
        System.Windows.Automation.AutomationProperties.SetName(_addBox, Strings.T("servers.add.tip"));
        _addBox.TextChanged += (_, _) => _addBox.ClearValue(Border.BorderBrushProperty);
        _addBox.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; _ = AddWatch(); } };
        row.Children.Add(_addBox);
        return row;
    }

    async Task AddWatch()
    {
        var items = _results.Select(r => r.Item).ToList();
        var item = WatchItem.TryNew(_addBox.Text, _addFlags[0].IsChecked == true, _addFlags[1].IsChecked == true, _addFlags[2].IsChecked == true, items);
        if (item is null) { _addBox.SetResourceReference(Border.BorderBrushProperty, "DangerBrush"); return; }
        _addBox.Clear();
        foreach (var f in _addFlags) f.IsChecked = false;
        items.Add(item);
        await ServersModule.SaveWatch(items);
        Show(_data, ServersModule.Results, _reach);
    }

    async Task RemoveWatch(WatchItem item)
    {
        _confirm = null;
        await ServersModule.SaveWatch(_results.Select(r => r.Item).Where(i => i != item).ToList());
        Show(_data, ServersModule.Results, _reach);
    }

    void SetReach(SshHost h, bool on)
    {
        var s = Settings.Current;
        s.SetOpt("servers.reachHosts", ServerInfo.SetReach(s.Opt("servers.reachHosts"), h.Alias, on));
        s.Save("Options");
        Refresh();
    }

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
        (_data, _results, _reach) = (d, results, reach);
        _rows.Children.Clear();
        if (d.Hosts.Count == 0 && d.Aws.Count == 0 && d.Accounts.Count == 0 && results.Count == 0)
        {
            var empty = Ui.Dim(Strings.T("servers.none"), wrap: true);
            empty.Margin = new Thickness(4, 6, 4, 0);
            _rows.Children.Add(empty);
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
        Caption("servers.watch");
        foreach (var (item, bad) in results) _rows.Children.Add(WatchRow(item, bad));
        _rows.Children.Add(_add);
    }

    FrameworkElement WatchRow(WatchItem item, List<string>? bad)
    {
        var down = bad is { Count: > 0 };
        var row = new DockPanel { Margin = new Thickness(4, 0, 0, 2) };
        var key = "watch:" + item.Target;
        var rm = _confirm == key
            ? Ui.TextButton(Strings.T("servers.removeConfirm"), "AccentButton", new Thickness(8, 1, 8, 1), () => _ = RemoveWatch(item), "servers remove")
            : Ui.IconAction("Icon.Close", "servers.remove", () => { _confirm = key; Show(_data, _results, _reach); }, "servers remove");
        if (_confirm != key) rm.ToolTip = Strings.T("servers.remove", item.Target);
        DockPanel.SetDock(rm, Dock.Right);
        row.Children.Add(rm);
        var tag = new TextBlock { Text = bad is null ? "…" : down ? "!" : "OK", Width = 62, VerticalAlignment = VerticalAlignment.Center };
        tag.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        tag.SetResourceReference(TextBlock.ForegroundProperty, down ? "DangerBrush" : "FgDimBrush");
        DockPanel.SetDock(tag, Dock.Left);
        row.Children.Add(tag);
        row.Children.Add(Label(item.Target, down ? string.Join(", ", bad!) : null, down ? "DangerBrush" : null));
        return row;
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
        var open = Ui.IconButton(Ui.MakeIcon("Icon.Play", 12), 26, () => ServersModule.OpenSsh(h.Alias), "servers ssh", tip: Strings.T("servers.open", h.Alias));
        DockPanel.SetDock(open, Dock.Right);
        row.Children.Add(open);
        var s = Settings.Current;
        var global = s.OptBool("servers.reach");
        var sw = SettingsPanel.SettingsView.Switch(ServerInfo.ReachWanted(global, s.Opt("servers.reachHosts"), h.Alias), on => SetReach(h, on));
        sw.IsEnabled = !global; // the global switch in Settings forces every host on
        sw.Margin = new Thickness(0, 0, 6, 0);
        sw.ToolTip = Strings.T("servers.reach.tip", h.Alias);
        System.Windows.Automation.AutomationProperties.SetName(sw, Strings.T("servers.reach.tip", h.Alias));
        DockPanel.SetDock(sw, Dock.Right);
        row.Children.Add(sw);
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
