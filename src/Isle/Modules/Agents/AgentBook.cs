using System.Text.Json;
using System.Text.Json.Nodes;
using Isle.Agents;

namespace Isle.Modules.Agents;

public enum AgentState { Idle, Done, Working, Waiting }

/// <summary>One hook call as received from Isle.Hook.exe.</summary>
public sealed record HookMessage(string Agent, bool Wait, int TimeoutSeconds, JsonObject Input,
    HookProtocol.ConsoleTarget? Terminal = null, long SentTicks = 0)
{
    public string Event => HookProtocol.Str(Input, "hook_event_name");

    public static HookMessage? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            if (JsonNode.Parse(line) is not JsonObject o || o["v"] is not JsonValue v ||
                !v.TryGetValue<int>(out var version) || version != HookProtocol.Version || o["input"] is not JsonObject input) return null;
            var agent = HookProtocol.Str(o, "agent") is { Length: > 0 } a ? a : "claude";
            if (agent == "gemini" && HookProtocol.GeminiEvents.TryGetValue(HookProtocol.Str(input, "hook_event_name"), out var ev)) input["hook_event_name"] = ev;
            var wait = o["wait"] is JsonValue w && w.TryGetValue<bool>(out var b) && b;
            var timeout = o["timeout"] is JsonValue t && t.TryGetValue<int>(out var n) ? Math.Clamp(n, 5, 3600) : HookProtocol.DefaultTimeoutSeconds;
            var sent = o["sent"] is JsonValue stamp && stamp.TryGetValue<long>(out var ticks) && ticks > 0 ? ticks : 0;
            var terminal = o["terminal"] is JsonObject target && target["pid"] is JsonValue process &&
                process.TryGetValue<int>(out var pid) && pid > 0 && target["started"] is JsonValue started &&
                started.TryGetValue<long>(out var created) && created > 0 ? new HookProtocol.ConsoleTarget(pid, created) : null;
            return new HookMessage(agent, wait, timeout, input, terminal, sent);
        }
        catch (JsonException) { return null; }
    }
}

public sealed record AgentQuestion(string Text, string Header, IReadOnlyList<(string Label, string Description)> Options, bool Multi);
public sealed record AgentWorker(string Id, string Name, bool Done, string Activity, long ObservedAt);

/// <summary>A hook call blocked until the user answers in the island (or the hook gives up).</summary>
public sealed class AgentRequest
{
    static long _ids;
    public long Id { get; } = Interlocked.Increment(ref _ids);
    public required AgentSession Session { get; init; }
    /// <summary>True: AskUserQuestion (options); false: permission (Allow / Deny).</summary>
    public bool IsQuestion { get; init; }
    public string Tool { get; init; } = "";
    public string? ApprovalInput { get; init; }
    public IReadOnlyList<AgentQuestion> Questions { get; init; } = [];
    /// <summary>Completed with the reply line for the hook (<see cref="HookProtocol.Reply"/>).</summary>
    public TaskCompletionSource<string> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class AgentSession
{
    public required string Key { get; init; }
    public required string Agent { get; init; }
    public string Project { get; set; } = "";
    public string Cwd { get; set; } = "";
    public AgentState Reported { get; set; } = AgentState.Idle;
    public string LastAction { get; set; } = "";
    public DateTimeOffset Updated { get; set; }
    public HookProtocol.ConsoleTarget? Terminal { get; internal set; }
    public AgentChange? LatestChange { get; internal set; }
    internal long ChangeAt { get; set; }
    internal long StateAt { get; set; }
    internal long TerminalAt { get; set; }
    internal Dictionary<string, AgentWorker> WorkersById { get; } = [];
    public IReadOnlyCollection<AgentWorker> Workers => WorkersById.Values;
    public List<AgentRequest> Pending { get; } = [];

    /// <summary>A pending request always wins over the last reported state.</summary>
    public AgentState State => Pending.Count > 0 ? AgentState.Waiting : Reported;
}

/// <summary>
/// All known agent sessions, fed by hook messages. Pure (no UI, injectable clock) — call it on one thread.
/// Event mapping (Claude Code and Codex share the names): SessionStart → idle; UserPromptSubmit / PreToolUse →
/// working; PermissionRequest / PreToolUse(AskUserQuestion) → waiting with a pending request; Notification
/// permission_prompt / elicitation_dialog / agent_needs_input → waiting; Stop → done; SessionEnd → removed.
/// </summary>
public sealed class AgentBook(Func<DateTimeOffset> now)
{
    public static readonly TimeSpan Stale = TimeSpan.FromHours(3);
    readonly Dictionary<string, AgentSession> _sessions = [];
    const int MaxWorkersPerSession = 64;

