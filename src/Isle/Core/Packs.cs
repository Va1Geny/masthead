using System.Reflection;

namespace Isle.Core;

/// <summary>
/// Loads the optional packs in a fixed order: exactly <c>Isle.Music.dll</c>, <c>Isle.Dev.dll</c>
/// next to Isle.exe, each only when built from the same commit, then calls <c>Isle.&lt;Name&gt;.Pack.Register()</c>.
/// A failing pack (missing
/// dependency, mismatch, exception) is logged and skipped; Isle runs with the rest.
/// </summary>
static class Packs
{
    internal static readonly string[] Names = ["Music", "Dev"];
    static bool _loaded;

    /// <summary>Loads once; later calls do nothing (a debug flag may load the packs before start-up does).</summary>
    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        var mine = Version(typeof(Packs).Assembly);
        if (mine?.Contains('+') != true) Logger.Warn($"packs: Isle has no commit in its version ({mine}); packs from other commits are not detected");
        var registered = new HashSet<string>();
        foreach (var name in Names)
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, $"Isle.{name}.dll");
                if (!File.Exists(path)) continue;
                var pack = Assembly.LoadFrom(path);
                if (SkipReason(mine, Version(pack), pack.GetReferencedAssemblies().Select(r => r.Name ?? ""), registered) is { } why) { Logger.Warn($"pack {name} skipped: {why}"); continue; }
                pack.GetType($"Isle.{name}.Pack", throwOnError: true)!.GetMethod("Register")!.Invoke(null, null);
                registered.Add($"Isle.{name}");
                Logger.Info($"pack {name} loaded");
            }
            catch (Exception ex) { Logger.Error($"pack {name}", ex is TargetInvocationException { InnerException: { } inner } ? inner : ex); }
        }
    }

    /// <summary>Why a pack must not load (built from another commit, or needs an Isle.* pack that is not registered), else null.</summary>
    internal static string? SkipReason(string? mine, string? theirs, IEnumerable<string> references, ISet<string> registered)
    {
        if (mine != theirs) return $"built from {theirs}, Isle is {mine}";
        var missing = references.FirstOrDefault(n => n.StartsWith("Isle.", StringComparison.Ordinal) && !registered.Contains(n));
        return missing is null ? null : $"needs {missing}";
    }

    static string? Version(Assembly a) => a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
}
