using System.Globalization;
using System.Text.Json;
using Isle.Core;
using Isle.Modules.Agents;

namespace Isle.Modules.Usage;

/// <summary>
/// Codex usage: one rollout file = one session. Token counts are cumulative per session, so each token_count event
/// adds its delta to the day it happened on. Reuses <see cref="FileState"/>/<see cref="UsageData"/>; Offset holds the file length
/// (a changed length or creation time means "reparse this file"). Only counters, model, time and the cwd leaf are read.
/// </summary>
internal static class CodexScanner
{
    public static string DefaultSessionsDir() => Path.Combine(Path.GetDirectoryName(HookTarget.Codex().ConfigPath)!, "sessions");

    /// <summary>Parses one rollout; null when it has no token data. <paramref name="limits"/> = its newest rate_limits.</summary>
    public static FileState? ParseFile(string path, out CodexLimits? limits)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        var st = new FileState();
        limits = null;
        var model = "codex";
        long start = 0, last = 0;
        long pUncached = 0, pCached = 0, pOut = 0; // previous cumulative totals: each event adds its delta to its own day
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
                switch (root.Str("type"))
                {
                    case "session_meta":
                        if (start == 0) start = time;
                        st.Cwd = JsonExt.Leaf(p.Str("cwd"));
                        break;
                    case "turn_context":
                        if (p.Str("model") is { Length: > 0 } m) model = m;
                        break;
                    case "event_msg" when p.Str("type") == "token_count":
                        if (Limits(p, time) is { } l) limits = l;
                        if (!p.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object
                            || !info.TryGetProperty("total_token_usage", out var u) || u.ValueKind != JsonValueKind.Object) break;
                        var input = u.Num("input_tokens");
                        var cached = Math.Min(u.Num("cached_input_tokens"), input); // a subset of input
                        var (uncached, output) = (input - cached, u.Num("output_tokens")); // output already includes reasoning
                        if (uncached < pUncached || cached < pCached || output < pOut) (pUncached, pCached, pOut) = (0, 0, 0); // counters restarted
                        var d = new Tokens { In = uncached - pUncached, CacheRead = cached - pCached, Out = output - pOut };
                        (pUncached, pCached, pOut) = (uncached, cached, output);
                        if (time > last) last = time;
                        if (d.Total == 0) break; // repeated total
                        var day = DateTimeOffset.FromUnixTimeMilliseconds(time > 0 ? time : Math.Max(last, start)).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        if (!st.Days.TryGetValue(day, out var models)) st.Days[day] = models = [];
                        if (models.TryGetValue(model, out var sum)) sum.Add(d); else models[model] = d;
                        break;
                }
            }
            catch (JsonException) { }
        }
        if (st.Days.Count == 0) return null;
        st.First = start > 0 ? start : last;
        st.Last = Math.Max(last, st.First);
        return st;
    }

    /// <summary>The rate_limits windows of a token_count payload (primary, secondary), or null when it has none.</summary>
    static CodexLimits? Limits(JsonElement p, long time)
    {
        if (!p.TryGetProperty("rate_limits", out var rl) || rl.ValueKind != JsonValueKind.Object) return null;
        var l = new CodexLimits { At = time };
        foreach (var name in (string[])["primary", "secondary"])
            if (rl.TryGetProperty(name, out var w) && w.ValueKind == JsonValueKind.Object
                && w.TryGetProperty("used_percent", out var up) && up.ValueKind == JsonValueKind.Number)
                l.Windows.Add(new RateWindow { UsedPercent = up.GetDouble(), WindowMinutes = (int)w.Num("window_minutes"), ResetsAt = w.Num("resets_at") });
        return l.Windows.Count > 0 ? l : null;
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
                        var st = ParseFile(path, out var limits) ?? new FileState();
                        if (limits is not null && limits.At >= (data.Limits?.At ?? 0)) data.Limits = limits;
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
}
