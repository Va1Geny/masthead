using Isle.Core;
using Isle.Views.SystemMonitor;

namespace Isle.Modules.SystemMonitor;

/// <summary>--snapshot states for the system monitor (a fixed busy minute; nothing is read from the machine).</summary>
static class SystemMonitorSnapshots
{
    public static void Register(Dictionary<string, Action<IslandWindow>> states)
    {
        states["sysmon-tab"] = w =>
        {
            var view = new SystemMonitorView(null, w);
            for (var i = 0; i < 60; i++)
            {
                var wave = Math.Sin(i / 6.0);
                view.Show(new SystemSample(
                    Cpu: i > 50 ? 93 : 35 + 20 * wave, Cores: [97, 88, 40, 31, 22, 18, 12, 9],
                    MemUsed: 12.4 * (1L << 30), MemTotal: 16.0 * (1L << 30),
                    Gpu: 55 + 25 * Math.Cos(i / 9.0), VramUsed: 2.9 * (1L << 30), VramTotal: 4.0 * (1L << 30),
                    NetDown: 1_400_000 * (1.2 + wave), NetUp: 48_000, DiskRead: 0, DiskWrite: 0));
            }
            view.ShowProcesses([
                new ProcessUsage(1, "dotnet", 41.3, 812L << 20),
                new ProcessUsage(2, "chrome", 12.8, 1_430L << 20),
                new ProcessUsage(3, "Code", 6.1, 960L << 20),
                new ProcessUsage(4, "node", 2.4, 310L << 20),
                new ProcessUsage(5, "Isle", 0.2, 24L << 20),
            ]);
            view.ShowWsl(6_240L << 20);
            w.AddTab(new TabInfo(SystemMonitorModule.TabId, "tab.sysmon", "Icon.Gauge", () => view, 95));
            w.Expand(SystemMonitorModule.TabId);
        };
        states["sysmon-alert"] = w => w.Pill.Set(new PillItem
        {
            Id = "sysmon.alert", Priority = PillPriority.Transient, Width = 200, Peek = true,
            View = () => SystemMonitorModule.AlertView(Strings.T("sysmon.alertCpu", 97)),
        });
    }
}