    /// <summary>Raised for a Stop event (the pill shows a short "done" check).</summary>
    public event Action<AgentSession>? Finished;

    public int Count => _sessions.Count;

    /// <summary>Most urgent first (waiting, working, done, idle), then most recent.</summary>
    public IReadOnlyList<AgentSession> Sessions =>
        [.. _sessions.Values.OrderByDescending(s => s.State).ThenByDescending(s => s.Updated)];

    public IEnumerable<AgentRequest> AllPending => _sessions.Values.SelectMany(s => s.Pending);

    /// <summary>Applies one hook message. Returns the request to wait on when the hook waits for an answer
    /// (<paramref name="allowRequests"/> false = status only, the hook gets "none" at once).</summary>
    public AgentRequest? Apply(HookMessage m, bool allowRequests = true)
    {
        var t = now();
        Prune(t);
        var input = m.Input;
        var sessionId = HookProtocol.Str(input, "session_id");
        var cwd = HookProtocol.Str(input, "cwd");
        var workerId = HookProtocol.Str(input, "agent_id");
        var isWorker = workerId is { Length: > 0 and <= 256 } && workerId != sessionId;
        var stamp = m.SentTicks > 0 ? Math.Min(m.SentTicks, t.UtcTicks) : t.UtcTicks;
        var key = m.Agent + ":" + (sessionId.Length > 0 ? sessionId : cwd);

        if (m.Event == "SessionEnd")
        {
            if (_sessions.Remove(key, out var gone)) CancelAll(gone);
            return null;
        }

        if (!_sessions.TryGetValue(key, out var s))
        {
            s = new AgentSession { Key = key, Agent = m.Agent };
            _sessions[key] = s;
        }
        if (cwd.Length > 0 && (!isWorker || s.Cwd.Length == 0)) { s.Cwd = cwd; s.Project = ProjectName(cwd); }
        if (m.Terminal is not null && stamp >= s.TerminalAt && (!isWorker || s.Terminal is null))
        {
            s.Terminal = m.Terminal;
            s.TerminalAt = stamp;
        }
        s.Updated = t;

        var tool = HookProtocol.Str(input, "tool_name");
        var updatesState = stamp >= s.StateAt && !isWorker;
        if (updatesState) s.StateAt = stamp;
        switch (m.Event)
        {
            case "SessionStart":
                if (updatesState && s.Reported != AgentState.Working) s.Reported = AgentState.Idle;
                break;
            case "UserPromptSubmit" when updatesState:
                s.Reported = AgentState.Working;
                s.LastAction = AgentText.OneLine(HookProtocol.Str(input, "prompt"));
                break;
            case "PreToolUse" or "PostToolUse":
                if (stamp >= s.StateAt && (isWorker ? s.Reported != AgentState.Done : m.Event == "PreToolUse" || s.Reported != AgentState.Done))
                    s.Reported = AgentState.Working;
                var action = tool == HookProtocol.AskUserQuestion ? "" : AgentText.Describe(tool, input["tool_input"] as JsonObject);
                if (isWorker) UpdateWorker(s, workerId, input, stamp, action);
                else if (updatesState && action.Length > 0 && s.Reported != AgentState.Done) s.LastAction = action;
                if (m.Event == "PostToolUse" && stamp >= s.ChangeAt && AgentChange.Read(m.Agent, tool, input) is { } change)
                {
                    s.LatestChange = change;
                    s.ChangeAt = stamp;
                }
                break;
            case "SubagentStart" or "SubagentStop" when isWorker:
                var done = m.Event == "SubagentStop";
                if (!done && stamp >= s.StateAt && s.Reported != AgentState.Done) s.Reported = AgentState.Working;
                UpdateWorker(s, workerId, input, stamp, done ? AgentText.OneLine(HookProtocol.Str(input, "last_assistant_message")) : "", done);
                break;
            case "Notification" when updatesState:
                var type = HookProtocol.Str(input, "notification_type");
                var msg = AgentText.OneLine(HookProtocol.Str(input, "message"));
                if (type is "permission_prompt" or "elicitation_dialog" or "agent_needs_input")
                {
                    s.Reported = AgentState.Waiting;
                    if (msg.Length > 0) s.LastAction = msg;
                }
                else if (type == "agent_completed") s.Reported = AgentState.Done;
                break;
            case "Stop" when updatesState:
                s.Reported = AgentState.Done;
                var last = AgentText.OneLine(HookProtocol.Str(input, "last_assistant_message"));
                if (last.Length > 0) s.LastAction = last;
                Finished?.Invoke(s);
                break;
        }

        if (!m.Wait || !allowRequests) return null;
        AgentRequest? r = null;
        if (m.Event == "PermissionRequest")
        {
            var ti = input["tool_input"] as JsonObject;
            r = new AgentRequest { Session = s, Tool = tool, ApprovalInput = AgentText.ApprovalInput(ti) };
        }
        else if (m.Event == "PreToolUse" && tool == HookProtocol.AskUserQuestion)
        {
            var qs = ParseQuestions(input["tool_input"] as JsonObject);
            if (qs.Count > 0) r = new AgentRequest { Session = s, IsQuestion = true, Tool = tool, Questions = qs };
        }
        if (r is null) return null;
        s.Pending.Add(r);
        return r;
    }

