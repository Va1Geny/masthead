using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Isle.Core;

namespace Isle.Views.Live;

/// <summary>Generic small pill content: optional coloured dot, icon, main text and dim secondary text.</summary>
public sealed class IndicatorPill : Grid
{
    readonly Ellipse _dot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    readonly Icon _icon = new() { Width = 15, Height = 15, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _text = new() { FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _sub = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 };

    public IndicatorPill(string? iconKey, string text, string sub = "", string? dotBrushKey = null, bool center = false)
    {
        _sub.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
        _icon.SetResourceReference(Icon.ForegroundProperty, "FgBrush");
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(14, 0, 14, 0), HorizontalAlignment = center ? HorizontalAlignment.Center : HorizontalAlignment.Stretch };
        DockPanel.SetDock(_dot, Dock.Left);
        DockPanel.SetDock(_icon, Dock.Left);
        DockPanel.SetDock(_sub, Dock.Right);
        row.Children.Add(_dot);
        row.Children.Add(_icon);
        row.Children.Add(_sub);
        row.Children.Add(_text);
        Children.Add(row);
        Set(iconKey, text, sub, dotBrushKey);
    }

    public void Set(string? iconKey, string text, string sub = "", string? dotBrushKey = null)
    {
        if (iconKey is null) _icon.Visibility = Visibility.Collapsed;
        else { _icon.Visibility = Visibility.Visible; _icon.SetResourceReference(Icon.DataProperty, iconKey); }
        if (dotBrushKey is null) _dot.Visibility = Visibility.Collapsed;
        else { _dot.Visibility = Visibility.Visible; _dot.SetResourceReference(Shape.FillProperty, dotBrushKey == "AccentBrush" ? "AccentTextBrush" : dotBrushKey); }
        _text.Text = text;
        _text.ToolTip = text;
        _sub.Text = sub;
        _sub.ToolTip = sub;
        _sub.Visibility = sub.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public static double MessageWidth(string text, string sub = "")
    {
        var label = new TextBlock { Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = 12.5, FontWeight = FontWeights.SemiBold };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var detail = new TextBlock { Text = sub, FontFamily = label.FontFamily, FontSize = 11.5 };
        detail.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Clamp(60 + label.DesiredSize.Width + (sub.Length > 0 ? 8 + Math.Min(150, detail.DesiredSize.Width) : 0), 200, 420);
    }

    /// <summary>Lasting status (engine down, prod context, server problem) with an icon-only view for the side slot.
    /// Sits just below the mic/camera privacy indicator, which must win.</summary>
    public static PillItem Status(string id, string icon, string text, string sub, string brush, double width, string? tabId = null) => new()
    {
        Id = id, Priority = PillPriority.MicCamera - 1, Width = width, TabId = tabId,
        View = () => new IndicatorPill(icon, text, sub, brush),
        Compact = () => new IndicatorPill(icon, "", "", brush, center: true), CompactWidth = 40,
    };

    /// <summary>One-line message, as wide as its text.</summary>
    public static PillItem Banner(string id, string icon, string text, string brush, string? tabId = null) => new()
    {
        Id = id, Priority = PillPriority.Transient, TabId = tabId, Peek = true,
        Width = MessageWidth(text) + 30,
        View = () => new IndicatorPill(icon, text, "", brush),
    };

    /// <summary>Centred coloured icon and text, for short notices.</summary>
    public static FrameworkElement Notice(string icon, string iconBrush, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var i = Ui.MakeIcon(icon, 14, iconBrush);
        i.Margin = new Thickness(0, 0, 6, 0);
        row.Children.Add(i);
        var t = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        row.Children.Add(t);
        return row;
    }
}
