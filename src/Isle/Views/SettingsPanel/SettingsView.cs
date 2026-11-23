using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Isle.Core;
using Isle.Modules.Hotkeys;

namespace Isle.Views.SettingsPanel;

/// <summary>What the settings panel asks the app to do (everything else it writes to <see cref="Core.Settings"/> itself).</summary>
public sealed record SettingsActions(
    Func<IReadOnlyList<HotkeyInfo>> Hotkeys,
    Func<string, string?, bool> Rebind,
    Action<bool> SuspendHotkeys,
    Func<string, ModuleState> StateOf,
    Action SyncModules,
    Action<string> RestartModule,
    Action<string> Open,
    Action Close);

/// <summary>
/// Settings: a full-panel overlay with a section list on the left and a scrolling page on the right.
/// Sections: general (appearance + behaviour + the toggle hotkey), options (per-module), about.
/// Simple changes are written and saved here; module restarts and links go through <see cref="SettingsActions"/>.
/// </summary>
public sealed class SettingsView : Grid, ITabView
{
    public static readonly string[] Sections = ["general", "options", "about"];

    static Core.Settings S => Core.Settings.Current;

    readonly SettingsActions _a;
    readonly StackPanel _nav = new() { Margin = new Thickness(0, 0, 0, 0) };
    readonly StackPanel _page = new() { Margin = new Thickness(0, 0, 8, 8) };
    readonly ScrollViewer _scroll;
    readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _head = new();
    readonly Button _close;
    readonly DispatcherTimer _scaleTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    string _section = "general";
    double _pendingScale;

    public SettingsView(SettingsActions actions)
    {
        _a = actions;
        SetResourceReference(BackgroundProperty, "BgBrush");
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(138) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new DockPanel { Margin = new Thickness(14, 12, 8, 10) };
        _head.Margin = new Thickness(8, 0, 0, 8);
        _head.SetResourceReference(StyleProperty, "TextTitle");
        DockPanel.SetDock(_head, Dock.Top);
        left.Children.Add(_head);
        left.Children.Add(_nav);
        Children.Add(left);

        var right = new Grid { Margin = new Thickness(4, 10, 10, 6) };
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        _close = IconButton("Icon.Close", "set.close", () => _a.Close());
        DockPanel.SetDock(_close, Dock.Right);
        top.Children.Add(_close);
        _title.SetResourceReference(StyleProperty, "Text");
        _title.FontWeight = FontWeights.SemiBold;
        top.Children.Add(_title);
        right.Children.Add(top);
        _scroll = new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        SetRow(_scroll, 1);
        right.Children.Add(_scroll);
        SetColumn(right, 1);
        Children.Add(right);

        _scaleTimer.Tick += (_, _) => CommitScale();
        Render();
    }

    public string Section => _section;
    internal SettingsActions Actions => _a;

    public void ShowSection(string section)
    {
        _section = Sections.Contains(section) ? section : "general";
        Render();
        _scroll.ScrollToTop();
    }

    public void OnShown() => Render();
    internal void ScrollToEnd() { _scroll.UpdateLayout(); _scroll.ScrollToEnd(); }
    public void OnHidden()
    {
        CommitScale();
        _a.SuspendHotkeys(false);
    }

    void CommitScale()
    {
        if (!_scaleTimer.IsEnabled) return;
        _scaleTimer.Stop();
        if (Math.Abs(S.Scale - _pendingScale) < 0.001) return;
        S.Scale = _pendingScale;
        S.Save("Scale");
    }

    /// <summary>Rebuilds the nav and the current page (cheap; called after every change that alters the page).</summary>
    public void Render()
    {
        var navFocused = _nav.IsKeyboardFocusWithin;
        _head.Text = Strings.T("set.title");
        _close.ToolTip = Strings.T("set.close");
        System.Windows.Automation.AutomationProperties.SetName(_close, Strings.T("set.close"));
        _nav.Children.Clear();
        foreach (var s in Sections) _nav.Children.Add(NavItem(s));
        _title.Text = Strings.T("set." + _section);
        var offset = _scroll.VerticalOffset;
        _page.Children.Clear();
        switch (_section)
        {
            case "general": General(); break;
            case "options": OptionsPage(); break;
            case "about": About(); break;
        }
        _scroll.UpdateLayout();
        _scroll.ScrollToVerticalOffset(offset);
        if (navFocused) FocusSection();
    }

