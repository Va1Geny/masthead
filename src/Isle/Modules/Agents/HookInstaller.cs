using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Isle.Agents;
using Isle.Core;

namespace Isle.Modules.Agents;

/// <summary>One agent whose config can receive Isle's hooks.</summary>
/// <param name="Agent">"claude", "codex" or "gemini" (first argument of Isle.Hook.exe).</param>
/// <param name="ConfigPath">~/.claude/settings.json or ~/.codex/hooks.json.</param>
public sealed record HookTarget(string Agent, string ConfigPath)
{
    public string Name => AgentText.AgentName(Agent);
    /// <summary>The agent looks installed (its config folder exists).</summary>
    public bool Detected => Directory.Exists(Path.GetDirectoryName(ConfigPath));

    public static HookTarget Claude()
    {
        var dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return new HookTarget("claude", Path.Combine(dir, "settings.json"));
    }

    public static HookTarget Codex()
    {
        var dir = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        return new HookTarget("codex", Path.Combine(dir, "hooks.json"));
    }

    public static HookTarget Gemini() =>
        new("gemini", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "settings.json"));

    public static HookTarget[] All() => [Claude(), Codex(), Gemini()];

    /// <summary>The target of one agent id ("claude" / "codex" / "gemini"); all targets for anything else (never chosen).</summary>
    public static HookTarget[] For(string? agent) => All().Where(t => t.Agent == agent).ToArray() is { Length: > 0 } one ? one : All();
}

/// <summary>What Install/Remove would do — shown to the user before anything is written.</summary>
public sealed record HookPlan(HookTarget Target, bool FileExists, string? BackupPath, IReadOnlyList<string> Events, string HookExe, bool AlreadyInstalled);

/// <summary>
/// Adds / removes Isle's entries in an agent's hook config. Never runs without a button press. Rules: back up the
/// file first, merge (other hooks untouched), Isle's handlers are recognised by an exact trusted helper path and supported arguments; removal
/// takes only those out. Invalid JSON is never overwritten. Both agents use the same hooks JSON shape
/// ({"hooks": {Event: [{matcher?, hooks: [{type: "command", …}]}]}}).
/// </summary>
public static class HookInstaller
{
    public const string ExeName = "Isle.Hook.exe";

    static readonly JsonSerializerOptions Write = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>One matcher group Isle adds.</summary>
    public sealed record Entry(string Event, string? Matcher, bool Waits);

    /// <summary>Claude Code: status hooks run async (no delay for the agent); the two waiting ones are synchronous.</summary>
    public static readonly Entry[] ClaudeEntries =
    [
        new("SessionStart", null, false),
        new("UserPromptSubmit", null, false),
        new("PreToolUse", null, false),
        new("PreToolUse", "AskUserQuestion", true),
        new("PostToolUse", "Edit|Write|MultiEdit", false),
        new("SubagentStart", null, false),
        new("SubagentStop", null, false),
        new("PermissionRequest", null, true),
        new("Notification", null, false),
        new("Stop", null, false),
        new("SessionEnd", null, false),
    ];

    /// <summary>Codex hooks (same names; no Notification event, no async flag, AskUserQuestion does not exist).</summary>
    public static readonly Entry[] CodexEntries =
    [
        new("SessionStart", null, false),
        new("UserPromptSubmit", null, false),
        new("PreToolUse", null, false),
        new("PostToolUse", "apply_patch", false),
        new("SubagentStart", null, false),
        new("SubagentStop", null, false),
        new("PermissionRequest", null, true),
        new("Stop", null, false),
        new("SessionEnd", null, false),
    ];

    /// <summary>Gemini CLI event names (mapped to the internal ones in <see cref="HookProtocol.GeminiEvents"/>). Observe-only:
    /// no BeforeTool/permission blocking, so nothing waits. Notification is left out (its payload has no Claude equivalent).</summary>
    public static readonly Entry[] GeminiEntries =
    [
        new("SessionStart", null, false),
        new("BeforeAgent", null, false),
        new("BeforeTool", null, false),
        new("AfterAgent", null, false),
        new("SessionEnd", null, false),
    ];

    public static Entry[] EntriesFor(string agent) => agent switch { "codex" => CodexEntries, "gemini" => GeminiEntries, _ => ClaudeEntries };

    /// <summary>Stable copy of the helper used by the hooks (survives rebuilds/moves of Isle itself).</summary>
    public static string InstalledExe => Path.Combine(Paths.Root, "bin", ExeName);

    public static HookPlan Plan(HookTarget t, DateTime now)
    {
        var exists = File.Exists(t.ConfigPath);
        var installed = false;
        if (exists)
            try { installed = ReadRoot(t.ConfigPath) is { } root && HasIsle(root); }
            catch (InvalidDataException) { }
        return new HookPlan(t, exists, exists ? BackupName(t.ConfigPath, now) : null,
            [.. EntriesFor(t.Agent).Select(e => e.Matcher is null ? e.Event : $"{e.Event} ({e.Matcher})").Distinct()],
            InstalledExe, installed);
    }

