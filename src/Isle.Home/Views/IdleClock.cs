using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Isle.Home.Views;

/// <summary>The idle pill's clock (HH:mm). Ticks once a minute, aligned to the minute, and only while it is on screen.</summary>
sealed class IdleClock : TextBlock
{
    readonly DispatcherTimer _timer;

    public IdleClock()
    {
        FontSize = 12.5;
        FontWeight = FontWeights.SemiBold;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        _timer.Tick += (_, _) => Update();
        IsVisibleChanged += (_, _) => Update();
        Update();
    }

    void Update()
    {
        var now = DateTime.Now;
        Text = now.ToString("HH:mm");
        if (!IsVisible) { _timer.Stop(); return; }
        _timer.Interval = TimeSpan.FromSeconds(60 - now.Second + 0.2);
        _timer.Start();
    }
}
