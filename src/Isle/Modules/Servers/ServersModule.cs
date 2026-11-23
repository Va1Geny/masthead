using System.Diagnostics;
using System.Net.Http;
using System.Windows.Threading;
using Isle.Core;
using Isle.Views.Live;
using Isle.Views.Servers;

namespace Isle.Modules.Servers;

/// <summary>Everything the tab shows besides watch results. Missing tools simply give empty lists.</summary>
public sealed record ServersData(List<SshHost> Hosts, List<AwsProfile> Aws, DateTimeOffset? SsoExpiry, List<CloudAccount> Accounts);

/// <summary>
/// Servers (module id "servers"): ssh hosts, read-only cloud account info and an opt-in watch list ("servers.watch").
/// Pills: AWS SSO expiring/expired, watched item down or certificate expiring. The network is only touched for watch-list items
/// the user added and for ssh hosts with reachability switched on.
/// </summary>
public sealed class ServersModule(IslandWindow island) : IModule, IDisposable
{
    public const string TabId = "servers";
    const string SsoId = "servers.sso", WatchId = "servers.watch";
    static readonly TimeSpan SsoWarn = TimeSpan.FromMinutes(30);

    /// <summary>Latest watch results (null problems = not checked yet); read by the tab.</summary>
    public static List<(WatchItem Item, List<string>? Bad)> Results { get; private set; } = [];

    /// <summary>Re-runs the checks now (set while the module runs); the tab awaits it after editing the watch list.</summary>
    public static Func<Task>? Recheck { get; private set; }

    /// <summary>Stores the watch list in "servers.watch", keeps known results for kept entries and re-checks.</summary>
    public static async Task SaveWatch(List<WatchItem> items)
    {
        var s = Settings.Current;
        s.SetOpt("servers.watch", WatchItem.Format(items));
        s.Save("Options");
        var old = Results;
        Results = items.Select(i => (i, old.FirstOrDefault(r => r.Item == i).Bad)).ToList();
        if (Recheck is { } r) await r();
    }

    // No credentials are configured; the cap bounds what a hostile status page can make Isle buffer.
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 1 << 20 };
    DispatcherTimer? _timer;
    bool _running, _busy;

    public string Id => "servers";

    public static string Profile => Environment.GetEnvironmentVariable("USERPROFILE") ?? "";

    public static ServersData Load()
    {
        var p = Profile;
        var aws = ServerInfo.ReadAws(p);
        var exp = aws.Any(a => a.StartUrl is not null) ? ServerInfo.SsoLatest(aws, ServerInfo.SsoExpiries(Path.Combine(p, ".aws", "sso", "cache"))) : null;
        var accounts = new[] { ServerInfo.ReadAzure(p), ServerInfo.ReadGcloud(Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "gcloud")) }.OfType<CloudAccount>().ToList();
        var ssh = Path.Combine(p, ".ssh");
        return new(ServerInfo.ParseSsh(Path.Combine(ssh, "config"), ssh), aws, exp, accounts);
    }

    public void Start()
    {
        _running = true;
        Recheck = RefreshAsync;
        island.AddTab(new TabInfo(TabId, "tab.servers", "Icon.Globe", () => new ServersView(Load, island), 99));
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(5) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    async Task RefreshAsync()
    {
        if (_busy || !_running) return;
        _busy = true;
        try
        {
            var s = Settings.Current;
            var sso = s.OptBool("servers.ssoPill", true) ? await Task.Run(() => Load().SsoExpiry) : null;
            if (!_running) return;
            if (sso is { } e && ServerInfo.SsoPillDue(e, DateTimeOffset.Now, SsoWarn)) island.Pill.Set(SsoPill(e)); else island.Pill.Remove(SsoId);

            var items = WatchItem.Parse(s.Opt("servers.watch"));
            Results = items.Select(i => (i, (List<string>?)null)).ToList();
            var days = s.OptInt("servers.certDays", 14);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var res = await Task.WhenAll(items.Select(async i => (i, await i.Run(Http, days, cts.Token))));
            if (!_running) return;
            Results = res.Select(r => (r.i, (List<string>?)r.Item2)).ToList();
            var bad = Results.Where(r => r.Bad is { Count: > 0 }).ToList();
            if (bad.Count > 0) island.Pill.Set(WatchPill(bad[0].Item.Target, bad[0].Bad![0], bad.Count)); else island.Pill.Remove(WatchId);
        }
        catch (Exception ex) { Logger.Error("servers refresh", ex); }
        finally { _busy = false; }
    }

    internal static string Left(DateTimeOffset exp, DateTimeOffset now) =>
        exp <= now ? Strings.T("servers.expired") : Strings.T("servers.inMin", Math.Max(1, (int)(exp - now).TotalMinutes));

    internal static PillItem SsoPill(DateTimeOffset exp) => Indicator(SsoId, Strings.T("servers.sso"), Left(exp, DateTimeOffset.Now), exp <= DateTimeOffset.Now ? "DangerBrush" : "WarnBrush");

    internal static PillItem WatchPill(string target, string problem, int count) =>
        Indicator(WatchId, target, count > 1 ? $"{problem} +{count - 1}" : problem, "DangerBrush");

    static PillItem Indicator(string id, string text, string sub, string brush) => new()
    {
        Id = id, Priority = PillPriority.MicCamera - 1, Width = 260,
        View = () => new IndicatorPill("Icon.Server", text, sub, brush),
        Compact = () => new IndicatorPill("Icon.Server", "", "", brush, center: true), CompactWidth = 40,
    };

    /// <summary>wt.exe ssh ALIAS; a plain ssh console when Windows Terminal isn't installed. Aliases come from ssh_config (<see cref="ServerInfo.SafeAlias"/>).</summary>
    public static void OpenSsh(string alias)
    {
        if (!ServerInfo.SafeAlias(alias)) return;
        try { Start("wt.exe", alias); }
        catch (System.ComponentModel.Win32Exception)
        {
            try { Start("ssh", alias, shell: true); }
            catch (System.ComponentModel.Win32Exception ex) { Logger.Warn($"ssh launch failed ({ex.GetType().Name})"); }
        }

        static void Start(string exe, string alias, bool shell = false)
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = shell };
            if (exe == "wt.exe") psi.ArgumentList.Add("ssh");
            psi.ArgumentList.Add(alias);
            Process.Start(psi)?.Dispose();
        }
    }

    public void Stop()
    {
        _running = false;
        Recheck = null;
        _timer?.Stop();
        _timer = null;
        island.Pill.Remove(SsoId);
        island.Pill.Remove(WatchId);
        island.RemoveTab(TabId);
    }

    public void Dispose() => Stop();
}

