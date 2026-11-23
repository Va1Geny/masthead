using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

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
}
