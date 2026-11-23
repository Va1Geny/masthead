using System.Text;
using System.Text.Json;
using Isle.Core;

namespace Isle.Modules.Ports;

public enum ContainerKind { Docker, Wsl }

/// <summary>A running Docker container or WSL distribution.</summary>
public sealed record ContainerRow(ContainerKind Kind, string Id, string Name, string Detail);

/// <summary>Parsers for <c>docker ps</c> and <c>wsl -l -v</c> output (pure; unit tested).</summary>
public static class ContainerParse
{
    /// <summary>Lines of <c>docker ps --format "{{json .}}"</c>.</summary>
    public static List<ContainerRow> Docker(string output)
    {
        var list = new List<ContainerRow>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                string S(string name) => r.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
                var ports = S("Ports");
                var detail = S("Image") + (ports.Length > 0 ? " · " + ShortPorts(ports) : "");
                list.Add(new ContainerRow(ContainerKind.Docker, S("ID"), S("Names"), detail));
            }
            catch (JsonException) { }
        }
        return list;
    }

    /// <summary>"0.0.0.0:5432->5432/tcp, :::5432->5432/tcp" → ":5432".</summary>
    internal static string ShortPorts(string ports) =>
        string.Join(" ", ports.Split(',', StringSplitOptions.TrimEntries)
            .Select(p => p.Contains("->") ? ":" + p[(p.LastIndexOf(':', p.IndexOf("->", StringComparison.Ordinal)) + 1)..p.IndexOf("->", StringComparison.Ordinal)] : "")
            .Where(p => p.Length > 1).Distinct());

    /// <summary>Running distributions from <c>wsl -l -v</c> (UTF-16 text already decoded).</summary>
    public static List<ContainerRow> Wsl(string output)
    {
        var list = new List<ContainerRow>();
        foreach (var raw in output.Replace("\0", "").Split('\n').Skip(1))
        {
            var parts = raw.Trim().TrimStart('*').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && parts[^2].Equals("Running", StringComparison.OrdinalIgnoreCase))
                list.Add(new ContainerRow(ContainerKind.Wsl, string.Join(' ', parts[..^2]), string.Join(' ', parts[..^2]), "WSL " + parts[^1]));
        }
        return list;
    }
}

/// <summary>Lists and stops containers through the docker and wsl command lines; absent tools give an empty list.</summary>
public static class Containers
{
    public static async Task<List<ContainerRow>> ListAsync(CancellationToken ct = default)
    {
        var rows = new List<ContainerRow>();
        var (dc, docker) = await Cli.RunAsync("docker", ["ps", "--format", "{{json .}}"], null, TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
        if (dc == 0) rows.AddRange(ContainerParse.Docker(docker));
        var wslExe = Path.Combine(Environment.SystemDirectory, "wsl.exe");
        if (File.Exists(wslExe))
        {
            var (wc, wsl) = await RunUtf16Async(wslExe, ["-l", "-v"], ct).ConfigureAwait(false);
            if (wc == 0) rows.AddRange(ContainerParse.Wsl(wsl));
        }
        return rows;
    }

    public static Task<(int Code, string Output)> StopAsync(ContainerRow row) => row.Kind == ContainerKind.Docker
        ? Cli.RunAsync("docker", ["stop", row.Id], null, TimeSpan.FromSeconds(30))
        : Cli.RunAsync(Path.Combine(Environment.SystemDirectory, "wsl.exe"), ["--terminate", row.Id], null, TimeSpan.FromSeconds(30));

    /// <summary>wsl.exe writes UTF-16LE to a pipe.</summary>
    static async Task<(int, string)> RunUtf16Async(string exe, string[] args, CancellationToken ct)
    {
        var info = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.Unicode };
        foreach (var a in args) info.ArgumentList.Add(a);
        try
        {
            using var p = System.Diagnostics.Process.Start(info);
            if (p is null) return (-1, "");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            var text = await p.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return (p.ExitCode, text);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException) { return (-1, ""); }
    }
}
