using System.Net.Http;
using System.Windows.Threading;
using Isle.Core;
using Isle.Views.Docker;
using Isle.Views.Live;

namespace Isle.Modules.Docker;

/// <summary>
/// Containers (module id "docker"; the engine is the setup answer docker|podman, none keeps it off): the Docker tab,
/// refreshed from the engine's /events stream instead of polling, a crash-loop pill and an engine-down pill.
/// </summary>
public sealed class DockerModule(IslandWindow island) : IModule, IDisposable
{
    public const string TabId = "docker";
    const string DownId = "docker.down", CrashId = "docker.crash";

    CancellationTokenSource? _cts;
    DispatcherTimer? _crashTimer;
    int _refreshing;
    volatile bool _again;

    public string Id => "docker";
    public DockerApi? Api { get; private set; }
    public string Engine { get; internal set; } = "docker";
    public IReadOnlyList<Box> Boxes { get; internal set; } = [];
    /// <summary>The engine answered the last time we asked.</summary>
    public bool Up { get; internal set; }
    public event Action? Changed;

    public void Start()
    {
        var choice = Settings.Current.Opt("setup.containers");
        if (choice is not ("docker" or "podman")) return; // none or never asked: stays off
        Engine = choice;
        Api = new DockerApi(DockerApi.PipeFor(choice));
        island.AddTab(new TabInfo(TabId, "tab.docker", "Icon.Docker", () => new DockerView(this, island), 97));
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => Loop(Api, _cts.Token));
    }

    async Task Loop(DockerApi api, CancellationToken ct)
    {
        var crash = new CrashTracker();
        bool wasUp = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!api.PipeExists()) throw new IOException("engine pipe missing");
                var boxes = await api.ListAsync().ConfigureAwait(false);
                wasUp = true;
                Post(() => { Boxes = boxes; SetUp(true); });
                await api.WatchAsync(e =>
                {
                    if (crash.Add(e)) Post(() => Crash(e.Name));
                    if (!e.Action.StartsWith("exec_", StringComparison.Ordinal)) Refresh(api);
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or HttpRequestException or TimeoutException or OperationCanceledException or System.Text.Json.JsonException) { }
            Post(() => { Boxes = []; SetUp(false, showDown: wasUp); });
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Re-reads the container list; events arriving meanwhile cause one more read.</summary>
    void Refresh(DockerApi api)
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) { _again = true; return; }
        _ = Task.Run(async () =>
        {
            try
            {
                do
                {
                    _again = false;
                    await Task.Delay(250).ConfigureAwait(false); // an event burst (compose up) becomes one read
                    var boxes = await api.ListAsync().ConfigureAwait(false);
                    Post(() => Boxes = boxes);
                } while (_again);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException) { }
            finally { Interlocked.Exchange(ref _refreshing, 0); }
        });
    }

    void Post(Action a) => island.Dispatcher.BeginInvoke(() => { if (_cts is { IsCancellationRequested: false }) { a(); Changed?.Invoke(); } });

    void SetUp(bool up, bool showDown = false)
    {
        var was = Up;
        Up = up;
        if (up) island.Pill.Remove(DownId);
        else if (showDown && was) island.Pill.Set(DownItem(Engine));
    }

    internal static PillItem DownItem(string engine) => new()
    {
        Id = DownId, Priority = PillPriority.MicCamera - 1, TabId = TabId,
        View = () => new IndicatorPill("Icon.Docker", Strings.T("docker.down", Name(engine)), "", "WarnBrush"),
        Compact = () => new IndicatorPill("Icon.Docker", "", "", "WarnBrush", center: true), CompactWidth = 40, Width = 190,
    };

    internal static PillItem CrashItem(string name) => new()
    {
        Id = CrashId, Priority = PillPriority.Transient, TabId = TabId, Peek = true,
        Width = IndicatorPill.MessageWidth(Strings.T("docker.crash", name)) + 30,
        View = () => new IndicatorPill("Icon.Docker", Strings.T("docker.crash", name), "", "DangerBrush"),
    };

    static string Name(string engine) => engine == "podman" ? "Podman" : "Docker";

    void Crash(string name)
    {
        island.Pill.Set(CrashItem(name));
        _crashTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _crashTimer.Tick -= HideCrash;
        _crashTimer.Tick += HideCrash;
        _crashTimer.Stop();
        _crashTimer.Start();
    }

    void HideCrash(object? s, EventArgs e) { _crashTimer?.Stop(); island.Pill.Remove(CrashId); }

    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        cts?.Cancel();
        _crashTimer?.Stop();
        island.Pill.Remove(DownId);
        island.Pill.Remove(CrashId);
        island.RemoveTab(TabId);
        Api?.Dispose();
        Api = null;
        Boxes = [];
        Up = false;
    }

    public void Dispose() => Stop();
}

/// <summary>--snapshot states: demo containers only, nothing talks to an engine.</summary>
static class DockerSnapshots
{
    static readonly Box[] Demo =
    [
        new("a1", "shop-web", "shop/web:1.4", "running", "Up 2 hours", "shop", @"C:\demo\shop", @"C:\demo\shop\compose.yaml", null, 8080),
        new("a2", "shop-db", "postgres:17", "running", "Up 2 hours", "shop", @"C:\demo\shop", @"C:\demo\shop\compose.yaml", "PostgreSQL", 5432),
        new("a3", "shop-cache", "redis:7", "exited", "Exited (0) 3 hours ago", "shop", @"C:\demo\shop", @"C:\demo\shop\compose.yaml", "Redis", 6379),
        new("b1", "blog-db", "mariadb:11", "running", "Up 5 minutes", "blog", @"C:\demo\blog", @"C:\demo\blog\compose.yaml", "MariaDB", 3306),
        new("c1", "scratch", "alpine:3", "exited", "Exited (137) 1 day ago", null, null, null, null, null),
    ];

    public static void Register(Dictionary<string, Action<IslandWindow>> states)
    {
        states["docker-tab"] = w => Tab(w, Demo, true);
        states["docker-down"] = w => Tab(w, [], false);
        states["docker-pill-down"] = w => w.Pill.Set(DockerModule.DownItem("docker"));
        states["docker-pill-crash"] = w => w.Pill.Set(DockerModule.CrashItem("shop-web"));
        states["docker-prune"] = w => Tab(w, Demo, true).ShowPrune(new DiskUsage(4_200_000_000, 12_000_000, 800_000_000, 1_100_000_000));
        states["docker-logs"] = w => Tab(w, Demo, true).ShowLogs("shop-web", "listening on :8080\nGET /health 200\nGET /items 200\nerror: upstream timeout\nretrying in 5s");
    }

    static DockerView Tab(IslandWindow w, IReadOnlyList<Box> boxes, bool up)
    {
        var m = new DockerModule(w) { Boxes = boxes, Up = up };
        var view = new DockerView(m, w);
        view.Render(new DiskUsage(4_200_000_000, 12_000_000, 800_000_000, 1_100_000_000));
        w.AddTab(new TabInfo(DockerModule.TabId, "tab.docker", "Icon.Docker", () => view, 97));
        w.Expand(DockerModule.TabId);
        return view;
    }
}
