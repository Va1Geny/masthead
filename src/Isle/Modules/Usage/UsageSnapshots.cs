using Isle.Core;
using Isle.Views.Usage;

namespace Isle.Modules.Usage;

/// <summary>--snapshot states for the Usage tab (fake data, nothing is read from disk).</summary>
static class UsageSnapshots
{
    public static void Register(Dictionary<string, Action<IslandWindow>> states)
    {
        states["usage-tab"] = w => Tab(w, () => Task.FromResult(Demo()));
        states["usage-scanning"] = w => Tab(w, () => new TaskCompletionSource<UsageReport>().Task); // never finishes
        states["usage-empty"] = w => Tab(w, () => Task.FromResult(new UsageReport(Zero, Zero, Zero, [], DateTimeOffset.Now)));
    }

    static readonly UsageTotal Zero = new(new Tokens(), 0);

    static void Tab(IslandWindow w, Func<Task<UsageReport>> scan)
    {
        var view = new UsageView(scan);
        w.AddTab(new TabInfo(UsageModule.TabId, "tab.usage", "Icon.Code", () => view, 98));
        w.Expand(UsageModule.TabId);
    }

    static Tokens T(long i, long o, long r, long w5, long w1 = 0) => new() { In = i, Out = o, CacheRead = r, Write5m = w5, Write1h = w1 };

    /// <summary>Made-up numbers, priced through the real table (one unknown model shows the "—" cost).</summary>
    static UsageReport Demo()
    {
        var now = DateTimeOffset.Now;
        SessionRow S(string project, double hoursAgo, double minutes, Tokens t, string model) =>
            new(project, [model], now.AddHours(-hoursAgo - minutes / 60), TimeSpan.FromMinutes(minutes), t, UsagePrices.Cost(model, t));
        UsageTotal Total(Tokens t) => new(t, UsagePrices.Cost("claude-opus-5-5", t));
        return new UsageReport(
            Total(T(48_200, 212_000, 18_400_000, 910_000)),
            Total(T(310_000, 1_450_000, 96_000_000, 5_200_000, 800_000)),
            Total(T(1_200_000, 5_600_000, 402_000_000, 21_000_000, 3_100_000)),
            [
                S("web-app", 0.2, 83, T(9_100, 61_000, 5_200_000, 240_000), "claude-sonnet-5-5"),
                S("api-server", 3, 22, T(4_000, 18_000, 1_100_000, 90_000), "claude-opus-5-5"),
                S("data-pipeline", 6, 140, T(21_000, 99_000, 8_800_000, 410_000), "claude-sonnet-5-5"),
                S("docs-site", 26, 7, T(800, 4_300, 220_000, 31_000), "claude-haiku-5-5"),
                S("web-app", 29, 305, T(33_000, 150_000, 19_000_000, 720_000, 90_000), "claude-opus-5-5"),
                S("mobile-app", 50, 41, T(3_000, 26_000, 2_900_000, 150_000), "claude-fable-9-9"),
            ],
            now.AddMinutes(-1));
    }
}
