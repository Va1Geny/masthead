using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Isle.Core;

/// <summary>Runs a console tool with no window and captures stdout; a timeout or missing tool gives exit code -1.</summary>
public static class Cli
{
    public static async Task<(int Code, string Output)> RunAsync(string exe, IEnumerable<string> args, string? workingDirectory, TimeSpan timeout, CancellationToken ct = default)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? "",
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        Process? p = null;
        try
        {
            p = Process.Start(info);
            if (p is null) return (-1, "");
            var output = p.StandardOutput.ReadToEndAsync(cts.Token);
            _ = p.StandardError.ReadToEndAsync(cts.Token); // drain so the tool never blocks on a full pipe
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return (p.ExitCode, await output.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { p?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (-1, "");
        }
        catch (Win32Exception) { return (-1, ""); }
        finally { p?.Dispose(); }
    }
}
