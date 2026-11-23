using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Isle.Core;

namespace Isle;

// Pointer logic (hover-intent, leave delay), keyboard focus hand-over, and the rules that hide
// the island (fullscreen app, excluded app, user).
public partial class IslandWindow
{
    const int HoverIntentMs = 120, HoverRearmMs = 150, LeaveDelayMs = 250;

    DispatcherTimer? _hoverTimer;
    long _insideSince, _outsideSince;
    int _intentMs;
    Native.POINT _lastCursor;
    bool _hoverMoved, _awaitLeave;
    bool _sticky;           // opened by hotkey/API: stay open until the cursor has visited once
    bool _hasFocus;         // island took keyboard focus for a text box
    bool _focusOnExpand;
    IntPtr _previousForeground;
    readonly HashSet<string> _hideReasons = [];
    string _lastSavedTab = "";

    void InitHover()
    {
        // The 30 ms cursor timer only runs while the pointer is engaged with the island (or it is expanded).
        _hoverTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
        _hoverTimer.Tick += OnHoverTick;
        Native.GetCursorPos(out _lastCursor);
        MouseEnter += (_, _) => EngagePointer();
        MouseMove += (_, _) => { if (!_hoverTimer.IsEnabled) EngagePointer(); };
        PreviewMouseLeftButtonDown += OnPreviewMouseDown;
        PreviewKeyDown += (_, e) =>
        {
            if (_hasFocus && e.Key == Key.Escape)
            {
                if (MoreOverlay.IsVisible) HideMoreOverlay();
                else Collapse();
                e.Handled = true;
            }
            if (_hasFocus && _expanded && _overlay?.Visibility != Visibility.Visible && Keyboard.Modifiers == ModifierKeys.Control &&
                e.Key >= Key.D1 && e.Key <= Key.D9)
            {
                var tabs = TabList.OrderBy(t => t.Order).ToList();
                var index = e.Key - Key.D1;
                if (index < tabs.Count) { SelectTab(tabs[index].Id, animate: true); e.Handled = true; }
            }
        };
        Deactivated += (_, _) => OnDeactivated();
        PillLayer.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var click = Pill.Primary?.Click;
            if (click is not null) Safe(click, "pill click");
            else Expand();
        };
        Loaded += (_, _) => CheckForeground();
        Panel.IsVisibleChanged += (_, _) => FocusExpandedPanel();
        KeyboardNavigation.SetTabNavigation(Panel, KeyboardNavigationMode.Cycle);
    }

    void EngagePointer()
    {
        if (_hoverTimer is { IsEnabled: false })
        {
            _insideSince = _outsideSince = 0;
            _hoverTimer.Start();
        }
    }

    void OnHoverTick(object? sender, EventArgs e)
    {
        if (!IsVisible) { _hoverTimer!.Stop(); return; }
        var now = Environment.TickCount64;
        var down = (Native.GetAsyncKeyState(0x01) & 0x8000) != 0 ||
            (Native.GetAsyncKeyState(0x02) & 0x8000) != 0;
        var inside = false;
        if (Native.GetCursorPos(out var cursor))
        {
            inside = IsCursorInside(CursorOnStage(cursor));
            if (Math.Abs(cursor.X - _lastCursor.X) + Math.Abs(cursor.Y - _lastCursor.Y) >= 2)
            {
                _lastCursor = cursor;
                if (inside && !down && !_awaitLeave) _hoverMoved = true;
            }
        }
        if (inside)
        {
            _outsideSince = 0;
            if (_expanded) { _sticky = false; return; }
            if (IsOverButton()) { _insideSince = 0; return; } // a button in the pill (music mini player) is for clicking
            if (down) _hoverMoved = false;
            if (_awaitLeave || !_hoverMoved) { _insideSince = 0; return; }
            if (_insideSince == 0)
            {
                _insideSince = now;
                _intentMs = Native.IsZoomed(Native.GetForegroundWindow()) ? 150 : HoverIntentMs;
            }
            if (now - _insideSince >= _intentMs && Pill.Primary?.HoverExpands != false) Expand();
            return;
        }

        _insideSince = 0;
        _hoverMoved = false;
        if (_outsideSince == 0) _outsideSince = now;
        if (_expanded)
        {
            if (now - _outsideSince >= LeaveDelayMs && !_sticky && !_hasFocus) Collapse();
            return;
        }
        if (_awaitLeave)
        {
            if (now - _outsideSince < HoverRearmMs) return;
            _awaitLeave = false;
        }
        _hoverTimer!.Stop();
    }

    /// <summary>Cursor position in stage coordinates (DIPs at 100% scale), or null if unavailable.</summary>
    Point? CursorOnStage(Native.POINT pt)
    {
        if (_source is null) return null;
        try
        {
            var p = PointFromScreen(new Point(pt.X, pt.Y));
            var s = UiScale.ScaleX;
            return new Point(p.X / s, p.Y / s);
        }
        catch { return null; }
    }

    bool IsCursorInside() => Native.GetCursorPos(out var cursor) && IsCursorInside(CursorOnStage(cursor));

    bool IsCursorInside(Point? position)
    {
        if (position is not { } p) return false;
        double w = _expanded ? ExpandedWidth : _pillW, h = _expanded ? ExpandedHeight : _pillH;
        double m = _expanded ? 10 : 3;
        // the gap above the island counts as inside, so pushing the cursor to the screen edge keeps it open
        return p.X >= BaseWidth / 2 - w / 2 - m && p.X <= BaseWidth / 2 + w / 2 + m && p.Y >= -4 && p.Y <= TopGap + h + m;
    }

    // ---- expand / collapse ----

    /// <summary>Opens the panel. <paramref name="sticky"/>: keep it open until the cursor has entered once (hotkeys).</summary>
    public void Expand(string? tabId = null, bool sticky = false)
    {
        var tab = tabId ?? _requestedTab ?? Pill.Primary?.TabId;
        _requestedTab = null;
        if (tab is not null) SelectTab(tab, animate: _expanded);
        if (_expanded) { _sticky |= sticky; return; }

        _expanded = true;
        _sticky = sticky;
        NotifyCurrentView(shown: true);
        SetTargets();
        if (!_snapshotMode && _hoverTimer is { IsEnabled: false })
        {
            _insideSince = _outsideSince = 0;
            _hoverTimer.Start();
        }
        ExpandedChanged?.Invoke(true);
    }

    public void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        _focusOnExpand = false;
        _insideSince = _outsideSince = 0;
        _hoverMoved = false;
        _awaitLeave = true;
        _sticky = false;
        HideMoreOverlay();
        HideOverlay();
        NotifyCurrentView(shown: false);
        ReleaseFocus(restore: true);
        SetTargets();
        if (_currentTab is not null && _currentTab != _lastSavedTab && !_snapshotMode)
        {
            _lastSavedTab = _currentTab;
            Settings.Current.LastTab = _currentTab;
            JsonStore.Save(Paths.SettingsFile, Settings.Current); // quiet save: no Changed broadcast needed
        }
        ExpandedChanged?.Invoke(false);
    }

    public void Toggle()
    {
        if (_expanded) Collapse();
        else
        {
            Expand(sticky: true);
            AcquireFocus();
            _focusOnExpand = _hasFocus;
            FocusExpandedPanel();
        }
    }

    void FocusExpandedPanel()
    {
        if (!_focusOnExpand || !_hasFocus || !_expanded || !Panel.IsVisible) return;
        _focusOnExpand = false;
        Dispatcher.InvokeAsync(() =>
        {
            if (!_hasFocus || !_expanded || !Panel.IsVisible) return;
            if (_overlay is Views.SettingsPanel.SettingsView { IsVisible: true } settings && !settings.IsKeyboardFocusWithin)
                settings.FocusSection();
            else if (!Panel.IsKeyboardFocusWithin)
                (_overlay is { IsVisible: true } view ? view : Panel).MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }, DispatcherPriority.Loaded);
    }

    // ---- keyboard focus ----

    void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (MoreOverlay.IsVisible && !MoreOverlay.IsMouseOver && !Tabs.MoreButton.IsMouseOver) HideMoreOverlay();
        // Text inputs need real keyboard focus; everything else works without activating the island.
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = GetParent(d))
        {
            if (d is TextBoxBase or PasswordBox) { AcquireFocus(); return; }
        }
    }

    static bool IsOverButton()
    {
        for (var d = Mouse.DirectlyOver as DependencyObject; d is not null; d = GetParent(d))
            if (d is ButtonBase) return true;
        return false;
    }

    static DependencyObject? GetParent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    /// <summary>Activates the island so a text box can receive typing; remembers who had focus before.</summary>
    public void AcquireFocus()
    {
        if (_hasFocus || _hwnd == IntPtr.Zero) return;
        var fg = Native.GetForegroundWindow();
        if (fg != _hwnd) _previousForeground = fg;
        _hasFocus = true;
        ApplyExStyle(); // drop WS_EX_NOACTIVATE so Windows gives the window real keyboard focus
        Native.SetForegroundWindow(_hwnd);
        Activate();
        Native.SetFocus(_hwnd);
    }

    void ReleaseFocus(bool restore)
    {
        if (!_hasFocus) return;
        _hasFocus = false;
        ApplyExStyle();
        Keyboard.ClearFocus();
        if (restore && _previousForeground != IntPtr.Zero && Native.IsWindow(_previousForeground) && Native.GetForegroundWindow() == _hwnd)
            Native.SetForegroundWindow(_previousForeground);
    }

    void OnDeactivated()
    {
        if (!_hasFocus) return;
        _hasFocus = false;      // the user clicked another window: focus is already gone, do not pull it back
        _focusOnExpand = false;
        ApplyExStyle();
        Keyboard.ClearFocus();
        if (_expanded && !IsCursorInside()) Collapse();
    }

    // ---- hide rules ----

    /// <summary>Hides/shows the whole island for a named reason ("user", "fullscreen", "excluded"). Shown only when no reason is left.</summary>
    public void SetHidden(string reason, bool hidden)
    {
        var changed = hidden ? _hideReasons.Add(reason) : _hideReasons.Remove(reason);
        if (!changed) return;
        if (_hideReasons.Count > 0)
        {
            if (IsVisible)
            {
                Collapse();
                _hoverTimer?.Stop();
                Hide();
            }
        }
        else if (!IsVisible)
        {
            Show();
            Reposition();
        }
    }

    public bool IsHiddenByUser => _hideReasons.Contains("user");

    void OnForegroundChanged(IntPtr hwnd)
    {
        CheckForeground();
        // apps often go fullscreen a moment after they come to the front
        Dispatcher.BeginInvoke(CheckForeground, DispatcherPriority.ApplicationIdle);
    }

    void CheckForeground()
    {
        if (_snapshotMode || _hwnd == IntPtr.Zero) return;
        try
        {
            var s = Settings.Current;
            var fg = Native.GetForegroundWindow();
            SetHidden("fullscreen", s.HideInFullscreen && !_fullscreenAlert && ForegroundWatcher.IsFullscreen(fg, Screen));
            var excluded = false;
            if (s.ExcludedApps.Count > 0 && fg != IntPtr.Zero && !ForegroundWatcher.IsOwn(fg))
            {
                var name = ForegroundWatcher.ProcessName(fg);
                excluded = name.Length > 0 && s.ExcludedApps.Any(a => IsSameApp(a, name));
                if (excluded && s.ExcludedAppsOnlyOnOverlap)
                    excluded = ForegroundWatcher.TryGetExternalWindowBounds(fg, out var bounds) && CapsuleOverlaps(bounds);
            }
            SetHidden("excluded", excluded);
        }
        catch (Exception ex) { Logger.Error("foreground check", ex); }
    }

    bool CapsuleOverlaps(Native.RECT bounds)
    {
        var scale = UiScale.ScaleX * VisualTreeHelper.GetDpi(this).DpiScaleX;
        var screen = Screen;
        return BoundsOverlapCapsule(bounds, screen, _pillW * scale, _pillH * scale, TopGap * scale);
    }

    internal static bool BoundsOverlapCapsule(Native.RECT bounds, ScreenInfo screen, double width, double height, double gap)
    {
        var left = screen.Left + (screen.Width - width) / 2;
        var top = screen.Top + gap;
        return bounds.Left < left + width && bounds.Right > left && bounds.Top < top + height && bounds.Bottom > top;
    }

    internal static bool IsSameApp(string listed, string processName)
    {
        listed = listed.Trim();
        if (listed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) listed = listed[..^4];
        return listed.Length > 0 && string.Equals(listed, processName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Recheck fullscreen or a moved window even when the foreground process stays the same.</summary>
    void UpdateHousekeeping()
    {
        if (_snapshotMode) return;
        var s = Settings.Current;
        var needed = s.HideInFullscreen || (s.ExcludedAppsOnlyOnOverlap && s.ExcludedApps.Count > 0);
        if (needed && _housekeeping is null)
        {
            _housekeeping = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _housekeeping.Tick += (_, _) => CheckForeground();
        }
        if (needed) _housekeeping!.Start();
        else _housekeeping?.Stop();
    }

    static void Safe(Action action, string what)
    {
        try { action(); }
        catch (Exception ex) { Logger.Error(what, ex); }
    }
}
