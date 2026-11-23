using Isle.Core;
using Isle.Modules.Usage;

namespace Isle.Tests;

public sealed class CodexUsageTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "isle-codex-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    static string Count(string ts, string? usage) =>
        "{\"timestamp\":\"" + ts + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":" +
        (usage is null ? "null" : "{\"total_token_usage\":" + usage + "}") + "}}";
    static string Usage(long i, long c, long o) => $"{{\"input_tokens\":{i},\"cached_input_tokens\":{c},\"output_tokens\":{o},\"reasoning_output_tokens\":1,\"total_tokens\":{i + o}}}";
    const string Meta = "{\"timestamp\":\"2026-11-22T10:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"timestamp\":\"2026-11-22T10:00:00Z\",\"cwd\":\"C:\\\\Code\\\\weather-api\"}}";

    string Rollout(string name, params string[] lines)
    {
        var path = Path.Combine(_dir, "2026", "11", "22", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    [Fact]
    public void Last_total_wins_null_info_and_garbage_are_skipped_and_model_is_read()
    {
        var path = Rollout("rollout-a.jsonl", Meta, "not json {",
            "{\"timestamp\":\"2026-11-22T10:00:01Z\",\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-5-codex\"}}",
            Count("2026-11-22T10:01:00Z", Usage(100, 40, 10)), Count("2026-11-22T10:02:00Z", null), Count("2026-11-22T10:03:00Z", Usage(300, 100, 50)),
            Count("2026-11-22T10:04:00Z", null));
        var st = CodexScanner.ParseFile(path)!;
        var (model, t) = st.Days.Values.Single().Single();
        Assert.Equal("gpt-5-codex", model);
        Assert.Equal((200, 100, 50), (t.In, t.CacheRead, t.Out)); // cached is a subset of input
        Assert.Equal("weather-api", st.Cwd);
    }

    [Fact]
    public void No_token_data_or_model_falls_back()
    {
        Assert.Null(CodexScanner.ParseFile(Rollout("rollout-b.jsonl", Meta, Count("2026-11-22T10:01:00Z", null))));
        var st = CodexScanner.ParseFile(Rollout("rollout-c.jsonl", Meta, Count("2026-11-22T10:01:00Z", Usage(5, 0, 5))))!;
        Assert.Equal("codex", st.Days.Values.Single().Keys.Single());
    }

    [Fact]
    public async Task Service_picks_source_by_agent_and_rescans_changed_files()
    {
        var path = Rollout("rollout-d.jsonl", Meta, Count("2026-11-22T10:01:00Z", Usage(100, 0, 10)));
        var store = Path.Combine(_dir, "usage.json");
        string? agent = "codex";
        var svc = new UsageService(Path.Combine(_dir, "none"), store, Path.Combine(_dir, "2026"), () => agent);
        Assert.Single((await svc.RefreshAsync(default)).Sessions);
        agent = "claude";
        Assert.Empty((await svc.RefreshAsync(default)).Sessions);
        agent = null;
        File.AppendAllText(path, Count("2026-11-22T10:05:00Z", Usage(500, 0, 10)) + "\n");
        var r = await svc.RefreshAsync(default);
        Assert.Equal(510, r.Sessions.Single().Tokens.Total);
    }
}
