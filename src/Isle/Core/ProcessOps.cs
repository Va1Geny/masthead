using System.Diagnostics;

namespace Isle.Core;

public static class ProcessOps
{
    /// <summary>Ends a process if it still has the name we showed (guards against PID reuse). False when it can't.</summary>
    public static bool TryEnd(int pid, string expectedName)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!string.Equals(p.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase)) return false;
            p.Kill();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            Logger.Warn($"end {expectedName} failed ({ex.GetType().Name})");
            return false;
        }
    }

    /// <summary>
    /// Opens a web link from outside data (CI runs, PRs/MRs, GitLab) in the browser. Only absolute http(s) URLs are opened, re-escaped,
    /// so a crafted value can't start file:, ms-*, a UNC path or a program, or add browser arguments.
    /// </summary>
    public static void OpenUrl(string url)
    {
        if (WebUrl(url) is { } safe) Open(safe);
        else Logger.Warn("open refused: not an http(s) URL");
    }

    internal static string? WebUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && u.Host.Length > 0 ? u.AbsoluteUri : null;

    /// <summary>Opens a local file or folder Isle itself chose (or a fixed-scheme link it built) with the shell; errors are logged.</summary>
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Logger.Warn($"open failed ({ex.GetType().Name})"); }
    }
}
