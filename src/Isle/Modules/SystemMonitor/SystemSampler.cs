using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Isle.Core;

namespace Isle.Modules.SystemMonitor;

/// <summary>One reading of the machine. Rates are per second since the previous reading.</summary>
public sealed record SystemSample(
    double Cpu, double[] Cores, double MemUsed, double MemTotal,
    double? Gpu, double? VramUsed, double? VramTotal,
    double NetDown, double NetUp, double? DiskRead, double? DiskWrite);

public sealed record ProcessUsage(int Pid, string Name, double Cpu, long Memory);

/// <summary>The WSL 2 VM process: "vmmemWSL" on recent Windows, "vmmem" on older builds (pure; unit tested).</summary>
public static class WslVm
{
    public static bool IsVmName(string name) =>
        name.Equals("vmmemWSL", StringComparison.OrdinalIgnoreCase) || name.Equals("vmmem", StringComparison.OrdinalIgnoreCase);

    /// <summary>Working set of the VM, preferring vmmemWSL over vmmem; null when neither is present.</summary>
    public static long? Pick(IEnumerable<(string Name, long Memory)> found)
    {
        long? old = null;
        foreach (var (name, memory) in found)
        {
            if (name.Equals("vmmemWSL", StringComparison.OrdinalIgnoreCase)) return memory;
            if (name.Equals("vmmem", StringComparison.OrdinalIgnoreCase)) old = memory;
        }
        return old;
    }
}

/// <summary>Pure arithmetic of the sampler, kept apart so it is unit tested.</summary>
public static class SystemMath
{
    /// <summary>CPU busy share from two GetSystemTimes readings (kernel time includes idle time).</summary>
    public static double CpuUsage(ulong idle0, ulong kernel0, ulong user0, ulong idle1, ulong kernel1, ulong user1)
    {
        var total = (kernel1 - kernel0) + (user1 - user0);
        if (total == 0) return 0;
        var busy = total - (idle1 - idle0);
        return Math.Clamp(100.0 * busy / total, 0, 100);
    }

    /// <summary>GPU load the way Task Manager shows it: engines of one type are summed, the busiest type wins.</summary>
    public static double GpuUsage(IEnumerable<(string Instance, double Value)> engines)
    {
        var byType = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, value) in engines)
        {
            var at = instance.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            var type = instance[(at + 8)..];
            byType[type] = byType.GetValueOrDefault(type) + value;
        }
        return byType.Count == 0 ? 0 : Math.Clamp(byType.Values.Max(), 0, 100);
    }

    /// <summary>"0 B/s", "512 KB/s", "1.4 MB/s" — 1024-based, one decimal from MB up.</summary>
    public static string Rate(double bytesPerSecond) => Size(bytesPerSecond) + "/s";

    public static string Size(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var u = 0;
        while (bytes >= 1024 && u < units.Length - 1) { bytes /= 1024; u++; }
        var text = u >= 2 ? bytes.ToString(bytes >= 100 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture) : Math.Round(bytes).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return text + " " + units[u];
    }

    /// <summary>Top processes by CPU share of the whole machine from two TotalProcessorTime readings.</summary>
    public static List<ProcessUsage> Top(
        IReadOnlyDictionary<int, (string Name, TimeSpan Cpu, long Memory)> before,
        IReadOnlyDictionary<int, (string Name, TimeSpan Cpu, long Memory)> after,
        TimeSpan elapsed, int cores, int count)
    {
        var list = new List<ProcessUsage>();
        if (elapsed <= TimeSpan.Zero || cores <= 0) return list;
        foreach (var (pid, now) in after)
        {
            var share = before.TryGetValue(pid, out var then) && then.Name == now.Name
                ? (now.Cpu - then.Cpu).TotalMilliseconds / (elapsed.TotalMilliseconds * cores) * 100
                : 0;
            list.Add(new ProcessUsage(pid, now.Name, Math.Clamp(share, 0, 100), now.Memory));
        }
        return list.OrderByDescending(p => p.Cpu).ThenByDescending(p => p.Memory).Take(count).ToList();
    }
}

