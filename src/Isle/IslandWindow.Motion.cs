using System.Windows;
using System.Windows.Media;
using Isle.Core;

namespace Isle;

// Shape animation: hand-written springs stepped on CompositionTarget.Rendering (subscribed only while moving).
public partial class IslandWindow
{
    // expand: ζ≈0.78 (≈2% overshoot, settles in ~400 ms); collapse: ζ≈0.9, stiffer (~260 ms)
    const double ExpandK = 170, ExpandC = 20.5, CollapseK = 290, CollapseC = 30.6;

    Spring _w = new(120), _h = new(32), _e = new(0);
    double _pillW = 120, _pillH = 32;
    bool _expanded, _animating;
    TimeSpan _lastFrame;

    readonly RectangleGeometry _shape = new();
    readonly RectangleGeometry _shadow1 = new(), _shadow2 = new(), _shadow3 = new();
    bool _notch;

    /// <summary>"notch" (default): hangs from the top edge of the screen like a curtain. "pill": floats below it.</summary>
    static bool NotchSetting() => Settings.Current.Opt("shape", "notch") != "pill";

    /// <summary>Raised when the island finishes changing between collapsed and expanded (argument: expanded).</summary>
    public event Action<bool>? ExpandedChanged;

    void InitMotion()
    {
        _notch = NotchSetting();
        UseGeometries();
        ApplyShape();
    }

    /// <summary>Switches between the pill and the notch when the setting changes.</summary>
    void UpdateShapeKind()
    {
        if (_notch == NotchSetting()) return;
        _notch = !_notch;
        UseGeometries();
        ApplyShape();
    }

    void UseGeometries()
    {
        PillLayer.Margin = new Thickness(0, _notch ? 3 : TopGap, 0, 0); // centred in the taller notch
        if (_notch) return; // the notch builds a new path every frame in ApplyShape
        Body.Clip = _shape;
        Outline.Data = _shape;
        Shadow1.Data = _shadow1;
        Shadow2.Data = _shadow2;
        Shadow3.Data = _shadow3;
    }