    public static bool IsInstalled(HookTarget t)
    {
        try { return File.Exists(t.ConfigPath) && ReadRoot(t.ConfigPath) is { } root && HasIsle(root); }
        catch (Exception) { return false; }
    }

    /// <summary>Copies the helper, backs up the config, merges Isle's hooks. Returns the backup path (null if the file was new).</summary>
    /// <exception cref="InvalidDataException">The config is not valid JSON (nothing is written).</exception>
    public static string? Install(HookTarget t, int timeoutSeconds, string sourceDir, DateTime now)
    {
        var root = File.Exists(t.ConfigPath) ? ReadRoot(t.ConfigPath) ?? new JsonObject() : new JsonObject();
        var exe = CopyHelper(sourceDir);
        HookProtocol.WritePrivate(HookProtocol.TrustedPathFile(Paths.Root), Environment.ProcessPath!);
        var backup = Backup(t.ConfigPath, now);
        Merge(root, t.Agent, exe, timeoutSeconds);
        JsonStore.WriteAtomic(t.ConfigPath, root.ToJsonString(Write) + Environment.NewLine);
        Logger.Info($"agent hooks installed for {t.Agent}");
        return backup;
    }

    /// <summary>Backs up the config and removes only Isle's handlers. Returns the backup path (null if nothing to do).</summary>
    public static string? Remove(HookTarget t, DateTime now)
    {
        if (!File.Exists(t.ConfigPath)) return null;
        var root = ReadRoot(t.ConfigPath);
        if (root is null || !HasIsle(root)) return null;
        var backup = Backup(t.ConfigPath, now);
        Strip(root);
        JsonStore.WriteAtomic(t.ConfigPath, root.ToJsonString(Write) + Environment.NewLine);
        Logger.Info($"agent hooks removed for {t.Agent}");
        return backup;
    }

    /// <summary>Switching agent: removes Isle's own hook entries from every agent that is not <paramref name="chosen"/>
    /// ("none" removes them everywhere); other hooks in those files stay. Returns the agents that were cleaned.</summary>
    public static List<string> RemoveFromOthers(string chosen, IEnumerable<HookTarget> targets, DateTime now)
    {
        List<string> cleaned = [];
        foreach (var t in targets.Where(t => t.Agent != chosen))
            try { if (Remove(t, now) is not null) cleaned.Add(t.Agent); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { Logger.Warn($"hooks: could not clean {t.Agent} ({ex.GetType().Name})"); }
        return cleaned;
    }

    // ---- pure JSON edits (tested) ----

    /// <summary>Removes old Isle entries, then appends one matcher group per <see cref="Entry"/>.</summary>
    public static void Merge(JsonObject root, string agent, string exe, int timeoutSeconds)
    {
        Strip(root, exe);
        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }
        foreach (var e in EntriesFor(agent))
        {
            if (hooks[e.Event] is not JsonArray groups)
            {
                groups = [];
                hooks[e.Event] = groups;
            }
            var group = new JsonObject();
            if (e.Matcher is not null) group["matcher"] = e.Matcher;
            group["hooks"] = new JsonArray(Handler(agent, exe, e.Waits, timeoutSeconds));
            groups.Add(group);
        }
    }