/// <summary>
/// Reads the machine with plain Win32 calls and PDH (no admin, no driver): GetSystemTimes, per-core idle from
/// NtQuerySystemInformation, GlobalMemoryStatusEx, network interface counters, and PDH for GPU engines,
/// GPU memory and disk throughput. Not thread-safe; call from one thread.
/// </summary>
public sealed class SystemSampler : IDisposable
{
    (ulong Idle, ulong Kernel, ulong User) _cpu;
    CoreTimes[] _cores = [];
    long _netIn, _netOut;
    long _lastTicks;
    Pdh? _pdh;
    bool _pdhFailed;
    double? _vramTotal;
    Dictionary<int, (string Name, TimeSpan Cpu, long Memory)> _procs = [];
    long _procTicks;
    bool _wslLogged;

    /// <summary>Working set of the WSL VM from the latest <see cref="TopProcesses"/> pass; null when absent or unreadable.</summary>
    public long? Wsl { get; private set; }

    public int CoreCount { get; } = Environment.ProcessorCount;

    /// <summary>Quick reading (CPU total and memory) for background alerts.</summary>
    public (double Cpu, double MemPercent) Light()
    {
        var cpu = ReadCpu();
        var (used, total) = Memory();
        return (cpu, total > 0 ? 100 * used / total : 0);
    }

    public SystemSample Full()
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = _lastTicks == 0 ? 0 : (now - _lastTicks) / (double)Stopwatch.Frequency;
        _lastTicks = now;

        var cpu = ReadCpu();
        var cores = ReadCores();
        var (used, total) = Memory();

        var (inBytes, outBytes) = Network();
        double down = 0, up = 0;
        if (seconds > 0 && _netIn > 0)
        {
            down = Math.Max(0, (inBytes - _netIn) / seconds);
            up = Math.Max(0, (outBytes - _netOut) / seconds);
        }
        (_netIn, _netOut) = (inBytes, outBytes);

        double? gpu = null, vram = null, read = null, write = null;
        if (!_pdhFailed)
        {
            try
            {
                _pdh ??= new Pdh();
                _pdh.Collect();
                gpu = SystemMath.GpuUsage(_pdh.Gpu());
                vram = _pdh.Sum(_pdh.VramCounter);
                read = _pdh.Sum(_pdh.ReadCounter);
                write = _pdh.Sum(_pdh.WriteCounter);
                if (_pdh.Collections < 2) gpu = read = write = null; // rates need two collections
            }
            catch (Exception ex)
            {
                Logger.Warn($"sysmon: PDH unavailable ({ex.GetType().Name})");
                _pdhFailed = true;
                _pdh?.Dispose();
                _pdh = null;
            }
        }
        _vramTotal ??= DedicatedVideoMemory();

