using System.Windows;
using System.Windows.Threading;
using Isle.Agents;
using Isle.Core;
using Isle.Views.Agents;

namespace Isle.Modules.Agents;

/// <summary>
/// Agents (module id "agents"): Claude Code / Codex sessions reported by Isle.Hook.exe over a named pipe.
/// Agents tab with cards and Allow / Deny / question answers, pill dot for the most urgent state, hook install/remove.
/// Options: agents.approvals (answer requests in the island, default on), agents.timeout (seconds, default 60).
/// </summary>
public sealed class AgentsModule(IslandWindow island) : IModule
{
    public const string TabId = "agents";
    const string PillId = "agents", DonePillId = "agents.done";

    readonly Dispatcher _ui = Application.Current.Dispatcher;
    AgentBook? _book;
    AgentPipeServer? _server;
    AgentsView? _view;
    DispatcherTimer? _tidy, _doneTimer;
    string _pillSig = "";
    bool _anyHooks;

    public string Id => "agents";

    static Settings S => Settings.Current;
    public static int TimeoutSeconds => Math.Clamp(S.OptInt("agents.timeout", HookProtocol.DefaultTimeoutSeconds), 10, 600);

    public void Start()
    {
        _book = new AgentBook(() => DateTimeOffset.Now);
        _book.Finished += OnFinished;
        island.AddTab(new TabInfo(TabId, "tab.agents", "Icon.Agents", () =>
        {
            _anyHooks = AnyHooksInstalled();
            _view = new AgentsView(Actions());
            Render();
            return _view;
        }, 101));
        var endpoint = HookProtocol.Endpoint.Create();
        _server = new AgentPipeServer(endpoint.PipeName, Handle);
        try
        {
            _server.Start();
            endpoint.Publish(Paths.Root);
        }
        catch (Exception ex)
        {
            _server.Dispose();
            _server = null;
            Logger.Error("agent pipe startup", ex);
            island.ShowToast(Strings.T("agents.pipeUnavailable"));
        }
        Settings.Changed += OnSettingsChanged;
    }

