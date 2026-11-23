using Isle.Modules.Servers;

namespace Isle.Tests;

public class ServersTests
{
    static string Dir() => Directory.CreateTempSubdirectory().FullName;

    [Fact]
    public void Ssh_skips_wildcards_reads_blocks_and_follows_include()
    {
        var d = Dir();
        File.WriteAllText(Path.Combine(d, "extra"), "Host extra-box\n  HostName extra.example.com\n  User bob\n");
        File.WriteAllText(Path.Combine(d, "config"), string.Join('\n',
            "Include extra", "Include missing*",
            "Host *", "  User everyone",
            "Host web web-alt  # two aliases", "  HostName web.example.com", "  Port 2222", "  User deploy",
            "Host !bad -x db?", "  HostName nope",
            "Host plain", "Match host foo", "  User ignored"));
        var h = ServerInfo.ParseSsh(Path.Combine(d, "config"), d);
        Assert.Equal(["extra-box", "web", "web-alt", "plain"], h.Select(x => x.Alias).ToArray());
        Assert.Equal(new SshHost("web", "web.example.com", "deploy", 2222), h[1]);
        Assert.Equal(new SshHost("plain", "plain", null, 22), h[3]);
        Assert.Equal("bob", h[0].User);
    }

    [Fact]
    public void Ssh_missing_file_and_include_loop_are_harmless()
    {
        Assert.Empty(ServerInfo.ParseSsh(Path.Combine(Dir(), "nope"), Dir()));
        var d = Dir();
        File.WriteAllText(Path.Combine(d, "config"), "Include config\nHost a\n");
        Assert.Single(ServerInfo.ParseSsh(Path.Combine(d, "config"), d));
    }

    [Fact]
    public void Aws_profiles_resolve_sso_start_url_via_session()
    {
        var p = ServerInfo.ParseAws(["[default]", "region = x", "[profile dev]", "sso_session = corp", "[profile old]", "sso_start_url = https://old.example.com/start", "[sso-session corp]", "sso_start_url = https://corp.example.com/start"]);
        Assert.Equal(["default", "dev", "old"], p.Select(x => x.Name).ToArray());
        Assert.Null(p[0].StartUrl);
        Assert.Equal("https://corp.example.com/start", p[1].StartUrl);
        Assert.Equal("https://old.example.com/start", p[2].StartUrl);
    }

