using Isle.Core;
using Isle.Views.Ports;

namespace Isle.Modules.Ports;

/// <summary>--snapshot states for the ports tab and the "new listener" pill (fixed rows; nothing is scanned).</summary>
static class PortsSnapshots
{
    static readonly Listener[] Demo =
    [
        new(3000, 18244, "node", true),
        new(5173, 20112, "node", true),
        new(5432, 4120, "postgres", false),
        new(8080, 9921, "java", false),
        new(11434, 7712, "ollama", true),
    ];

    public static void Register(Dictionary<string, Action<IslandWindow>> states)
    {
        states["ports-tab"] = w => Tab(w, Demo);
        states["ports-empty"] = w => Tab(w, []);
        states["ports-containers"] = w => Tab(w, Demo[..2], [
            new ContainerRow(ContainerKind.Docker, "a1", "db", "postgres:17 · :5432"),
            new ContainerRow(ContainerKind.Docker, "b2", "cache", "redis:7 · :6379"),
            new ContainerRow(ContainerKind.Wsl, "Ubuntu", "Ubuntu", "WSL 2"),
        ]);
        states["ports-pill"] = w => w.Pill.Set(new PillItem
        {
            Id = "ports.new", Priority = PillPriority.Transient, Width = 220, Peek = true, View = () => PortsModule.PillView(Demo[1]),
        });
    }

    static void Tab(IslandWindow w, IReadOnlyList<Listener> rows, IReadOnlyList<ContainerRow>? boxes = null)
    {
        var view = new PortsView(() => rows, w, _ => Task.FromResult((boxes ?? []).ToList()));
        view.Show(rows);
        if (boxes is not null) view.ShowContainers(boxes);
        w.AddTab(new TabInfo(PortsModule.TabId, "tab.ports", "Icon.Server", () => view, 96));
        w.Expand(PortsModule.TabId);
    }
}
