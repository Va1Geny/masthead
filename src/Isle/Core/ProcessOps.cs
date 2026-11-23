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

    /// <summary>Opens a URL or file with the shell; errors are logged.</summary>
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Logger.Warn($"open failed ({ex.GetType().Name})"); }
    }
}
