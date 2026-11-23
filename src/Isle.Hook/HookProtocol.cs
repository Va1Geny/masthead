using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Isle.Agents;

/// <summary>
/// Wire protocol between Isle.Hook.exe (pipe client, one connection per hook call) and the island (pipe server).
/// Compiled into both projects. One UTF-8 JSON line each way:
///   hook → island: {"v":2,"agent":"claude","wait":true,"timeout":60,"input":{…the hook's stdin JSON…}}
///   island → hook (only when wait = true): {"decision":"allow"|"deny"|"answer"|"none","message":"…","answers":{"question":"label"}}
/// The hook turns the reply into the agent's hook output JSON (<see cref="BuildOutput"/>); "none" or any failure
/// prints nothing and exits 0, so the agent's normal terminal prompt appears (fail open).
/// </summary>
public static class HookProtocol
{
    public const int Version = 2;
    public const int DefaultTimeoutSeconds = 60;
    public const int MaxLineBytes = 1 << 20;

    public sealed record ConsoleTarget(int ProcessId, long StartedUtcTicks);

    public record Endpoint(string PipeName, int ProcessId)
    {
        const string Prefix = "Isle.Agents.v2.";

        public static Endpoint Create() => new(Prefix + Guid.NewGuid().ToString("N"), Environment.ProcessId);

        public static Endpoint? Read(string root)
        {
            var path = Path.Combine(root, "agents-endpoint.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 1024) return null;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject json || json[nameof(ProcessId)] is not JsonValue value ||
                !value.TryGetValue<int>(out var pid) || pid <= 0) return null;
            var name = Str(json, nameof(PipeName));
            if (name.Length == 0 ||
                !name.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[Prefix.Length..], "N", out _))
                return null;
            return new Endpoint(name, pid);
        }

