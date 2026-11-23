using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Isle.Core;
using Isle.Views.SystemMonitor;

namespace Isle.Modules.SystemMonitor;

/// <summary>Fires once a value has stayed at or above a threshold for a while, then stays quiet for a cooldown.</summary>
public sealed class SustainedAlert(double threshold, TimeSpan hold, TimeSpan cooldown)
{
    DateTimeOffset? _since;
    DateTimeOffset _quietUntil = DateTimeOffset.MinValue;

    public bool Update(double value, DateTimeOffset now)
    {
        if (value < threshold) { _since = null; return false; }
        _since ??= now;
        if (now - _since < hold || now < _quietUntil) return false;
        _quietUntil = now + cooldown;
        return true;
    }
}

/// <summary>
/// System monitor (module id "sysmon"): the System tab, plus a cheap background check that shows a short pill
/// when CPU or memory stays above 90% (CPU and memory only; every 5 s; nothing else is read in the background).
/// </summary>
public sealed class SystemMonitorModule(IslandWindow island) : IModule, IDisposable
{
    public const string TabId = "sysmon";
    const string PillId = "sysmon.alert";

    SystemSampler? _sampler;
    DispatcherTimer? _background, _pillTimer;
    readonly SustainedAlert _cpuAlert = new(90, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10));
    readonly SustainedAlert _memAlert = new(90, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10));

    public string Id => "sysmon";

    public void Start()
    {
        _sampler = new SystemSampler();
        island.AddTab(new TabInfo(TabId, "tab.sysmon", "Icon.Gauge", () => new SystemMonitorView(_sampler, island), 95));
        if (Settings.Current.OptBool("sysmon.alerts", true))
        {
            _background = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
            _background.Tick += (_, _) => Check();
            _background.Start();
        }
    }

    void Check()
    {
        if (_sampler is null || island.TabViewFor(TabId) is SystemMonitorView { IsLive: true }) return; // the tab is already showing it
        try
        {
            var (cpu, mem) = _sampler.Light();
            var now = DateTimeOffset.Now;
            if (_cpuAlert.Update(cpu, now)) Alert(Strings.T("sysmon.alertCpu", Math.Round(cpu)));
            else if (_memAlert.Update(mem, now)) Alert(Strings.T("sysmon.alertMem", Math.Round(mem)));
        }
        catch (Exception ex) { Logger.Error("sysmon check", ex); }
    }

    void Alert(string text)
    {
        island.Pill.Set(new PillItem
        {
            Id = PillId, Priority = PillPriority.Transient, Width = 200, Peek = true, TabId = TabId,
            View = () => AlertView(text),
        });
        _pillTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _pillTimer.Tick -= HidePill;
        _pillTimer.Tick += HidePill;
        _pillTimer.Stop();
        _pillTimer.Start();
    }

    void HidePill(object? sender, EventArgs e)
    {
        _pillTimer?.Stop();
        island.Pill.Remove(PillId);
    }

    internal static FrameworkElement AlertView(string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var icon = Views.Ui.MakeIcon("Icon.Gauge", 14, "WarnBrush");
        icon.Margin = new Thickness(0, 0, 6, 0);
        row.Children.Add(icon);
        var t = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        row.Children.Add(t);
        return row;
    }

    public void Stop()
    {
        _background?.Stop();
        _background = null;
        _pillTimer?.Stop();
        island.Pill.Remove(PillId);
        island.RemoveTab(TabId);
        _sampler?.Dispose();
        _sampler = null;
    }

    public void Dispose() { if (_sampler is not null) Stop(); }
}
