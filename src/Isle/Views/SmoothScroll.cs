using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Isle.Views;

/// <summary>
/// Eased mouse-wheel scrolling for every ScrollViewer in the app: smaller steps, and the content glides
/// to its target instead of jumping. Registered once as a class handler, so no view has to opt in.
/// </summary>
static class SmoothScroll
{
    internal const double Step = 0.25; // px per wheel delta unit: 30 px per notch (WPF's default is 48)
    internal const double Rate = 10;   // easing speed per second: ~90% of the way in 230 ms. Tuning knob.

    static readonly Dictionary<ScrollViewer, double> Targets = [];
    static TimeSpan _last;
    static bool _running;

    public static void Enable() =>
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWheel));

    // Preview tunnels outermost-first, so pick the innermost scrollable viewer under the mouse ourselves.
    static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is not ScrollViewer sv || sv.ScrollableHeight <= 0 || sv.CanContentScroll) continue;
            Targets[sv] = NextTarget(Targets.TryGetValue(sv, out var t) ? t : sv.VerticalOffset, e.Delta, sv.ScrollableHeight);
            e.Handled = true;
            if (!_running) { _running = true; _last = default; CompositionTarget.Rendering += OnFrame; }
            return;
        }
    }

    internal static double NextTarget(double from, int delta, double max) => Math.Clamp(from - delta * Step, 0, max);

    internal static double Ease(double y, double target, double dt) => y + (target - y) * (1 - Math.Exp(-Rate * dt));

    static void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _last) return; // Rendering can fire more than once per frame
        var dt = _last == default ? 1 / 60.0 : Math.Min((now - _last).TotalSeconds, 0.05);
        _last = now;
        foreach (var (sv, goal) in Targets.ToArray())
        {
            var target = Math.Min(goal, sv.ScrollableHeight);
            var y = Ease(sv.VerticalOffset, target, dt);
            if (!sv.IsLoaded || Math.Abs(target - y) < 0.5) { y = target; Targets.Remove(sv); }
            sv.ScrollToVerticalOffset(y);
        }
        if (Targets.Count == 0) { CompositionTarget.Rendering -= OnFrame; _running = false; }
    }
}