    internal void FocusSection() => _nav.Children.OfType<Button>().First(b => Equals(b.Tag, _section)).Focus();

    // ---- pages ----

    void General()
    {
        Group("set.appearance");
        Row("set.theme", Choice(["Dark", "Light", "System"], S.Theme, v => { S.Theme = v; S.Save("Theme"); }, v => "set.theme." + v));
        Row("set.language", Choice(["en", "uk"], S.Language, v => { S.Language = v; Save("Language"); }, v => "set.lang." + v));
        Row("set.scale", ScaleSlider(), "set.scale.hint");
        var screens = SafeScreens();
        if (screens.Count > 1)
        {
            var ids = new List<string> { "-1" };
            for (var i = 0; i < screens.Count; i++) ids.Add(i.ToString(CultureInfo.InvariantCulture));
            var selectedMonitor = S.MonitorDevice.Length > 0 ? screens.FindIndex(screen => string.Equals(screen.Device, S.MonitorDevice, StringComparison.OrdinalIgnoreCase)) : S.Monitor;
            Row("set.monitor", Choice(ids, selectedMonitor.ToString(CultureInfo.InvariantCulture), v => { S.Monitor = int.Parse(v, CultureInfo.InvariantCulture); S.MonitorDevice = S.Monitor >= 0 ? screens[S.Monitor].Device : ""; Save("Monitor"); },
                v => v == "-1" ? "set.monitor.primary" : null, v => (int.Parse(v, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture)));
        }
        Row("set.shape", Choice(["notch", "pill"], S.Opt("shape", "notch") == "pill" ? "pill" : "notch", v => { S.SetOpt("shape", v); Save("Options"); }, v => "set.shape." + v));

        Group("set.behaviour");
        Row("set.fullscreen", Switch(S.HideInFullscreen, on => { S.HideInFullscreen = on; Save("HideInFullscreen"); }), "set.fullscreen.hint");
        Row("set.autostart", Switch(SafeAutostart(), on => { try { Autostart.Set(on); } catch (Exception ex) { Logger.Error("autostart", ex); } Render(); }));
        Row("set.excluded", TextField(string.Join(", ", S.ExcludedApps), v =>
        {
            S.ExcludedApps = [.. v.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];
            Save("ExcludedApps");
        }, "notepad, obs64"), "set.excluded.hint", wide: true);
        var openApps = new WrapPanel { Margin = new Thickness(0, 0, 0, 5) };
        _page.Children.Add(Link("", "set.excluded.pick", () =>
        {
            openApps.Children.Clear();
            foreach (var name in ForegroundWatcher.OpenAppNames().Where(name => !S.ExcludedApps.Any(app => IslandWindow.IsSameApp(app, name))))
            {
                var button = new Button { Content = name, Margin = new Thickness(0, 3, 4, 0), Padding = new Thickness(8, 3, 8, 3) };
                button.SetResourceReference(StyleProperty, "FlatButton");
                button.Click += (_, _) => { S.ExcludedApps.Add(name); Save("ExcludedApps"); };
                openApps.Children.Add(button);
            }
            if (openApps.Children.Count == 0) openApps.Children.Add(new TextBlock { Text = Strings.T("set.excluded.none") });
        }));
        _page.Children.Add(openApps);
        Row("set.excluded.overlap", Switch(S.ExcludedAppsOnlyOnOverlap, on => { S.ExcludedAppsOnlyOnOverlap = on; Save("ExcludedAppsOnlyOnOverlap"); }));
        foreach (var h in Hotkeys().Where(h => h.Id == "toggle")) HotkeyRow(h);
    }

    void OptionsPage()
    {
        var jumps = new ComboBox { MinWidth = 150, Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetName(jumps, Strings.T("set.options.jump"));
        jumps.Items.Add(new ComboBoxItem { Content = Strings.T("set.options.jump"), IsEnabled = false, IsSelected = true });
        var groups = new Dictionary<string, FrameworkElement>();
        _page.Children.Add(jumps);
        void OptionGroup(string id)
        {
            Group("mod." + id);
            groups[id] = (FrameworkElement)_page.Children[_page.Children.Count - 1];
            jumps.Items.Add(new ComboBoxItem { Content = Strings.T("mod." + id), Tag = id });
        }
        jumps.SelectionChanged += (_, _) =>
        {
            if (jumps.SelectedItem is ComboBoxItem { Tag: string id } && groups.TryGetValue(id, out var group))
            {
                _page.UpdateLayout();
                _scroll.ScrollToVerticalOffset(group.TranslatePoint(new Point(), _page).Y);
            }
        };

        OptionGroup("setup");
        Row("set.setupAgain", Ui.TextButton(Strings.T("set.setupAgain.run"), "FlatButton", new Thickness(12, 4, 12, 4), () => { if (App.Island is { } w) Modules.Setup.SetupFlow.Show(w); }, "setup again"), "set.setupAgain.hint");

        OptionGroup("sysmon");
        Row("set.sysmonAlerts", OptSwitch("sysmon.alerts", true, restart: "sysmon"), "set.sysmonAlerts.hint");

        OptionGroup("ports");
        Row("set.portsNotify", OptSwitch("ports.notify", true, restart: "ports"), "set.portsNotify.hint");

        OptionGroup("projects");
        Row("set.projectsPaths", OptText("projects.paths", @"C:\Users\me\Projects\app", "projects"), "set.projectsPaths.hint", wide: true);
        Row("set.projectsBuilds", OptSwitch("projects.builds", true, restart: "projects"), "set.projectsBuilds.hint");
        Row("set.projectsToken", SecretField("projects.githubToken", restart: "projects"), "set.projectsToken.hint");
        Row("set.gitlabHost", OptText("gitlab.host", "gitlab.com", "projects", "gitlab.com"), "set.gitlabHost.hint");
        Row("set.gitlabToken", SecretField("gitlab.token", restart: "projects"), "set.gitlabToken.hint");

        OptionGroup("logs");
        Row("set.logsFiles", OptText("logs.files", @"C:\Users\me\Projects\api\logs\app.log", "logs"), "set.logsFiles.hint", wide: true);
        Row("set.logsPattern", OptText("logs.pattern", Modules.Logs.LogWatchModule.DefaultPattern, "logs", Modules.Logs.LogWatchModule.DefaultPattern), "set.logsPattern.hint", wide: true);

        OptionGroup("kube");
        Row("set.kubePattern", OptText("kube.prodPattern", Modules.Kube.KubeConfig.DefaultPattern, "kube", Modules.Kube.KubeConfig.DefaultPattern), "set.kubePattern.hint", wide: true);

        OptionGroup("agents");
        Row("set.agentsApprovals", OptSwitch("agents.approvals", true), "set.agentsApprovals.hint");
        Row("set.agentsTimeout", Choice(["30", "60", "120", "300"], Modules.Agents.AgentsModule.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            v => { S.SetOpt("agents.timeout", v); Save("Options"); }, null, v => Strings.T("set.seconds", v)));
        Row("set.hooks", Link("", "set.manage", () => _a.Open("hooks")), "set.hooks.hint");
    }

    IReadOnlyList<HotkeyInfo> Hotkeys()
    {
        try { return _a.Hotkeys(); }
        catch (Exception ex) { Logger.Error("hotkey list", ex); return []; }
    }

    void HotkeyRow(HotkeyInfo h)
    {
        var cell = new StackPanel { Orientation = Orientation.Horizontal };
        if (!string.Equals(h.Gesture, h.DefaultGesture, StringComparison.OrdinalIgnoreCase))
            cell.Children.Add(IconButton("Icon.Reset", "set.hkReset", () => Rebind(h.Id, null)));
        cell.Children.Add(HotkeyBox(h));
        var hint = h.Gesture.Length == 0 ? "set.hkOff" : !h.Registered ? "set.hkFailed" : null;
        Row(h.TitleKey, cell, hint, hintBrush: hint == "set.hkFailed" ? "DangerBrush" : null);
    }

    void About()
    {
        var v = typeof(SettingsView).Assembly.GetName().Version;
        Row("set.version", Text(v is null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}", dim: true));
        Row("set.dataFolder", Link(Paths.Root, "set.open", () => OpenPath(Paths.Root)), wide: true);
        Row("set.log", Link("log.txt", "set.open", () => OpenPath(Path.Combine(Paths.Root, "log.txt"))));
        Hint("set.about.hint");
    }

    // ---- actions ----

    internal void Save(string name, string? restart = null)
    {
        S.Save(name);
        if (restart is not null) _a.RestartModule(restart);
        Render();
    }

    void Rebind(string id, string? gesture)
    {
        try { _a.Rebind(id, gesture); }
        catch (Exception ex) { Logger.Error("rebind", ex); }
        Render();
    }

    static void OpenPath(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) path = Paths.Root;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Logger.Error("open path", ex); }
    }


    static List<ScreenInfo> SafeScreens()
    {
        try { return Screens.All(); } catch { return []; }
    }

    static bool SafeAutostart()
    {
        try { return Autostart.IsEnabled; } catch { return false; }
    }

    // ---- building blocks ----

    FrameworkElement NavItem(string section)
    {
        var selected = section == _section;
        var t = new TextBlock { Text = Strings.T("set." + section), TextTrimming = TextTrimming.CharacterEllipsis };
        t.SetResourceReference(TextBlock.FontSizeProperty, "NavigationFontSize");
        t.SetResourceReference(TextBlock.ForegroundProperty, selected ? "FgBrush" : "FgDimBrush");
        if (selected) t.FontWeight = FontWeights.SemiBold;
        var b = new Button { Content = t, Tag = section, Padding = new Thickness(8, 5, 4, 5), MinHeight = 0, Margin = new Thickness(0, 0, 0, 2), HorizontalContentAlignment = HorizontalAlignment.Left };
        b.SetResourceReference(StyleProperty, "GhostButton");
        if (selected) b.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "SurfaceBrush");
        else b.Background = Brushes.Transparent;
        b.Click += (_, _) => { ShowSection(section); FocusSection(); };
        System.Windows.Automation.AutomationProperties.SetName(b, t.Text);
        return b;
    }