    static JsonObject Handler(string agent, string exe, bool waits, int timeoutSeconds)
    {
        if (agent == "gemini")
        {
            // Gemini runs `command` through a shell; timeout is in milliseconds.
            var cmd = (exe.Contains(' ') ? $"\"{exe}\"" : exe) + " gemini --async";
            return new JsonObject { ["type"] = "command", ["command"] = cmd, ["timeout"] = 10_000 };
        }
        if (agent == "codex")
        {
            // Codex runs `command` through a shell; quote the path only when needed.
            var cmd = (exe.Contains(' ') ? $"\"{exe}\"" : exe) + $" codex --timeout {timeoutSeconds}";
            return new JsonObject { ["type"] = "command", ["command"] = cmd, ["timeout"] = waits ? timeoutSeconds + 30 : 3 }; // Codex caps SessionEnd at 3 s
        }
        // Claude Code exec form: no shell, so the path needs no quoting in Git Bash or PowerShell.
        var args = waits ? new JsonArray("claude", "--timeout", timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)) : new JsonArray("claude", "--async");
        var h = new JsonObject { ["type"] = "command", ["command"] = exe, ["args"] = args, ["timeout"] = waits ? timeoutSeconds + 30 : 10 };
        if (!waits) h["async"] = true;
        return h;
    }

    /// <summary>Removes handlers invoking the trusted Isle helper with supported arguments; drops groups/events left empty by that.</summary>
    public static bool Strip(JsonObject root, string? helperPath = null)
    {
        if (root["hooks"] is not JsonObject hooks) return false;
        var changed = false;
        foreach (var ev in hooks.Select(p => p.Key).ToList())
        {
            if (hooks[ev] is not JsonArray groups) continue;
            var here = false;
            for (var g = groups.Count - 1; g >= 0; g--)
            {
                if (groups[g] is not JsonObject group || group["hooks"] is not JsonArray handlers) continue;
                var before = handlers.Count;
                for (var h = handlers.Count - 1; h >= 0; h--)
                    if (IsIsleHandler(handlers[h], helperPath)) handlers.RemoveAt(h);
                if (handlers.Count == before) continue;
                changed = here = true;
                if (handlers.Count == 0) groups.RemoveAt(g);
            }
            if (here && groups.Count == 0) hooks.Remove(ev);
        }
        if (changed && hooks.Count == 0) root.Remove("hooks");
        return changed;
    }

    public static bool HasIsle(JsonObject root, string? helperPath = null) =>
        root["hooks"] is JsonObject hooks && hooks.Any(p => p.Value is JsonArray groups &&
            groups.Any(g => g is JsonObject go && go["hooks"] is JsonArray hs && hs.Any(h => IsIsleHandler(h, helperPath))));

    // Parse only our direct exec/shell forms. Never evaluate a command or infer ownership from its basename.
    static bool IsIsleHandler(JsonNode? n, string? helperPath)
    {
        if (n is not JsonObject h || HookTextOf(h, "type") != "command") return false;
        var command = HookTextOf(h, "command");
        if (command.IndexOfAny(['\r', '\n']) >= 0) return false;
        string executable;
        string[] args;
        if (h["args"] is JsonArray array)
        {
            executable = command;
            args = new string[array.Count];
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is not JsonValue value || !value.TryGetValue<string>(out var arg)) return false;
                args[i] = arg;
            }
        }
        else
        {
            if (h.ContainsKey("args")) return false;
            if (command.StartsWith('"'))
            {
                var end = command.IndexOf('"', 1);
                if (end < 0 || end + 1 < command.Length && !char.IsWhiteSpace(command[end + 1])) return false;
                executable = command[1..end];
                command = command[(end + 1)..];
            }
            else
            {
                var end = command.IndexOfAny([' ', '\t']);
                executable = end < 0 ? command : command[..end];
                command = end < 0 ? "" : command[end..];
            }
            args = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        }
        if (!TrustedHelper(executable, helperPath) || args.Length == 0 || args[0] is not ("claude" or "codex" or "gemini")) return false;
        // Both current exec-array hooks and legacy direct command lines use these switches.
        var asyncSeen = false;
        var timeoutSeen = false;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--async" && !asyncSeen) asyncSeen = true;
            else if (args[i] == "--timeout" && !timeoutSeen && ++i < args.Length &&
                     int.TryParse(args[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var timeout) && timeout > 0)
                timeoutSeen = true;
            else return false;
        }
        return true;
    }

    static bool TrustedHelper(string executable, string? helperPath)
    {
        try
        {
            if (!Path.IsPathFullyQualified(executable)) return false;
            var path = Path.GetFullPath(executable);
            return new[] { InstalledExe, Path.Combine(AppContext.BaseDirectory, ExeName), helperPath }
                .Any(trusted => trusted is not null && string.Equals(path, Path.GetFullPath(trusted), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    static string HookTextOf(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    // ---- files ----

    /// <summary>Parses the config; null for an empty file. Throws InvalidDataException for anything else that is not a JSON object.</summary>
    static JsonObject? ReadRoot(string path)
    {
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow }) as JsonObject
                   ?? throw new InvalidDataException("not a JSON object");
        }
        catch (JsonException ex) { throw new InvalidDataException("invalid JSON", ex); }
    }

    static string BackupName(string path, DateTime now) => $"{path}.isle-backup-{now:yyyyMMdd-HHmmss}";

    static string? Backup(string path, DateTime now)
    {
        if (!File.Exists(path)) return null;
        var bak = BackupName(path, now);
        File.Copy(path, bak, overwrite: true);
        return bak;
    }

    /// <summary>Copies Isle.Hook.exe (+ its dll/runtimeconfig when not single-file) to %AppData%\Isle\bin.</summary>
    static string CopyHelper(string sourceDir)
    {
        var src = Path.Combine(sourceDir, ExeName);
        if (!File.Exists(src)) throw new FileNotFoundException("hook helper missing next to Isle.exe", src);
        var dir = Path.GetDirectoryName(InstalledExe)!;
        Directory.CreateDirectory(dir);
        foreach (var name in new[] { ExeName, "Isle.Hook.dll", "Isle.Hook.runtimeconfig.json", "Isle.Hook.deps.json" })
        {
            var from = Path.Combine(sourceDir, name);
            var to = Path.Combine(dir, name);
            if (File.Exists(from))
                File.Copy(from, to, overwrite: true);
            else if (name != ExeName && File.Exists(to)) File.Delete(to); // switched to a single-file helper
        }
        return InstalledExe;
    }
}
