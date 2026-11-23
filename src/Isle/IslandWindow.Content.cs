using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Isle.Core;
using Isle.Views;

namespace Isle;

// What is inside the island: the collapsed pill (priority queue), the tabs of the expanded panel, and the toast.
public partial class IslandWindow
{
    readonly List<TabInfo> _tabs = [];
    readonly Dictionary<string, FrameworkElement> _views = [];
    string? _currentTab, _requestedTab;
    bool _tabChosen; // the user (or a module) picked a tab this session; until then the remembered tab wins
    IdlePillView? _idleView;
    DispatcherTimer? _toastTimer;
    Action? _toastAction;

    /// <summary>Raised when the gear button is pressed.</summary>
    public event Action? SettingsRequested;

    void InitContent()
    {
        Tabs.TabSelected += id => { _tabChosen = true; ShowTab(id, animate: true); };
        Tabs.MoreRequested += ShowMoreOverlay;
        Tabs.SettingsRequested += () => SettingsRequested?.Invoke();
        ToastAction.Click += (_, _) =>
        {
            var a = _toastAction;
            HideToast();
            if (a is not null) Safe(a, "toast action");
        };
        Pill.Changed += OnPillChanged;
        if (!_snapshotMode)
            ExpandedChanged += expanded => { if (expanded) _idleView?.DismissHint(); };
        UpdateIdlePill();
        RefreshTabs();
    }

    // ---- pill ----

    /// <summary>(Re)creates the idle item; called at start and when idle-related settings change.</summary>
    public void UpdateIdlePill()
    {
        _idleView ??= new IdlePillView();
        _idleView.Refresh();
        if (!Pill.Contains("idle"))
            Pill.Set(new PillItem { Id = "idle", Priority = PillPriority.Idle, View = () => _idleView, Width = _idleView.DesiredPillWidth });
        else if (Pill.Primary?.Id == "idle" && Math.Abs(_pillW - _idleView.DesiredPillWidth) > 0.5)
        {
            Pill.Remove("idle");
            Pill.Set(new PillItem { Id = "idle", Priority = PillPriority.Idle, View = () => _idleView, Width = _idleView.DesiredPillWidth });
        }
    }

    /// <summary>The idle pill view; Music widens it to match the mini player.</summary>
    public IdlePillView IdleView => _idleView ??= new IdlePillView();