    static void UpdateWorker(AgentSession session, string id, JsonObject input, long stamp, string activity, bool? done = null)
    {
        var workers = session.WorkersById;
        if (workers.TryGetValue(id, out var previous) && previous.ObservedAt > stamp) return;
        if (previous is null && workers.Count >= MaxWorkersPerSession)
        {
            var oldest = workers.Values.Where(worker => worker.Done).MinBy(worker => worker.ObservedAt);
            if (oldest is null) return;
            workers.Remove(oldest.Id);
        }
        var name = AgentText.OneLine(HookProtocol.Str(input, "agent_type"), 48);
        workers[id] = new(id, name.Length > 0 ? name : previous?.Name ?? "",
            done ?? previous?.Done ?? false, activity.Length > 0 ? activity : previous?.Activity ?? "", stamp);
    }

    /// <summary>Answers a pending request (the reply line goes to the hook) and removes it.</summary>
    public bool Resolve(AgentRequest r, string reply)
    {
        if (!r.Session.Pending.Remove(r)) return false;
        r.Reply.TrySetResult(reply);
        if (r.Session.Pending.Count == 0 && r.Session.Reported == AgentState.Waiting) r.Session.Reported = AgentState.Working;
        r.Session.Updated = now();
        return true;
    }

    /// <summary>The hook gave up (timeout / closed) or the user chose the terminal: no decision.</summary>
    public bool Cancel(AgentRequest r) => Resolve(r, HookProtocol.Reply("none"));

    public void Remove(string key)
    {
        if (_sessions.Remove(key, out var s)) CancelAll(s);
    }

    /// <summary>Drops sessions not heard from for <see cref="Stale"/> (crashed / closed terminals without SessionEnd).</summary>
    public bool Prune(DateTimeOffset t)
    {
        var old = _sessions.Values.Where(s => s.Pending.Count == 0 && t - s.Updated > Stale).Select(s => s.Key).ToList();
        foreach (var k in old) _sessions.Remove(k);
        return old.Count > 0;
    }

    /// <summary>"Working" with no hook event for this long is shown as idle (an interrupted run sends no Stop).</summary>
    public static readonly TimeSpan WorkingSilence = TimeSpan.FromMinutes(15);

    /// <summary>Demotes silent working sessions and prunes stale ones. True when anything changed.</summary>
    public bool Tidy(DateTimeOffset t)
    {
        var changed = Prune(t);
        foreach (var s in _sessions.Values)
            if (s.Reported == AgentState.Working && s.Pending.Count == 0 && t - s.Updated > WorkingSilence)
            {
                s.Reported = AgentState.Idle;
                changed = true;
            }
        return changed;
    }

    void CancelAll(AgentSession s)
    {
        foreach (var r in s.Pending.ToList()) r.Reply.TrySetResult(HookProtocol.Reply("none"));
        s.Pending.Clear();
    }

    public static string ProjectName(string cwd)
    {
        var trimmed = cwd.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : trimmed;
    }

    public static List<AgentQuestion> ParseQuestions(JsonObject? toolInput)
    {
        var list = new List<AgentQuestion>();
        if (toolInput?["questions"] is not JsonArray arr) return list;
        foreach (var n in arr)
        {
            if (n is not JsonObject q) continue;
            var text = HookProtocol.Str(q, "question");
            if (text.Length == 0) continue;
            var opts = new List<(string, string)>();
            if (q["options"] is JsonArray oa)
                foreach (var o in oa)
                    if (o is JsonObject oo && HookProtocol.Str(oo, "label") is { Length: > 0 } label)
                        opts.Add((label, HookProtocol.Str(oo, "description")));
            var multi = q["multiSelect"] is JsonValue mv && mv.TryGetValue<bool>(out var mb) && mb;
            if (opts.Count > 0) list.Add(new AgentQuestion(text, HookProtocol.Str(q, "header"), opts, multi));
        }
        return list;
    }
}