    [Fact]
    public void Sso_cache_reads_only_expiry_newest_wins_and_ignores_other_files()
    {
        var d = Dir();
        File.WriteAllText(Path.Combine(d, "a.json"), """{"startUrl":"https://corp.example.com/start","accessToken":"SECRET","expiresAt":"2026-01-01T10:00:00Z"}""");
        File.WriteAllText(Path.Combine(d, "b.json"), """{"startUrl":"https://corp.example.com/start","expiresAt":"2026-01-02T10:00:00Z"}""");
        File.WriteAllText(Path.Combine(d, "client.json"), """{"clientId":"x","expiresAt":"2030-01-01T00:00:00Z"}""");
        File.WriteAllText(Path.Combine(d, "bad.json"), "{nope");
        var c = ServerInfo.SsoExpiries(d);
        Assert.Single(c);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.Zero), c["https://corp.example.com/start"]);
        Assert.Equal(c["https://corp.example.com/start"], ServerInfo.SsoLatest([new("dev", "https://corp.example.com/start")], c));
        Assert.Null(ServerInfo.SsoLatest([new("dev", null)], c));
        Assert.Empty(ServerInfo.SsoExpiries(Path.Combine(d, "missing")));
    }

    [Fact]
    public void Sso_state_ok_soon_expired()
    {
        var now = DateTimeOffset.UtcNow;
        var w = TimeSpan.FromMinutes(30);
        Assert.Equal("expired", ServerInfo.SsoState(now.AddSeconds(-1), now, w));
        Assert.Equal("soon", ServerInfo.SsoState(now.AddMinutes(10), now, w));
        Assert.Equal("ok", ServerInfo.SsoState(now.AddHours(3), now, w));
    }

    [Fact]
    public void Sso_pill_skips_long_expired()
    {
        var now = DateTimeOffset.UtcNow;
        var w = TimeSpan.FromMinutes(30);
        Assert.True(ServerInfo.SsoPillDue(now.AddMinutes(10), now, w));
        Assert.True(ServerInfo.SsoPillDue(now.AddHours(-23), now, w));
        Assert.False(ServerInfo.SsoPillDue(now.AddHours(-25), now, w));
        Assert.False(ServerInfo.SsoPillDue(now.AddHours(3), now, w));
    }

    [Fact]
    public async Task Tls_check_times_out_on_silent_host()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var item = new WatchItem("127.0.0.1:" + ((System.Net.IPEndPoint)l.LocalEndpoint).Port, false, true, false);
            using var http = new System.Net.Http.HttpClient();
            var bad = await item.Run(http, 14, cts.Token).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(["tls n/a"], bad);
        }
        finally { l.Stop(); }
    }

    [Fact]
    public void Azure_default_subscription_and_user()
    {
        var a = ServerInfo.ParseAzure("﻿" + """{"subscriptions":[{"name":"Other","isDefault":false},{"name":"Main","isDefault":true,"user":{"name":"me@example.com","type":"user"}}]}""");
        Assert.Equal(new CloudAccount("Azure", "Main", "me@example.com"), a);
        Assert.Null(ServerInfo.ParseAzure("""{"subscriptions":[]}"""));
        Assert.Null(ServerInfo.ParseAzure("garbage"));
    }

    [Fact]
    public void Gcloud_active_config_account_and_missing_files()
    {
        var d = Dir();
        Assert.Null(ServerInfo.ReadGcloud(d));
        Directory.CreateDirectory(Path.Combine(d, "configurations"));
        File.WriteAllText(Path.Combine(d, "configurations", "config_work"), "[core]\naccount = me@example.com\nproject = p\n");
        File.WriteAllText(Path.Combine(d, "active_config"), "work\n");
        Assert.Equal(new CloudAccount("gcloud", "work", "me@example.com"), ServerInfo.ReadGcloud(d));
        File.WriteAllText(Path.Combine(d, "active_config"), "..\\x");
        Assert.Null(ServerInfo.ReadGcloud(d)); // path-like names fall back to "default", which is absent
    }

    [Fact]
    public void Watch_entries_default_flags()
    {
        var w = WatchItem.Parse("https://a.example.com; b.example.com ; https://c.example.com tls status ;");
        Assert.Equal([new("https://a.example.com", true, false, false), new("b.example.com", false, true, false), new("https://c.example.com", false, true, true)], w);
        Assert.Empty(WatchItem.Parse(""));
    }

    [Fact]
    public void Watch_decisions()
    {
        Assert.True(WatchItem.HttpDown(null));
        Assert.True(WatchItem.HttpDown(503));
        Assert.True(WatchItem.HttpDown(404));
        Assert.False(WatchItem.HttpDown(200));
        Assert.False(WatchItem.HttpDown(302));
        var now = DateTime.UtcNow;
        Assert.True(WatchItem.CertSoon(now.AddDays(5), now, 14));
        Assert.True(WatchItem.CertSoon(now.AddDays(-1), now, 14));
        Assert.False(WatchItem.CertSoon(now.AddDays(40), now, 14));
        Assert.Null(WatchItem.StatusProblem("""{"status":{"indicator":"none","description":"All Systems Operational"}}"""));
        Assert.Equal("major", WatchItem.StatusProblem("""{"status":{"indicator":"major"}}"""));
        Assert.Null(WatchItem.StatusProblem("<html>"));
    }

    [Fact]
    public void Reach_is_opt_in_globally_or_per_host()
    {
        Assert.False(ServerInfo.ReachWanted(false, "", "web"));
        Assert.True(ServerInfo.ReachWanted(true, "", "web"));
        Assert.True(ServerInfo.ReachWanted(false, "db; WEB", "web"));
        Assert.False(ServerInfo.ReachWanted(false, "db", "web"));
    }

    [Fact]
    public async Task Reachable_true_for_open_local_port_false_for_closed()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        Assert.True(await ServerInfo.Reachable("127.0.0.1", port));
        l.Stop();
        Assert.False(await ServerInfo.Reachable("127.0.0.1", port, 500));
    }
}
