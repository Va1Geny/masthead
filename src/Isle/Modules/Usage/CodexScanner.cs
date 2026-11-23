using System.Globalization;
using System.Text.Json;
using Isle.Core;
using Isle.Modules.Agents;

namespace Isle.Modules.Usage;

/// <summary>
/// Codex usage: one rollout file = one session. Token counts are cumulative per session, so the last non-null
/// total_token_usage wins. Reuses <see cref="FileState"/>/<see cref="UsageData"/>; Offset holds the file length
/// (a changed length or creation time means "reparse this file"). Only counters, model, time and the cwd leaf are read.
/// </summary>
internal static class CodexScanner
{
    public static string DefaultSessionsDir() => Path.Combine(Path.GetDirectoryName(HookTarget.Codex().ConfigPath)!, "sessions");

    /// <summary>Parses one rollout; null when it has no token data.</summary>
    public static FileState? ParseFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        var st = new FileState();
        Tokens? total = null;
        var model = "codex";
        long start = 0, last = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!line.Contains("\"token_count\"") && !line.Contains("\"turn_context\"") && !line.Contains("\"session_meta\"")) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object) continue;
                var time = root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String && ts.TryGetDateTimeOffset(out var t0) ? t0.ToUnixTimeMilliseconds() : 0;
                switch (Str(root, "type"))
                {
                    case "session_meta":
                        if (start == 0) start = time;
                        st.Cwd = Leaf(Str(p, "cwd"));
                        break;
                    case "turn_context":
                        if (Str(p, "model") is { Length: > 0 } m) model = m;
                        break;
                    case "event_msg" when Str(p, "type") == "token_count"
                        && p.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                        && info.TryGetProperty("total_token_usage", out var u) && u.ValueKind == JsonValueKind.Object:
                        var input = Num(u, "input_tokens");
                        var cached = Math.Min(Num(u, "cached_input_tokens"), input); // a subset of input
                        total = new Tokens { In = input - cached, CacheRead = cached, Out = Num(u, "output_tokens") }; // output already includes reasoning
                        if (time > last) last = time;
                        break;
                }
            }
            catch (JsonException) { }
        }
        if (total is null || total.Total == 0) return null;
        st.First = start > 0 ? start : last;
        st.Last = Math.Max(last, st.First);
        var day = DateTimeOffset.FromUnixTimeMilliseconds(st.Last).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        st.Days[day] = new() { [model] = total };
        return st;
    }

    /// <summary>Reparses new or changed rollouts into <paramref name="data"/> (keys "codex/…"); true when anything changed.</summary>
    public static bool Scan(string sessionsDir, UsageData data, CancellationToken ct = default)
    {
        var changed = false;
        var present = new HashSet<string>();
        if (Directory.Exists(sessionsDir))
            try
            {
                foreach (var path in Directory.EnumerateFiles(sessionsDir, "rollout-*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    ct.ThrowIfCancellationRequested();
                    var key = "codex/" + Path.GetRelativePath(sessionsDir, path).Replace('\\', '/');
                    present.Add(key);
                    try
                    {
                        var info = new FileInfo(path);
                        var created = info.CreationTimeUtc.Ticks;
                        if (data.Files.TryGetValue(key, out var old) && old.Offset == info.Length && old.Created == created) continue;
                        var st = ParseFile(path) ?? new FileState();
                        st.Offset = info.Length; st.Created = created; st.Session = key; st.Slug = "codex";
                        data.Files[key] = st;
                        changed = true;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.Warn($"usage: could not read a codex rollout ({ex.GetType().Name})"); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"usage: could not list codex rollouts ({ex.GetType().Name})");
                return changed; // listing unreliable: keep what we have
            }
        foreach (var gone in data.Files.Keys.Where(k => !present.Contains(k)).ToList()) { data.Files.Remove(gone); changed = true; }
        return changed;
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static long Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n > 0 ? n : 0;

    static string Leaf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var p = path.TrimEnd('\\', '/');
        return p[(p.LastIndexOfAny(['\\', '/']) + 1)..];
    }
}
