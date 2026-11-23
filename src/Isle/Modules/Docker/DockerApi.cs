using System.IO.Pipes;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Isle.Core;

namespace Isle.Modules.Docker;

/// <summary>One container, as the Docker tab shows it.</summary>
public sealed record Box(string Id, string Name, string Image, string State, string Status, string? Project, string? WorkDir, string? ConfigFiles, string? Db, int? Port)
{
    public bool Running => State == "running";
    /// <summary>Friendly label for local databases ("PostgreSQL :5432"), else the image.</summary>
    public string Detail => Db is null ? Image : Port is int p ? $"{Db} :{p}" : Db;
}

/// <summary>An engine event for a container (die, start, ...).</summary>
public sealed record DockerEvent(string Action, string Id, string Name, DateTimeOffset Time);

public sealed record DiskUsage(long Images, long Containers, long Volumes, long BuildCache) { public long Total => Images + Containers + Volumes + BuildCache; }

/// <summary>Pure parsers and rules (unit tested).</summary>
public static class DockerParse
{
    const string ProjectLabel = "com.docker.compose.project";
    static readonly (string Image, string Label, int Port)[] Dbs =
        [("postgres", "PostgreSQL", 5432), ("mysql", "MySQL", 3306), ("mariadb", "MariaDB", 3306), ("redis", "Redis", 6379), ("mongo", "MongoDB", 27017)];

    /// <summary>"registry:5000/lib/postgres:17" gives "postgres".</summary>
    public static string Repo(string image)
    {
        var s = image.Split('@')[0];
        s = s[(s.LastIndexOf('/') + 1)..];
        var colon = s.IndexOf(':');
        return (colon < 0 ? s : s[..colon]).ToLowerInvariant();
    }