    void OnPillChanged()
    {
        var primary = Pill.Primary;
        var secondary = Pill.Secondary;
        try
        {
            PillPrimary.Content = primary is null ? null : primary.ViewCache ??= primary.View();
            PillSecondary.Content = secondary is null ? null : secondary.CompactCache ??= secondary.Compact!();
        }
        catch (Exception ex)
        {
            Logger.Error($"pill view {primary?.Id}", ex);
            PillPrimary.Content = null;
            PillSecondary.Content = null;
        }
        // the side slot gets its declared width, inset from the rounded end so its content is never clipped
        PillSecondary.Width = secondary is null ? double.NaN : secondary.CompactWidth;
        PillSecondary.Margin = secondary is null ? new Thickness(0) : new Thickness(0, 0, 8, 0);
        _pillW = (primary?.Width ?? 120) + (secondary is null ? 0 : secondary.CompactWidth + 8);
        _pillH = primary?.Height ?? 32;
        PillLayer.Width = _pillW;
        PillLayer.Height = _pillH;
        if (!_snapshotMode && IsLoaded && SystemParameters.ClientAreaAnimation)
        {
            // quick fade so a content swap does not pop
            PillPrimary.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(160)));
        }
        SetTargets();
        var showFullscreenAlert = primary is { IsBanner: true, Priority: PillPriority.Finished };
        if (_fullscreenAlert != showFullscreenAlert)
        {
            _fullscreenAlert = showFullscreenAlert;
            CheckForeground();
        }
    }

    // ---- tabs ----

    public void AddTab(TabInfo tab)
    {
        _tabs.RemoveAll(t => t.Id == tab.Id);
        _tabs.Add(tab);
        RefreshTabs();
    }

    public void RemoveTab(string id)
    {
        if (_tabs.RemoveAll(t => t.Id == id) == 0) return;
        if (_views.Remove(id, out var view))
        {
            if (_currentTab == id && _expanded) (view as ITabView)?.OnHidden();
            TabContent.Children.Remove(view);
            (view as IDisposable)?.Dispose();
        }
        if (_currentTab == id) _currentTab = null;
        RefreshTabs();
    }

    public IReadOnlyList<TabInfo> TabList => _tabs;

    /// <summary>The view of a tab if it has been created.</summary>
    public FrameworkElement? TabViewFor(string id) => _views.GetValueOrDefault(id);

    /// <summary>Asks for this tab to be shown the next time the island expands (e.g. Music after a track change).</summary>
    public void RequestTab(string id) => _requestedTab = id;

    void RefreshTabs()
    {
        var last = Settings.Current.LastTab;
        var wanted = !_tabChosen && _tabs.Any(t => t.Id == last) ? last : _currentTab ?? last;
        Tabs.SetTabs(_tabs, wanted);
        EmptyText.Visibility = _tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = Strings.T("panel.empty");
        if (Tabs.SelectedId is { } id) ShowTab(id, animate: false);
    }

    public void SelectTab(string id, bool animate = false)
    {
        if (_tabs.All(t => t.Id != id)) return;
        _tabChosen = true;
        Tabs.Select(id, animate);
        ShowTab(id, animate);
    }

    void ShowTab(string id, bool animate)
    {
        HideMoreOverlay();
        if (_currentTab == id && _views.ContainsKey(id)) return;
        var tab = _tabs.FirstOrDefault(t => t.Id == id);
        if (tab is null) return;

        if (!_views.TryGetValue(id, out var view))
        {
            try { view = tab.CreateView(); }
            catch (Exception ex)
            {
                Logger.Error($"tab {id} view", ex);
                var msg = new TextBlock { Text = Strings.T("panel.tabFailed"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                msg.SetResourceReference(StyleProperty, "TextDim");
                view = msg;
            }
            _views[id] = view;
            view.Visibility = Visibility.Collapsed;
            TabContent.Children.Add(view);
        }

        var old = _currentTab is not null && _views.TryGetValue(_currentTab, out var o) ? o : null;
        _currentTab = id;
        if (old is not null && !ReferenceEquals(old, view))
        {
            if (_expanded) Safe(() => (old as ITabView)?.OnHidden(), "tab hidden");
            if (animate && !_snapshotMode && SystemParameters.ClientAreaAnimation)
            {
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
                fade.Completed += (_, _) =>
                {
                    if (!ReferenceEquals(_views.GetValueOrDefault(_currentTab ?? ""), old)) old.Visibility = Visibility.Collapsed;
                };
                old.BeginAnimation(OpacityProperty, fade);
            }
            else
            {
                old.BeginAnimation(OpacityProperty, null);
                old.Visibility = Visibility.Collapsed;
            }
        }
        view.Visibility = Visibility.Visible;
        if (animate && !_snapshotMode && SystemParameters.ClientAreaAnimation)
            view.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { BeginTime = TimeSpan.FromMilliseconds(60) });
        else
        {
            view.BeginAnimation(OpacityProperty, null);
            view.Opacity = 1;
        }
        if (_expanded) Safe(() => (view as ITabView)?.OnShown(), "tab shown");
    }

    void NotifyCurrentView(bool shown)
    {
        if (_currentTab is null || !_views.TryGetValue(_currentTab, out var v) || v is not ITabView tv) return;
        Safe(() => { if (shown) tv.OnShown(); else tv.OnHidden(); }, "tab visibility");
    }

    void ShowMoreOverlay(IReadOnlyList<TabInfo> hidden)
    {
        if (MoreOverlay.IsVisible) { HideMoreOverlay(); return; }
        if (hidden.Count == 0) return;
        MoreList.Children.Clear();
        Button? focusRow = null;
        foreach (var tab in hidden)
        {
            var icon = new Views.Icon { Width = 15, Height = 15, Margin = new Thickness(0, 0, 8, 0) };
            icon.SetResourceReference(Views.Icon.DataProperty, tab.IconKey);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(icon);
            var title = Strings.T(tab.TitleKey);
            row.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Content = row, Padding = new Thickness(8, 4, 12, 4) };
            AutomationProperties.SetName(b, title);
            b.SetResourceReference(StyleProperty, "GhostButton");
            b.SetResourceReference(FontSizeProperty, "NavigationFontSize");
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            var id = tab.Id;
            if (id == Tabs.SelectedId)
            {
                focusRow = b;
                b.SetResourceReference(Button.BackgroundProperty, "SurfaceBrush");
            }
            focusRow ??= b;
            b.Click += (_, _) => SelectTab(id, animate: true);
            MoreList.Children.Add(b);
        }
        MoreOverlay.Visibility = Visibility.Visible;
        AcquireFocus();
        MoreOverlay.UpdateLayout();
        focusRow?.Focus();
    }

    void HideMoreOverlay()
    {
        if (MoreOverlay.Visibility != Visibility.Visible) return;
        if (MoreOverlay.IsKeyboardFocusWithin) Tabs.MoreButton.Focus();
        MoreOverlay.Visibility = Visibility.Collapsed;
    }

    // ---- toast (inside the expanded panel) ----

    /// <summary>Shows a short message at the bottom of the panel, optionally with one action (e.g. Undo).</summary>
    public void ShowToast(string text, string? actionText = null, Action? action = null, double seconds = 5)
    {
        ToastText.Text = text;
        _toastAction = action;
        ToastAction.Content = actionText;
        ToastAction.Visibility = actionText is null ? Visibility.Collapsed : Visibility.Visible;
        Toast.Padding = actionText is null ? new Thickness(12, 6, 12, 6) : new Thickness(12, 4, 4, 4);
        Toast.Visibility = Visibility.Visible;
        if (_snapshotMode) return;
        if (SystemParameters.ClientAreaAnimation)
            Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        _toastTimer ??= new DispatcherTimer(DispatcherPriority.Background);
        _toastTimer.Stop();
        _toastTimer.Tick -= OnToastTick;
        _toastTimer.Tick += OnToastTick;
        _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
        _toastTimer.Start();
    }

    void OnToastTick(object? sender, EventArgs e) => HideToast();

    public void HideToast()
    {
        _toastTimer?.Stop();
        _toastAction = null;
        Toast.Visibility = Visibility.Collapsed;
    }
}