    void Group(string key)
    {
        var t = new TextBlock { Text = Strings.T(key).ToUpper(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, _page.Children.Count == 0 ? 2 : 12, 0, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgFaintBrush");
        _page.Children.Add(t);
    }

    internal void Hint(string key)
    {
        var t = Text(Strings.T(key), dim: true);
        t.FontSize = 11.5;
        t.Margin = new Thickness(0, 2, 0, 8);
        _page.Children.Add(t);
    }

    /// <summary>Label (+ optional hint below it) on the left, control on the right; <paramref name="wide"/> puts the control below.</summary>
    internal void Row(string labelKey, FrameworkElement control, string? hintKey = null, bool wide = false, string? hintBrush = null)
    {
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var l = Text(Strings.T(labelKey), dim: false);
        l.FontSize = 12.5;
        label.Children.Add(l);
        AssociateLabel(control, l);
        if (hintKey is not null)
        {
            var h = Text(Strings.T(hintKey), dim: true);
            h.FontSize = 11;
            if (hintBrush is not null) h.SetResourceReference(TextBlock.ForegroundProperty, hintBrush);
            label.Children.Add(h);
        }
        if (wide)
        {
            control.Margin = new Thickness(0, 4, 0, 0);
            control.HorizontalAlignment = HorizontalAlignment.Stretch;
            _page.Children.Add(new StackPanel { Margin = new Thickness(0, 3, 0, 5), Children = { label, control } });
            return;
        }
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), MinHeight = 28 };
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(control, Dock.Right);
        row.Children.Add(control);
        row.Children.Add(label);
        _page.Children.Add(row);
    }

