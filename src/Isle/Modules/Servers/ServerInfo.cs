using System.Globalization;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Isle.Modules.Servers;

public sealed record SshHost(string Alias, string HostName, string? User, int Port);
public sealed record AwsProfile(string Name, string? StartUrl);
public sealed record CloudAccount(string Tool, string Name, string? Who);

/// <summary>Read-only parsers for ~/.ssh/config and the cloud CLIs' config files. Secrets (tokens, keys) are never read.</summary>
public static class ServerInfo
{
    static readonly char[] Sep = [' ', '\t', '='];

    /// <summary>Concrete Host aliases (wildcards, negations and "-x" skipped) with HostName/User/Port; Include is followed (max depth 4).</summary>
    public static List<SshHost> ParseSsh(string path, string sshDir, int depth = 0)
    {
        var hosts = new List<SshHost>();
        if (depth > 4 || !TryLines(path, out var lines)) return hosts;
        string?[] cur = [null, null, null]; // HostName, User, Port of the open block
        List<string> aliases = [];
        void Flush()
        {
            foreach (var a in aliases)
                hosts.Add(new SshHost(a, cur[0] ?? a, cur[1], int.TryParse(cur[2], out var p) && p is > 0 and < 65536 ? p : 22));
            aliases = [];
            cur = [null, null, null];
        }
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split(Sep, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;
            var val = Regex.Replace(parts[1], @"\s#.*$", "").Trim().Trim('"');
            switch (parts[0].ToLowerInvariant())
            {
                case "host":
                    Flush();
                    aliases = val.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Where(a => a.IndexOfAny(['*', '?', '!']) < 0 && !a.StartsWith('-')).Distinct().ToList();
                    break;
                case "match": Flush(); break;
                case "hostname": cur[0] ??= val; break;
                case "user": cur[1] ??= val; break;
                case "port": cur[2] ??= val; break;
                case "include":
                    foreach (var pat in val.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        foreach (var f in Expand(pat.Trim('"'), sshDir)) hosts.AddRange(ParseSsh(f, sshDir, depth + 1));
                    break;
            }
        }
        Flush();
        return hosts.DistinctBy(h => h.Alias).ToList();
    }

    static IEnumerable<string> Expand(string pattern, string sshDir)
    {
        if (pattern.StartsWith("~/")) pattern = Path.Combine(Path.GetDirectoryName(sshDir) ?? "", pattern[2..]);
        else if (!Path.IsPathRooted(pattern)) pattern = Path.Combine(sshDir, pattern);
        var dir = Path.GetDirectoryName(pattern);
        if (dir is null || !Directory.Exists(dir)) return [];
        try { return Directory.GetFiles(dir, Path.GetFileName(pattern)).Order().ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    /// <summary>Profiles from ~/.aws/config; an SSO profile carries its start URL (own sso_start_url or via sso_session).</summary>
    public static List<AwsProfile> ParseAws(IEnumerable<string> lines)
    {
        Dictionary<string, Dictionary<string, string>> secs = [];
        Dictionary<string, string>? cur = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[' && line[^1] == ']') { cur = secs[line[1..^1].Trim()] = []; continue; }
            var eq = line.IndexOf('=');
            if (eq > 0 && cur is not null) cur[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return secs.Where(s => s.Key == "default" || s.Key.StartsWith("profile ")).Select(s =>
        {
            var url = s.Value.GetValueOrDefault("sso_start_url");
            if (url is null && s.Value.TryGetValue("sso_session", out var sess) && secs.TryGetValue("sso-session " + sess, out var ss)) url = ss.GetValueOrDefault("sso_start_url");
            return new AwsProfile(s.Key.StartsWith("profile ") ? s.Key[8..].Trim() : s.Key, url);
        }).ToList();
    }

    /// <summary>Newest expiresAt per startUrl from the SSO cache. Only those two fields are read; accessToken is never touched.</summary>
    public static Dictionary<string, DateTimeOffset> SsoExpiries(string cacheDir)
    {
        Dictionary<string, DateTimeOffset> r = new(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(cacheDir)) return r;
        foreach (var f in Directory.EnumerateFiles(cacheDir, "*.json"))
        {
            try
            {
                using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var d = JsonDocument.Parse(s);
                if (Expiry(d.RootElement) is { } e && (!r.TryGetValue(e.Url, out var old) || e.At > old)) r[e.Url] = e.At;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return r;
    }

    static (string Url, DateTimeOffset At)? Expiry(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("startUrl", out var u) && u.ValueKind == JsonValueKind.String
        && root.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(e.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? (u.GetString()!, at) : null;

    /// <summary>The latest expiry among the SSO start URLs the profiles use (the most recent login), or null without any.</summary>
    public static DateTimeOffset? SsoLatest(IEnumerable<AwsProfile> profiles, IReadOnlyDictionary<string, DateTimeOffset> cache)
    {
        var all = profiles.Where(p => p.StartUrl is not null && cache.ContainsKey(p.StartUrl)).Select(p => cache[p.StartUrl!]).ToList();
        return all.Count == 0 ? null : all.Max();
    }

    /// <summary>Pill only for "soon" or an expiry within the last 24 h; older expiries show in the tab only.</summary>
    public static bool SsoPillDue(DateTimeOffset expires, DateTimeOffset now, TimeSpan warn) =>
        SsoState(expires, now, warn) is var st && st != "ok" && (st != "expired" || now - expires <= TimeSpan.FromHours(24));

    public static string SsoState(DateTimeOffset expires, DateTimeOffset now, TimeSpan warn) =>
        expires <= now ? "expired" : expires - now <= warn ? "soon" : "ok";

    public static CloudAccount? ParseAzure(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json.TrimStart('﻿'));
            if (!d.RootElement.TryGetProperty("subscriptions", out var subs)) return null;
            foreach (var s in subs.EnumerateArray())
                if (s.TryGetProperty("isDefault", out var def) && def.ValueKind == JsonValueKind.True && s.TryGetProperty("name", out var n))
                    return new("Azure", n.GetString() ?? "", s.TryGetProperty("user", out var u) && u.TryGetProperty("name", out var un) ? un.GetString() : null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        return null;
    }

    /// <summary>active_config + configurations\config_NAME ([core] account); null when the files are missing.</summary>
    public static CloudAccount? ReadGcloud(string dir)
    {
        try
        {
            var active = Path.Combine(dir, "active_config");
            var name = File.Exists(active) ? File.ReadAllText(active).Trim() : "default";
            if (name.Length == 0 || name.IndexOfAny(['/', '\\']) >= 0) name = "default";
            var cfg = Path.Combine(dir, "configurations", "config_" + name);
            if (!File.Exists(cfg)) return null;
            string? account = null;
            foreach (var l in File.ReadLines(cfg))
            {
                var m = Regex.Match(l, @"^\s*account\s*=\s*(.+?)\s*$");
                if (m.Success) account = m.Groups[1].Value;
            }
            return new("gcloud", name, account);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static CloudAccount? ReadAzure(string profile)
    {
        try { var f = Path.Combine(profile, ".azure", "azureProfile.json"); return File.Exists(f) ? ParseAzure(File.ReadAllText(f)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    static bool TryLines(string path, out string[] lines)
    {
        try { lines = File.ReadAllLines(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lines = []; return false; }
    }

    public static List<AwsProfile> ReadAws(string profile) => TryLines(Path.Combine(profile, ".aws", "config"), out var l) ? ParseAws(l) : [];

    /// <summary>Plain TCP connect; true when the port accepts within the timeout. No login, no data sent.</summary>
    public static async Task<bool> Reachable(string host, int port, int timeoutMs = 1500)
    {
        try
        {
            using var c = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await c.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException) { return false; }
    }

    /// <summary>Adds or removes <paramref name="alias"/> in the ";"-separated per-host list (case-insensitive, no duplicates).</summary>
    public static string SetReach(string perHost, string alias, bool on)
    {
        var l = perHost.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(a => !a.Equals(alias, StringComparison.OrdinalIgnoreCase)).ToList();
        if (on) l.Add(alias);
        return string.Join("; ", l);
    }

    public static bool ReachWanted(bool global, string perHost, string alias) =>
        global || perHost.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(alias, StringComparer.OrdinalIgnoreCase);
}

/// <summary>One watch-list entry: "target [http] [tls] [status]" (no flags: http for a URL, tls for a bare host).</summary>
public sealed record WatchItem(string Target, bool Http, bool Tls, bool Status)
{
    public static List<WatchItem> Parse(string text) => text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(e =>
    {
        var p = e.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var flags = p.Skip(1).Select(f => f.ToLowerInvariant()).ToHashSet();
        var url = p[0].StartsWith("http://", StringComparison.OrdinalIgnoreCase) || p[0].StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (!flags.Overlaps(["http", "tls", "status"])) flags.Add(url ? "http" : "tls");
        return new WatchItem(p[0], flags.Contains("http"), flags.Contains("tls"), flags.Contains("status"));
    }).ToList();

    /// <summary>Same text form Parse reads, with explicit flags so it round-trips.</summary>
    public string Format() => Target + (Http ? " http" : "") + (Tls ? " tls" : "") + (Status ? " status" : "");

    public static string Format(IEnumerable<WatchItem> items) => string.Join("; ", items.Select(i => i.Format()));

    /// <summary>A new entry from the Add row: trimmed, one valid http(s) target without whitespace or ';', not already listed. No flags = Parse's defaults. Null when rejected.</summary>
    public static WatchItem? TryNew(string input, bool http, bool tls, bool status, IEnumerable<WatchItem> existing)
    {
        var t = input.Trim();
        if (t.Length == 0 || t.Any(c => char.IsWhiteSpace(c) || c == ';')) return null;
        if (existing.Any(i => i.Target.Equals(t, StringComparison.OrdinalIgnoreCase))) return null;
        var item = Parse(t + (http ? " http" : "") + (tls ? " tls" : "") + (status ? " status" : "")).Single();
        return item.Url is null ? null : item;
    }

    Uri? Url => Uri.TryCreate(Target.Contains("://") ? Target : "https://" + Target, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u : null;

    public static bool HttpDown(int? status) => status is null or >= 400;
    public static bool CertSoon(DateTime notAfterUtc, DateTime nowUtc, int days) => notAfterUtc - nowUtc <= TimeSpan.FromDays(days);

    /// <summary>
    /// Problems of the TLS check from the certificate expiry and the handshake's trust verdict. <paramref name="chainOnlyExpired"/>: the only chain
    /// error is NotTimeValid, which the expiry text already covers. Untrusted = name mismatch, chain error (incl. self-signed) or no certificate.
    /// </summary>
    public static List<string> TlsProblems(SslPolicyErrors errors, bool chainOnlyExpired, DateTime notAfterUtc, DateTime nowUtc, int days)
    {
        List<string> bad = [];
        if (CertSoon(notAfterUtc, nowUtc, days)) bad.Add(notAfterUtc < nowUtc ? "cert expired" : $"cert {(int)(notAfterUtc - nowUtc).TotalDays}d");
        if (chainOnlyExpired) errors &= ~SslPolicyErrors.RemoteCertificateChainErrors;
        if (errors != SslPolicyErrors.None)
        {
            List<string> why = [];
            if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)) why.Add("name");
            if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors)) why.Add("chain");
            if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) why.Add("none");
            bad.Add($"untrusted cert ({string.Join(", ", why)})");
        }
        return bad;
    }

    /// <summary>The "indicator" of a statuspage /api/v2/status.json when it is not "none"; null when fine or unparsable.</summary>
    public static string? StatusProblem(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            var i = d.RootElement.GetProperty("status").GetProperty("indicator").GetString();
            return i is null or "none" ? null : i;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    /// <summary>Runs the enabled checks; returns short problem texts (empty = fine). Only this item's own target is contacted.</summary>
    public async Task<List<string>> Run(HttpClient http, int certDays, CancellationToken ct)
    {
        List<string> bad = [];
        if (Url is not { } u) return ["invalid"];
        if (Http)
        {
            int? code = null;
            try { using var r = await http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct); code = (int)r.StatusCode; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
            if (HttpDown(code)) bad.Add(code is null ? "down" : $"HTTP {code}");
        }
        if (Status)
        {
            try
            {
                var j = await http.GetStringAsync(new Uri(u, "/api/v2/status.json"), ct);
                if (StatusProblem(j) is { } p) bad.Add("status " + p);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { bad.Add("status n/a"); }
        }
        if (Tls)
        {
            try
            {
                using var c = new TcpClient();
                await c.ConnectAsync(u.Host, u.Port, ct);
                // accepts any chain so the expiry can still be read; the trust verdict is recorded and reported
                var errors = SslPolicyErrors.None;
                var onlyExpired = false;
                using var ssl = new SslStream(c.GetStream(), false, (_, _, chain, e) =>
                {
                    errors = e;
                    onlyExpired = chain is { ChainStatus.Length: > 0 } && chain.ChainStatus.All(x => x.Status == System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.NotTimeValid);
                    return true;
                });
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = u.Host }, ct);
                if (ssl.RemoteCertificate is { } cert)
                    bad.AddRange(TlsProblems(errors, onlyExpired, new System.Security.Cryptography.X509Certificates.X509Certificate2(cert).NotAfter.ToUniversalTime(), DateTime.UtcNow, certDays));
            }
            catch (Exception ex) when (ex is IOException or SocketException or System.Security.Authentication.AuthenticationException or OperationCanceledException) { bad.Add("tls n/a"); }
        }
        return bad;
    }
}
