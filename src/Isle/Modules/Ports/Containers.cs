using System.Text;
using Isle.Core;

namespace Isle.Modules.Ports;

/// <summary>A running WSL distribution.</summary>
public sealed record ContainerRow(string Id, string Name, string Detail);

/// <summary>Parser for <c>wsl -l -v</c> output (pure; unit tested).</summary>
public static class ContainerParse
{
    /// <summary>Running distributions from <c>wsl -l -v</c> (UTF-16 text already decoded).</summary>
    public static List<ContainerRow> Wsl(string output)
    {
        var list = new List<ContainerRow>();
        foreach (var raw in output.Replace("\0", "").Split('\n').Skip(1))
        {
            var parts = raw.Trim().TrimStart('*').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && parts[^2].Equals("Running", StringComparison.OrdinalIgnoreCase))
                list.Add(new ContainerRow(string.Join(' ', parts[..^2]), string.Join(' ', parts[..^2]), "WSL " + parts[^1]));
        }
        return list;
    }
}

/// <summary>Lists and stops WSL distributions through wsl.exe; absent tools give an empty list.</summary>
public static class Containers
{
    public static async Task<List<ContainerRow>> ListAsync(CancellationToken ct = default)
    {
        var rows = new List<ContainerRow>();
        var wslExe = Path.Combine(Environment.SystemDirectory, "wsl.exe");
        if (File.Exists(wslExe))
        {
            var (wc, wsl) = await Cli.RunAsync(wslExe, ["-l", "-v"], null, TimeSpan.FromSeconds(6), ct, enc: Encoding.Unicode).ConfigureAwait(false);
            if (wc == 0) rows.AddRange(ContainerParse.Wsl(wsl));
        }
        return rows;
    }

    public static Task<(int Code, string Output)> StopAsync(ContainerRow row) =>
        Cli.RunAsync(Path.Combine(Environment.SystemDirectory, "wsl.exe"), ["--terminate", row.Id], null, TimeSpan.FromSeconds(30));
}