    static void AssociateLabel(DependencyObject control, TextBlock label)
    {
        if (control is System.Windows.Controls.Control input)
        {
            System.Windows.Automation.AutomationProperties.SetLabeledBy(input, label);
            if (input is RadioButton { Content: string value })
                System.Windows.Automation.AutomationProperties.SetName(input, label.Text + ": " + value);
        }
        foreach (var child in LogicalTreeHelper.GetChildren(control).OfType<DependencyObject>())
            AssociateLabel(child, label);
    }

    internal static TextBlock Text(string text, bool dim)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(StyleProperty, dim ? "TextDim" : "Text");
        return t;
    }

    internal static ToggleButton Switch(bool on, Action<bool> changed)
    {
        var s = new ToggleButton { IsChecked = on };
        s.SetResourceReference(StyleProperty, "Switch");
        s.Click += (_, _) => changed(s.IsChecked == true);
        return s;
    }

    internal ToggleButton OptSwitch(string key, bool fallback, string? restart = null) =>
        Switch(S.OptBool(key, fallback), on => { S.SetOpt(key, on); Save("Options", restart); });

    internal FrameworkElement OptText(string key, string placeholder, string? restart = null, string fallback = "") =>
        TextField(S.Opt(key, fallback), t => { S.SetOpt(key, t.Trim()); Save("Options", restart); }, placeholder);

    /// <param name="labelKey">Strings key for a value (null → <paramref name="literal"/> or the value itself).</param>
    internal WrapPanel Choice(IReadOnlyList<string> values, string current, Action<string> picked, Func<string, string?>? labelKey = null, Func<string, string>? literal = null)
    {
        var panel = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var group = "set" + Guid.NewGuid().ToString("N");
        foreach (var v in values)
        {
            var key = labelKey?.Invoke(v);
            var text = key is not null ? Strings.T(key) : literal?.Invoke(v) ?? v;
            var r = new RadioButton { Content = text, IsChecked = string.Equals(v, current, StringComparison.OrdinalIgnoreCase), GroupName = group, Margin = new Thickness(4, 0, 0, 0) };
            r.SetResourceReference(StyleProperty, "ChipRadio");
            r.Padding = new Thickness(9, 3, 9, 3);
            r.Checked += (_, _) => // also fires for keyboard and UI Automation selection, unlike Click
            {
                if (string.Equals(v, current, StringComparison.OrdinalIgnoreCase)) return;
                current = v;
                picked(v);
            };
            panel.Children.Add(r);
        }
        return panel;
    }

    FrameworkElement ScaleSlider()
    {
        var pct = new TextBlock { Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        pct.SetResourceReference(StyleProperty, "TextDim");
        var slider = new Slider { Minimum = 0.8, Maximum = 2.0, TickFrequency = 0.05, IsSnapToTickEnabled = true, Value = S.Scale, Width = 150, VerticalAlignment = VerticalAlignment.Center };
        slider.SetResourceReference(MinHeightProperty, "CompactTargetMin");
        pct.Text = $"{Math.Round(S.Scale * 100)} %";
        slider.ValueChanged += (_, e) =>
        {
            pct.Text = $"{Math.Round(e.NewValue * 100)} %";
            _pendingScale = Math.Round(e.NewValue, 2);
            _scaleTimer.Stop();
            _scaleTimer.Start(); // live preview, applied after the thumb rests briefly
        };
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { slider, pct } };
    }

    /// <summary>Text box that commits on Enter or when it loses focus (only when the text changed).</summary>
    internal static FrameworkElement TextField(string value, Action<string> commit, string placeholder, double width = 0)
    {
        var box = new TextBox { Text = value, MinHeight = 26, Padding = new Thickness(8, 2, 8, 2), FontSize = 12 };
        if (width > 0) box.Width = width;
        var hint = new TextBlock { Text = placeholder, IsHitTestVisible = false, Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "FgFaintBrush");
        hint.Visibility = value.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var last = value;
        void Commit()
        {
            if (box.Text == last) return;
            last = box.Text;
            commit(box.Text);
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Commit(); } };
        box.LostKeyboardFocus += (_, _) => Commit();
        return new Grid { Children = { box, hint } };
    }

    internal FrameworkElement SecretField(string key, string? restart)
    {
        var box = new PasswordBox { Width = 150, MinHeight = 26, Padding = new Thickness(8, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
        var hint = new TextBlock { IsHitTestVisible = false, Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5 };
        hint.Text = Strings.T(S.Opt(key).Length > 0 ? "set.secretSaved" : "set.secretEmpty");
        hint.SetResourceReference(TextBlock.ForegroundProperty, "FgFaintBrush");
        box.PasswordChanged += (_, _) => hint.Visibility = box.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        void Commit()
        {
            if (box.Password.Length == 0) return;
            S.SetOpt(key, Secret.Protect(box.Password));
            box.Password = "";
            Save("Options", restart);
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Commit(); } };
        box.LostKeyboardFocus += (_, _) => Commit();
        var clear = IconButton("Icon.Trash", "set.secretClear", () => { S.Options.Remove(key); Save("Options", restart); });
        clear.Visibility = S.Opt(key).Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { clear, new Grid { Children = { box, hint } } } };
    }

    internal static FrameworkElement Link(string value, string buttonKey, Action click)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (value.Length > 0)
        {
            var t = new TextBlock { Text = value, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(0, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 190 };
            t.SetResourceReference(StyleProperty, "TextDim");
            t.FontSize = 12;
            t.ToolTip = value;
            panel.Children.Add(t);
        }
        var b = new Button { Content = Strings.T(buttonKey), Padding = new Thickness(10, 3, 10, 3), MinHeight = 26, FontSize = 12 };
        b.SetResourceReference(StyleProperty, "FlatButton");
        b.Click += (_, _) => click();
        panel.Children.Add(b);
        return panel;
    }

    static Button IconButton(string icon, string tipKey, Action click)
    {
        var b = Ui.IconButton(Ui.MakeIcon(icon, 13), 26, click, area: null);
        b.ToolTip = Strings.T(tipKey);
        System.Windows.Automation.AutomationProperties.SetName(b, Strings.T(tipKey));
        return b;
    }

    /// <summary>Read-only box: click it, then press the new combination. Backspace/Delete turns the hotkey off.</summary>
    FrameworkElement HotkeyBox(HotkeyInfo h)
    {
        var shown = h.Gesture.Length > 0 ? h.Gesture : Strings.T("set.hkNone");
        var box = new TextBox
        {
            Text = shown, IsReadOnly = true, Width = 130, MinHeight = 26, Padding = new Thickness(8, 2, 8, 2), FontSize = 12,
            TextAlignment = TextAlignment.Center, Cursor = Cursors.Hand, IsReadOnlyCaretVisible = false,
        };
        box.SetResourceReference(System.Windows.Controls.Control.FontFamilyProperty, "UiFont");
        box.GotKeyboardFocus += (_, _) => { box.Text = Strings.T("set.hkPress"); _a.SuspendHotkeys(true); };
        box.LostKeyboardFocus += (_, _) => { box.Text = shown; _a.SuspendHotkeys(false); };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Tab) return;
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            {
                box.Text = ModifierText(Keyboard.Modifiers, WinDown()) + "…";
                return;
            }
            var mods = Keyboard.Modifiers;
            var win = WinDown();
            if (mods == ModifierKeys.None && !win && key is Key.Back or Key.Delete) { Keyboard.ClearFocus(); Rebind(h.Id, ""); return; }
            var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            var text = HotkeyText(mods, win, vk);
            if (text is null) { box.Text = Strings.T("set.hkInvalid"); return; }
            Keyboard.ClearFocus();
            Rebind(h.Id, text);
        };
        return box;
    }

    static bool WinDown() => Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);

    static string ModifierText(ModifierKeys mods, bool win)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (win) parts.Add("Win");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        return string.Join("+", parts) + (parts.Count > 0 ? "+" : "");
    }

    /// <summary>Gesture text for a key press, or null when the key is unsupported or needs a modifier (letters, digits, …).</summary>
    public static string? HotkeyText(ModifierKeys mods, bool win, uint vk)
    {
        if (Gesture.KeyName(vk).Length == 0) return null;
        uint m = 0;
        if (mods.HasFlag(ModifierKeys.Control)) m |= Native.MOD_CONTROL;
        if (mods.HasFlag(ModifierKeys.Alt)) m |= Native.MOD_ALT;
        if (mods.HasFlag(ModifierKeys.Shift)) m |= Native.MOD_SHIFT;
        if (win) m |= Native.MOD_WIN;
        var fkey = vk is >= 0x70 and <= 0x87;
        if (!fkey && (m & ~Native.MOD_SHIFT) == 0) return null; // a bare/shifted key would swallow normal typing
        return new Gesture(m, vk).ToString();
    }
}