    public static List<Box> Containers(string json)
    {
        var list = new List<Box>();
        using var doc = JsonDocument.Parse(json);
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            JsonElement labels = default;
            if (c.TryGetProperty("Labels", out var l) && l.ValueKind == JsonValueKind.Object) labels = l;
            string? Label(string n) => labels.ValueKind == JsonValueKind.Object && labels.TryGetProperty(n, out var v) && v.GetString() is { Length: > 0 } s ? s : null;
            var id = c.Str("Id");
            var name = c.TryGetProperty("Names", out var ns) && ns.ValueKind == JsonValueKind.Array && ns.GetArrayLength() > 0 ? ns[0].GetString()!.TrimStart('/') : id[..Math.Min(12, id.Length)];
            var image = c.Str("Image");
            var db = Dbs.Where(d => d.Image == Repo(image)).Select(d => ((string Label, int Port)?)(d.Label, d.Port)).FirstOrDefault();
            int? port = null;
            if (c.TryGetProperty("Ports", out var ports) && ports.ValueKind == JsonValueKind.Array)
                foreach (var p in ports.EnumerateArray())
                    if (p.TryGetProperty("PublicPort", out var pub) && (port is null || (db is not null && p.GetProperty("PrivatePort").GetInt32() == db.Value.Port))) port = pub.GetInt32();
            list.Add(new Box(id, name, image, c.Str("State"), c.Str("Status"), Label(ProjectLabel), Label("com.docker.compose.project.working_dir"), Label("com.docker.compose.project.config_files"), db?.Label, port));
        }
        return list;
    }

    /// <summary>Compose projects in name order, standalone containers (empty project name) last.</summary>
    public static List<(string Project, List<Box> Boxes)> Group(IEnumerable<Box> boxes) =>
        boxes.GroupBy(b => b.Project ?? "").OrderBy(g => g.Key.Length == 0).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList())).ToList();

    /// <summary>One line of the /events stream; null when it is not a container event.</summary>
    public static DockerEvent? Event(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (!r.TryGetProperty("Type", out var t) || t.GetString() != "container") return null;
            var actor = r.GetProperty("Actor");
            var name = actor.TryGetProperty("Attributes", out var a) && a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var time = r.TryGetProperty("time", out var tm) ? DateTimeOffset.FromUnixTimeSeconds(tm.GetInt64()) : DateTimeOffset.UtcNow;
            return new DockerEvent(r.GetProperty("Action").GetString() ?? "", actor.GetProperty("ID").GetString() ?? "", name, time);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    /// <summary>/system/df. Volumes report -1 while the engine has not measured them (counted as 0).</summary>
    public static DiskUsage Df(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        long L(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? Math.Max(0, v.GetInt64()) : 0;
        long Sum(string list, Func<JsonElement, long> f) => r.TryGetProperty(list, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Sum(f) : 0;
        var images = r.TryGetProperty("LayersSize", out var ls) && ls.ValueKind == JsonValueKind.Number ? ls.GetInt64() : Sum("Images", i => L(i, "Size"));
        return new DiskUsage(images, Sum("Containers", c => L(c, "SizeRw")),
            Sum("Volumes", v => v.TryGetProperty("UsageData", out var u) ? L(u, "Size") : 0), Sum("BuildCache", b => L(b, "Size")));
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024} KB",
    };

    /// <summary>Docker's log stream is framed (8-byte header per chunk) unless the container has a TTY.</summary>
    public static string Logs(byte[] data)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i + 8 <= data.Length && data[i] <= 2 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 0)
        {
            int len = (data[i + 4] << 24) | (data[i + 5] << 16) | (data[i + 6] << 8) | data[i + 7];
            len = Math.Min(len, data.Length - i - 8);
            sb.Append(Encoding.UTF8.GetString(data, i + 8, len));
            i += 8 + len;
        }
        return i == 0 ? Encoding.UTF8.GetString(data) : sb.ToString(); // no frames: TTY, plain text
    }

    /// <summary>Arguments for <c>docker compose</c> on a project, from the labels compose wrote on its containers.</summary>
    public static List<string> ComposeArgs(string project, string? workDir, string? configFiles, params string[] verb)
    {
        var args = new List<string> { "compose", "-p", project };
        if (!string.IsNullOrEmpty(workDir)) { args.Add("--project-directory"); args.Add(workDir); }
        foreach (var f in (configFiles ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { args.Add("-f"); args.Add(f); }
        args.AddRange(verb);
        return args;
    }
}

/// <summary>
/// Incremental form of <see cref="DockerParse.Logs"/> for a followed stream: bytes arrive in arbitrary chunks (headers, payloads and
/// multi-byte characters can be split anywhere) and complete lines come out. Framed (8-byte header) or TTY (plain) is decided from the first bytes.
/// </summary>
public sealed class LogDemux
{
    readonly List<byte> _pending = [];
    readonly Dictionary<int, (Decoder Dec, StringBuilder Line)> _streams = [];
    bool? _framed;

    public List<string> Push(ReadOnlySpan<byte> chunk)
    {
        List<string> lines = [];
        foreach (var b in chunk) _pending.Add(b);
        if (_framed is null)
        {
            if (_pending.Count == 0) return lines;
            if (_pending[0] > 2) _framed = false;
            else if (_pending.Count < 4) return lines;
            else _framed = _pending[1] == 0 && _pending[2] == 0 && _pending[3] == 0;
        }
        if (_framed == false)
        {
            Text(0, _pending.ToArray(), lines);
            _pending.Clear();
            return lines;
        }
        while (_pending.Count >= 8)
        {
            var len = (_pending[4] << 24) | (_pending[5] << 16) | (_pending[6] << 8) | _pending[7];
            if (len < 0 || _pending.Count < 8 + len) break; // wait for the rest of the frame
            Text(_pending[0], _pending.GetRange(8, len).ToArray(), lines);
            _pending.RemoveRange(0, 8 + len);
        }
        return lines;
    }

    /// <summary>At the end of the stream: a last line that had no newline.</summary>
    public List<string> Flush()
    {
        var rest = _streams.Values.Where(s => s.Line.Length > 0).Select(s => s.Line.ToString()).ToList();
        foreach (var s in _streams.Values) s.Line.Clear();
        return rest;
    }

    void Text(int stream, byte[] bytes, List<string> lines)
    {
        if (!_streams.TryGetValue(stream, out var s)) _streams[stream] = s = (Encoding.UTF8.GetDecoder(), new StringBuilder());
        var chars = new char[s.Dec.GetCharCount(bytes, 0, bytes.Length)];
        s.Dec.GetChars(bytes, 0, bytes.Length, chars, 0);
        foreach (var c in chars)
        {
            if (c != '\n') { s.Line.Append(c); continue; }
            if (s.Line.Length > 0 && s.Line[^1] == '\r') s.Line.Length--;
            lines.Add(s.Line.ToString());
            s.Line.Clear();
        }
    }
}

/// <summary>Several "die" events of one container inside a short window mean a crash loop.</summary>
public sealed class CrashTracker
{
    const int Threshold = 3;
    static readonly TimeSpan Window = TimeSpan.FromMinutes(2);
    readonly Dictionary<string, List<DateTimeOffset>> _dies = [];

    /// <summary>True when this event completes a crash loop (the counter then starts over).</summary>
    public bool Add(DockerEvent e)
    {
        if (e.Action != "die") return false;
        if (!_dies.TryGetValue(e.Id, out var list)) _dies[e.Id] = list = [];
        list.RemoveAll(t => e.Time - t > Window);
        list.Add(e.Time);
        if (list.Count < Threshold) return false;
        list.Clear();
        return true;
    }
}

/// <summary>Docker Engine API over a named pipe (Docker Desktop and the Podman machine both speak it).</summary>
public sealed class DockerApi : IDisposable
{
    public const string DockerPipe = @"\\.\pipe\docker_engine", PodmanPipe = @"\\.\pipe\podman-machine-default";

    readonly string _pipe;
    readonly HttpClient _http;
    readonly CancellationTokenSource _stop = new();

    public DockerApi(string pipePath)
    {
        _pipe = pipePath;
        var name = pipePath[(pipePath.LastIndexOf('\\') + 1)..];
        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var p = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                try { await p.ConnectAsync(2000, ct).ConfigureAwait(false); return p; }
                catch { await p.DisposeAsync().ConfigureAwait(false); throw; }
            },
        }) { BaseAddress = new Uri("http://engine/"), Timeout = Timeout.InfiniteTimeSpan };
    }

    public static string PipeFor(string choice) => choice == "podman" ? PodmanPipe : DockerPipe;
    public bool PipeExists() => File.Exists(_pipe);

    async Task<HttpResponseMessage> Send(HttpMethod m, string path, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var r = await _http.SendAsync(new HttpRequestMessage(m, path), cts.Token).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode) { r.Dispose(); throw new HttpRequestException($"{m} {path}: {(int)r.StatusCode}"); }
        return r;
    }

    async Task<string> Get(string path)
    {
        using var r = await Send(HttpMethod.Get, path, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        return await r.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    public async Task<List<Box>> ListAsync() => DockerParse.Containers(await Get("containers/json?all=1").ConfigureAwait(false));
    public async Task<DiskUsage> DfAsync() => DockerParse.Df(await Get("system/df").ConfigureAwait(false));

    /// <summary>The last 100 lines, then new ones as they are written, until <paramref name="ct"/> is cancelled, the container's stream ends or the engine goes away (the exception ends the call).
    /// <paramref name="onLines"/> runs on a pool thread, once per received chunk.</summary>
    public async Task FollowLogsAsync(string id, Action<List<string>> onLines, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        using var r = await _http.GetAsync($"containers/{id}/logs?follow=1&stdout=1&stderr=1&tail=100", HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        r.EnsureSuccessStatusCode();
        await using var s = await r.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        var demux = new LogDemux();
        var buf = new byte[8192];
        int n;
        while ((n = await s.ReadAsync(buf, cts.Token).ConfigureAwait(false)) > 0)
            if (demux.Push(buf.AsSpan(0, n)) is { Count: > 0 } lines) onLines(lines);
        if (demux.Flush() is { Count: > 0 } rest) onLines(rest);
    }

    /// <summary>action: start | stop | restart.</summary>
    public async Task ActAsync(string id, string action) =>
        (await Send(HttpMethod.Post, $"containers/{id}/{action}", TimeSpan.FromSeconds(40)).ConfigureAwait(false)).Dispose();

    /// <summary>Removes stopped containers, dangling images and build cache (never volumes); returns bytes freed.</summary>
    public async Task<long> PruneAsync()
    {
        long freed = 0;
        foreach (var p in new[] { "containers/prune", "images/prune", "build/prune" })
        {
            using var r = await Send(HttpMethod.Post, p, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("SpaceReclaimed", out var s) && s.ValueKind == JsonValueKind.Number) freed += s.GetInt64();
        }
        return freed;
    }

    /// <summary>Streams container events until the engine goes away (the exception then ends the call).</summary>
    public async Task WatchAsync(Action<DockerEvent> onEvent, CancellationToken ct)
    {
        using var r = await _http.GetAsync("events?filters=%7B%22type%22%3A%5B%22container%22%5D%7D", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        r.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await r.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            if (DockerParse.Event(line) is { } e) onEvent(e);
    }

    public void Dispose()
    {
        _stop.Cancel(); // ends followed log streams
        _http.Dispose();
        _stop.Dispose();
    }
}
