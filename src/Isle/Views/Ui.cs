using System.Windows;
using System.Windows.Controls;
using Isle.Core;

namespace Isle.Views;

/// <summary>Small widget builders shared by the code-behind views.</summary>
static class Ui
{
    /// <summary>Binding to <c>Strings.Instance[key]</c>, so the text follows a language switch.</summary>
    public static System.Windows.Data.Binding Loc(string key) => new($"[{key}]") { Source = Strings.Instance };

    public static Icon MakeIcon(string key, double size, string brush = "FgDimBrush", bool filled = false, double? stroke = null)
    {
        var i = new Icon { Width = size, Height = size };
        if (filled) i.Filled = true;
        if (stroke is { } s) i.Stroke = s;
        i.SetResourceReference(Icon.DataProperty, key);
        i.SetResourceReference(Icon.ForegroundProperty, brush);
        return i;
    }

    /// <summary>Wires <paramref name="click"/>; with an <paramref name="area"/> a throw is logged instead of reaching the dispatcher.</summary>
    public static void OnClick(Button b, Action click, string? area, bool handled = false) =>
        b.Click += (_, e) =>
        {
            if (handled) e.Handled = true;
            if (area is null) { click(); return; }
            try { click(); } catch (Exception ex) { Logger.Error(area, ex); }
        };

    public static Button TextButton(string text, string style, Thickness padding, Action click, string? area, double minHeight = 0)
    {
        var b = new Button { Content = text, Padding = padding };
        if (minHeight > 0) b.MinHeight = minHeight; // a local 0 would override the style's MinHeight
        b.SetResourceReference(FrameworkElement.StyleProperty, style);
        OnClick(b, click, area);
        return b;
    }

    /// <param name="tip">Tooltip; the IconButton style also uses it as the accessible name.</param>
    public static Button IconButton(FrameworkElement icon, double size, Action click, string? area, bool handled = false, string? tip = null)
    {
        var b = new Button { Content = icon, Width = size, Height = size, ToolTip = tip };
        b.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
        OnClick(b, click, area, handled);
        return b;
    }

    /// <summary>28 px icon button with a translated tooltip that screen readers also announce.</summary>
    public static Button IconAction(string icon, string tipKey, Action click, string area, string brush = "FgDimBrush")
    {
        return IconButton(MakeIcon(icon, 15, brush), 28, click, area, tip: Strings.T(tipKey));
    }

    public static TextBlock Dim(string text, bool wrap = false)
    {
        var t = new TextBlock { Text = text };
        if (wrap) t.TextWrapping = TextWrapping.Wrap;
        t.SetResourceReference(FrameworkElement.StyleProperty, "TextDim");
        return t;
    }

    /// <summary>Title row (with optional close and extra buttons on the right) above a body that fills the rest.</summary>
    public static Grid OverlayFrame(TextBlock title, FrameworkElement body, Button? close, FrameworkElement? extra, double headGap)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, Math.Max(0, headGap - 2)) };
        foreach (var e in new[] { close, extra })
            if (e is not null) { DockPanel.SetDock(e, Dock.Right); head.Children.Add(e); }
        title.VerticalAlignment = VerticalAlignment.Center;
        title.SetResourceReference(FrameworkElement.StyleProperty, "TextTitle");
        head.Children.Add(title);
        grid.Children.Add(head);
        Grid.SetRow(body, 1);
        grid.Children.Add(body);
        return grid;
    }
}