    void SetTargets(bool immediate = false)
    {
        _w.Target = _expanded ? ExpandedWidth : _pillW;
        _h.Target = _expanded ? ExpandedHeight : _pillH;
        _e.Target = _expanded ? 1 : 0;
        if (immediate || _snapshotMode || !SystemParameters.ClientAreaAnimation)
        {
            _w.Snap(); _h.Snap(); _e.Snap();
            StopAnimation();
            ApplyShape();
            return;
        }
        if (!_animating)
        {
            _animating = true;
            _lastFrame = TimeSpan.Zero;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return; // WPF can raise Rendering more than once per frame
        var dt = _lastFrame == TimeSpan.Zero ? 1.0 / 60 : (now - _lastFrame).TotalSeconds;
        _lastFrame = now;

        var (k, c) = _expanded ? (ExpandK, ExpandC) : (CollapseK, CollapseC);
        var moving = _w.Step(dt, k, c);
        moving |= _h.Step(dt, k, c);
        moving |= _e.Step(dt, k, c);
        ApplyShape();
        if (!moving) StopAnimation();
    }

    /// <summary>Pushes the current spring values into the clip, shadow, and content opacities. No layout involved.</summary>
    void ApplyShape()
    {
        double w = Math.Max(8, _w.Value), h = Math.Max(8, _h.Value);
        var r = Math.Min(Math.Min(h / 2, w / 2), MaxRadius);
        var rect = new Rect(BaseWidth / 2 - w / 2, TopGap, w, h);
        var p = Math.Clamp(_e.Value, 0, 1);
        var spread = 0.7 + 0.9 * p; // bigger, softer shadow when expanded
        if (_notch)
        {
            // flush with the top edge (a little above it, so the outline's top stroke is off-screen)
            var notch = new Rect(rect.X, -2, rect.Width, rect.Bottom + 2);
            var ear = 9 + 7 * p;
            var shape = NotchGeometry(notch, r, ear);
            Body.Clip = shape;
            Outline.Data = shape;
            Shadow1.Data = NotchShadow(notch, r, ear, 1.0 * spread, 1.0 * spread);
            Shadow2.Data = NotchShadow(notch, r, ear, 2.5 * spread, 2.0 * spread);
            Shadow3.Data = NotchShadow(notch, r, ear, 4.5 * spread, 3.5 * spread);
        }
        else
        {
            _shape.Rect = rect;
            _shape.RadiusX = _shape.RadiusY = r;
            SetShadow(_shadow1, rect, r, 1.0 * spread, 1.0 * spread);
            SetShadow(_shadow2, rect, r, 2.5 * spread, 2.0 * spread);
            SetShadow(_shadow3, rect, r, 4.5 * spread, 3.5 * spread);
        }

        // content: pill fades out first, panel fades/scales in a little after the shape starts moving
        var pillOpacity = 1 - Math.Clamp(p * 3.2, 0, 1);
        var panelOpacity = Smooth(Math.Clamp((p - 0.25) / 0.6, 0, 1));
        PillLayer.Opacity = pillOpacity;
        PillLayer.Visibility = pillOpacity > 0.01 ? Visibility.Visible : Visibility.Hidden;
        Panel.Opacity = panelOpacity;
        var panelVisible = panelOpacity > 0.01;
        if (panelVisible != (Panel.Visibility == Visibility.Visible))
            Panel.Visibility = panelVisible ? Visibility.Visible : Visibility.Collapsed;
        var scale = 0.95 + 0.05 * panelOpacity;
        PanelScale.ScaleX = PanelScale.ScaleY = scale;
    }

    /// <summary>Attention pulse: the shape bounces <paramref name="times"/> times (finished timer, reminder).</summary>
    public void Pulse(int times = 3)
    {
        if (_snapshotMode || !SystemParameters.ClientAreaAnimation) return;
        for (var i = 0; i < times; i++)
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30 + 420 * i) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                _w.Velocity += 260;
                _h.Velocity += 70;
                SetTargets();
            };
            t.Start();
        }
    }

    static void SetShadow(RectangleGeometry g, Rect rect, double r, double inflate, double dy)
    {
        var s = rect;
        s.Inflate(inflate, inflate);
        s.Offset(0, dy);
        g.Rect = s;
        g.RadiusX = g.RadiusY = r + inflate;
    }

    /// <summary>
    /// A curtain hanging from the top edge: straight sides, rounded bottom corners (<paramref name="r"/>) and concave
    /// "ears" (<paramref name="ear"/>) where the sides meet the top edge, so the shape grows out of the screen border.
    /// </summary>
    internal static StreamGeometry NotchGeometry(Rect rect, double r, double ear)
    {
        double x0 = rect.Left, x1 = rect.Right, top = rect.Top, bottom = rect.Bottom;
        r = Math.Max(0, Math.Min(r, Math.Min(rect.Width / 2, rect.Height - ear)));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(x0 - ear, top), isFilled: true, isClosed: true);
            c.QuadraticBezierTo(new Point(x0, top), new Point(x0, top + ear), true, true);
            c.LineTo(new Point(x0, bottom - r), true, true);
            c.ArcTo(new Point(x0 + r, bottom), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, true);
            c.LineTo(new Point(x1 - r, bottom), true, true);
            c.ArcTo(new Point(x1, bottom - r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, true);
            c.LineTo(new Point(x1, top + ear), true, true);
            c.QuadraticBezierTo(new Point(x1, top), new Point(x1 + ear, top), true, true);
        }
        g.Freeze();
        return g;
    }

    /// <summary>The notch shadow spreads sideways and down only; the top stays on the screen edge.</summary>
    static StreamGeometry NotchShadow(Rect rect, double r, double ear, double inflate, double dy) =>
        NotchGeometry(new Rect(rect.X - inflate, rect.Y, rect.Width + 2 * inflate, rect.Height + inflate + dy), r + inflate, ear);

    static double Smooth(double t) => t * t * (3 - 2 * t);
}
