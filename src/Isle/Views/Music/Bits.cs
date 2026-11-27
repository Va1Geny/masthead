using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Isle.Modules.Media;
using MediaState = Isle.Modules.Media.MediaState;

namespace Isle.Views.Music;

/// <summary>Rounded album art; a gradient from the title hash plus a note icon when the track has no art.</summary>
public sealed class ArtImage : Border
{
    readonly Icon _note = new() { Data = (Geometry)Application.Current.FindResource("Icon.Music"), Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)) };
    object? _artKey;

    public ArtImage(double size, double radius)
    {
        Width = Height = size;
        CornerRadius = new CornerRadius(radius);
        _note.Width = _note.Height = Math.Round(size * 0.45);
        _note.HorizontalAlignment = HorizontalAlignment.Center;
        _note.VerticalAlignment = VerticalAlignment.Center;
    }

    public void Set(MediaState s)
    {
        object key = (object?)s.Art ?? s.Title;
        if (Equals(key, _artKey)) return;
        _artKey = key;
        if (s.Art is not null)
        {
            var b = new ImageBrush(s.Art) { Stretch = Stretch.UniformToFill };
            b.Freeze();
            Background = b;
            Child = null;
        }
        else
        {
            Background = ArtColors.Placeholder(s.Title);
            Child = _note;
        }
    }
}

/// <summary>Four bouncing bars. Animates (20 fps) only while playing and visible; otherwise static low bars.</summary>
public sealed class Equalizer : StackPanel
{
    static readonly double[] Periods = [0.47, 0.62, 0.39, 0.55];
    static readonly double[] Rest = [0.35, 0.55, 0.3, 0.45];
    readonly ScaleTransform[] _scales = new ScaleTransform[4];
    bool _playing, _running;

    public Equalizer(double height = 14, double barWidth = 3)
    {
        Orientation = Orientation.Horizontal;
        Height = height;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
        for (var i = 0; i < 4; i++)
        {
            _scales[i] = new ScaleTransform(1, Rest[i]);
            var bar = new Border
            {
                Width = barWidth, Height = height, CornerRadius = new CornerRadius(barWidth / 2),
                Margin = new Thickness(i == 0 ? 0 : barWidth * 0.6, 0, 0, 0),
                RenderTransformOrigin = new Point(0.5, 1), RenderTransform = _scales[i],
            };
            bar.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            Children.Add(bar);
        }
        IsVisibleChanged += (_, _) => Apply();
        Loaded += (_, _) => { SystemParameters.StaticPropertyChanged += OnSystemParametersChanged; Apply(); };
        Unloaded += (_, _) => SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
    }

    public bool Playing
    {
        get => _playing;
        set { if (_playing != value) { _playing = value; Apply(); } }
    }

    void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Dispatcher.Invoke(Apply);
    }

    void Apply()
    {
        var run = _playing && IsVisible && SystemParameters.ClientAreaAnimation;
        if (run == _running) return;
        _running = run;
        for (var i = 0; i < 4; i++)
        {
            if (run)
            {
                var a = new DoubleAnimation(0.2, 1.0, TimeSpan.FromSeconds(Periods[i]))
                {
                    AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    BeginTime = TimeSpan.FromSeconds(-Periods[i] * i * 0.37),
                };
                Timeline.SetDesiredFrameRate(a, 20); // each frame repaints the layered window; 20 fps reads fine for bars
                _scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, a);
            }
            else
            {
                _scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _scales[i].ScaleY = Rest[i];
            }
        }
    }
}
