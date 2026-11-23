using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Isle.Core;

/// <summary>
/// Returns memory to the OS once start-up has finished (see App startup).
/// Call <see cref="TrimSoon"/>; repeated calls within the delay collapse into one trim.
/// </summary>
public static partial class Memory
{
    static DispatcherTimer? _timer;

    [LibraryImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyWorkingSet(IntPtr hProcess);

    /// <summary>Must be called on the UI thread.</summary>
    public static void TrimSoon(double seconds = 3)
    {
        _timer ??= new DispatcherTimer(DispatcherPriority.Background); // ApplicationIdle starves while an animation renders every frame
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(seconds);
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    static void OnTick(object? sender, EventArgs e)
    {
        _timer?.Stop();
        TrimNow();
    }

    public static void TrimNow()
    {
        try
        {
            var before = GC.GetTotalMemory(false);
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            using var p = Process.GetCurrentProcess();
            EmptyWorkingSet(p.Handle);
            Logger.Info($"memory trim: managed {before / 1048576} -> {GC.GetTotalMemory(false) / 1048576} MB, private {p.PrivateMemorySize64 / 1048576} MB");
        }
        catch (Exception ex) { Logger.Error("memory trim", ex); }
    }
}