        public void Publish(string root) => WritePrivate(Path.Combine(root, "agents-endpoint.json"), new JsonObject
        {
            [nameof(PipeName)] = PipeName,
            [nameof(ProcessId)] = ProcessId,
        }.ToJsonString());
    }

    public static string TrustedPathFile(string root) => Path.Combine(root, "bin", "Isle.Hook.server");

    /// <summary>Atomically publish a file with access limited to the installing Windows account.</summary>
    public static void WritePrivate(string path, string text)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        var temp = new FileInfo(path + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = temp.Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security))
            using (var writer = new StreamWriter(stream)) writer.Write(text);
            File.Move(temp.FullName, path, overwrite: true);
        }
        finally { if (File.Exists(temp.FullName)) File.Delete(temp.FullName); }
    }

    public const string AskUserQuestion = "AskUserQuestion";

    /// <summary>True when this hook call must wait for the island's answer.</summary>
    /// <param name="async">The hook was registered as an async status hook (never waits, skips AskUserQuestion).</param>
    public static bool NeedsAnswer(JsonObject input, bool async)
    {
        if (async) return false;
        var ev = Str(input, "hook_event_name");
        var tool = Str(input, "tool_name");
        return ev switch
        {
            // AskUserQuestion is answered through PreToolUse (documented updatedInput.answers); its permission
            // request, if any, is left to the terminal so the same question is never asked twice.
            "PermissionRequest" => tool != AskUserQuestion,
            "PreToolUse" => tool == AskUserQuestion,
            _ => false,
        };
    }

    /// <summary>Async status hooks skip the events a synchronous hook already reports.</summary>
    /// <summary>Gemini CLI event → the internal (Claude/Codex) event name; the only place that knows the difference.</summary>
    public static readonly IReadOnlyDictionary<string, string> GeminiEvents = new Dictionary<string, string>
    {
        ["BeforeAgent"] = "UserPromptSubmit", ["BeforeTool"] = "PreToolUse", ["AfterTool"] = "PostToolUse", ["AfterAgent"] = "Stop",
    };

    public static bool SkipInAsync(JsonObject input) =>
        Str(input, "hook_event_name") == "PreToolUse" && Str(input, "tool_name") == AskUserQuestion;

    public static string Request(string agent, bool wait, int timeoutSeconds, JsonObject input, ConsoleTarget? terminal = null)
    {
        var request = new JsonObject
        {
            ["v"] = Version,
            ["agent"] = agent,
            ["wait"] = wait,
            ["timeout"] = timeoutSeconds,
            ["sent"] = DateTime.UtcNow.Ticks,
            ["input"] = input.DeepClone(),
        };
        if (terminal is not null)
            request["terminal"] = new JsonObject { ["pid"] = terminal.ProcessId, ["started"] = terminal.StartedUtcTicks };
        return request.ToJsonString();
    }

    public static string Reply(string decision, string? message = null, IReadOnlyDictionary<string, string>? answers = null)
    {
        var o = new JsonObject { ["decision"] = decision };
        if (!string.IsNullOrEmpty(message)) o["message"] = message;
        if (answers is { Count: > 0 })
        {
            var a = new JsonObject();
            foreach (var (q, label) in answers) a[q] = label;
            o["answers"] = a;
        }
        return o.ToJsonString();
    }

    /// <summary>
    /// The hook's stdout for a reply, or null for "no decision" (print nothing, exit 0).
    /// PermissionRequest → hookSpecificOutput.decision {behavior allow|deny, message on deny}.
    /// PreToolUse AskUserQuestion → permissionDecision allow + updatedInput = original tool_input + answers.
    /// </summary>
    public static string? BuildOutput(JsonObject input, string? replyLine)
    {
        if (string.IsNullOrWhiteSpace(replyLine)) return null;
        JsonObject? reply;
        try { reply = JsonNode.Parse(replyLine) as JsonObject; }
        catch (JsonException) { return null; }
        if (reply is null) return null;
        var decision = Str(reply, "decision");
        var ev = Str(input, "hook_event_name");

        if (ev == "PermissionRequest" && decision is "allow" or "deny")
        {
            var d = new JsonObject { ["behavior"] = decision };
            if (decision == "deny") d["message"] = Str(reply, "message") is { Length: > 0 } m ? m : "Denied from Isle.";
            return new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = d },
            }.ToJsonString();
        }

        if (ev == "PreToolUse" && Str(input, "tool_name") == AskUserQuestion)
        {
            if (decision == "answer" && reply["answers"] is JsonObject answers && answers.Count > 0)
            {
                var updated = input["tool_input"] is JsonObject ti ? (JsonObject)ti.DeepClone() : new JsonObject();
                updated["answers"] = answers.DeepClone();
                return new JsonObject
                {
                    ["hookSpecificOutput"] = new JsonObject
                    {
                        ["hookEventName"] = "PreToolUse",
                        ["permissionDecision"] = "allow",
                        ["updatedInput"] = updated,
                    },
                }.ToJsonString();
            }
            if (decision == "deny")
                return new JsonObject
                {
                    ["hookSpecificOutput"] = new JsonObject
                    {
                        ["hookEventName"] = "PreToolUse",
                        ["permissionDecision"] = "deny",
                        ["permissionDecisionReason"] = Str(reply, "message") is { Length: > 0 } m ? m : "The user dismissed the question from Isle.",
                    },
                }.ToJsonString();
        }
        return null;
    }

    /// <summary>
    /// The whole hook run: parse args + stdin, talk to the island, return the text for stdout (null = print nothing).
    /// Never throws; every failure means "no decision" (fail open).
    /// </summary>
    public static async Task<string?> RunClientAsync(string[] args, string stdin, Endpoint endpoint, string trustedPath, int connectMs = 400, ConsoleTarget? terminal = null)
    {
        try
        {
            var agent = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "claude";
            var timeout = DefaultTimeoutSeconds;
            var async = false;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--timeout" && i + 1 < args.Length && int.TryParse(args[i + 1], out var t)) timeout = Math.Clamp(t, 5, 3600);
                if (args[i] == "--async") async = true;
            }
            if (JsonNode.Parse(stdin) is not JsonObject input) return null;
            if (async && SkipInAsync(input)) return null;

            var wait = NeedsAnswer(input, async);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(wait ? timeout : 5));
            if (!PipeMayExist(endpoint.PipeName)) return null; // no island → don't spend the connect timeout
            using var pipe = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.ConnectAsync(connectMs, cts.Token).ConfigureAwait(false); }
            catch (Exception) { return null; } // island not running → normal flow

            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid != endpoint.ProcessId) return null;
            using var server = Process.GetProcessById(endpoint.ProcessId);
            if (!string.Equals(server.MainModule?.FileName, trustedPath, StringComparison.OrdinalIgnoreCase)) return null;

            await WriteLineAsync(pipe, Request(agent, wait, timeout, input, terminal), cts.Token).ConfigureAwait(false);
            if (!wait) return null;
            var reply = await ReadLineAsync(pipe, cts.Token).ConfigureAwait(false);
            return BuildOutput(input, reply);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>False only when no pipe of that name exists (WaitNamedPipe fails at once with "file not found").</summary>
    static bool PipeMayExist(string name)
    {
        if (!OperatingSystem.IsWindows()) return true;
        if (WaitNamedPipe(@"\\.\pipe\" + name, 1)) return true;
        return Marshal.GetLastWin32Error() != 2;
    }

    [DllImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WaitNamedPipe(string name, uint timeoutMs);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    public static string Str(JsonObject o, string name) =>
        o.TryGetPropertyValue(name, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : "";

    /// <summary>Reads one '\n'-terminated UTF-8 line (without the newline); null on end of stream or over-long line.</summary>
    public static async Task<string?> ReadLineAsync(Stream s, CancellationToken ct)
    {
        using var buf = new MemoryStream();
        var one = new byte[4096];
        while (true)
        {
            var n = await s.ReadAsync(one, ct).ConfigureAwait(false);
            if (n == 0) return null;
            var nl = Array.IndexOf(one, (byte)'\n', 0, n);
            if (buf.Length + (nl < 0 ? n : nl) > MaxLineBytes) return null;
            if (nl >= 0)
            {
                buf.Write(one, 0, nl);
                return Encoding.UTF8.GetString(buf.GetBuffer(), 0, (int)buf.Length);
            }
            buf.Write(one, 0, n);
        }
    }

    public static async Task WriteLineAsync(Stream s, string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line.Replace("\n", " ") + "\n");
        await s.WriteAsync(bytes, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }
}
