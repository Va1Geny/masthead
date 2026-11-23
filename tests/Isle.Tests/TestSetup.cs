using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Isle.Core;

namespace Isle.Tests;

static class TestSetup
{
    /// <summary>Keeps tests away from the real %AppData%\Isle.</summary>
    [ModuleInitializer]
    public static void Init() => Paths.Root = Path.Combine(Path.GetTempPath(), "isle-tests-root-" + Environment.ProcessId);
}

/// <summary>A private STA dispatcher thread (WPF elements need one).</summary>
sealed class Sta(Dispatcher ui)
{
    /// <summary>Runs <paramref name="body"/> on a new STA thread with a dispatcher.</summary>
    public static void Run(Action<Sta> body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { body(new Sta(Dispatcher.CurrentDispatcher)); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    /// <summary>Pumps the dispatcher until <paramref name="done"/> or the timeout.</summary>
    public bool PumpUntil(Func<bool> done, int timeoutMs = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!done() && DateTime.UtcNow < end)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => frame.Continue = false, ui);
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }
        return done();
    }
}
