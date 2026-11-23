using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace Isle.Modules.Ports;

/// <summary>
/// A TCP socket in LISTEN state. <see cref="Local"/>: only this machine can reach it. <see cref="Service"/>: the
/// process runs in session 0 (a Windows service). <see cref="Pid"/> is the lowest of the processes sharing the port.
/// </summary>
public sealed record Listener(int Port, int Pid, string Process, bool Local, bool Service = false);

/// <summary>Which listeners are worth showing: what developers start, not what Windows runs.</summary>
public static class PortFilter
{
    static readonly HashSet<string> SystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "svchost", "lsass", "wininit", "services", "spoolsv", "smss", "csrss", "winlogon",
        "SearchHost", "SearchIndexer", "MsMpEng", "NisSrv", "jhi_service", "WmiPrvSE", "dasHost", "Registry",
        "LsaIso", "fontdrvhost", "sihost", "MpDefenderCoreService", "SecurityHealthService", "OneApp.IGCC.WinService",
        "NVDisplay.Container", "RtkAudUService64", "IntelCpHDCPSvc", "IntelCpHeciSvc", "Isle",
        "OneDrive", "OneDrive.Sync.Service", "testhost",
    };

    /// <summary>Servers developers run as Windows services; other services are vendor or OS plumbing.</summary>
    static readonly HashSet<string> DevServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "postgres", "mysqld", "mariadbd", "mongod", "redis-server", "memurai", "sqlservr", "nginx", "httpd", "caddy",
        "java", "node", "dotnet", "python", "php-cgi", "rabbitmq-server", "erl", "com.docker.backend", "wslrelay",
        "vmmem", "vmmemWSL", "elasticsearch", "ollama", "influxd", "clickhouse-server", "etcd", "consul", "vault",
    };

    /// <summary>Desktop apps that open local ports for themselves (process name → shown name); not dev servers.</summary>
    static readonly Dictionary<string, string> DesktopApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Spotify"] = "Spotify", ["Discord"] = "Discord", ["Steam"] = "Steam", ["steamwebhelper"] = "Steam", ["Slack"] = "Slack",
        ["Teams"] = "Teams", ["ms-teams"] = "Teams", ["Zoom"] = "Zoom", ["Telegram"] = "Telegram", ["Signal"] = "Signal",
        ["EpicGamesLauncher"] = "Epic Games", ["Dropbox"] = "Dropbox", ["Notion"] = "Notion", ["WhatsApp"] = "WhatsApp",
    };

    /// <summary>The app's shown name when <paramref name="process"/> is a known desktop app, else null.</summary>
    public static string? DesktopApp(string process) => DesktopApps.GetValueOrDefault(process);

    /// <summary>First port of the dynamic (ephemeral) range; listeners above it are almost always plumbing.</summary>
    public const int DynamicPorts = 49152;

    public static bool IsInteresting(Listener l) =>
        l.Pid > 4 && l.Process.Length > 0 && l.Port < DynamicPorts && !SystemProcesses.Contains(l.Process)
        && (!l.Service || DevServices.Contains(l.Process));

    /// <summary>
    /// One row per port and program: IPv4/IPv6 sockets and worker processes (nginx, php-fpm) sharing a port merge;
    /// "all interfaces" wins over loopback.
    /// </summary>
    public static List<Listener> Merge(IEnumerable<Listener> all) =>
        all.GroupBy(l => (l.Port, Name: l.Process.ToLowerInvariant()))
           .Select(g => g.OrderBy(x => x.Pid).First() with { Local = g.All(x => x.Local), Service = g.All(x => x.Service) })
           .OrderBy(l => l.Port)
           .ToList();

    /// <summary>Listeners in <paramref name="now"/> whose port and program were not in <paramref name="before"/>.</summary>
    public static IEnumerable<Listener> Added(IReadOnlyCollection<Listener> before, IEnumerable<Listener> now)
    {
        var known = before.Select(l => (l.Port, l.Process)).ToHashSet();
        return now.Where(l => !known.Contains((l.Port, l.Process)));
    }
}

/// <summary>Reads the TCP listener table with owning PIDs (GetExtendedTcpTable; no admin needed).</summary>
public static class PortScanner
{
    public static List<Listener> Scan(bool interestingOnly = true)
    {
        var info = new Dictionary<int, (string Name, bool Service)>();
        (string Name, bool Service) Info(int pid)
        {
            if (info.TryGetValue(pid, out var n)) return n;
            try { using var p = Process.GetProcessById(pid); n = (p.ProcessName, p.SessionId == 0); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { n = ("", true); }
            return info[pid] = n;
        }

        var raw = new List<Listener>();
        foreach (var (port, pid, local) in Read(AfInet).Concat(Read(AfInet6)))
        {
            var (name, service) = Info(pid);
            raw.Add(new Listener(port, pid, name, local, service));
        }
        var merged = PortFilter.Merge(raw);
        return interestingOnly ? merged.Where(PortFilter.IsInteresting).ToList() : merged;
    }

    static List<(int Port, int Pid, bool Local)> Read(int family)
    {
        var list = new List<(int, int, bool)>();
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidListener, 0);
        if (size <= 0) return list;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidListener, 0) != 0) return list;
            var count = Marshal.ReadInt32(buffer);
            var rowSize = family == AfInet ? 24 : 56;
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + i * rowSize;
                if (family == AfInet)
                {
                    // { state, localAddr, localPort, remoteAddr, remotePort, pid }
                    var addr = (uint)Marshal.ReadInt32(row, 4);
                    var port = Port(Marshal.ReadInt32(row, 8));
                    list.Add((port, Marshal.ReadInt32(row, 20), (addr & 0xFF) == 127));
                }
                else
                {
                    // { localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, pid }
                    var bytes = new byte[16];
                    Marshal.Copy(row, bytes, 0, 16);
                    var port = Port(Marshal.ReadInt32(row, 20));
                    list.Add((port, Marshal.ReadInt32(row, 52), new IPAddress(bytes).Equals(IPAddress.IPv6Loopback)));
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return list;
    }

    /// <summary>The port sits in the low 16 bits in network byte order.</summary>
    internal static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    const int AfInet = 2, AfInet6 = 23, TcpTableOwnerPidListener = 3;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}
