using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Isle.Modules.Projects;

/// <summary>A build or test run that finished.</summary>
public sealed record BuildResult(string Label, int? ExitCode, TimeSpan Duration);

/// <summary>Recognises front-door build and test commands from a command line (pure; unit tested).</summary>
public static partial class BuildCommand
{
    [GeneratedRegex(@"(?:^|[\s""])(watch|--watch|-w|serve|dev|start|run\s+dev|run\s+start|run\s+serve)(?:$|[\s""])", RegexOptions.IgnoreCase)]
    private static partial Regex LongRunning();

    [GeneratedRegex(@"(?:^|\s)(build|test|publish|pack|check|clippy|lint|typecheck|compile|install|ci|assemble|verify|package)(?::[\w:-]+)?(?:$|\s)", RegexOptions.IgnoreCase)]
    private static partial Regex Verb();

    [GeneratedRegex(@"(?:npm-cli\.js|pnpm\.c?js|yarn\.c?js|bun)""?\s+(?<rest>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex NodePackageManager();

    /// <summary>A short label such as "dotnet test" or "npm run build", or null when this is not a build/test.</summary>
    public static string? Label(string process, string commandLine)
    {
        var exe = Path.GetFileNameWithoutExtension(process).ToLowerInvariant();
        var args = ArgsAfterExe(commandLine);
        if (LongRunning().IsMatch(args)) return null;
        switch (exe)
        {
            case "dotnet":
                if (args.Contains("MSBuild.dll", StringComparison.OrdinalIgnoreCase) || args.Contains("nodemode", StringComparison.OrdinalIgnoreCase) || args.Contains("VBCSCompiler", StringComparison.OrdinalIgnoreCase)) return null;
                return FirstWord(args) is "build" or "test" or "publish" or "pack" or "restore" ? "dotnet " + FirstWord(args) : null;
            case "cargo":
                return FirstWord(args) is "build" or "test" or "check" or "clippy" or "bench" ? "cargo " + FirstWord(args) : null;
            case "go":
                return FirstWord(args) is "build" or "test" or "vet" ? "go " + FirstWord(args) : null;
            case "node":
                var m = NodePackageManager().Match(args);
                if (!m.Success) return null;
                var tool = m.Value.StartsWith("npm", StringComparison.OrdinalIgnoreCase) ? "npm" : m.Value.StartsWith("pnpm", StringComparison.OrdinalIgnoreCase) ? "pnpm" : m.Value.StartsWith("yarn", StringComparison.OrdinalIgnoreCase) ? "yarn" : "bun";
                var rest = m.Groups["rest"].Value.Trim();
                return Verb().IsMatch(rest) ? $"{tool} {Shorten(rest)}" : null;
            case "java":
                if (args.Contains("GradleWrapperMain", StringComparison.Ordinal) || args.Contains("org.gradle.launcher", StringComparison.Ordinal)) return Verb().IsMatch(args) ? "gradle " + VerbOf(args) : null;
                if (args.Contains("plexus-classworlds", StringComparison.Ordinal)) return "mvn " + (VerbOf(args) ?? "build");
                return null;
            case "python" or "python3" or "py":
                return Regex.IsMatch(args, @"-m\s+pytest\b") ? "pytest" : null;
            case "pytest": return "pytest";
            case "make" or "mingw32-make" or "nmake": return "make";
            case "cmake": return args.Contains("--build", StringComparison.Ordinal) ? "cmake --build" : null;
            case "ninja": return "ninja";
            case "msbuild": return args.Contains("nodemode", StringComparison.OrdinalIgnoreCase) ? null : "msbuild";
            default: return null;
        }
    }

    static string ArgsAfterExe(string commandLine)
    {
        var s = commandLine.TrimStart();
        if (s.StartsWith('"')) { var end = s.IndexOf('"', 1); return end < 0 ? "" : s[(end + 1)..].Trim(); }
        var space = s.IndexOf(' ');
        return space < 0 ? "" : s[(space + 1)..].Trim();
    }

    static string FirstWord(string args)
    {
        foreach (var w in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (!w.StartsWith('-')) return w.Trim('"').ToLowerInvariant();
        return "";
    }

    static string? VerbOf(string args) => Verb().Match(args) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;

    static string Shorten(string s)
    {
        s = Regex.Replace(s, @"\s+", " ");
        return s.Length <= 32 ? s : s[..31] + "…";
    }
}

/// <summary>
/// Polls the process list every few seconds for build/test commands (reading the command line only of processes it
/// has not seen before) and reports each one that ran at least <see cref="MinDuration"/> when it exits. It holds a
/// handle to tracked processes so the exit code stays readable after they exit.
/// </summary>
public sealed class BuildWatcher : IDisposable
{
    static readonly HashSet<string> Candidates = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet", "cargo", "go", "node", "java", "python", "python3", "py", "pytest", "make", "mingw32-make", "nmake", "cmake", "ninja", "MSBuild",
    };

    public TimeSpan MinDuration { get; init; } = TimeSpan.FromSeconds(8);

    readonly Dictionary<int, (SafeProcessHandle Handle, string Label)> _tracked = [];
    HashSet<int> _seen = [];
    bool _primed;

    public event Action<BuildResult>? Finished;

    /// <summary>One poll. The first poll only records what is already running.</summary>
    public void Poll()
    {
        var now = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!Candidates.Contains(p.ProcessName)) continue;
                now.Add(p.Id);
                if (!_primed || _seen.Contains(p.Id) || _tracked.ContainsKey(p.Id)) continue;
                if (CommandLine(p.Id) is not { } cmd || BuildCommand.Label(p.ProcessName, cmd) is not { } label) continue;
                // our own handle keeps the exit code and times readable after the process is gone
                var h = OpenProcessHandle(Synchronize | QueryLimited, false, p.Id);
                if (h.IsInvalid) { h.Dispose(); continue; }
                _tracked[p.Id] = (h, label);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { p.Dispose(); }
        }
        _seen = now;
        _primed = true;

        foreach (var (pid, t) in _tracked.ToList())
        {
            if (WaitForSingleObject(t.Handle, 0) != 0) continue; // WAIT_OBJECT_0 = exited
            _tracked.Remove(pid);
            int? code = GetExitCodeProcess(t.Handle, out var c) ? (int)c : null;
            var duration = GetProcessTimes(t.Handle, out var created, out var exited, out _, out _)
                ? DateTime.FromFileTimeUtc(exited) - DateTime.FromFileTimeUtc(created)
                : TimeSpan.Zero;
            t.Handle.Dispose();
            if (duration >= MinDuration) Finished?.Invoke(new BuildResult(t.Label, code, duration));
        }
    }

    public void Dispose()
    {
        foreach (var t in _tracked.Values) t.Handle.Dispose();
        _tracked.Clear();
    }

    const uint Synchronize = 0x00100000, QueryLimited = 0x1000;

    [DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    static extern SafeProcessHandle OpenProcessHandle(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(SafeProcessHandle handle, out uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessTimes(SafeProcessHandle handle, out long creation, out long exit, out long kernel, out long user);

    /// <summary>Command line of another process of this user (NtQueryInformationProcess class 60; no admin, no WMI).</summary>
    internal static string? CommandLine(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var size = 4096;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    var status = NtQueryInformationProcess(h, 60, buffer, size, out var needed);
                    if (status == unchecked((int)0xC0000004) && attempt == 0) { size = needed; continue; } // length mismatch
                    if (status != 0) return null;
                    var length = (ushort)Marshal.ReadInt16(buffer);
                    var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                    return Marshal.PtrToStringUni(text, length / 2);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returnLength);
}