        return new SystemSample(cpu, cores, used, total, gpu, vram, _vramTotal > 0 ? _vramTotal : null, down, up, read, write);
    }

    /// <summary>Busiest processes since the previous call (the first call returns zero CPU for all).</summary>
    public List<ProcessUsage> TopProcesses(int count)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = _procTicks == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_procTicks, now);
        _procTicks = now;
        var snapshot = new Dictionary<int, (string, TimeSpan, long)>();
        var wsl = new List<(string, long)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == 0 || p.Id == 4) continue; // Idle and System
                try
                {
                    var name = p.ProcessName;
                    if (WslVm.IsVmName(name)) // the VM process refuses TotalProcessorTime, so read its memory first, from this same pass
                    {
                        try { wsl.Add((name, p.WorkingSet64)); }
                        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                        {
                            if (!_wslLogged) { _wslLogged = true; Logger.Warn($"sysmon: WSL VM unreadable ({ex.GetType().Name})"); }
                        }
                    }
                    snapshot[p.Id] = (name, p.TotalProcessorTime, p.WorkingSet64);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { /* protected or exited */ }
            }
        }
        Wsl = WslVm.Pick(wsl);
        var top = SystemMath.Top(_procs, snapshot, elapsed, CoreCount, count);
        _procs = snapshot;
        return top;
    }

    double ReadCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var cur = (idle, kernel, user);
        var prev = _cpu;
        _cpu = cur;
        return prev.Kernel == 0 ? 0 : SystemMath.CpuUsage(prev.Idle, prev.Kernel, prev.User, idle, kernel, user);
    }

    double[] ReadCores()
    {
        var n = CoreCount;
        var size = Marshal.SizeOf<CoreTimes>();
        var buffer = new CoreTimes[n];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            if (NtQuerySystemInformation(8 /* SystemProcessorPerformanceInformation */, handle.AddrOfPinnedObject(), (uint)(size * n), out _) != 0) return [];
        }
        finally { handle.Free(); }
        var prev = _cores;
        _cores = buffer;
        if (prev.Length != n) return new double[n];
        var result = new double[n];
        for (var i = 0; i < n; i++)
            result[i] = SystemMath.CpuUsage((ulong)prev[i].Idle, (ulong)prev[i].Kernel, (ulong)prev[i].User, (ulong)buffer[i].Idle, (ulong)buffer[i].Kernel, (ulong)buffer[i].User);
        return result;
    }

    static (double Used, double Total) Memory()
    {
        var m = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref m) ? (m.TotalPhys - m.AvailPhys, m.TotalPhys) : (0, 0);
    }

    static (long In, long Out) Network()
    {
        long i = 0, o = 0;
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var s = n.GetIPStatistics();
                i += s.BytesReceived;
                o += s.BytesSent;
            }
        }
        catch (NetworkInformationException) { }
        return (i, o);
    }

    static double? DedicatedVideoMemory()
    {
        try
        {
            using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory1>();
            ulong best = 0;
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
                using (adapter) best = Math.Max(best, (ulong)adapter.Description1.DedicatedVideoMemory);
            return best;
        }
        catch (Exception ex)
        {
            Logger.Warn($"sysmon: no DXGI ({ex.GetType().Name})");
            return 0;
        }
    }

    public void Dispose() { _pdh?.Dispose(); _pdh = null; }

    /// <summary>Minimal PDH wrapper: one query with wildcard counters read as formatted arrays.</summary>
    sealed class Pdh : IDisposable
    {
        IntPtr _query;
        readonly IntPtr _gpu;
        public readonly IntPtr VramCounter, ReadCounter, WriteCounter;
        public int Collections { get; private set; }

        public Pdh()
        {
            Check(PdhOpenQuery(null, IntPtr.Zero, out _query));
            Check(PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpu));
            Check(PdhAddEnglishCounter(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out VramCounter));
            Check(PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", IntPtr.Zero, out ReadCounter));
            Check(PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", IntPtr.Zero, out WriteCounter));
        }

        public void Collect() { Check(PdhCollectQueryData(_query)); Collections++; }

        public IEnumerable<(string, double)> Gpu() => Read(_gpu);

        public double Sum(IntPtr counter) => Read(counter).Sum(x => x.Value);

        static List<(string Name, double Value)> Read(IntPtr counter)
        {
            var list = new List<(string, double)>();
            uint size = 0;
            var status = PdhGetFormattedCounterArray(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out _, IntPtr.Zero);
            if (status != PdhMoreData || size == 0) return list;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out var count, buffer) != 0) return list;
                for (var i = 0; i < count; i++)
                {
                    var item = buffer + i * 24; // { wchar* name; DWORD status; (pad); double value }
                    var cstatus = Marshal.ReadInt32(item, 8);
                    if (cstatus is not (0 or 1)) continue; // PDH_CSTATUS_VALID_DATA / NEW_DATA
                    list.Add((Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "", BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16))));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return list;
        }

        static void Check(int status)
        {
            if (status != 0) throw new InvalidOperationException($"PDH 0x{status:X8}");
        }

        public void Dispose()
        {
            if (_query != IntPtr.Zero) PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }

        const uint PdhFmtDouble = 0x200, PdhFmtNoCap100 = 0x8000;
        const int PdhMoreData = unchecked((int)0x800007D2);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern int PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        static extern int PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

        [DllImport("pdh.dll")]
        static extern int PdhCloseQuery(IntPtr query);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CoreTimes { public long Idle, Kernel, User, Dpc, Interrupt; public uint InterruptCount; }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("ntdll.dll")]
    static extern int NtQuerySystemInformation(int infoClass, IntPtr info, uint length, out uint returnLength);
}
