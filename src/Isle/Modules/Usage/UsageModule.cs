using Isle.Core;
using Isle.Views.Usage;

namespace Isle.Modules.Usage;

/// <summary>
/// Claude Code usage (module id "usage"): the Usage tab. Reads the session transcripts under
/// %USERPROFILE%\.claude\projects (or CLAUDE_CONFIG_DIR) incrementally into usage.json, only while the tab is shown
/// and when refresh is pressed: no timer, nothing runs while idle. Only token counters are read, never message text.
/// </summary>
public sealed class UsageModule(IslandWindow island) : IModule, IDisposable
{
    public const string TabId = "usage";

    CancellationTokenSource? _cts;

    public string Id => "usage";

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token; // taken now: after Stop the source is disposed
        var service = new UsageService(UsageScanner.DefaultProjectsDir(), Paths.File("usage.json"));
        island.AddTab(new TabInfo(TabId, "tab.usage", "Icon.Code", () => Settings.Current.Opt("setup.agent") == "codex" ? Views.Setup.SetupView.ComingSoon() : new UsageView(() => service.RefreshAsync(token)), 98));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        island.RemoveTab(TabId);
    }

    public void Dispose() => Stop();
}
