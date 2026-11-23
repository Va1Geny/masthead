using System.Globalization;

namespace Isle.Modules.Usage;

/// <param name="Partial">Some tokens had no known price: Cost is a lower bound.</param>
public sealed record UsageTotal(Tokens Tokens, decimal? Cost, bool Partial = false);

public sealed record SessionRow(string Project, string[] Models, DateTimeOffset Start, TimeSpan Duration, Tokens Tokens, decimal? Cost, bool Partial = false);

/// <summary>What the Usage tab shows: totals for today / 7 / 30 days and the most recent sessions.</summary>
public sealed record UsageReport(UsageTotal Today, UsageTotal Week, UsageTotal Month, IReadOnlyList<SessionRow> Sessions, DateTimeOffset At)
{
    public const int MaxSessions = 20;

    public bool IsEmpty => Sessions.Count == 0;

    public static UsageReport Build(UsageData data, DateTimeOffset now)
    {
        // Days are local "yyyy-MM-dd" keys, so ordinal string comparison is date order.
        string Day(int back) => now.Date.AddDays(-back).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string today = Day(0), week = Day(6), month = Day(29);
        Dictionary<string, Tokens> t0 = [], t7 = [], t30 = [];
        foreach (var f in data.Files.Values)
            foreach (var (day, models) in f.Days)
            {
                if (string.CompareOrdinal(day, month) < 0 || string.CompareOrdinal(day, today) > 0) continue;
                foreach (var (model, t) in models)
                {
                    Merge(t30, model, t);
                    if (string.CompareOrdinal(day, week) >= 0) Merge(t7, model, t);
                    if (day == today) Merge(t0, model, t);
                }
            }

        var sessions = data.Files.Values.Where(f => f.Last > 0).GroupBy(f => f.Session).Select(g =>
        {
            Dictionary<string, Tokens> models = [];
            foreach (var f in g)
                foreach (var day in f.Days.Values)
                    foreach (var (model, t) in day) Merge(models, model, t);
            var first = g.Min(f => f.First);
            var last = g.Max(f => f.Last);
            var project = g.Select(f => f.Cwd).FirstOrDefault(c => c.Length > 0) ?? g.First().Slug;
            var all = new Tokens();
            foreach (var t in models.Values) all.Add(t);
            var (cost, partial) = UsagePrices.Cost(models);
            return new SessionRow(project, [.. models.Keys.Order()], DateTimeOffset.FromUnixTimeMilliseconds(first).ToLocalTime(),
                TimeSpan.FromMilliseconds(last - first), all, cost, partial);
        }).OrderByDescending(s => s.Start + s.Duration).Take(MaxSessions).ToList();

        return new UsageReport(Total(t0), Total(t7), Total(t30), sessions, now);
    }

    static void Merge(Dictionary<string, Tokens> into, string model, Tokens t)
    {
        if (!into.TryGetValue(model, out var sum)) into[model] = sum = new Tokens();
        sum.Add(t);
    }

    static UsageTotal Total(Dictionary<string, Tokens> byModel)
    {
        var all = new Tokens();
        foreach (var t in byModel.Values) all.Add(t);
        var (cost, partial) = UsagePrices.Cost(byModel);
        return new UsageTotal(all, cost, partial);
    }

    // ---- formatting (shared by the view and the tests) ----

    /// <summary>1234 → "1.2k", 5_600_000 → "5.6M".</summary>
    public static string Count(long n) => n switch
    {
        >= 1_000_000_000 => (n / 1e9).ToString("0.#", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (n / 1e6).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (n / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>"$1.23", "$123", "≥ $1.23" when some tokens have no known price, "—" when none do.</summary>
    public static string Money(decimal? cost, bool partial = false) =>
        cost is not { } c ? "—" : (partial ? "≥ $" : "$") + c.ToString(c >= 100 ? "N0" : "N2", CultureInfo.InvariantCulture);

    /// <summary>"claude-sonnet-5-5" → "Sonnet 5.5"; ids it does not recognise are shown as they are.</summary>
    public static string ModelName(string id)
    {
        var parts = id.StartsWith("claude-", StringComparison.Ordinal) ? id[7..].Split('-') : [];
        if (parts.Length >= 2 && parts[0].Length > 0 && parts[1..].All(p => p.Length is > 0 and <= 2 && p.All(char.IsDigit)))
            return char.ToUpperInvariant(parts[0][0]) + parts[0][1..] + " " + string.Join('.', parts[1..]);
        // a trailing date stamp ("-20251001") is noise
        if (parts.Length >= 3 && parts[^1].Length == 8 && parts[^1].All(char.IsDigit))
            return ModelName("claude-" + string.Join('-', parts[..^1]));
        return id.Length == 0 ? "?" : id;
    }
}
