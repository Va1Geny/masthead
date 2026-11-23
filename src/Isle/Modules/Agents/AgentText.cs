using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Isle.Agents;

namespace Isle.Modules.Agents;

/// <summary>Short, single-line texts for agent cards (tool calls, prompts, last messages). Shown, never logged.</summary>
public static class AgentText
{
    public const int MaxLine = 160;
    public const int MaxDetail = 400;
    const int MaxReviewChars = 16_384;
    static readonly JsonSerializerOptions ReviewFormat = new() { WriteIndented = true };

    /// <summary>The entire input for review. Large or missing inputs must be reviewed in the terminal.</summary>
    public static string? ApprovalInput(JsonObject? input)
    {
        var text = input?.ToJsonString(ReviewFormat);
        return text is { Length: <= MaxReviewChars } ? text : null;
    }

    /// <summary>Show every leaf with its JSON path; string values retain their actual newlines and backslashes.</summary>
    public static string ReviewInput(string json)
    {
        var text = new StringBuilder();
        void Add(JsonNode? node, string path)
        {
            if (node is JsonObject obj && obj.Count > 0)
                foreach (var (key, value) in obj) Add(value, path + "[" + JsonSerializer.Serialize(key) + "]");
            else if (node is JsonArray array && array.Count > 0)
                for (var i = 0; i < array.Count; i++) Add(array[i], path + "[" + i + "]");
            else
            {
                if (text.Length > 0) text.Append("\n\n");
                text.Append(path).Append(":\n");
                text.Append(node is JsonValue value && value.TryGetValue<string>(out var str) ? str : node?.ToJsonString(ReviewFormat) ?? "null");
            }
        }
        Add(JsonNode.Parse(json), "$");
        return text.ToString();
    }

    /// <summary>"Bash · npm test", "Edit · Program.cs", … for the "last action" line.</summary>
    public static string Describe(string tool, JsonObject? input)
    {
        if (tool.Length == 0) return "";
        var d = OneLine(Detail(tool, input), MaxLine);
        return d.Length > 0 ? $"{tool} · {d}" : tool;
    }

    /// <summary>The interesting part of a tool call: the command, file, pattern, URL or query.</summary>
    public static string Detail(string tool, JsonObject? input)
    {
        if (input is null) return "";
        string Get(string name) => input[name] switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonArray a => string.Join(' ', a.Select(x => x is JsonValue xv && xv.TryGetValue<string>(out var xs) ? xs : "")),
            _ => "",
        };
        string FileOf(string name) { var p = Get(name); return p.Length > 0 ? Path.GetFileName(p.TrimEnd('\\', '/')) : ""; }

        var text = tool switch
        {
            "Bash" or "PowerShell" or "shell" or "exec_command" or "local_shell" or "unified_exec" => Get("command") is { Length: > 0 } c ? c : Get("cmd"),
            "Edit" or "Write" or "Read" or "MultiEdit" => FileOf("file_path"),
            "NotebookEdit" => FileOf("notebook_path"),
            "Glob" or "Grep" => Get("pattern"),
            "WebFetch" => Get("url"),
            "WebSearch" => Get("query"),
            "Task" or "Agent" => Get("description"),
            "apply_patch" => PatchFiles(Get("command") is { Length: > 0 } command ? command : Get("input") is { Length: > 0 } patch ? patch : Get("patch")),
            _ => "",
        };
        text = text.Trim();
        return text.Length > MaxDetail ? text[..MaxDetail] + "…" : text;
    }

    /// <summary>File names from an apply_patch body ("*** Update File: a.cs").</summary>
    static string PatchFiles(string patch)
    {
        var names = new List<string>();
        foreach (var line in patch.Split('\n'))
        {
            var l = line.Trim();
            foreach (var p in new[] { "*** Update File: ", "*** Add File: ", "*** Delete File: " })
                if (l.StartsWith(p, StringComparison.Ordinal)) names.Add(Path.GetFileName(l[p.Length..].Trim()));
        }
        return names.Count > 0 ? string.Join(", ", names) : "";
    }

    /// <summary>First non-empty line, whitespace collapsed, pasted blocks dropped, cut to <paramref name="max"/>.</summary>
    public static string OneLine(string text, int max = MaxLine)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("<pasted_content", StringComparison.Ordinal) || line.StartsWith("</pasted_content", StringComparison.Ordinal)) continue;
            line = line.TrimStart('#', '*', '-', '>', ' ');
            if (line.Length == 0) continue;
            var space = false;
            foreach (var c in line)
            {
                if (char.IsWhiteSpace(c)) { space = true; continue; }
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(c);
            }
            break;
        }
        var s = sb.ToString();
        return s.Length > max ? s[..(max - 1)].TrimEnd() + "…" : s;
    }

    public static string AgentName(string agent) => agent switch
    {
        "claude" => "Claude Code",
        "codex" => "Codex",
        "gemini" => "Gemini CLI",
        _ => agent,
    };

    /// <summary>Reply for answered questions: question text → chosen label(s), multi-select comma-joined.</summary>
    public static string AnswerReply(IReadOnlyList<AgentQuestion> qs, IReadOnlyList<IReadOnlyCollection<string>> picks)
    {
        var map = new Dictionary<string, string>();
        for (var i = 0; i < qs.Count && i < picks.Count; i++)
            if (picks[i].Count > 0) map[qs[i].Text] = string.Join(", ", qs[i].Options.Select(o => o.Label).Where(picks[i].Contains));
        return HookProtocol.Reply(map.Count > 0 ? "answer" : "none", answers: map);
    }
}
