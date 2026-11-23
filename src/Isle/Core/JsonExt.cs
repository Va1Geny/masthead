using System.Text.Json;

namespace Isle.Core;

/// <summary>Forgiving JSON reads: a missing or wrongly typed property gives "" or 0 instead of throwing.</summary>
internal static class JsonExt
{
    /// <summary>String property (or <paramref name="inner"/> of an object property), "" when absent or not a string.</summary>
    public static string Str(this JsonElement e, string name, string? inner = null)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var x)) return "";
        if (inner is not null && (x.ValueKind != JsonValueKind.Object || !x.TryGetProperty(inner, out x))) return "";
        return x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";
    }

    /// <summary>Positive integer property, 0 otherwise.</summary>
    public static long Num(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n > 0 ? n : 0;

    /// <summary>Last segment of a Windows or POSIX path.</summary>
    public static string Leaf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var p = path.TrimEnd('\\', '/');
        return p[(p.LastIndexOfAny(['\\', '/']) + 1)..];
    }
}