static class ServersSnapshots
{
    static readonly ServersData Demo = new(
        [new("web-1", "web1.example.com", "deploy", 22), new("db-staging", "10.0.0.12", "admin", 2222), new("backup", "backup.example.net", null, 22)],
        [new("default", "https://example.awsapps.com/start")], DateTimeOffset.Now.AddMinutes(20),
        [new("Azure", "Pay-As-You-Go", "me@example.com"), new("gcloud", "default", "me@example.com")]);

    public static void Register(Dictionary<string, Action<IslandWindow>> states)
    {
        states["servers-tab"] = w => Tab(w, Demo, [(new WatchItem("https://example.com", true, true, false), []), (new WatchItem("https://status.example.com", true, false, true), ["status minor"])]);
        states["servers-watch"] = w => Tab(w, new([], [], null, []), [(new WatchItem("https://example.com", true, true, false), []), (new WatchItem("self-signed.example.com", false, true, false), ["untrusted cert (chain)"]), (new WatchItem("https://status.example.com", true, false, true), ["status minor"])]);
        states["servers-empty"] = w => Tab(w, new([], [], null, []), []);
        states["servers-sso"] = w => w.Pill.Set(ServersModule.SsoPill(DateTimeOffset.Now.AddMinutes(20)));
        states["servers-sso-expired"] = w => w.Pill.Set(ServersModule.SsoPill(DateTimeOffset.Now.AddMinutes(-5)));
        states["servers-down"] = w => w.Pill.Set(ServersModule.WatchPill("api.example.com", "HTTP 503", 2));
    }

    static void Tab(IslandWindow w, ServersData d, List<(WatchItem, List<string>?)> results)
    {
        var view = new ServersView(() => d, w);
        view.Show(d, results, new Dictionary<string, bool> { ["web-1"] = true, ["db-staging"] = false });
        w.AddTab(new TabInfo(ServersModule.TabId, "tab.servers", "Icon.Globe", () => view, 99));
        w.Expand(ServersModule.TabId);
    }
}
