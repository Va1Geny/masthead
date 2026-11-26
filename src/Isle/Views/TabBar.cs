using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Isle.Core;

namespace Isle.Views;

/// <summary>
/// Icon tab strip with a sliding indicator. The selected tab also shows its label. Tabs that do not fit go to
/// the "more" button (the island shows them in an overlay list). A gear button on the right opens settings.
/// </summary>
public sealed class TabBar : Grid
{
    const double TabWidth = 34, MaxLabelWidth = 90;
    double _labelFontSize = 12;

    readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal };
    readonly Border _indicator = new() { CornerRadius = new CornerRadius(14), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    readonly TranslateTransform _indicatorX = new();
    readonly Button _more, _gear;
    readonly TextBlock _moreLabel = new()
    {
        FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(6, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
    };
    readonly ContentPresenter _mascot = new() { Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly Dictionary<string, RadioButton> _buttons = [];
    List<TabInfo> _tabs = [];
    List<TabInfo> _hidden = [];
    string? _selected;
    bool _animateNext;

    public event Action<string>? TabSelected;
    public event Action<IReadOnlyList<TabInfo>>? MoreRequested;
    public event Action? SettingsRequested;

    public string? SelectedId => _selected;
    /// <summary>Tabs that did not fit and live in the "more" list.</summary>
    public IReadOnlyList<TabInfo> Hidden => _hidden;
    internal Button MoreButton => _more;
    public double AvailableWidth { get; set; } = 496;

    /// <summary>Optional character next to the gear (characters module); takes room from the tab strip.</summary>
    public FrameworkElement? Mascot
    {
        get => _mascot.Content as FrameworkElement;
        set
        {
            if (ReferenceEquals(_mascot.Content, value)) return;
            _mascot.Content = value;
            Rebuild();
        }
    }

    public TabBar()
    {
        Height = 28;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(_mascot, 1);
        Children.Add(_mascot);

        _indicator.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        _indicator.RenderTransform = _indicatorX;
        var host = new Grid();
        host.Children.Add(_indicator);
        host.Children.Add(_strip);
        Children.Add(host);

        _more = SideIcon("Icon.More", "tabs.more");
        var moreContent = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 6, 0) };
        var moreIcon = (Icon)_more.Content;
        _more.Content = moreContent;
        moreContent.Children.Add(moreIcon);
        moreContent.Children.Add(_moreLabel);
        _more.Click += (_, _) => MoreRequested?.Invoke(_hidden);
        _more.Visibility = Visibility.Collapsed;
        SetColumn(_more, 2);
        Children.Add(_more);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Left or Key.Right) || _tabs.Count == 0) return;
            var current = _tabs.FindIndex(t => t.Id == _selected);
            var next = (current + (e.Key == Key.Right ? 1 : _tabs.Count - 1)) % _tabs.Count;
            Select(_tabs[next].Id);
            TabSelected?.Invoke(_tabs[next].Id);
            if (_buttons.TryGetValue(_selected!, out var button)) button.Focus();
            else _more.Focus();
            e.Handled = true;
        };

        _gear = SideIcon("Icon.Settings", "tray.settings");
        _gear.Click += (_, _) => SettingsRequested?.Invoke();
        SetColumn(_gear, 3);
        Children.Add(_gear);

        Unloaded += (_, _) => Strings.LanguageChanged -= Rebuild;
        Loaded += (_, _) => { Strings.LanguageChanged += Rebuild; Rebuild(); };
        _strip.LayoutUpdated += (_, _) => MoveIndicator();
    }

    Button SideIcon(string iconKey, string tipKey)
    {
        var b = new Button { Margin = new Thickness(4, 0, 0, 0) };
        b.SetResourceReference(StyleProperty, "IconButton");
        var icon = new Icon { Width = 16, Height = 16 };
        icon.SetResourceReference(Icon.DataProperty, iconKey);
        icon.SetResourceReference(Icon.ForegroundProperty, "FgDimBrush");
        b.Content = icon;
        b.SetBinding(ToolTipProperty, new System.Windows.Data.Binding($"[{tipKey}]") { Source = Strings.Instance });
        b.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, new System.Windows.Data.Binding($"[{tipKey}]") { Source = Strings.Instance });
        return b;
    }

    public void SetTabs(IEnumerable<TabInfo> tabs, string? selected)
    {
        _tabs = [.. tabs.OrderBy(t => t.Order)];
        _selected = selected is not null && _tabs.Any(t => t.Id == selected) ? selected : _tabs.FirstOrDefault()?.Id;
        Rebuild();
    }

    internal void SetMetrics(double targetHeight, double fontSize)
    {
        if (Height == targetHeight && _labelFontSize == fontSize) return;
        Height = targetHeight;
        _labelFontSize = fontSize;
        Rebuild();
    }

    public void Select(string id, bool animate = true)
    {
        if (_tabs.All(t => t.Id != id)) return;
        _selected = id;
        _animateNext = animate;
        UpdateButtons();
    }

    void Rebuild()
    {
        var focused = _buttons.FirstOrDefault(entry => entry.Value.IsKeyboardFocused).Key;
        _strip.Children.Clear();
        _buttons.Clear();

        // how many fit?
        var maxLabelWidth = MaxLabelWidth * _labelFontSize / 12;
        var labelRoom = _tabs.Count == 0 ? 0 : _tabs.Max(t =>
        {
            var measure = new TextBlock { Text = Strings.T(t.TitleKey), FontFamily = System.Windows.Documents.TextElement.GetFontFamily(this), FontSize = _labelFontSize, FontWeight = FontWeights.SemiBold, MaxWidth = maxLabelWidth };
            measure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Ceiling(measure.DesiredSize.Width) + 7;
        });
        var sideWidth = Height + _gear.Margin.Left;
        var tabWidth = Math.Max(TabWidth, Height);
        var avail = AvailableWidth - sideWidth - _mascot.Margin.Left - (Mascot is { } m ? (double.IsNaN(m.Width) ? 28 : m.Width) : 0);
        var visible = _tabs;
        _hidden = [];
        if (_tabs.Count * tabWidth + labelRoom > avail)
        {
            var n = Math.Max(1, (int)((avail - sideWidth - labelRoom) / tabWidth));
            visible = [.. _tabs.Take(n)];
            _hidden = [.. _tabs.Skip(n)];
        }
        _more.Visibility = _hidden.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var tab in visible)
        {
            var icon = new Icon { Width = 16, Height = 16 };
            icon.SetResourceReference(Icon.DataProperty, tab.IconKey);
            var label = new TextBlock
            {
                Text = Strings.T(tab.TitleKey), FontSize = _labelFontSize, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 0, 1, 0),
                VerticalAlignment = VerticalAlignment.Center, MaxWidth = maxLabelWidth, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(icon);
            content.Children.Add(label);
            var rb = new RadioButton { Content = content, Tag = label, GroupName = "IsleTabs" + GetHashCode(), ToolTip = Strings.T(tab.TitleKey) };
            rb.SetResourceReference(StyleProperty, "TabButton");
            var id = tab.Id;
            // Checked, not Click: keyboard, mouse and UI Automation (screen readers' "select") all switch the tab
            rb.Checked += (_, _) =>
            {
                if (_selected == id) return;
                _selected = id;
                _animateNext = true;
                UpdateButtons();
                TabSelected?.Invoke(id);
            };
            _buttons[tab.Id] = rb;
            _strip.Children.Add(rb);
        }
        _animateNext = false;
        UpdateButtons();
        if (focused is not null)
        {
            if (_buttons.TryGetValue(focused, out var button)) button.Focus();
            else _more.Focus();
        }
    }

    void UpdateButtons()
    {
        var hidden = _hidden.FirstOrDefault(t => t.Id == _selected);
        var moreName = Strings.T("tabs.more");
        _moreLabel.Visibility = hidden is null ? Visibility.Collapsed : Visibility.Visible;
        if (hidden is null)
        {
            _more.ClearValue(WidthProperty);
            _more.ClearValue(Button.BackgroundProperty);
        }
        else
        {
            _moreLabel.Text = Strings.T(hidden.TitleKey);
            _moreLabel.FontSize = _labelFontSize;
            _moreLabel.MaxWidth = MaxLabelWidth * _labelFontSize / 12;
            _more.Width = double.NaN;
            _more.SetResourceReference(Button.BackgroundProperty, "SurfaceBrush");
            moreName += $" ({_moreLabel.Text})";
        }
        _more.ToolTip = moreName;
        AutomationProperties.SetName(_more, moreName);
        foreach (var (id, rb) in _buttons)
        {
            var on = id == _selected;
            rb.IsChecked = on;
            rb.IsTabStop = on; // one tab stop for the strip; arrows move between tabs
            ((TextBlock)rb.Tag).Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetIsEnabled(rb, !on);
        }
        _indicatorDirty = true;
        _strip.InvalidateArrange();
    }

    bool _indicatorDirty;

    void MoveIndicator()
    {
        if (!_indicatorDirty) return;
        if (_selected is null || !_buttons.TryGetValue(_selected, out var rb))
        {
            _indicatorDirty = false;
            _indicator.BeginAnimation(WidthProperty, null);
            _indicator.Width = 0;
            return;
        }
        if (rb.ActualWidth <= 0) return; // not laid out yet
        _indicatorDirty = false;
        var x = rb.TranslatePoint(new Point(0, 0), _strip).X;
        var w = rb.ActualWidth;
        if (_animateNext && SystemParameters.ClientAreaAnimation && _indicator.Width > 0)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var d = TimeSpan.FromMilliseconds(220);
            _indicatorX.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, d) { EasingFunction = ease });
            _indicator.BeginAnimation(WidthProperty, new DoubleAnimation(w, d) { EasingFunction = ease });
        }
        else
        {
            _indicatorX.BeginAnimation(TranslateTransform.XProperty, null);
            _indicator.BeginAnimation(WidthProperty, null);
            _indicatorX.X = x;
            _indicator.Width = w;
        }
        _animateNext = false;
    }
}
