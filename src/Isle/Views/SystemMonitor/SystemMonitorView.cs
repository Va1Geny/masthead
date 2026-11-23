using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Isle.Core;
using Isle.Modules.SystemMonitor;

namespace Isle.Views.SystemMonitor;

/// <summary>
/// System tab: CPU, memory, GPU and network cards with a one-minute sparkline each, then the busiest processes
/// (with End task). Samples once a second, only while the tab is on screen.
/// </summary>
public sealed class SystemMonitorView : Grid, ITabView, IDisposable
{
    const int History = 60;

    readonly SystemSampler? _sampler;
    readonly IslandWindow _island;
    readonly Stat _cpu = new("CPU"), _mem = new("RAM"), _gpu = new("GPU"), _net = new("NET");
    readonly StackPanel _procs = new(), _wsl = new();
    long? _wslBytes;
    bool _confirmWsl;
    DispatcherTimer? _timer;
    int _ticks;
    int? _confirmPid;

    /// <summary>True while the tab samples (the module's background check stands down meanwhile).</summary>
    public bool IsLive => _timer?.IsEnabled == true;

    public SystemMonitorView(SystemSampler? sampler, IslandWindow island)
    {
        _sampler = sampler;
        _island = island;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var cards = new UniformGrid { Rows = 1, Columns = 4, Margin = new Thickness(0, 0, -6, 0) };
        foreach (var s in new[] { _cpu, _mem, _gpu, _net }) cards.Children.Add(s.Root);
        Children.Add(cards);

        var scroll = new ScrollViewer { Content = _procs, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0) };
        SetRow(_wsl, 1);
        _wsl.Margin = new Thickness(0, 6, 0, 0);
        Children.Add(_wsl);
        SetRow(scroll, 2);
        Children.Add(scroll);
    }

    public void OnShown()
    {
        if (_sampler is null) return;
        _timer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _sampler.Full(); // baseline for the rates
        _sampler.TopProcesses(5);
        _ticks = 0;
        _timer.Start();
    }

    public void OnHidden()
    {
        _timer?.Stop();
        _confirmPid = null;
        _confirmWsl = false;
    }

    void OnTick(object? sender, EventArgs e)
    {
        if (_sampler is null) return;
        try
        {
            Show(_sampler.Full());
            if (_ticks++ % 2 == 0)
            {
                ShowProcesses(_sampler.TopProcesses(5));
                ShowWsl(_sampler.Wsl);
            }
        }
        catch (Exception ex) { Logger.Error("sysmon tick", ex); }
    }

    internal void Show(SystemSample s)
    {
        var hottest = s.Cores.Length > 0 ? s.Cores.Max() : 0;
        _cpu.Set(s.Cpu, $"{s.Cpu:0}%", s.Cores.Length > 0 ? Strings.T("sysmon.hotCore", Math.Round(hottest)) : "");
        var memPct = s.MemTotal > 0 ? 100 * s.MemUsed / s.MemTotal : 0;
        _mem.Set(memPct, $"{memPct:0}%", $"{SystemMath.Size(s.MemUsed)} / {SystemMath.Size(s.MemTotal)}");
        if (s.Gpu is { } g)
            _gpu.Set(g, $"{g:0}%", s.VramUsed is { } used && s.VramTotal is { } vt ? $"{SystemMath.Size(used)} / {SystemMath.Size(vt)}" : "");
        else _gpu.Set(0, "—", "");
        // network sparkline: share of the largest rate seen in the window
        _net.SetRate(s.NetDown + s.NetUp, "↓ " + SystemMath.Rate(s.NetDown), "↑ " + SystemMath.Rate(s.NetUp));
    }

    internal void ShowProcesses(IReadOnlyList<ProcessUsage> top)
    {
        _lastTop = top;
        _procs.Children.Clear();
        foreach (var p in top) _procs.Children.Add(Row(p));
    }

    /// <summary>The WSL row: shown only while the VM process exists (bytes null hides it).</summary>
    internal void ShowWsl(long? bytes)
    {
        _wslBytes = bytes;
        _wsl.Children.Clear();
        if (bytes is null) { _confirmWsl = false; return; }
        var row = new DockPanel { Margin = new Thickness(4, 0, 0, 0), LastChildFill = true };
        var button = _confirmWsl
            ? Ui.TextButton(Strings.T("sysmon.wslConfirm"), "AccentButton", new Thickness(8, 1, 8, 1), () => _ = ShutDownWslAsync(), "sysmon wsl")
            : Ui.TextButton(Strings.T("sysmon.wslStop"), "FlatButton", new Thickness(8, 1, 8, 1), () => { _confirmWsl = true; ShowWsl(_wslBytes); }, "sysmon wsl");
        button.ToolTip = Strings.T("sysmon.wslWarn");
        DockPanel.SetDock(button, Dock.Right);
        row.Children.Add(button);
        var mem = new TextBlock { Text = SystemMath.Size(bytes.Value), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        mem.SetResourceReference(StyleProperty, "TextDim");
        DockPanel.SetDock(mem, Dock.Right);
        row.Children.Add(mem);
        var name = new TextBlock { Text = Strings.T("sysmon.wsl"), ToolTip = Strings.T("sysmon.wslTip"), VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(StyleProperty, "Text");
        row.Children.Add(name);
        _wsl.Children.Add(row);
    }

    async Task ShutDownWslAsync()
    {
        _confirmWsl = false;
        ShowWsl(_wslBytes);
        var ok = false;
        try { ok = (await Cli.RunAsync(System.IO.Path.Combine(Environment.SystemDirectory, "wsl.exe"), ["--shutdown"], null, TimeSpan.FromSeconds(30))).Code == 0; }
        catch (Exception ex) { Logger.Warn($"sysmon: wsl --shutdown failed ({ex.GetType().Name})"); }
        _island.ShowToast(Strings.T(ok ? "sysmon.wslDone" : "sysmon.wslFailed"));
        if (ok) ShowWsl(null);
    }

    FrameworkElement Row(ProcessUsage p)
    {
        var row = new DockPanel { Margin = new Thickness(4, 0, 0, 2), LastChildFill = true };
        var confirming = _confirmPid == p.Pid;
        var kill = confirming
            ? Ui.TextButton(Strings.T("sysmon.endConfirm"), "AccentButton", new Thickness(8, 1, 8, 1), () => End(p), "sysmon end")
            : Ui.IconButton(Ui.MakeIcon("Icon.Close", 11), 22, () => { _confirmPid = p.Pid; ShowProcesses(_lastTop); }, "sysmon end");
        kill.ToolTip = Strings.T("sysmon.end", p.Name);
        System.Windows.Automation.AutomationProperties.SetName(kill, Strings.T("sysmon.end", p.Name));
        DockPanel.SetDock(kill, Dock.Right);
        row.Children.Add(kill);
        foreach (var (text, width) in new[] { (SystemMath.Size(p.Memory), 64.0), (p.Cpu.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%", 48.0) })
        {
            var t = new TextBlock { Text = text, Width = width, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            t.SetResourceReference(StyleProperty, "TextDim");
            DockPanel.SetDock(t, Dock.Right);
            row.Children.Add(t);
        }
        var name = new TextBlock { Text = p.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(StyleProperty, "Text");
        row.Children.Add(name);
        return row;
    }

    IReadOnlyList<ProcessUsage> _lastTop = [];

    void End(ProcessUsage p)
    {
        _confirmPid = null;
        _island.ShowToast(Strings.T(ProcessOps.TryEnd(p.Pid, p.Name) ? "sysmon.ended" : "sysmon.endFailed", p.Name));
        ShowProcesses(_lastTop.Where(x => x.Pid != p.Pid).ToList());
    }

    public void Dispose() => _timer?.Stop();

    /// <summary>One card: caption, big value, small detail and a sparkline of the last minute.</summary>
    sealed class Stat
    {
        public readonly Border Root = new() { Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 6, 10, 6) };
        readonly TextBlock _value = new() { FontSize = 18, FontWeight = FontWeights.SemiBold };
        readonly TextBlock _detail = new() { FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly Polyline _line = new() { StrokeThickness = 1.5, Height = 18, Stretch = Stretch.None, StrokeLineJoin = PenLineJoin.Round };
        readonly Queue<double> _history = new();

        public Stat(string caption)
        {
            Root.SetResourceReference(StyleProperty, "Card");
            var stack = new StackPanel();
            var c = new TextBlock { Text = caption };
            c.SetResourceReference(StyleProperty, "TextCaption");
            stack.Children.Add(c);
            _value.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            stack.Children.Add(_value);
            _detail.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
            stack.Children.Add(_detail);
            _line.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
            _line.Margin = new Thickness(0, 3, 0, 0);
            stack.Children.Add(_line);
            Root.Child = stack;
            Root.SizeChanged += (_, _) => Draw(null);
        }

        public void Set(double percent, string value, string detail)
        {
            _value.Text = value;
            _detail.Text = detail;
            _value.SetResourceReference(TextBlock.ForegroundProperty, percent >= 90 ? "DangerBrush" : percent >= 75 ? "WarnBrush" : "FgBrush");
            Push(percent);
            Draw(100);
        }

        public void SetRate(double total, string value, string detail)
        {
            _value.Text = value;
            _value.FontSize = 13;
            _detail.Text = detail;
            Push(total);
            Draw(null);
        }

        void Push(double v)
        {
            _history.Enqueue(v);
            while (_history.Count > History) _history.Dequeue();
        }

        double? _scale;

        void Draw(double? max)
        {
            if (max is not null) _scale = max;
            var width = Root.ActualWidth - Root.Padding.Left - Root.Padding.Right;
            if (width <= 0 || _history.Count < 2) return;
            var top = _scale ?? Math.Max(1, _history.Max());
            var points = new PointCollection();
            var i = History - _history.Count;
            foreach (var v in _history)
                points.Add(new Point(width * i++ / (History - 1), 18 - 17 * Math.Clamp(v / top, 0, 1)));
            _line.Points = points;
        }
    }
}