    /// <summary>Opens the Agents tab with the hooks install/remove panel (from the settings panel).</summary>
    public void OpenHooks()
    {
        island.HideOverlay();
        island.Expand(TabId, sticky: true);
        island.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => _view?.ShowHooks());
    }

    internal AgentsActions Actions() => new(
        Allow: r => Answer(r, HookProtocol.Reply("allow")),
        Deny: r => Answer(r, HookProtocol.Reply("deny", Strings.T("agents.deniedMessage"))),
        Terminal: r => { if (_book?.Cancel(r) == true) { Changed(); _ = OpenTerminal(r.Session); } },
        OpenTerminal: s => _ = OpenTerminal(s),
        Answer: (r, picks) => Answer(r, AgentText.AnswerReply(r.Questions, picks)),
        Dismiss: s => { _book?.Remove(s.Key); Changed(); },
        Targets: Targets,
        Install: Install,
        Remove: Remove,
        TimeoutSeconds: () => TimeoutSeconds);

    async Task OpenTerminal(AgentSession session)
    {
        if (session.Terminal is not { } target)
        {
            island.ShowToast(Strings.T("agents.terminalUnavailable"));
            return;
        }
        var window = await AgentTerminal.FindAsync(target);
        if (!AgentTerminal.Activate(window)) island.ShowToast(Strings.T("agents.terminalUnavailable"));
        else island.Collapse();
    }

    void OnSettingsChanged(string name)
    {
        if (name == "Language") { _pillSig = ""; Changed(); }
    }

    /// <summary>Pipe thread → UI thread; for waiting calls, holds until answered, cancelled or timed out.</summary>
    async Task<string?> Handle(HookMessage m, CancellationToken ct)
    {
        var r = await _ui.InvokeAsync(() => OnMessage(m));
        if (r is null) return m.Wait ? HookProtocol.Reply("none") : null;
        try { return await r.Reply.Task.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            // Hook gave up (timeout / closed). Not a ct.Register callback: WaitAsync's own callback resumes this
            // method inline and disposing the registration would drop ours before it runs (found live).
            _ = _ui.BeginInvoke(() => { if (_book?.Cancel(r) == true) Changed(); });
            return HookProtocol.Reply("none");
        }
    }

    AgentRequest? OnMessage(HookMessage m)
    {
        if (_book is null) return null;
        AgentRequest? r = null;
        try { r = _book.Apply(m, S.OptBool("agents.approvals", true)); }
        catch (Exception ex) { Logger.Error("agents message", ex); }
        if (r is not null) island.Pulse(2);
        Changed();
        return r;
    }

    void Answer(AgentRequest r, string reply)
    {
        if (_book?.Resolve(r, reply) != true) return;
        Logger.Info($"agent request answered ({(r.IsQuestion ? "question" : "permission")})");
        Changed();
    }

    void OnFinished(AgentSession s)
    {
        island.Pill.Set(DonePill(s));
        _doneTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => { _doneTimer!.Stop(); island.Pill.Remove(DonePillId); }, _ui);
        _doneTimer.Stop();
        _doneTimer.Start();
    }

    static string Title(AgentSession s) => s.Project.Length > 0 ? s.Project : AgentText.AgentName(s.Agent);

    /// <summary>Re-renders the tab and the pill after any change.</summary>
    void Changed()
    {
        if (_book is null) return;
        Render();
        UpdatePill();
        // Demote silent "working" sessions (interrupted runs send no Stop) — a 1-min check only while one exists.
        var working = _book.Sessions.Any(s => s.State == AgentState.Working);
        if (working && _tidy is null)
        {
            _tidy = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) =>
            {
                if (_book?.Tidy(DateTimeOffset.Now) == true) Changed();
                if (_book is null || !_book.Sessions.Any(x => x.State == AgentState.Working)) { _tidy?.Stop(); _tidy = null; }
            }, _ui);
        }
    }

    void Render()
    {
        if (_view is null || _book is null) return;
        try { _view.Show(_book.Sessions, _anyHooks); }
        catch (Exception ex) { Logger.Error("agents render", ex); }
    }

    void UpdatePill()
    {
        if (_book is null) return;
        var (item, sig) = BuildPill(_book.Sessions);
        if (sig == _pillSig) return;
        _pillSig = sig;
        if (item is null) island.Pill.Remove(PillId);
        else island.Pill.Set(item);
    }

    /// <summary>The "agents" pill item for the most urgent session (null when nothing is working or waiting) + a change signature.</summary>
    internal static (PillItem? Item, string Sig) BuildPill(IReadOnlyList<AgentSession> sessions)
    {
        var top = sessions.FirstOrDefault();
        if (top is null || top.State is AgentState.Idle or AgentState.Done) return (null, "");
        var count = sessions.Count(s => s.State == top.State);
        var waiting = top.State == AgentState.Waiting;
        var text = Strings.T(waiting ? "agents.pillWaiting" : "agents.pillWorking", Title(top));
        var sub = waiting
            ? (top.Pending.FirstOrDefault() is { } r ? (r.IsQuestion ? Strings.T("agents.pillQuestion") : r.Tool) : "")
            : count > 1 ? Strings.T("agents.more", count - 1) : "";
        var state = top.State;
        return (new PillItem
        {
            Id = PillId, Priority = waiting ? PillPriority.AgentWaiting : PillPriority.AgentWorking,
            Width = waiting ? 300 : 230, Peek = waiting, TabId = TabId,
            View = () => new AgentPill(state, text, sub),
            Compact = () => AgentPill.Compact(state),
        }, $"{state}|{text}|{sub}");
    }

    internal static PillItem DonePill(AgentSession s) => new()
    {
        Id = DonePillId, Priority = PillPriority.AgentDone, Width = 240, TabId = TabId,
        View = () => new AgentPill(AgentState.Done, Strings.T("agents.pillDone", Title(s))),
    };

    // ---- hooks ----

    /// <summary>Hook targets to offer: only the agent chosen in the developer setup ("setup.agent"), all when never chosen.</summary>
    static HookTarget[] Targets() => HookTarget.For(S.Opt("setup.agent"));

    static bool AnyHooksInstalled() => Targets().Any(HookInstaller.IsInstalled);

    string Install(HookTarget t)
    {
        try
        {
            var backup = HookInstaller.Install(t, TimeoutSeconds, AppContext.BaseDirectory, DateTime.Now);
            _anyHooks = true;
            Render();
            return backup is null ? Strings.T("agents.doneInstallNew") : Strings.T("agents.doneInstall", backup);
        }
        catch (InvalidDataException) { return Strings.T("agents.errInvalid", t.ConfigPath); }
        catch (FileNotFoundException) { return Strings.T("agents.errHelper"); }
        catch (Exception ex) { Logger.Error("agent hooks install", ex); return Strings.T("agents.errFailed", ex.Message); }
    }

    string Remove(HookTarget t)
    {
        try
        {
            var backup = HookInstaller.Remove(t, DateTime.Now);
            _anyHooks = AnyHooksInstalled();
            Render();
            return backup is null ? Strings.T("agents.nothingToRemove") : Strings.T("agents.doneRemove", backup);
        }
        catch (InvalidDataException) { return Strings.T("agents.errInvalid", t.ConfigPath); }
        catch (Exception ex) { Logger.Error("agent hooks remove", ex); return Strings.T("agents.errFailed", ex.Message); }
    }

    public void Stop()
    {
        Settings.Changed -= OnSettingsChanged;
        _server?.Dispose();
        _server = null;
        if (_book is not null)
        {
            foreach (var r in _book.AllPending.ToList()) _book.Cancel(r); // hooks fall back to the terminal at once
            _book.Finished -= OnFinished;
            _book = null;
        }
        _tidy?.Stop();
        _tidy = null;
        _doneTimer?.Stop();
        island.Pill.Remove(PillId);
        island.Pill.Remove(DonePillId);
        island.RemoveTab(TabId);
        _view = null;
        _pillSig = "";
    }
}
